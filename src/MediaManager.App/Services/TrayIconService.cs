using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace MediaManager.App.Services;

/// <summary>系统托盘图标：最小化到托盘、双击恢复、右键菜单。</summary>
public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;

    public TrayIconService(Action showMainWindow, Action exitApplication, string? dataDirectory = null)
    {
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;

        var version = UpdateChecker.CurrentVersion;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = $"媒体管理器 {version} · 双击打开",
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => _showMainWindow();

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            MinimumSize = new Size(216, 0),
            Padding = new Padding(0, 4, 0, 4),
        };

        // 顶部放版本号而不是直接堆功能：托盘菜单一眼能确认「这是哪个版本」，
        // 也避免菜单第一项就是可点击项、误触后窗口突然弹出来。
        var header = new ToolStripMenuItem($"媒体管理器  v{version}")
        {
            Enabled = false,
            Font = new Font(menu.Font, FontStyle.Bold),
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(10, 6, 10, 4),
        };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var open = CreateItem("打开主界面", _showMainWindow);
        open.Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(open);

        if (!string.IsNullOrWhiteSpace(dataDirectory))
            menu.Items.Add(CreateItem("打开数据目录", () => OpenFolder(dataDirectory)));

        var update = new ToolStripMenuItem("检查更新") { Padding = new Padding(10, 6, 10, 6) };
        update.Click += async (_, _) => await CheckForUpdatesAsync(update);
        menu.Items.Add(update);

        menu.Items.Add(CreateItem("项目主页", () => OpenUrl(UpdateChecker.RepositoryUrl)));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("退出", _exitApplication));

        _notifyIcon.ContextMenuStrip = menu;
    }

    private static ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(10, 6, 10, 6) };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// 托盘里检查更新：不想为了看一眼版本就打开设置页。
    /// 有新版直接打开下载页；没有新版只弹气泡，不打断当前操作。
    /// </summary>
    private async Task CheckForUpdatesAsync(ToolStripMenuItem item)
    {
        var original = item.Text;
        item.Enabled = false;
        item.Text = "检查更新中…";
        try
        {
            var result = await UpdateChecker.CheckAsync();

            if (result.HasUpdate)
            {
                ShowBalloon("发现新版本", $"{result.LatestVersion} 已发布，正在打开下载页");
                OpenUrl(result.ReleaseUrl ?? UpdateChecker.ReleasesUrl);
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                ShowBalloon("暂时无法检查更新", result.Error);
            }
            else
            {
                ShowBalloon("已是最新版本", $"当前版本 {result.CurrentVersion}");
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("检查更新失败", ex.Message);
        }
        finally
        {
            item.Text = original;
            item.Enabled = true;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 资源管理器打不开不影响托盘其他功能
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 浏览器启动失败不致命
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "MediaManager.ico");
            if (System.IO.File.Exists(icoPath))
                return new Icon(icoPath);
        }
        catch
        {
            // 忽略，回退到系统图标
        }

        // 回退：从当前 exe 提取图标
        try
        {
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>显示气泡提示（可选）。</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(2000);
        }
        catch
        {
            // 托盘气泡失败不致命
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
