using System.Windows;
using System.Windows.Controls;
using MediaManager.App.Services;

namespace MediaManager.App.Controls;

/// <summary>
/// 彩色文件类型图标（圆角卡片 + 折角 + 白色扩展名）。
///
/// 抽成控件而不是把视觉写死在 MainWindow 的 DataTemplate 里，是为了让
/// 「缩略图网格 / 最近删除 / 重复文件」共用同一份绘制代码，
/// 也方便用独立的小程序把它渲染成 PNG 做视觉回归。
/// </summary>
public partial class FileTypeIconView : UserControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(FileTypeIcon),
        typeof(FileTypeIconView),
        new PropertyMetadata(null, OnIconChanged));

    public FileTypeIcon? Icon
    {
        get => (FileTypeIcon?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public FileTypeIconView()
    {
        InitializeComponent();
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FileTypeIconView view) return;

        var icon = e.NewValue as FileTypeIcon;
        view.Card.Background = icon?.Background;
        view.Label.Text = icon?.Label ?? string.Empty;
    }
}
