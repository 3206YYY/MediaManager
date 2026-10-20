using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaManager.App.Converters;

/// <summary>发现页池子类型为「指定分类/收藏夹」(索引 3) 时显示。</summary>
public class DiscoveryPoolVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int idx && idx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
