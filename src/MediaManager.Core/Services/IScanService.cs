using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>媒体扫描服务接口。</summary>
public interface IScanService
{
    /// <summary>扫描单个根文件夹。</summary>
    Task ScanRootFolderAsync(long rootFolderId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);

    /// <summary>增量扫描（文件监控触发）。</summary>
    Task ScanIncrementalAsync(long rootFolderId, string directory, CancellationToken ct = default);

    /// <summary>
    /// 补齐历史数据的图片宽高（老版本入库时没写这一列，界面一直显示「未知」）。
    /// 分批处理、可取消，跑完即止。
    /// </summary>
    Task BackfillSizesAsync(CancellationToken ct = default);
}
