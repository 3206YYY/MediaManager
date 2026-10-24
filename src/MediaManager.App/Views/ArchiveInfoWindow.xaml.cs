using System.Windows;
using MediaManager.App.Services;
using SharpCompress.Archives;

namespace MediaManager.App.Views;

/// <summary>压缩包只读查看：列出条目，不解压、不修改。</summary>
public partial class ArchiveInfoWindow : Window
{
    private string? _path;

    public ArchiveInfoWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowThemeHelper.ApplyTitleBarDarkMode(this, App.IsDarkTheme);
    }

    public void Load(string path)
    {
        _path = path;
        Title = System.IO.Path.GetFileName(path);
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        try
        {
            var rows = new List<ArchiveEntryRow>();
            using (var archive = ArchiveFactory.Open(path))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    rows.Add(new ArchiveEntryRow
                    {
                        Name = entry.Key ?? string.Empty,
                        SizeDisplay = FormatSize(entry.Size),
                        CompressedDisplay = FormatSize(entry.CompressedSize),
                    });
                    if (rows.Count >= 5000) break;
                }
            }

            EntryList.ItemsSource = rows;
            InfoText.Text = $"{ext} · 共 {rows.Count} 个文件";
            HintText.Text = "只读预览，不解压、不修改；大压缩包最多列出 5000 项。";
        }
        catch (Exception ex)
        {
            InfoText.Text = "无法读取该压缩包：" + ex.Message;
            HintText.Text = "可点「用系统程序打开」交给系统解压软件处理。";
        }
    }

    private void OnOpenWithSystem(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开失败：" + ex.Message, "压缩包",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    public sealed class ArchiveEntryRow
    {
        public string Name { get; init; } = string.Empty;
        public string SizeDisplay { get; init; } = string.Empty;
        public string CompressedDisplay { get; init; } = string.Empty;
    }
}
