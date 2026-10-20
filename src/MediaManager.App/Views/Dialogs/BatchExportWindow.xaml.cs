using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaManager.Infrastructure.FileSystem;
using MediaManager.Core.Models;
using Microsoft.Win32;

namespace MediaManager.App.Views.Dialogs;

/// <summary>批量导出：统一长边尺寸 + 统一 JPEG 画质，输出到指定目录。</summary>
public partial class BatchExportWindow : Window
{
    private readonly IReadOnlyList<MediaItem> _items;

    /// <summary>点「开始导出」后填好；取消则为 null。</summary>
    public BatchExportOptions? Options { get; private set; }

    public BatchExportWindow(IReadOnlyList<MediaItem> items)
    {
        InitializeComponent();
        _items = items;

        var images = items.Count(i => i.MediaType == MediaManager.Core.Enums.MediaType.Image);
        var videos = items.Count - images;

        CountText.Text = videos == 0
            ? $"将导出 {images} 张图片。"
            : $"将导出 {images} 张图片（另有 {videos} 个视频会自动跳过）。";

        // 默认导出到「图片\媒体管理器导出\时间戳」，省得用户每次手选
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var baseDir = string.IsNullOrEmpty(pictures)
            ? Path.Combine(AppContext.BaseDirectory, "导出")
            : Path.Combine(pictures, "媒体管理器导出");
        OutputBox.Text = Path.Combine(baseDir, DateTime.Now.ToString("yyyyMMdd_HHmm"));

        DragArea.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择导出目录" };
        if (Directory.Exists(OutputBox.Text))
            dlg.InitialDirectory = OutputBox.Text;

        if (dlg.ShowDialog() == true)
            OutputBox.Text = dlg.FolderName;
    }

    private static int TagOf(ComboBox box)
    {
        return int.TryParse((box.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) ? v : 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var dir = OutputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            CustomMessageBox.Warn("请先选择导出目录。", "批量导出");
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error("无法创建导出目录：" +
                                   MediaManager.Core.Diagnostics.StorageErrors.FriendlyMessage(ex),
                                   "批量导出");
            return;
        }

        Options = new BatchExportOptions(dir, TagOf(SizeBox), TagOf(QualityBox));
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Options = null;
        DialogResult = false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Options = null;
            DialogResult = false;
        }
    }
}
