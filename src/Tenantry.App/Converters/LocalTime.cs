using System.Globalization;
using Avalonia.Data.Converters;

namespace Tenantry.App.Converters;

/// <summary>
/// A moment as this computer's local time, in the format given as the parameter ("h:mm:ss tt"),
/// so a bound date reads in the same time zone as the rest of the page (a raw DateTimeOffset
/// shows whatever offset Microsoft sent, usually UTC).
/// </summary>
public sealed class LocalTime : IValueConverter
{
    public static readonly LocalTime Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset at => at.ToLocalTime().ToString(parameter as string ?? "d MMM yyyy h:mm tt", culture),
        DateTime at => at.ToLocalTime().ToString(parameter as string ?? "d MMM yyyy h:mm tt", culture),
        _ => null,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
