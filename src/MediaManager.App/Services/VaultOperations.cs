using MediaManager.Core.Models;
using MediaManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Services;

/// <summary>批量搬移的结果。</summary>
/// <param name="Moved">成功数量。</param>
/// <param name="Failures">失败描述（文件名：原因），文件都还在原处。</param>
internal sealed record VaultMoveResult(int Moved, List<string> Failures);

/// <summary>
/// 「移入 / 移出保险箱」的共用逻辑。
///
/// 主窗口右键菜单和私密空间窗口都要用同一套动作 —— 分开写两份，迟早一份改了缩略图清理、
/// 另一份忘了，结果就是保险箱门口又贴上了明文预览图。
/// </summary>
internal static class VaultOperations
{
    /// <summary>把已经在库里的媒体移入保险箱（挪文件 + 打 IsPrivate 标记）。</summary>
    public static async Task<VaultMoveResult> MoveIntoVaultAsync(IReadOnlyList<MediaItem> items)
    {
        var vault = App.Services.GetRequiredService<IVaultService>();
        var repo = App.Services.GetRequiredService<IMediaRepository>();
        var thumbs = App.Services.GetRequiredService<IThumbnailService>();

        var failures = new List<string>();
        var moved = 0;

        foreach (var media in items)
        {
            try
            {
                // 先删公共缓存里的预览图，再挪文件：否则 Data/Thumbnails 下会留着
                // 保险箱内容的明文缩略图，等于保险箱门口贴了照片
                thumbs.Invalidate(media.Id);

                var newPath = await vault.MoveIntoVaultAsync(media.Path);
                await repo.SetPrivateAsync(media.Id, true, newPath, media.Path);
                await repo.InvalidateThumbnailAsync(media.Id);
                moved++;
            }
            catch (Exception ex)
            {
                failures.Add($"{media.FileName}：{ex.Message}");
            }
        }

        return new VaultMoveResult(moved, failures);
    }

    /// <summary>把保险箱里的媒体移回原位置（原目录不在就留在保险箱里）。</summary>
    public static async Task<VaultMoveResult> MoveOutOfVaultAsync(IReadOnlyList<MediaItem> items)
    {
        var vault = App.Services.GetRequiredService<IVaultService>();
        var repo = App.Services.GetRequiredService<IMediaRepository>();
        var thumbs = App.Services.GetRequiredService<IThumbnailService>();

        var failures = new List<string>();
        var moved = 0;

        foreach (var media in items)
        {
            try
            {
                var newPath = await vault.MoveOutOfVaultAsync(media.Path, media.OriginalPath);
                if (newPath == null)
                {
                    failures.Add($"{media.FileName}：原目录已不存在");
                    continue;
                }

                await repo.SetPrivateAsync(media.Id, false, newPath, null);
                await repo.InvalidateThumbnailAsync(media.Id);

                // 保险箱里那份预览图也要清掉，不然 Vault/Thumbs 会一直堆孤儿文件
                thumbs.Invalidate(media.Id);
                moved++;
            }
            catch (Exception ex)
            {
                failures.Add($"{media.FileName}：{ex.Message}");
            }
        }

        return new VaultMoveResult(moved, failures);
    }

    /// <summary>
    /// 把库外的文件直接移入保险箱（拖拽 / 「＋ 导入文件」入口）。
    /// 文件被挪走，所以不存在「要不要把源目录收成媒体文件夹」的问题。
    /// </summary>
    public static async Task<(List<MediaItem> Imported, int Skipped)> ImportIntoVaultAsync(
        IReadOnlyList<string> files)
    {
        var import = App.Services.GetRequiredService<IMediaImportService>();
        var imported = await import.ImportIntoVaultAsync(files);
        return (imported, files.Count - imported.Count);
    }
}
