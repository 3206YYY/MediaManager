using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaManager.App.Services;
using MediaManager.Core.Enums;
using MediaManager.Infrastructure.FileSystem;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// Explorer 风格的文件夹选择器：
/// 左侧目录树导航，右侧缩略图网格（图片真缩略图 / 其它类型图标），全程鼠标操作。
/// </summary>
public partial class FolderPickerWindow : Window
{
    private const int MaxItems = 500;
    private const int MaxThumbnails = 160;
    private const int ThumbSize = 112;

    private string _currentPath = string.Empty;
    private CancellationTokenSource? _navCts;

    /// <summary>用户选中的文件夹（DialogResult=true 时有效）。</summary>
    public string? SelectedPath { get; private set; }

    public FolderPickerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
        Loaded += (_, _) =>
        {
            LoadDrives();
            var initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
                initial = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            NavigateTo(initial);
        };
    }

    // ---------- 目录树 ----------

    private void LoadDrives()
    {
        FolderTree.Items.Clear();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    FolderTree.Items.Add(CreateNode(drive.RootDirectory.FullName, drive.Name, isDrive: true));
                }
                catch { }
            }
        }
        catch { }
    }

    private TreeViewItem CreateNode(string path, string header, bool isDrive)
    {
        ImageSource? icon = isDrive
            ? SystemIconProvider.GetFileIcon(path)
            : SystemIconProvider.GetFolderIcon();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
            panel.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });

        var node = new TreeViewItem { Header = panel, Tag = path };
        node.Items.Add(new TreeViewItem()); // 占位，展开时再加载子目录
        node.Expanded += OnNodeExpanded;
        node.Selected += OnNodeSelected;
        return node;
    }

    private async void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || node.Tag is not string path) return;
        // 只有一个 Tag 为 null 的占位项 → 还没加载过
        if (node.Items.Count != 1 || node.Items[0] is not TreeViewItem placeholder || placeholder.Tag != null)
            return;

        node.Items.Clear();
        try
        {
            // 展开大目录时枚举子目录放到后台，避免 UI 卡住
            var dirs = await Task.Run(() => Directory.GetDirectories(path));
            foreach (var dir in dirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                    node.Items.Add(CreateNode(dir, info.Name, isDrive: false));
                }
                catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OnNodeSelected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem node && node.Tag is string path)
        {
            NavigateTo(path);
            e.Handled = true;
        }
    }

    // ---------- 右侧内容 ----------

    private async void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        _currentPath = path;
        PathBox.Text = path;

        _navCts?.Cancel();
        var cts = new CancellationTokenSource();
        _navCts = cts;

        ItemList.ItemsSource = null;
        HintText.Text = "正在读取…";

        List<FolderEntry> entries;
        try
        {
            // 枚举 + 图片缩略图解码放后台：网络盘 / 上千文件的目录不再卡 UI
            entries = await Task.Run(() => BuildEntries(path, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            HintText.Text = "读取失败：" + ex.Message;
            return;
        }

        // 期间用户又点了别的目录：丢弃本次结果
        if (cts.IsCancellationRequested ||
            !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        // 系统图标在 UI 线程补齐（Shell COM 依赖 UI 线程；已按扩展名缓存，开销很小）
        foreach (var entry in entries)
        {
            if (entry.DisplayImage != null) continue;
            entry.DisplayImage = entry.IsFolder
                ? SystemIconProvider.GetFolderIcon()
                : SystemIconProvider.GetFileIcon(entry.FullPath);
        }

        ItemList.ItemsSource = entries;

        var folderCount = entries.Count(e => e.IsFolder);
        var fileCount = entries.Count(e => !e.IsFolder);
        HintText.Text = $"此文件夹：{folderCount} 个子文件夹，{fileCount} 个文件" +
                        (entries.Count >= MaxItems ? $"（仅显示前 {MaxItems} 项）" : "");
    }

    /// <summary>后台线程：枚举目录/文件并解码图片缩略图（不碰 Shell COM 和 UI）。</summary>
    private static List<FolderEntry> BuildEntries(string path, CancellationToken ct)
    {
        var entries = new List<FolderEntry>();
        var thumbnailBudget = MaxThumbnails;

        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxItems) break;
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((info.Attributes & FileAttributes.Hidden) != 0) continue;
                    entries.Add(new FolderEntry
                    {
                        Name = info.Name,
                        FullPath = dir,
                        IsFolder = true,
                        Glyph = "📁",
                        TypeName = "文件夹",
                    });
                }
                catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            foreach (var file in Directory.GetFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxItems) break;
                try
                {
                    var info = new FileInfo(file);
                    var type = MediaFormat.GetMediaTypeForOpen(file);
                    var item = new FolderEntry
                    {
                        Name = info.Name,
                        FullPath = file,
                        IsFolder = false,
                        Glyph = GlyphFor(type),
                        TypeName = TypeNameFor(type),
                    };

                    if (type == MediaType.Image && thumbnailBudget > 0)
                    {
                        item.DisplayImage = LoadThumbnail(file);
                        if (item.DisplayImage != null) thumbnailBudget--;
                    }

                    entries.Add(item);
                }
                catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return entries;
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

    // ---------- 交互 ----------

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is FolderEntry { IsFolder: true } entry)
            NavigateTo(entry.FullPath);
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        try
        {
            var parent = Directory.GetParent(_currentPath);
            if (parent != null) NavigateTo(parent.FullName);
        }
        catch { }
    }

    private void OnHome(object sender, RoutedEventArgs e)
        => NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void OnDesktop(object sender, RoutedEventArgs e)
        => NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    private void OnDocuments(object sender, RoutedEventArgs e)
        => NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    private void OnDownloads(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        NavigateTo(path);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !Directory.Exists(_currentPath)) return;
        SelectedPath = _currentPath;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        _navCts?.Cancel();
        base.OnClosed(e);
    }
}

/// <summary>文件夹选择器里的网格项（子文件夹或文件）。</summary>
public sealed class FolderEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
    public string Glyph { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public ImageSource? DisplayImage { get; set; }
}
