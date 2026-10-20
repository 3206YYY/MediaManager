using System.Globalization;
using System.Windows.Data;

namespace MediaManager.App.Converters;

/// <summary>布尔值转展开箭头（true=▼，false=▶）。</summary>
public class BoolToArrowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "▼" : "▶";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
