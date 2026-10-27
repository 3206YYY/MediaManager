using MediaManager.Core.Models;

namespace MediaManager.Core.Services;

/// <summary>日记的数据访问。</summary>
public interface IDiaryRepository
{
    /// <summary>
    /// 取某段日期区间内的日记（含关联的图片/视频），按日期升序、同一天按创建时间升序。
    /// 日期参数格式 <c>yyyy-MM-dd</c>，闭区间。
    /// </summary>
    Task<List<DiaryEntry>> GetEntriesAsync(string fromDate, string toDate);

    /// <summary>取单篇（含媒体列表）；不存在返回 null。</summary>
    Task<DiaryEntry?> GetEntryAsync(long id);

    /// <summary>
    /// 新增或更新一篇（<c>Id == 0</c> 视为新增），同时同步它关联的媒体列表。
    /// 返回日记 id。
    /// </summary>
    Task<long> SaveEntryAsync(DiaryEntry entry);

    /// <summary>删除一篇（关联表靠外键级联删，媒体文件本身不动）。</summary>
    Task DeleteEntryAsync(long id);

    /// <summary>有日记的日期 → 篇数，用于日历上的「发光」标记。</summary>
    Task<Dictionary<string, int>> GetDateCountsAsync(string fromDate, string toDate);

    /// <summary>日记总篇数。</summary>
    Task<long> GetEntryCountAsync();

    /// <summary>把所有日记挪到另一天（拖拽换日、或改正写错的日期）。</summary>
    Task MoveEntryAsync(long id, string newDate);
}
