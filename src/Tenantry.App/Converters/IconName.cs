using System.Globalization;
using Avalonia.Data.Converters;
using FluentIcons.Common;

namespace Tenantry.App.Converters;

/// <summary>An icon name from a view model ("PersonAdd") to the Fluent icon. Unknown names fall back to a neutral icon.</summary>
public sealed class IconName : IValueConverter
{
    public static readonly IconName Instance = new();

    public static Icon Parse(string? name) => Enum.TryParse<Icon>(name, ignoreCase: false, out var icon) ? icon : Icon.Apps;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Parse(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
