using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.Database;

/// <summary>清单条目：库里的媒体 id → 包内文件名 + 原文件名（恢复时还原名字，去掉防重名序号）。</summary>
internal sealed record BackupManifestItem(long Id, string Entry, string FileName);

/// <summary>
/// 完整备份 / 恢复（.mmbak，本质是 zip）。
///
/// 包结构：
/// <code>
///   library.db            ← 一致性快照（SQLite 在线备份导出，单文件即完整）
///   vault/0001_photo.jpg  ← 私密空间的文件（序号防重名）
///   vault/0002_video.mp4
///   manifest.json         ← [{ id, entry }]：库里的媒体 id → 包内文件
/// </code>
///
/// 恢复的关键动作是**按新数据目录重写路径**：库里的 Path 是绝对路径，
/// 换了电脑 / 换了数据目录就直接失效；把文件放回当前 Vault 目录后，
/// 逐条把 Path 改到新位置，OriginalPath（搬移历史）原样保留。
/// </summary>
public sealed class FullBackupService : IFullBackupService
{
    private const string DbEntryName = "library.db";
    private const string ManifestEntryName = "manifest.json";

    private readonly IDbConnectionFactory _dbFactory;
    private readonly IDatabaseBackupService _backup;
    private readonly IVaultService _vault;
    private readonly IMediaRepository _repo;
    private readonly ILogger<FullBackupService> _logger;

    public FullBackupService(
        IDbConnectionFactory dbFactory,
        IDatabaseBackupService backup,
        IVaultService vault,
        IMediaRepository repo,
        ILogger<FullBackupService> logger)
    {
        _dbFactory = dbFactory;
        _backup = backup;
        _vault = vault;
        _repo = repo;
        _logger = logger;
    }

    // ---------- 导出 ----------

    public async Task<FullBackupResult> ExportAsync(string zipPath, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string? tempDb = null;
        try
        {
            progress?.Report("正在导出数据库…");

            // 复用数据库备份服务的一致性导出（在线备份 + WAL 归一），落在临时目录
            tempDb = Path.Combine(Path.GetTempPath(), $"MediaManager_export_{Guid.NewGuid():N}.db");
            await _backup.ExportAsync(tempDb).ConfigureAwait(false);

            progress?.Report("正在收集私密空间的文件…");
            var privateItems = await _repo.QueryAsync(new MediaFilter
            {
                IncludePrivate = true,
                Limit = 1_000_000,
            }).ConfigureAwait(false);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var manifest = new List<BackupManifestItem>();
            var packed = 0;
            var nextIndex = 1;

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDb, DbEntryName, CompressionLevel.Optimal);

                foreach (var item in privateItems)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!File.Exists(item.Path))
                    {
                        _logger.LogWarning("私密文件不存在，跳过打包：{Path}", item.Path);
                        continue;
                    }

