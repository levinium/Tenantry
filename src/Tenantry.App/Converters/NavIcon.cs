using System.Globalization;
using Avalonia.Data.Converters;
using FluentIcons.Common;

namespace Tenantry.App.Converters;

/// <summary>Maps a sidebar section key to its icon. View models stay free of icon types.</summary>
public sealed class NavIcon : IValueConverter
{
    public static readonly NavIcon Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "users" => Icon.Person,
        "groups" => Icon.PeopleTeam,
        "mail" => Icon.Mail,
        "sharepoint" => Icon.Cloud,
        "devices" => Icon.Laptop,
        "security" => Icon.ShieldLock,
        "reports" => Icon.DataBarVertical,
        _ => Icon.Apps,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
