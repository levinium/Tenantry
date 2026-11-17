using System.Globalization;
using Avalonia.Data.Converters;

namespace Tenantry.App.Converters;

/// <summary>Text in capitals, for the small field labels ("NAME") when the words come from a view model.</summary>
public sealed class Capitals : IValueConverter
{
    public static readonly Capitals Instance = new();

    // Invariant: the words are English whatever the computer's language (a Turkish culture would
    // turn "Mailbox" into "MAİLBOX").
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string)?.ToUpperInvariant();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
