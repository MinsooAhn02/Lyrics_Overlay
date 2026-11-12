using System;
using System.Globalization;
using System.Windows.Data;

namespace LyricsOnTheGo.Controls;

/// <summary>
/// 0–100 ↔ 100–0. The stored setting is the tint's OPACITY (higher = darker), but users think of the
/// slider as TRANSPARENCY (higher = more see-through). Binding through this converter flips the
/// presentation without changing the stored value or its meaning anywhere else.
/// </summary>
public sealed class InvertPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IConvertible c ? 100 - System.Convert.ToInt32(c, CultureInfo.InvariantCulture) : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IConvertible c ? 100 - System.Convert.ToInt32(System.Math.Round(System.Convert.ToDouble(c, CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture) : 0;
}
