using MediaManager.Core.Enums;

namespace MediaManager.Core.Models;

/// <summary>媒体项。</summary>
public class MediaItem
{
    public long Id { get; set; }
    public long RootFolderId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public MediaType MediaType { get; set; }
    public long SizeBytes { get; set; }
    public string? CreatedAtUtc { get; set; }
    public string? ModifiedAtUtc { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }
    public bool Favorite { get; set; }
    public int Rating { get; set; }
    public string? Note { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? QuickHash { get; set; }
    public bool IsMissing { get; set; }
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? LastScanUtc { get; set; }
}
