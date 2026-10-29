using System.Windows;

namespace MediaManager.App.Views.Dialogs;

/// <summary>弹窗类型（决定图标与语义）。</summary>
public enum MsgBoxKind
{
    Info,
    Success,
    Warning,
    Error,
    Question,
}

/// <summary>弹窗按钮组合。</summary>
public enum MsgBoxButtons
{
    OK,
    YesNo,
    OKCancel,
    /// <summary>关闭提示专用：最小化到托盘 / 直接退出 / 取消。</summary>
    TrayExitCancel,
}

/// <summary>弹窗返回结果。</summary>
public enum MsgBoxResult
{
    None,
    OK,
    Yes,
    No,
    Cancel,
    Tray,
    Exit,
}

/// <summary>
/// 主题化弹窗门面：全局替代系统 MessageBox（系统弹窗在深色模式下永远是白底黑字）。
/// 所有弹窗都使用自定义 WPF 窗口，背景/文字随主题动态变化。
/// </summary>
public static class CustomMessageBox
{
    // ---------- 常用语义化方法 ----------

    public static void Info(string message, string? title = null)
        => Show(message, title ?? "提示", MsgBoxKind.Info);

    public static void Success(string message, string? title = null)
        => Show(message, title ?? "完成", MsgBoxKind.Success);

    public static void Warn(string message, string? title = null)
        => Show(message, title ?? "提示", MsgBoxKind.Warning);

    public static void Error(string message, string? title = null)
        => Show(message, title ?? "出错了", MsgBoxKind.Error);

    /// <summary>确认框：返回 true 表示用户点了确认。</summary>
    public static bool Confirm(
        string message,
        string? title = null,
        string? confirmText = null,
        string cancelText = "取消",
        bool danger = false)
    {
        var result = Show(message, title ?? "确认", MsgBoxKind.Question, MsgBoxButtons.YesNo,
            primaryText: confirmText, secondaryText: cancelText, dangerPrimary: danger);
        return result == MsgBoxResult.Yes;
    }

    /// <summary>危险确认框（删除等），确认按钮为红色。</summary>
    public static bool ConfirmDanger(string message, string? title = null, string? confirmText = "删除")
        => Confirm(message, title ?? "确认删除", confirmText, "取消", danger: true);

    // ---------- 通用入口 ----------

    public static MsgBoxResult Show(
        string message,
        string? title = null,
        MsgBoxKind kind = MsgBoxKind.Info,
        MsgBoxButtons buttons = MsgBoxButtons.OK,
        string? primaryText = null,
        string? secondaryText = null,
        string? cancelText = null,
        bool dangerPrimary = false,
        string? checkboxText = null)
    {
        var (result, _) = ShowInternal(message, title, kind, buttons, primaryText, secondaryText,
            cancelText, dangerPrimary, checkboxText);
        return result;
    }

    /// <summary>带「记住我的选择」复选框的弹窗。</summary>
    public static MsgBoxResult ShowWithRemember(
        string message,
        string? title,
        MsgBoxKind kind,
        MsgBoxButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? cancelText,
        string? checkboxText,
        out bool remember)
    {
        var (result, remembered) = ShowInternal(message, title, kind, buttons, primaryText, secondaryText,
            cancelText, false, checkboxText);
        remember = remembered;
        return result;
    }

    /// <summary>关闭主窗口时的三选一提示（托盘 / 退出 / 取消 + 记住选择）。</summary>
    public static MsgBoxResult ShowClosePrompt(out bool remember)
    {
        var (result, remembered) = ShowInternal(
            "点击右上角关闭按钮后，您希望程序怎么做？\n\n" +
            "· 最小化到托盘：程序继续在后台运行，用于自动备份与文件监控，双击托盘图标可恢复。\n" +
            "· 直接退出：彻底关闭程序，不再后台运行。",
            "关闭提示",
            MsgBoxKind.Question,
            MsgBoxButtons.TrayExitCancel,
            primaryText: "最小化到托盘",
            secondaryText: "直接退出",
            cancelText: "取消",
            dangerPrimary: false,
            checkboxText: "记住我的选择，下次不再提示");
        remember = remembered;
        return result;
    }

