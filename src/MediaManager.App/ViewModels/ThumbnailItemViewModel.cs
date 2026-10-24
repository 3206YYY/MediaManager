using CommunityToolkit.Mvvm.ComponentModel;
using MediaManager.Core.Models;

namespace MediaManager.App.ViewModels;

/// <summary>缩略图网格中的项（含缩略图加载状态）。</summary>
public partial class ThumbnailItemViewModel : ObservableObject
{
    public MediaItem Media { get; }

    public ThumbnailItemViewModel(MediaItem media)
    {
        Media = media;
    }

    [ObservableProperty]
    private string? _thumbnailPath;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private int _rating;

    [ObservableProperty]
    private string? _note;

    /// <summary>已加入的分类名称（逗号分隔），用于缩略图标记与右键菜单显示。</summary>
    [ObservableProperty]
    private string _categoryNames = string.Empty;

    /// <summary>已加入的分类 id 列表（用于右键菜单勾选态）。</summary>
    public HashSet<long> CategoryIds { get; } = new();

    /// <summary>是否已加入至少一个分类。</summary>
    public bool IsCategorized => CategoryIds.Count > 0;

    /// <summary>分类标记变化后，通知绑定刷新 IsCategorized。</summary>
    public void RefreshCategoryState()
    {
        OnPropertyChanged(nameof(IsCategorized));
    }

    public long Id => Media.Id;
    public string FileName => Media.FileName;
    public string Path => Media.Path;
    public bool IsVideo => Media.MediaType == Core.Enums.MediaType.Video;
    public bool IsAudio => Media.MediaType == Core.Enums.MediaType.Audio;
    public bool IsText => Media.MediaType == Core.Enums.MediaType.Text;
    public bool IsArchive => Media.MediaType == Core.Enums.MediaType.Archive;

    /// <summary>是否图片/视频（会显示真实缩略图）。</summary>
    public bool IsVisual => Media.MediaType is Core.Enums.MediaType.Image or Core.Enums.MediaType.Video;

    /// <summary>非视觉类型（音频/文档/压缩包）显示固定图标。</summary>
    public bool IsNonVisual => !IsVisual;

    /// <summary>非视觉类型的固定图标（音频统一同一个图标）。</summary>
    public string TypeGlyph => Media.MediaType switch
    {
        Core.Enums.MediaType.Audio => "🎵",
        Core.Enums.MediaType.Text => "📄",
        Core.Enums.MediaType.Archive => "🗜️",
        _ => string.Empty,
    };

    public string TypeLabel => Media.MediaType switch
    {
        Core.Enums.MediaType.Audio => "音频",
        Core.Enums.MediaType.Text => "文档",
        Core.Enums.MediaType.Archive => "压缩包",
        _ => string.Empty,
    };

    public string SizeDisplay => FormatSize(Media.SizeBytes);

    public string ResolutionDisplay =>
        Media.Width.HasValue && Media.Height.HasValue
            ? $"{Media.Width}×{Media.Height}"
            : "未知";

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
}

/// <summary>侧边栏导航项。</summary>
public partial class NavigationItemViewModel : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Key { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>关联的根文件夹/标签/分类 id（可空）。</summary>
    public long? RelatedId { get; set; }
}

/// <summary>顶部「类型」多选筛选项。</summary>
public partial class TypeFilterOption : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    public Core.Enums.MediaType MediaType { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;
}
