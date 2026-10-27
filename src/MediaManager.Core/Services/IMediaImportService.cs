using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>
/// 把「磁盘上的单个文件」直接导入媒体库。
///
/// 和扫描的区别：扫描是「按文件夹整体收录」，这里是「用户点名的这几个文件立刻可用」。
/// 典型场景：写日记时从资源管理器拖一张图进来 —— 不该逼用户先去添加整个文件夹。
/// </summary>
public interface IMediaImportService
{
    /// <summary>
    /// 导入一批文件，返回成功入库的媒体项（顺序与入参一致，重复项只返回一次）。
    /// <para>
    /// 已经在库里的文件直接返回库里那条记录，不会重复插入；
    /// 不存在、类型不支持、无权限的文件会被跳过（不抛异常，由调用方提示数量差异）。
    /// </para>
    /// </summary>
    Task<List<MediaItem>> ImportFilesAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>
    /// 同上，但允许指定「不在库内的文件」的处理策略（见 <see cref="MediaImportOptions"/>）。
    /// UI 在导入前应先调 <see cref="FindUncoveredAsync"/>，有未覆盖文件时先问用户再传策略进来。
    /// </summary>
    Task<List<MediaItem>> ImportFilesAsync(IEnumerable<string> paths, MediaImportOptions options,
        CancellationToken ct = default);

    /// <summary>导入单个文件；类型不支持或文件不存在时返回 null。</summary>
    Task<MediaItem?> ImportFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 找出这批文件里「不在任何媒体文件夹覆盖范围内」的（文件必须存在、类型受支持才算数）。
    /// UI 据此决定是否要弹「这个文件不在媒体库里，要怎么处理」的确认框。
    /// </summary>
    Task<IReadOnlyList<string>> FindUncoveredAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>
    /// 把外部文件**移入保险箱**并入库（IsPrivate=1，OriginalPath 记原位置）。
    /// 返回成功入库的记录。注意这是移动不是复制：文件会离开原目录。
    /// </summary>
    Task<List<MediaItem>> ImportIntoVaultAsync(IEnumerable<string> paths, CancellationToken ct = default);
}
