using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MediaManager.App.Views.Dialogs;

/// <summary>弹窗按钮的视觉级别。</summary>
public enum DialogButtonKind
{
    Primary,
    Normal,
    Danger,
}

/// <summary>一个弹窗按钮的定义。</summary>
public sealed class DialogAction
{
    public string Text { get; init; } = "确定";
    public MsgBoxResult Result { get; init; } = MsgBoxResult.OK;
    public DialogButtonKind Kind { get; init; } = DialogButtonKind.Normal;
    /// <summary>是否是 Enter 键触发的默认按钮（同一窗口只应有一个）。</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// 完全跟随软件主题的自定义弹窗（替代系统 MessageBox）。
/// 背景/文字/边框全部走 DynamicResource，深色模式下自动变深底浅字。
/// </summary>
public partial class DialogWindow : Window
{
    /// <summary>用户点击的按钮结果（关闭窗口/取消时为 None 或 Cancel）。</summary>
    public MsgBoxResult Result { get; private set; } = MsgBoxResult.None;

    /// <summary>是否勾选了「记住我的选择」。</summary>
    public bool Remember { get; private set; }

    private MsgBoxResult _cancelResult = MsgBoxResult.Cancel;

    public DialogWindow(
        string title,
        string message,
        MsgBoxKind kind,
        IReadOnlyList<DialogAction> actions,
        string? checkboxText = null)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "提示" : title;
        TitleText.Text = Title;
        MessageText.Text = message;

        (IconText.Text, IconText.Foreground) = kind switch
        {
            MsgBoxKind.Success => ("✅", Brushes.SeaGreen),
            MsgBoxKind.Warning => ("⚠", new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06))),
            MsgBoxKind.Error => ("⛔", new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))),
            MsgBoxKind.Question => ("❓", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))),
            _ => ("ℹ", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))),
        };

        if (!string.IsNullOrWhiteSpace(checkboxText))
        {
            RememberBox.Content = checkboxText;
            RememberBox.Visibility = Visibility.Visible;
        }

        foreach (var action in actions)
        {
            var styleKey = action.Kind switch
            {
                DialogButtonKind.Primary => "DialogPrimaryButton",
                DialogButtonKind.Danger => "DialogDangerButton",
                _ => "DialogButton",
            };

            var button = new Button
            {
                Content = action.Text,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = action.IsDefault,
                Style = (Style)FindResource(styleKey),
            };
            button.Click += (_, _) =>
            {
                Result = action.Result;
                Remember = RememberBox.IsChecked == true;
                Close();
            };
            ButtonPanel.Children.Add(button);

            if (action.Result is MsgBoxResult.Cancel or MsgBoxResult.No)
                _cancelResult = action.Result;
        }
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 拖动失败忽略 */ }
        }
    }

    private void OnCancelButtonClick(object sender, RoutedEventArgs e)
    {
        Result = _cancelResult;
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Result = _cancelResult;
            Close();
        }
    }
}
