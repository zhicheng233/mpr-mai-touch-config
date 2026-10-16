using System.Globalization;
using Avalonia.Data.Converters;

namespace MaimaiTouchTool.Touch;

public sealed class SvgPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string path ? TouchManifest.Load(path) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
