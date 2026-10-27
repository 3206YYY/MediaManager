namespace MediaManager.Core.Models;

/// <summary>
/// 一篇日记（一条回忆）。
///
/// 一天可以写多篇 —— 所以「日期」不是主键的一部分，只是一个普通字段，
/// 日历上某天有没有内容靠查询统计，不靠唯一约束。
/// </summary>
public class DiaryEntry
{
    public long Id { get; set; }

    /// <summary>归属日期，格式 <c>yyyy-MM-dd</c>（本地日期，不做 UTC 换算）。</summary>
    public string EntryDate { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Content { get; set; }

    /// <summary>心情标记（一个 emoji，可空）。</summary>
    public string? Mood { get; set; }

    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

    public string UpdatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

    /// <summary>这篇日记关联的图片/视频（读取时填充，写入时按顺序同步）。</summary>
    public List<MediaItem> Media { get; set; } = new();

    /// <summary>给列表绑定用的简短摘要。</summary>
    public string Summary
    {
        get
        {
            var text = Content?.Replace("\r", "").Replace("\n", " ").Trim();
            if (string.IsNullOrEmpty(text)) return "（只有图片）";
            return text.Length <= 60 ? text : text[..60] + "…";
        }
    }

    /// <summary>日期显示成「3 月 5 日」。</summary>
    public string DateDisplay =>
        DateTime.TryParse(EntryDate, out var d) ? $"{d.Month} 月 {d.Day} 日" : EntryDate;
}
