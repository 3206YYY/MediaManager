using System.IO;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace MediaManager.Infrastructure.FileSystem;

/// <summary>
/// 单文件导入：把用户点名的文件立刻变成媒体库里的一条记录。
///
/// 关键取舍：
/// <list type="bullet">
/// <item>不动用户的文件 —— 只入库，不复制、不移动。删掉库记录文件还在。</item>
/// <item>没有现成的根文件夹覆盖它时，自动把它所在的**这一层**目录加成根文件夹。
/// 只插入这一条记录、不顺手全盘扫描：扫描是用户在主界面显式做的动作，
/// 不该被一次拖拽偷偷触发（把一个放着几万张图的目录拖进日记，不能因此卡住几秒）。</item>
/// <item>保险箱目录里的文件一律拒绝导入 —— 否则「拖出保险箱 → 变回普通文件」，
/// 用户以为只是写进日记，实际上隐私门自己开了。</item>
/// </list>
/// </summary>
public sealed class MediaImportService : IMediaImportService
{
    private readonly IMediaRepository _repo;
    private readonly IThumbnailService _thumbs;
    private readonly IVaultService _vault;
    private readonly ILogger<MediaImportService> _logger;

    public MediaImportService(IMediaRepository repo, IThumbnailService thumbs,
        IVaultService vault, ILogger<MediaImportService> logger)
    {
        _repo = repo;
        _thumbs = thumbs;
        _vault = vault;
        _logger = logger;
    }

    public async Task<List<MediaItem>> ImportFilesAsync(IEnumerable<string> paths,
        CancellationToken ct = default)
        => await ImportFilesAsync(paths, new MediaImportOptions(), ct).ConfigureAwait(false);

    public async Task<List<MediaItem>> ImportFilesAsync(IEnumerable<string> paths, MediaImportOptions options,
        CancellationToken ct = default)
    {
        var result = new List<MediaItem>();
        if (paths == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = await _repo.GetRootFoldersAsync().ConfigureAwait(false);

        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            MediaItem? item;
            try
            {
                item = await ImportCoreAsync(raw, roots, options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个文件失败不能拖垮整批（设备掉线 / 权限不足 / 文件正被写入）
                _logger.LogDebug(ex, "导入文件失败，已跳过：{Path}", raw);
                continue;
            }

            if (item == null) continue;
            if (!seen.Add(item.Path)) continue;
            result.Add(item);
        }

        return result;
    }

    public async Task<MediaItem?> ImportFileAsync(string path, CancellationToken ct = default)
    {
        var list = await ImportFilesAsync(new[] { path }, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    public async Task<IReadOnlyList<string>> FindUncoveredAsync(IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        var uncovered = new List<string>();
        if (paths == null) return uncovered;

        var roots = await _repo.GetRootFoldersAsync().ConfigureAwait(false);

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch
            {
                continue;
            }

            var directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory)) continue;
            if (IsInsideVault(full)) continue;   // 保险箱内容走 ImportIntoVaultAsync，不参与这个判断

            if (MediaFormat.GetMediaType(full) == null) continue;

            if (!roots.Any(r => IsUnder(directory, r.Path)))
                uncovered.Add(full);
        }

        return uncovered;
    }

    public async Task<List<MediaItem>> ImportIntoVaultAsync(IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        var result = new List<MediaItem>();
        if (paths == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = await _repo.GetRootFoldersAsync().ConfigureAwait(false);
        var vaultRootId = await EnsureVaultRootAsync(roots).ConfigureAwait(false);

        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            MediaItem? item;
            try
            {
                item = await ImportIntoVaultCoreAsync(raw, vaultRootId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "移入保险箱失败，已跳过：{Path}", raw);
                continue;
            }

            if (item == null) continue;
            if (!seen.Add(item.Path)) continue;
            result.Add(item);
        }

        return result;
    }

    // ---------- 内部 ----------

    private async Task<MediaItem?> ImportIntoVaultCoreAsync(string raw, long vaultRootId, CancellationToken ct)
    {
        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(full)) return null;
        if (IsInsideVault(full)) return null;   // 已经在保险箱里了

        var type = MediaFormat.GetMediaType(full);
        if (type == null) return null;

        try
        {
            if (MediaFormat.IsSystemJunk(full, File.GetAttributes(full))) return null;
        }
        catch
        {
            // 读不到属性不影响导入，继续
        }

        var originalPath = full;

