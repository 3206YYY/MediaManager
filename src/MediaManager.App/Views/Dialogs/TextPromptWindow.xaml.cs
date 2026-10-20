using System.Windows;
using System.Windows.Input;

namespace MediaManager.App.Views.Dialogs;

/// <summary>
/// 主题化文本输入框（替代 Microsoft.VisualBasic.Interaction.InputBox，
/// 后者是系统对话框，深色模式下会突然闪出白底窗口）。
/// </summary>
public partial class TextPromptWindow : Window
{
    public string? Value { get; private set; }

    public TextPromptWindow(string title, string message, string initialValue)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            CustomMessageBox.Warn("名称不能为空。", "提示");
            InputBox.Focus();
            return;
        }

        Value = text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Value = null;
            DialogResult = false;
        }
    }
}

/// <summary>文本输入弹窗门面。</summary>
public static class CustomPrompt
{
    /// <summary>弹出输入框；返回 null 表示用户取消。</summary>
    public static string? Show(string message, string title = "输入", string initialValue = "")
    {
        var app = Application.Current;
        if (app == null) return null;

        try
        {
            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Show(message, title, initialValue));

            var window = new TextPromptWindow(title, message, initialValue);
            var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                        ?? (app.MainWindow is { IsVisible: true } mw ? mw : null);
            if (owner != null && owner != window && owner.IsVisible)
                window.Owner = owner;

            return window.ShowDialog() == true ? window.Value : null;
        }
        catch
        {
            return null;
        }
    }
}
