using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace MediaManager.App.Services;

/// <summary>系统托盘图标：最小化到托盘、双击恢复、右键菜单退出。</summary>
public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;

    public TrayIconService(Action showMainWindow, Action exitApplication)
    {
        _showMainWindow = showMainWindow;
        _exitApplication = exitApplication;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "媒体管理器",
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => _showMainWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _showMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exitApplication());
        _notifyIcon.ContextMenuStrip = menu;
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
