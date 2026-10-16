using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MaimaiTouchTool.Touch;

/// <summary>
/// 把「触发阈值的 filtered 值」（0..1023）映射为进度条上标记线的左边距 Thickness。
/// 条宽从 ConverterParameter 读取（与 XAML 中进度条区域宽度一致），缺省 172。
/// </summary>
public sealed class ThresholdMarkerConverter : IValueConverter
{
    private const double DefaultBarWidth = 172.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double threshold;
        try { threshold = value is double d ? d : System.Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { threshold = 0; }

        var width = DefaultBarWidth;
        if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            width = parsed;
        }

        var left = Math.Clamp(threshold / 1023.0 * width - 1.0, 0.0, Math.Max(0.0, width - 2.0));
        return new Thickness(left, 0, 0, 0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
