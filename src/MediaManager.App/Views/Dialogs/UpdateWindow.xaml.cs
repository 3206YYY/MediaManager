using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaManager.Core.Services;

namespace MediaManager.App.Views.Dialogs;

/// <summary>用户在更新提示里选了什么。</summary>
public enum UpdateChoice
{
    /// <summary>关掉了窗口，什么也没选。</summary>
    Later,

    /// <summary>去下载（浏览器已打开）。</summary>
    Download,

    /// <summary>跳过这个版本，以后不再提示它。</summary>
    SkipThisVersion,
}

/// <summary>
/// 发现新版本时的提示窗口。
/// <para>
/// 刻意不做「一键更新」：不下载、不替换文件、不重启程序。只把「哪个版本、改了什么、
/// 去哪下」讲清楚，剩下的交给用户。设置里的开关只管「要不要检查」，从来不管「要不要装」。
/// </para>
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateCheckResult _result;

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public UpdateWindow(UpdateCheckResult result)
    {
        InitializeComponent();
        _result = result;

        CurrentVersionText.Text = $"当前版本 v{result.CurrentVersion}";
        NewVersionText.Text = $"v{result.LatestVersion ?? "?"}";
        MetaText.Text = BuildMeta(result);
        Title = $"发现新版本 v{result.LatestVersion}";

        RenderDigest(result.ReleaseNotes);

        // 没有安装包直链时，主按钮改成「去发布页」，避免点了没反应
        if (string.IsNullOrWhiteSpace(result.DownloadUrl) && string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            DownloadButton.IsEnabled = false;
            DownloadButton.ToolTip = "这个版本没有提供下载链接，可以稍后到项目主页看看";
        }

        if (string.IsNullOrWhiteSpace(result.ReleaseUrl))
            NotesButton.Visibility = Visibility.Collapsed;

        Loaded += (_, _) => DownloadButton.Focus();
    }

    private static string BuildMeta(UpdateCheckResult result)
    {
        var parts = new List<string>();
        if (result.PublishedAt is { } when)
        {
            var local = when.ToLocalTime();
            parts.Add($"发布于 {local:yyyy 年 M 月 d 日}");
        }

        if (result.DownloadSize is { } bytes and > 0)
            parts.Add($"安装包 {bytes / 1024d / 1024d:0.#} MB");

        var name = result.ReleaseName?.Trim();
        if (!string.IsNullOrWhiteSpace(name)
            && !name.Contains(result.LatestVersion ?? "?", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(name);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 只显示「一句概括 + 最多 5 条要点」。
    /// 更新说明写成博客也没人在这里读；想深入的自然会点「查看完整说明」。
    /// </summary>
    private void RenderDigest(string? markdown)
    {
        var digest = UpdatePolicy.BuildDigest(markdown);
        var secondaryBrush = (Brush)FindResource("TextSecondaryBrush");

        if (digest.Summary is null && digest.Bullets.Count == 0)
        {
            NoNotesText.Visibility = Visibility.Visible;
            return;
        }

        if (!string.IsNullOrEmpty(digest.Summary))
        {
            SummaryText.Text = digest.Summary;
            SummaryText.Margin = new Thickness(0, 0, 0, digest.Bullets.Count > 0 ? 10 : 0);
            SummaryText.Visibility = Visibility.Visible;
        }

        foreach (var bullet in digest.Bullets)
        {
            BulletsPanel.Children.Add(new TextBlock
            {
                Text = "· " + bullet,
                FontSize = 12.5,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Foreground = secondaryBrush,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        if (digest.HasMore)
        {
            MoreText.Text = $"另外还有 {digest.TotalBullets - digest.Bullets.Count} 条改动，"
                            + "点「查看完整说明」看全部。";
            MoreText.Visibility = Visibility.Visible;
        }
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Download;
        OpenUrl(_result.DownloadUrl ?? _result.ReleaseUrl);
        DialogResult = true;
    }

    private void OnOpenNotes(object sender, RoutedEventArgs e) => OpenUrl(_result.ReleaseUrl);

    /// <summary>用系统默认浏览器打开链接（explorer.exe 比 Process.Start(url) 更稳）。</summary>
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", url);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Error($"无法打开链接，请手动访问：\n{url}\n\n{ex.Message}", "打开链接失败");
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.SkipThisVersion;
        DialogResult = true;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Later;
        DialogResult = false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Choice = UpdateChoice.Later;
            DialogResult = false;
        }
    }
}