        // 先挪文件，再写库：挪失败就跳过，库里不会留下指向不存在文件的记录
        string newPath;
        try
        {
            newPath = await _vault.MoveIntoVaultAsync(full).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "挪进保险箱目录失败：{Path}", full);
            return null;
        }

        // ⚠ FileInfo 必须基于挪好之后的新路径现取：如果沿用挪之前的 FileInfo，
        // 它的 Length/CreationTimeUtc 是惰性求值 —— 第一次访问时源文件已经被挪走，
        // 直接抛 FileNotFoundException，而且发生在下面的 try 之外，回滚逻辑接不住。
        var fi = new FileInfo(newPath);
        var item = new MediaItem
        {
            RootFolderId = vaultRootId,
            Path = newPath,
            FileName = Path.GetFileName(newPath),
            Extension = Path.GetExtension(newPath).TrimStart('.'),
            MediaType = type.Value,
            SizeBytes = fi.Length,
            CreatedAtUtc = fi.CreationTimeUtc.ToString("O"),
            ModifiedAtUtc = fi.LastWriteTimeUtc.ToString("O"),
            LastScanUtc = DateTime.UtcNow.ToString("O"),
        };

        if (type.Value == MediaType.Image
            && MediaSizeReader.TryReadImageSize(newPath, out var w, out var h))
        {
            item.Width = w;
            item.Height = h;
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            var id = await _repo.UpsertMediaAsync(item).ConfigureAwait(false);
            await _repo.SetPrivateAsync(id, isPrivate: true, newPath, originalPath).ConfigureAwait(false);

            var stored = await _repo.GetByIdAsync(id).ConfigureAwait(false) ?? item;
            if (string.IsNullOrEmpty(stored.ThumbnailPath))
                _thumbs.Enqueue(stored);
            return stored;
        }
        catch
        {
            // 写库失败：把文件挪回去，别让用户的东西「消失」在保险箱里
            try
            {
                await _vault.MoveOutOfVaultAsync(newPath, originalPath).ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogWarning(rollbackEx, "入库失败且回滚搬移也失败，文件留在保险箱目录：{Path}", newPath);
            }
            throw;
        }
    }

    /// <summary>
    /// 保险箱内容的 RootFolderId 锚点。
    /// 外键要求每个 MediaItem 必须挂在某个根文件夹下，而保险箱文件不该把用户的源目录
    /// 收进媒体库（尤其从桌面拖几张图进保险箱，不该把桌面变成媒体文件夹）。
    /// 所以用一个路径等于 VaultDirectory 的根文件夹做锚：扫描器本来就整目录跳过 Vault，
    /// 侧栏导航也会把这个目录隐藏（见 MainViewModel），界面上看不见它。
    /// </summary>
    private async Task<long> EnsureVaultRootAsync(List<RootFolder> roots)
    {
        var vaultDir = _vault.VaultDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var existing = roots.FirstOrDefault(r =>
            string.Equals(r.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                vaultDir, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;

        var serial = VolumeHelper.GetVolumeSerial(vaultDir);
        var relative = VolumeHelper.GetRelativePath(vaultDir);
        var id = await _repo.AddRootFolderAsync(vaultDir, serial, relative).ConfigureAwait(false);
        roots.Add(new RootFolder { Id = id, Path = vaultDir, VolumeSerial = serial, RelativePath = relative });
        return id;
    }

    private async Task<MediaItem?> ImportCoreAsync(string raw, List<RootFolder> roots,
        MediaImportOptions options, CancellationToken ct)
    {
        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(full)) return null;

        // 保险箱内容不许回流到普通库（见类注释）
        if (IsInsideVault(full))
        {
            _logger.LogDebug("拒绝导入保险箱内的文件：{Path}", full);
            return null;
        }

        // 与扫描同一套规则：受「扩展格式」开关约束，系统垃圾文件一律不收
        var type = MediaFormat.GetMediaType(full);
        if (type == null) return null;

        try
        {
            if (MediaFormat.IsSystemJunk(full, File.GetAttributes(full))) return null;
        }
        catch
        {
            // 读不到属性不影响导入，继续
        }

        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) return null;

        var (rootId, importPath) = await ResolveRootAndPathAsync(full, directory, roots, options).ConfigureAwait(false);

        var fi = new FileInfo(importPath);
        var item = new MediaItem
        {
            RootFolderId = rootId,
            Path = importPath,
            FileName = fi.Name,
            Extension = fi.Extension.TrimStart('.'),
            MediaType = type.Value,
            SizeBytes = fi.Length,
            CreatedAtUtc = fi.CreationTimeUtc.ToString("O"),
            ModifiedAtUtc = fi.LastWriteTimeUtc.ToString("O"),
            LastScanUtc = DateTime.UtcNow.ToString("O"),
        };

        if (type.Value == MediaType.Image
            && MediaSizeReader.TryReadImageSize(importPath, out var w, out var h))
        {
            item.Width = w;
            item.Height = h;
        }

        ct.ThrowIfCancellationRequested();

        var id = await _repo.UpsertMediaAsync(item).ConfigureAwait(false);

        // 重新读一遍：已存在的旧记录可能带着缩略图路径 / 收藏 / 评分，
        // 用原始 item 返回的话界面会当成「没有缩略图」，白等一轮生成。
        var stored = await _repo.GetByIdAsync(id).ConfigureAwait(false) ?? item;

        // 缩略图丢后台队列生成，不在这里同步等（一张也要几十毫秒，感受得到卡顿）
        if (string.IsNullOrEmpty(stored.ThumbnailPath))
            _thumbs.Enqueue(stored);

        return stored;
    }

    private bool IsInsideVault(string path)
    {
        var vaultDir = _vault.VaultDirectory;
        if (string.IsNullOrWhiteSpace(vaultDir)) return false;

        var root = vaultDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 找出覆盖这个目录的根文件夹；一个都没有时按策略处理：
    /// <list type="bullet">
    /// <item><see cref="UnknownFolderPolicy.CopyToImportFolder"/>：把文件复制到「导入文件夹」，
    /// 改为导入那份副本 —— 不动用户的原件，也不把用户随手拖文件的目录收成媒体文件夹。</item>
    /// <item><see cref="UnknownFolderPolicy.AddParentFolder"/>（默认）：把该目录本身加进去。</item>
    /// </list>
    /// 取「最长前缀」而不是第一个命中：库里有 D:\ 和 D:\照片 两个根时，
    /// 后者才是它真正的归属，选错会让「文件夹」视图里的归属看起来莫名其妙。
    /// </summary>
    private async Task<(long RootId, string Path)> ResolveRootAndPathAsync(string fullPath, string directory,
        List<RootFolder> roots, MediaImportOptions options)
    {
        var match = roots
            .Where(r => IsUnder(directory, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();

        if (match != null) return (match.Id, fullPath);

        if (options.UnknownFolderPolicy == UnknownFolderPolicy.CopyToImportFolder)
        {
            var folder = string.IsNullOrWhiteSpace(options.ImportFolder)
                ? MediaImportOptions.DefaultImportFolder
                : options.ImportFolder!;
            var copied = CopyIntoImportFolder(fullPath, folder);
            if (copied != null)
            {
                var copiedDir = Path.GetDirectoryName(copied)!;
                var copiedRoot = roots
                    .Where(r => IsUnder(copiedDir, r.Path))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();

                long rootId;
                if (copiedRoot != null)
                {
                    rootId = copiedRoot.Id;
                }
                else
                {
                    var serial = VolumeHelper.GetVolumeSerial(copiedDir);
                    var relative = VolumeHelper.GetRelativePath(copiedDir);
                    rootId = await _repo.AddRootFolderAsync(copiedDir, serial, relative).ConfigureAwait(false);
                    roots.Add(new RootFolder { Id = rootId, Path = copiedDir, VolumeSerial = serial, RelativePath = relative });
                    _logger.LogInformation("导入文件时新增导入文件夹为根目录：{Path}", copiedDir);
                }

                return (rootId, copied);
            }

            // 复制失败（盘空间不足 / 目标不可写）退回原地加入，别让导入直接失败
            _logger.LogWarning("复制到导入文件夹失败，退回原地加入：{Path}", fullPath);
        }

        var serial2 = VolumeHelper.GetVolumeSerial(directory);
        var relative2 = VolumeHelper.GetRelativePath(directory);
        var id = await _repo.AddRootFolderAsync(directory, serial2, relative2).ConfigureAwait(false);

        // 同一批里剩下的文件多半也在这一层：补进缓存，省掉重复查询
        roots.Add(new RootFolder { Id = id, Path = directory, VolumeSerial = serial2, RelativePath = relative2 });

        _logger.LogInformation("导入文件时新增根文件夹：{Path}", directory);
        return (id, fullPath);
    }

    /// <summary>把文件复制进导入文件夹（重名自动追加 " (1)"）。失败返回 null。</summary>
    private static string? CopyIntoImportFolder(string source, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var target = Path.Combine(folder, Path.GetFileName(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(source);
                var ext = Path.GetExtension(source);
                for (var i = 1; File.Exists(target) && i < 10_000; i++)
                    target = Path.Combine(folder, $"{stem} ({i}){ext}");
            }

            File.Copy(source, target, overwrite: false);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnder(string directory, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return false;

        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0) return false;

        return directory.Equals(root, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
