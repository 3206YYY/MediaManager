using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaManager.App.Converters;

/// <summary>
/// bool → Visibility 取反：true 折叠、false 显示。
/// 用于「极简模式开着就藏起来」这类绑定，省得再套一层 Style 触发器。
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
