using MediaManager.App.Views.Dialogs;
using System.Windows;
using System.Windows.Controls;

namespace MediaManager.App.Views;

/// <summary>
/// 添加文字对话框：输入文字、选择位置（9 宫格）、选择颜色。
/// </summary>
public partial class TextEditWindow : Window
{
    /// <summary>输入的文字。</summary>
    public string? Text => TextBox.Text;

    /// <summary>位置（topleft/topcenter/.../bottomright）。</summary>
    public string Position => (PositionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "bottomcenter";

    /// <summary>颜色（#RRGGBB）。</summary>
    public string Color => (ColorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FFFFFF";

    public TextEditWindow(string initialText = "")
    {
        InitializeComponent();
        TextBox.Text = initialText;
        TextBox.Focus();
        TextBox.CaretIndex = TextBox.Text.Length;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextBox.Text))
        {
            CustomMessageBox.Warn("请先输入文字，再点击确定。", "提示");
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
