using CommunityToolkit.Mvvm.ComponentModel;
using MediaManager.App.Services;
using MediaManager.Core.Models;

namespace MediaManager.App.ViewModels;

/// <summary>缩略图网格中的项（含缩略图加载状态）。</summary>
public partial class ThumbnailItemViewModel : ObservableObject
{
    public MediaItem Media { get; }

    /// <summary>非图片/视频的彩色类型图标（构造时算一次，绑定只读）。</summary>
    public FileTypeIcon FileIcon { get; }

    public ThumbnailItemViewModel(MediaItem media)
    {
        Media = media;

        // 老记录可能没有 Extension（早期版本没写），回落到文件名再兜一次
        var ext = string.IsNullOrEmpty(media.Extension)
            ? System.IO.Path.GetExtension(media.FileName)
            : media.Extension;
        FileIcon = FileTypeIcon.For(ext, media.MediaType);
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

    /// <summary>
    /// 类型说明。以前只有「文档 / 音频 / 压缩包」三个大类，
    /// 现在直接用图标自带的描述（"PDF 文档" / "压缩包"），状态栏信息量更足。
    /// </summary>
    public string TypeLabel => FileIcon.Description;

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

    /// <summary>
    /// 副标题（只有文件夹项用）：完整路径。
    ///
    /// 为什么要有它：文件夹以前直接拿完整路径当标题，侧栏宽度只够显示
    /// 「C:\Users\xxx\Videos\Capt…」，两个不同目录看起来一模一样，用户挑哪个全靠猜。
    /// 现在标题只放末级目录名，路径放副标题小字 + 完整路径进 ToolTip。
    /// </summary>
    public string? SubTitle { get; set; }

    /// <summary>鼠标悬停时的完整路径说明。</summary>
    public string? ToolTipText { get; set; }

    public string? Icon { get; set; }
    public string? Key { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>关联的根文件夹/标签/分类 id（可空）。</summary>
    public long? RelatedId { get; set; }

    /// <summary>
    /// 列表项内容模板里放的是 StackPanel（两级文字），UI 自动化合成不出名字，
    /// 会退化成「类名」；给个 ToString 让读屏软件和自动化测试都能读到正常文字。
    /// </summary>
    public override string ToString() => Title;
}

/// <summary>顶部「类型」多选筛选项。</summary>
public partial class TypeFilterOption : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    public Core.Enums.MediaType MediaType { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;
}
