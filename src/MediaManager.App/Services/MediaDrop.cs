using System.Windows;
using MediaManager.Core.Enums;
using MediaManager.Core.Models;
using MediaManager.Core.Services;
using MediaManager.Infrastructure.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace MediaManager.App.Services;

/// <summary>一次拖拽的解析结果。</summary>
/// <param name="Items">可以挂到日记上的媒体项。</param>
/// <param name="Skipped">被跳过的数量（不是图片/视频、文件不存在、导入失败）。</param>
internal readonly record struct DropResult(List<MediaItem> Items, int Skipped);

/// <summary>
/// 日记相关窗口共用的「拖进来」解析。
///
/// 两种来源要分开处理：
/// <list type="number">
/// <item><b>软件内部拖拽</b>（主网格 → 日记）：数据里只有媒体 id，直接查库，不能导入 ——
/// 库里已有的东西再导一次会平白多出根文件夹。</item>
/// <item><b>资源管理器拖文件</b>：先过 <see cref="IMediaImportService"/> 入库拿到 id，
/// 否则 <c>DiaryMedia</c> 没有可关联的记录。</item>
/// </list>
/// 两条路径都只收图片和视频：日记的媒体格是按缩略图排的，
/// 音频/文档/压缩包放进去只会显示一个空方块，不如直接告诉用户「这几个没加」。
/// </summary>
internal static class MediaDrop
{
    /// <summary>与主界面拖拽一致的自定义格式（媒体 id 数组）。</summary>
    public const string MediaIdsFormat = "MediaManager.MediaIds";

    /// <summary>能否接受当前拖拽内容（拖动过程中决定光标）。</summary>
    public static bool CanAccept(IDataObject? data)
    {
        if (data == null) return false;

        if (data.GetData(MediaIdsFormat) is long[] { Length: > 0 }) return true;

        return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsDiaryMedia);
    }

    /// <summary>解析拖拽内容。永远不抛异常：拖拽失败不该让窗口崩掉。</summary>
    public static async Task<DropResult> ResolveAsync(IDataObject? data)
    {
        if (data == null) return new DropResult(new List<MediaItem>(), 0);

        try
        {
            // 1) 内部拖拽：按 id 取（私密项会被仓库过滤掉，正好也不该进日记）
            if (data.GetData(MediaIdsFormat) is long[] { Length: > 0 } ids)
            {
                var repo = App.Services.GetRequiredService<IMediaRepository>();
                var items = await repo.GetByIdsAsync(ids);
                return new DropResult(items, ids.Length - items.Count);
            }

            // 2) 外部文件：先入库再说（不在库内的文件先问一次「放哪里」）
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                var picked = files.Where(IsDiaryMedia).ToList();
                var skipped = files.Length - picked.Count;

                if (picked.Count == 0) return new DropResult(new List<MediaItem>(), skipped);

                var options = await ImportPolicy.ResolveAsync(picked);
                if (options == null) return new DropResult(new List<MediaItem>(), files.Length);

                var import = App.Services.GetRequiredService<IMediaImportService>();
                var imported = await import.ImportFilesAsync(picked, options);

                skipped += picked.Count - imported.Count;

                // 导入可能会新建一个根文件夹：不通知侧栏的话，用户回主界面会看到
                // 「文件夹列表里没有我刚拖的那个目录」。只刷侧栏，不动网格和缓存。
                if (imported.Count > 0) _ = RefreshSidebarAsync();

                return new DropResult(imported, skipped);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "解析拖拽内容失败");
        }

        return new DropResult(new List<MediaItem>(), 0);
    }

    private static bool IsDiaryMedia(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (System.IO.Directory.Exists(path)) return false;

        var type = MediaFormat.GetMediaTypeForOpen(path);
        return type is MediaType.Image or MediaType.Video;
    }

    /// <summary>导入完成后的侧栏同步（调用方 fire-and-forget 即可）。</summary>
    public static async Task RefreshSidebarAsync()
    {
        try
        {
            await App.Services.GetRequiredService<ViewModels.MainViewModel>().ReloadNavigationAsync();
        }
        catch (Exception ex)
        {
            // 侧栏没刷上不影响日记本身，下次刷新自然就对了
            Serilog.Log.Debug(ex, "导入后刷新侧栏失败");
        }
    }
}
