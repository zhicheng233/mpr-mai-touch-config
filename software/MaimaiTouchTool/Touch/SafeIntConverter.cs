using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace MaimaiTouchTool.Touch;

public sealed class SafeIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }
        // 非法/空输入：拒绝更新源（防止 InvalidCastException），并通知界面弹 toast 提示。
        NumericInputNotifier.Report();
        return BindingOperations.DoNothing;
    }
}
