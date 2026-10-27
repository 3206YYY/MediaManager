using System.Globalization;
using System.Windows.Data;

namespace MediaManager.App.Converters;

/// <summary>
/// 数值乘法：<c>{Binding ThumbnailSize, Converter={StaticResource Multiply}, ConverterParameter=0.44}</c>。
/// 用来让子元素尺寸跟着宿主（缩略图格子大小滑块）等比变化。
/// </summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double d || !double.IsFinite(d)) return value;

        var factor = double.TryParse(
            parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? f
            : 1.0;

        return d * factor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
