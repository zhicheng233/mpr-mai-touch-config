using System.Globalization;
using Avalonia.Data.Converters;

namespace MaimaiTouchTool.Touch;

/// <summary>把 <c>avares://</c> 的 SVG 路径转成可直接给 <c>Image.Source</c> 的 <see cref="Avalonia.Svg.Skia.SvgImage"/>。</summary>
public sealed class SvgPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string path
        ? new Avalonia.Svg.Skia.SvgImage { Source = TouchManifest.Load(path) }
        : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
