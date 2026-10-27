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

    /// <summary>是否已移入私密空间（文件同时会被挪到 Data/Vault 下）。</summary>
    public bool IsPrivate { get; set; }

    /// <summary>移入私密空间之前的绝对路径；还原时挪回这里。普通文件为 null。</summary>
    public string? OriginalPath { get; set; }

    /// <summary>私密空间里的分区 id；普通文件或未分区时为 null。</summary>
    public long? VaultSectionId { get; set; }
    public string DateAddedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? LastScanUtc { get; set; }
}
