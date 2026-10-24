using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MediaManager.App.Services;
using MediaManager.Core.Enums;
using MediaManager.Infrastructure.FileSystem;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 选完文件夹后的预览确认：用缩略图 + 类型把文件夹里的文件列出来，
/// 让用户点「添加此文件夹」前就知道里面是什么。
/// </summary>
public partial class FolderPreviewWindow : Window
{
    private const int MaxItems = 500;
    private const int MaxThumbnails = 120;
    private const int ThumbSize = 128;

    private readonly string _folderPath;

    public FolderPreviewWindow(string folderPath)
    {
        InitializeComponent();
        _folderPath = folderPath;
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        Loaded += (_, _) => LoadPreview();
    }

    private void LoadPreview()
    {
        PathText.Text = _folderPath;

        var subFolders = 0;
        try { subFolders = Directory.GetDirectories(_folderPath).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var items = new List<PreviewItem>();
        var counts = new Dictionary<MediaType, int>();
        var thumbnailBudget = MaxThumbnails;

        try
        {
            foreach (var file in Directory.GetFiles(_folderPath))
            {
                if (items.Count >= MaxItems) break;
                try
                {
                    var info = new FileInfo(file);
                    var type = MediaFormat.GetMediaTypeForOpen(file);
                    if (type.HasValue)
                        counts[type.Value] = counts.GetValueOrDefault(type.Value) + 1;

                    var item = new PreviewItem
                    {
                        Name = info.Name,
                        TypeName = TypeNameFor(type),
                        Glyph = GlyphFor(type),
                    };

                    if (type == MediaType.Image && thumbnailBudget > 0)
                    {
                        item.Thumbnail = LoadThumbnail(file);
                        if (item.Thumbnail != null) thumbnailBudget--;
                    }

                    items.Add(item);
                }
                catch { /* 单个文件读不到就跳过 */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        FileList.ItemsSource = items;

        var parts = new List<string>();
        foreach (var pair in counts)
            parts.Add($"{TypeNameFor(pair.Key)} {pair.Value}");

        SummaryText.Text = $"子文件夹 {subFolders} 个 · 文件 {items.Count} 个" +
                           (parts.Count > 0 ? "（" + string.Join("、", parts) + "）" : "");

        HintText.Text = items.Count >= MaxItems
            ? $"文件较多，仅预览前 {MaxItems} 个"
            : "双击/滚动查看，确认后点「添加此文件夹」";
    }

    private static BitmapImage? LoadThumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = ThumbSize;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string GlyphFor(MediaType? type) => type switch
    {
        MediaType.Image => "🖼",
        MediaType.Video => "🎬",
        MediaType.Audio => "🎵",
        MediaType.Text => "📄",
        MediaType.Archive => "🗜️",
        _ => "📎",
    };

    private static string TypeNameFor(MediaType? type) => type switch
    {
        MediaType.Image => "图片",
        MediaType.Video => "视频",
        MediaType.Audio => "音频",
        MediaType.Text => "文档",
        MediaType.Archive => "压缩包",
        _ => "文件",
    };

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>文件夹预览项。</summary>
public sealed class PreviewItem
{
    public string Name { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;
    public BitmapImage? Thumbnail { get; set; }
}
