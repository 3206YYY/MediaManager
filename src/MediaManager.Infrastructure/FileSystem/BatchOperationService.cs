using System.IO;
using MediaManager.Core.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MediaManager.Infrastructure.FileSystem;

public sealed record BatchRenameOptions(string Prefix, int Start, int Digits, string Suffix = "");

public sealed record BatchRenamePlanItem(long Id, string OldPath, string OldName, string NewName, string NewPath);

public sealed record BatchRenameResult(
    int Renamed, int Skipped, int Failed, IReadOnlyList<string> Errors,
    IReadOnlyList<(long Id, string NewPath)> RenamedItems)
{
    public int Total => Renamed + Skipped + Failed;
}

public sealed record BatchExportOptions(string OutputDir, int MaxLongEdge, int JpegQuality);

public sealed record BatchExportResult(
    int Ok, int Skipped, int Failed, IReadOnlyList<string> Errors, string OutputDir)
{
    public int Total => Ok + Skipped + Failed;
}

/// <summary>
/// 批量文件操作（重命名 / 导出压缩）。纯文件层面的事情放这里，
/// 数据库与 UI 进度由 ViewModel 负责，方便单独测试。
/// </summary>
public static class BatchOperationService
{
    // ---------- 批量重命名 ----------

    /// <summary>生成重命名计划：只计算新名字，不动文件。</summary>
    public static List<BatchRenamePlanItem> BuildRenamePlan(
        IReadOnlyList<MediaItem> items, BatchRenameOptions options, out List<string> conflicts)
    {
        conflicts = new List<string>();
        var plan = new List<BatchRenamePlanItem>();
        if (items.Count == 0) return plan;

        var digits = Math.Clamp(options.Digits, 1, 6);
        var index = options.Start;
        var prefix = options.Prefix ?? string.Empty;
        var suffix = options.Suffix ?? string.Empty;

        // 同一目录内不能出现重名：先用 HashSet 记录目标名（不区分大小写，Windows 语义）
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var ext = Path.GetExtension(item.Path);
            var newName = $"{prefix}{index.ToString().PadLeft(digits, '0')}{suffix}{ext}";
            index++;

            var dir = Path.GetDirectoryName(item.Path) ?? ".";
            var newPath = Path.Combine(dir, newName);

            if (!taken.Add(dir + "|" + newName))
            {
                conflicts.Add($"{item.FileName} → {newName}（本批目标名重复）");
                continue;
            }

            // 目标已存在且不是它自己 → 冲突（例如 旅行_001.jpg 已有别的文件）
            if (File.Exists(newPath) && !string.Equals(newPath, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add($"{item.FileName} → {newName}（目标文件已存在）");
                continue;
            }

            plan.Add(new BatchRenamePlanItem(item.Id, item.Path, item.FileName, newName, newPath));
        }

        return plan;
    }

    /// <summary>
    /// 执行重命名。用「先改成临时名，再改成目标名」两步走，
    /// 这样 001→002、002→003 这种连环改名不会互相覆盖。
    /// </summary>
    public static BatchRenameResult ExecuteRename(
        IReadOnlyList<BatchRenamePlanItem> plan,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        int renamed = 0, skipped = 0, failed = 0;
        var errors = new List<string>();
        var renamedItems = new List<(long Id, string NewPath)>();
        var temps = new List<(BatchRenamePlanItem Item, string TempPath)>();

        // 第一步：改成临时名
        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(p.OldPath))
                {
                    skipped++;
                    errors.Add($"{p.OldName}：文件已不存在");
                    continue;
                }

                if (string.Equals(p.OldPath, p.NewPath, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var dir = Path.GetDirectoryName(p.OldPath)!;
                var temp = Path.Combine(dir, $"~mm_{Guid.NewGuid():N}{Path.GetExtension(p.OldPath)}");
                File.Move(p.OldPath, temp);
                temps.Add((p, temp));
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{p.OldName}：{MediaManager.Core.Diagnostics.StorageErrors.FriendlyMessage(ex)}");
            }
        }

        // 第二步：临时名 → 目标名
        int done = 0;
        foreach (var (item, temp) in temps)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(temp, item.NewPath, overwrite: false);
                renamed++;
                renamedItems.Add((item.Id, item.NewPath));
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{item.OldName} → {item.NewName}：{MediaManager.Core.Diagnostics.StorageErrors.FriendlyMessage(ex)}");
                // 尽力恢复原名，避免文件停在一个奇怪的名字上
                try { if (File.Exists(temp)) File.Move(temp, item.OldPath); } catch { /* 无法恢复就留给用户手动处理 */ }
            }

            progress?.Report((++done, temps.Count));
        }

        return new BatchRenameResult(renamed, skipped, failed, errors, renamedItems);
    }

    // ---------- 批量导出 / 统一尺寸 + 压缩 ----------

    /// <summary>把一批图片导出到指定目录，可统一长边尺寸、统一 JPEG 质量。</summary>
    public static async Task<BatchExportResult> ExportAsync(
        IReadOnlyList<MediaItem> items,
        BatchExportOptions options,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        int ok = 0, skipped = 0, failed = 0;
        var errors = new List<string>();

        Directory.CreateDirectory(options.OutputDir);

        // 同批重名时自动加 (2)(3)
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            try
            {
                if (!File.Exists(item.Path))
                {
                    skipped++;
                    errors.Add($"{item.FileName}：文件已不存在");
                    continue;
                }

                if (!IsImage(item.Path))
                {
                    skipped++;
                    errors.Add($"{item.FileName}：视频暂不支持统一尺寸/压缩，已跳过");
                    continue;
                }

                var target = BuildUniquePath(options.OutputDir, item.Path, used);
                await Task.Run(() => ExportOne(item.Path, target, options, ct), ct);
                ok++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{item.FileName}：{MediaManager.Core.Diagnostics.StorageErrors.FriendlyMessage(ex)}");
            }

            progress?.Report((i + 1, items.Count));
        }

        return new BatchExportResult(ok, skipped, failed, errors, options.OutputDir);
    }

    private static void ExportOne(string source, string target, BatchExportOptions options, CancellationToken ct)
    {
        var ext = Path.GetExtension(target).ToLowerInvariant();

        using var image = SixLabors.ImageSharp.Image.Load(source);
        ct.ThrowIfCancellationRequested();

        if (options.MaxLongEdge > 0)
        {
            var longEdge = Math.Max(image.Width, image.Height);
            if (longEdge > options.MaxLongEdge)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(options.MaxLongEdge, options.MaxLongEdge),
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }
        }

        if (ext is ".jpg" or ".jpeg")
        {
            var quality = options.JpegQuality is > 0 and <= 100 ? options.JpegQuality : 90;
            image.SaveAsJpeg(target, new JpegEncoder { Quality = quality });
            return;
        }

        if (ext == ".png") image.SaveAsPng(target);
        else if (ext == ".bmp") image.SaveAsBmp(target);
        else if (ext == ".webp") image.SaveAsWebp(target);
        else if (ext == ".gif") image.SaveAsGif(target);
        else if (ext is ".tif" or ".tiff") image.SaveAsTiff(target);
        else image.SaveAsJpeg(target, new JpegEncoder { Quality = 90 });
    }

    private static string BuildUniquePath(string dir, string sourcePath, HashSet<string> used)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        var candidate = Path.Combine(dir, name + ext);

        int n = 2;
        while (used.Contains(candidate) || File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            n++;
            if (n > 9999) break;
        }

        used.Add(candidate);
        return candidate;
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff"
            or ".heic" or ".avif";
    }
}
