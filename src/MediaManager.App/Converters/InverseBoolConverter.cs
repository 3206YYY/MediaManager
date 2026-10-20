using System.Globalization;
using System.Windows.Data;

namespace MediaManager.App.Converters;

/// <summary>布尔值取反。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
}
