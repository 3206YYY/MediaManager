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

    private CancellationTokenSource? _loadCts;

    public void Load(string path)
    {
        _path = path;
        Title = System.IO.Path.GetFileName(path);

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        _ = LoadAsync(path, _loadCts.Token);
    }

    /// <summary>
    /// 后台读取压缩包条目：大 7z / rar 的目录解析可能要几秒，
    /// 放在 UI 线程会直接假死；这里顺带支持关闭窗口时取消。
    /// </summary>
    private async Task LoadAsync(string path, CancellationToken ct)
    {
        var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        InfoText.Text = "正在读取压缩包…";
        HintText.Text = "只读预览，不解压、不修改";

        try
        {
            var rows = await Task.Run(() => EnumerateEntries(path, ct), ct);
            if (ct.IsCancellationRequested) return;

            EntryList.ItemsSource = rows;
            InfoText.Text = $"{ext} · 共 {rows.Count} 个文件";
            HintText.Text = "只读预览，不解压、不修改；大压缩包最多列出 5000 项。";
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭 / 又打开了别的压缩包
        }
        catch (Exception ex)
        {
            InfoText.Text = "无法读取该压缩包：" + ex.Message;
            HintText.Text = "可点「用系统程序打开」交给系统解压软件处理。";
        }
    }

    private static List<ArchiveEntryRow> EnumerateEntries(string path, CancellationToken ct)
    {
        var rows = new List<ArchiveEntryRow>();
        using var archive = ArchiveFactory.Open(path);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;

            rows.Add(new ArchiveEntryRow
            {
                Name = entry.Key ?? string.Empty,
                SizeDisplay = FormatSize(entry.Size),
                CompressedDisplay = FormatSize(entry.CompressedSize),
            });
            if (rows.Count >= 5000) break;
        }
        return rows;
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        base.OnClosed(e);
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
