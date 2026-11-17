using System.Globalization;
using Avalonia.Data.Converters;

namespace Tenantry.App.Converters;

/// <summary>The word after a typed number of days: "day" for 1, otherwise "days" (never "after 1 days").</summary>
public sealed class DayWord : IValueConverter
{
    public static readonly DayWord Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.Trim() == "1" ? "day" : "days";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
