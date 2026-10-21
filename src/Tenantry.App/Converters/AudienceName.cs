using System.Globalization;
using Avalonia.Data.Converters;
using Tenantry.Core.Directory;

namespace Tenantry.App.Converters;

/// <summary>Who outside gets an automatic reply, in words ("Only their contacts outside").</summary>
public sealed class AudienceName : IValueConverter
{
    public static readonly AudienceName Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AutoReplyAudience audience ? AutoReply.AudienceName(audience) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
