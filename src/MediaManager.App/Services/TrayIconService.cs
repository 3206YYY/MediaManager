using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace MediaManager.App.Services;

/// <summary>
/// 系统托盘图标：最小化到托盘、双击恢复、右键菜单。
///
/// 菜单是 WinForms 的 <see cref="ContextMenuStrip"/>，**不吃 WPF 的资源字典**，
/// 深色主题下如果只改 BackColor 仍会出现白色渐变块 / 白底文字 ——
/// 所以这里整套自绘（<see cref="ThemedMenuRenderer"/>），并在主题切换时重新上色。
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showMainWindow;
    private readonly Action _exitApplication;
    private readonly ContextMenuStrip _menu;
    private readonly List<ToolStripItem> _themedItems = new();
    private ToolStripMenuItem? _header;

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

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            MinimumSize = new Size(216, 0),
            Padding = new Padding(0, 4, 0, 4),
            // ⚠ 不要在这里写 RenderMode = Custom：WinForms 会直接抛 ArgumentException
            // （「RenderMode 属性不能直接设置为 Custom」）。赋 Renderer 时会自动切成 Custom。
        };

        // 顶部放版本号而不是直接堆功能：托盘菜单一眼能确认「这是哪个版本」，
        // 也避免菜单第一项就是可点击项、误触后窗口突然弹出来。
        _header = new ToolStripMenuItem($"媒体管理器  v{version}")
        {
            Enabled = false,
            Font = new Font(_menu.Font, FontStyle.Bold),
            Padding = new Padding(10, 6, 10, 4),
        };
        Add(_header);
        Add(new ToolStripSeparator());

        var open = CreateItem("打开主界面", _showMainWindow);
        open.Font = new Font(_menu.Font, FontStyle.Bold);
        Add(open);

        if (!string.IsNullOrWhiteSpace(dataDirectory))
            Add(CreateItem("打开数据目录", () => OpenFolder(dataDirectory)));

        var update = new ToolStripMenuItem("检查更新") { Padding = new Padding(10, 6, 10, 6) };
        update.Click += async (_, _) => await CheckForUpdatesAsync(update);
        Add(update);

        Add(CreateItem("项目主页", () => OpenUrl(UpdateChecker.RepositoryUrl)));

        Add(new ToolStripSeparator());
        Add(CreateItem("退出", _exitApplication));

        _notifyIcon.ContextMenuStrip = _menu;
        ApplyTheme();
    }

    private void Add(ToolStripItem item)
    {
        _menu.Items.Add(item);
        _themedItems.Add(item);
    }

    private static ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(10, 6, 10, 6) };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>按当前主题给托盘菜单重新上色（浅色 / 深色都自绘，避免系统色块）。</summary>
    public void ApplyTheme()
    {
        var palette = MenuPalette.Current;
        _menu.BackColor = palette.Back;
        _menu.ForeColor = palette.Text;
        _menu.Renderer = new ThemedMenuRenderer(palette);

        foreach (var item in _themedItems)
        {
            item.BackColor = palette.Back;
            item.ForeColor = item.Enabled ? palette.Text : palette.Muted;
        }

        // 版本号那行始终压暗一档，视觉上像标题栏而不是可点项
        if (_header != null)
            _header.ForeColor = palette.Muted;
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
        _menu.Dispose();
    }

    // ---------------------------------------------------------------- 主题配色

    /// <summary>菜单配色：直接取当前主题资源，保证和主界面一致。</summary>
    private sealed record MenuPalette(Color Back, Color Text, Color Muted, Color Hover, Color Border)
    {
        public static MenuPalette Current
        {
            get
            {
                // 深色：PanelColor #252526 / TextColor #F0F0F0 / HoverColor #3E3E42 / BorderColor #3F3F46
                // 浅色：对应主题里同名资源，取不到时回退到这组安全值
                var back = Resolve("PanelColor", Color.FromArgb(37, 37, 38));
                var text = Resolve("TextColor", Color.FromArgb(240, 240, 240));
                var hover = Resolve("HoverColor", Color.FromArgb(62, 62, 66));
                var border = Resolve("BorderColor", Color.FromArgb(63, 63, 70));
                var muted = Resolve("TextSecondaryColor", Color.FromArgb(160, 160, 160));
                return new MenuPalette(back, text, muted, hover, border);
            }
        }

        private static Color Resolve(string key, Color fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is System.Windows.Media.Color c)
                    return Color.FromArgb(c.A, c.R, c.G, c.B);
                if (Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
                    return Color.FromArgb(b.Color.A, b.Color.R, b.Color.G, b.Color.B);
            }
            catch
            {
                // 资源字典还没加载（极早期）→ 用回退值
            }
            return fallback;
        }
    }

    /// <summary>自绘托盘菜单：统一背景、悬停高亮、分隔线、禁用项颜色。</summary>
    private sealed class ThemedMenuRenderer : ToolStripRenderer
    {
        private readonly MenuPalette _p;

        public ThemedMenuRenderer(MenuPalette palette) => _p = palette;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(_p.Back);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_p.Border);
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // 只有可点项才给悬停底色；禁用项保持干净，避免版本号那行像按钮
            if (!e.Item.Selected || !e.Item.Enabled) return;
            using var brush = new SolidBrush(_p.Hover);
            e.Graphics.FillRectangle(brush, new Rectangle(0, 0, e.Item.Width, e.Item.Height));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _p.Text : _p.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(_p.Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // ShowImageMargin=false，不需要左侧色带
        }

        protected override void OnRenderToolStripPanelBackground(ToolStripPanelRenderEventArgs e)
        {
            // 不画默认渐变
        }
    }
}
