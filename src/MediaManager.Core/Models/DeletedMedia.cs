namespace MediaManager.Core.Models;

/// <summary>
/// 「最近删除」记录：删除文件时保存一份快照，用于 5 秒撤销与事后还原。
/// 文件本身仍在系统回收站里，还原时从回收站移回原位置。
/// </summary>
public class DeletedMedia
{
    public long Id { get; set; }
    public long OriginalMediaId { get; set; }
    public long RootFolderId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "Image";
    public long SizeBytes { get; set; }
    public string? ThumbnailPath { get; set; }
    public string DeletedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? BatchId { get; set; }

    /// <summary>删除前的 MediaItem 快照（JSON），还原时用于恢复收藏/评分/备注等。</summary>
    public string? SnapshotJson { get; set; }
    public string? TagIdsJson { get; set; }
    public string? CategoryIdsJson { get; set; }
    public string? CollectionIdsJson { get; set; }
    public bool IsRestored { get; set; }

    // ---------- 展示用（不参与数据库映射） ----------

    public DateTime DeletedAtLocal =>
        DateTime.TryParse(DeletedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime()
            : DateTime.Now;

    public string DeletedAtDisplay => DeletedAtLocal.ToString("yyyy-MM-dd HH:mm");

    public string SizeDisplay => SizeBytes switch
    {
        <= 0 => "-",
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:0.0} MB",
        _ => $"{SizeBytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    public string DirectoryDisplay
    {
        get
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                return string.IsNullOrEmpty(dir) ? Path : dir;
            }
            catch
            {
                return Path;
            }
        }
    }
}