    // ---------- 内部实现 ----------

    private static (MsgBoxResult Result, bool Remember) ShowInternal(
        string message,
        string? title,
        MsgBoxKind kind,
        MsgBoxButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? cancelText,
        bool dangerPrimary,
        string? checkboxText)
    {
        var app = Application.Current;
        if (app == null)
            return (MsgBoxResult.None, false);

        try
        {
            // 后台线程调用时切到 UI 线程，避免出现「调用线程无法访问此对象」
            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => ShowInternal(message, title, kind, buttons, primaryText,
                    secondaryText, cancelText, dangerPrimary, checkboxText));

            // 主窗口已关闭 / 应用正在退出时不再弹窗
            if (app.Dispatcher.HasShutdownStarted)
                return (MsgBoxResult.None, false);

            var actions = BuildActions(buttons, primaryText, secondaryText, cancelText, dangerPrimary);

            var window = new DialogWindow(title ?? "提示", message, kind, actions, checkboxText);
            var owner = ResolveOwner();
            if (owner != null && owner != window && owner.IsVisible)
                window.Owner = owner;

            window.ShowDialog();
            return (window.Result, window.Remember);
        }
        catch (Exception ex)
        {
            // 弹窗自身异常绝不能影响业务流程：忽略并返回取消语义。
            // 但绝不能「无声无息」—— 否则「为什么没弹窗」这种问题连查都没处查。
            Serilog.Log.Warning(ex, "弹窗显示失败：{Title}", title ?? "提示");
            return (MsgBoxResult.None, false);
        }
    }

    private static IReadOnlyList<DialogAction> BuildActions(
        MsgBoxButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? cancelText,
        bool dangerPrimary)
    {
        var primaryKind = dangerPrimary ? DialogButtonKind.Danger : DialogButtonKind.Primary;

        switch (buttons)
        {
            case MsgBoxButtons.YesNo:
                return new List<DialogAction>
                {
                    new()
                    {
                        Text = primaryText ?? "确定",
                        Result = MsgBoxResult.Yes,
                        Kind = primaryKind,
                        IsDefault = true,
                    },
                    new()
                    {
                        Text = secondaryText ?? cancelText ?? "取消",
                        Result = MsgBoxResult.No,
                        Kind = DialogButtonKind.Normal,
                    },
                };

            case MsgBoxButtons.OKCancel:
                return new List<DialogAction>
                {
                    new() { Text = primaryText ?? "确定", Result = MsgBoxResult.OK, Kind = primaryKind, IsDefault = true },
                    new() { Text = cancelText ?? "取消", Result = MsgBoxResult.Cancel, Kind = DialogButtonKind.Normal },
                };

            case MsgBoxButtons.TrayExitCancel:
                return new List<DialogAction>
                {
                    new() { Text = primaryText ?? "最小化到托盘", Result = MsgBoxResult.Tray, Kind = DialogButtonKind.Primary, IsDefault = true },
                    new() { Text = secondaryText ?? "直接退出", Result = MsgBoxResult.Exit, Kind = DialogButtonKind.Normal },
                    new() { Text = cancelText ?? "取消", Result = MsgBoxResult.Cancel, Kind = DialogButtonKind.Normal },
                };

            default:
                return new List<DialogAction>
                {
                    new() { Text = primaryText ?? "知道了", Result = MsgBoxResult.OK, Kind = DialogButtonKind.Primary, IsDefault = true },
                };
        }
    }

    private static Window? ResolveOwner()
    {
        var app = Application.Current;
        if (app == null) return null;

        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        return active ?? (app.MainWindow is { IsVisible: true } mw ? mw : null);
    }
}