                    // 0001_photo.jpg：同一目录里重名文件本来就不存在（入库时唯一），
                    // 但保险箱历史数据可能同名，加序号兜底
                    var entryName = $"vault/{nextIndex++:D4}_{item.FileName}";
                    try
                    {
                        // 媒体文件基本已是压缩格式，Fastest 省下的时间远大于多占的空间
                        zip.CreateEntryFromFile(item.Path, entryName, CompressionLevel.Fastest);
                        manifest.Add(new BackupManifestItem(item.Id, entryName, item.FileName));
                        packed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "打包私密文件失败，跳过：{Path}", item.Path);
                    }
                }

                progress?.Report("正在写清单…");
                var manifestEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    await writer.WriteAsync(
                        JsonSerializer.Serialize(manifest, JsonOptions)).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("完整备份已导出到 {Path}：私密文件 {Count} 个", zipPath, packed);
            progress?.Report("完成");
            return FullBackupResult.Ok(packed);
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* 清理失败无所谓 */ }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出完整备份失败");
            return FullBackupResult.Fail(ex.Message);
        }
        finally
        {
            TryDelete(tempDb);
        }
    }

    // ---------- 恢复 ----------

    public async Task<FullBackupResult> ImportAsync(string zipPath, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string? tempDir = null;
        try
        {
            if (!File.Exists(zipPath))
                return FullBackupResult.Fail("备份包不存在");

            tempDir = Path.Combine(Path.GetTempPath(), $"MediaManager_restore_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            progress?.Report("正在读取备份包…");

            List<BackupManifestItem> manifest;
            string extractedDb;
            var vaultFiles = new List<(string Entry, string ExtractedPath)>();

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var dbEntry = zip.GetEntry(DbEntryName)
                    ?? throw new InvalidDataException("备份包里没有数据库（不是完整备份包？）");

                extractedDb = Path.Combine(tempDir, "library.db");
                dbEntry.ExtractToFile(extractedDb, overwrite: true);

                var manifestEntry = zip.GetEntry(ManifestEntryName);
                if (manifestEntry != null)
                {
                    using var reader = new StreamReader(manifestEntry.Open());
                    manifest = JsonSerializer.Deserialize<List<BackupManifestItem>>(
                        await reader.ReadToEndAsync().ConfigureAwait(false), JsonOptions)
                        ?? new List<BackupManifestItem>();
                }
                else
                {
                    // 没有清单 = 老格式 / 纯数据库包：只恢复数据库，不报错
                    manifest = new List<BackupManifestItem>();
                }

                var vaultDir = Path.Combine(tempDir, "vault");
                Directory.CreateDirectory(vaultDir);

                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith("vault/", StringComparison.OrdinalIgnoreCase)) continue;

                    var target = Path.Combine(vaultDir, entry.FullName["vault/".Length..]);
                    var targetDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                    entry.ExtractToFile(target, overwrite: true);
                    vaultFiles.Add((entry.FullName, target));
                }
            }

            progress?.Report("正在恢复数据库…");
            await _backup.ImportAsync(extractedDb).ConfigureAwait(false);

            progress?.Report("正在恢复私密空间的文件…");
            var vaultDirectory = _vault.VaultDirectory;
            Directory.CreateDirectory(vaultDirectory);

            var entryToPath = vaultFiles.ToDictionary(v => v.Entry, v => v.ExtractedPath);
            var restored = 0;
            var missing = 0;

            foreach (var item in manifest)
            {
                ct.ThrowIfCancellationRequested();

                if (!entryToPath.TryGetValue(item.Entry, out var extracted))
                {
                    missing++;
                    continue;
                }

                // 放回当前数据目录的 Vault，用原文件名（去掉打包时加的防重名序号）；重名追加 " (1)"
                var target = UniquePath(vaultDirectory, item.FileName);
                File.Copy(extracted, target, overwrite: false);

                // 库里记的还是旧机器的绝对路径，改到新位置（IsPrivate / OriginalPath 不动）
                await _repo.UpdateMediaPathAsync(item.Id, target).ConfigureAwait(false);
                // 缩略图路径指向旧机器的 Vault/Thumbs，作废后由缩略图队列按新路径重新生成
                await _repo.InvalidateThumbnailAsync(item.Id).ConfigureAwait(false);
                restored++;
            }

            _logger.LogInformation("完整备份已恢复：数据库已覆盖，私密文件还原 {Restored} 个（缺失 {Missing}）",
                restored, missing);
            progress?.Report("完成");
            return FullBackupResult.Ok(restored, missing);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复完整备份失败");
            return FullBackupResult.Fail(ex.Message);
        }
        finally
        {
            try { if (tempDir != null) Directory.Delete(tempDir, recursive: true); }
            catch { /* 临时目录删不掉不影响结果 */ }
        }
    }

    // ---------- 工具 ----------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = false,
    };

    /// <summary>同名文件已存在时追加 " (1)"、" (2)"……（与 VaultService 的规则一致）。</summary>
    private static string UniquePath(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10_000; i++)
        {
            target = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(target)) return target;
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 临时文件删不掉无所谓 */ }
    }
}
