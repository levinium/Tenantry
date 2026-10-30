using System.Globalization;
using Avalonia.Data.Converters;

namespace Tenantry.App.Converters;

/// <summary>Text in capitals, for the small field labels ("NAME") when the words come from a view model.</summary>
public sealed class Capitals : IValueConverter
{
    public static readonly Capitals Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string)?.ToUpper(culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
