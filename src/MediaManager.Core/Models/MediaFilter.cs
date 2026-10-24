using MediaManager.Core.Enums;

namespace MediaManager.Core.Models;

/// <summary>媒体项查询筛选条件。</summary>
public class MediaFilter
{
    public string? SearchText { get; set; }
    public MediaType? MediaType { get; set; }

    /// <summary>多类型筛选（优先于单值 MediaType；为空表示不筛选类型）。</summary>
    public List<MediaType>? MediaTypes { get; set; }

    public bool? Favorite { get; set; }

    /// <summary>仅显示未分类（未进入任何分类）的媒体。</summary>
    public bool? Unfiled { get; set; }

    /// <summary>按标签筛选。</summary>
    public long? TagId { get; set; }

    /// <summary>按分类筛选（含子分类）。</summary>
    public long? CategoryId { get; set; }

    /// <summary>展开后的分类 id（含所有子分类），由仓库层填充。</summary>
    public List<long>? CategoryIds { get; set; }

    /// <summary>按收藏夹筛选。</summary>
    public long? CollectionId { get; set; }

    /// <summary>仅显示编辑副本（文件名含 _edited）。</summary>
    public bool? EditedOnly { get; set; }

    /// <summary>按文件夹（根目录）筛选。</summary>
    public long? RootFolderId { get; set; }

    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }

    public SortField SortField { get; set; } = SortField.DateAdded;
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;

    /// <summary>最近添加（近 N 天）。</summary>
    public int? RecentDays { get; set; }

    public int Offset { get; set; }
    public int Limit { get; set; } = 500;
}

/// <summary>重复媒体组（一组内容相同的文件）。</summary>
public class DuplicateGroup
{
    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>内容哈希。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>组内重复的媒体项。</summary>
    public List<MediaItem> Items { get; set; } = new();
}

/// <summary>发现页每日推荐批次。</summary>
public class DiscoveryBatch
{
    public string Date { get; set; } = string.Empty;
    public List<long> MediaIds { get; set; } = new();
    public string PoolType { get; set; } = string.Empty;
    public long? PoolId { get; set; }

    /// <summary>生成这批时使用的类型筛选键（null/空 视为“全部”）。</summary>
    public string? TypeFilter { get; set; }
}

/// <summary>扫描进度。</summary>
public class ScanProgress
{
    public long RootFolderId { get; set; }
    public int ScannedFiles { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int MarkedMissing { get; set; }
    public int Total { get; set; }
    public bool IsCompleted { get; set; }
    public string? CurrentDirectory { get; set; }
}
