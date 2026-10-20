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
