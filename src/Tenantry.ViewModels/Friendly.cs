namespace Tenantry.ViewModels;

/// <summary>Turns sign-in and API failures into sentences an admin can act on.</summary>
public static class Friendly
{
    public static string Message(Exception e)
    {
        var text = e.Message;
        if (e is OperationCanceledException || text.Contains("authentication_canceled", StringComparison.OrdinalIgnoreCase)
            || text.Contains("User canceled", StringComparison.OrdinalIgnoreCase))
            return "Sign-in was cancelled.";
        if (text.Contains("AADSTS50105", StringComparison.Ordinal))
            return "Your account isn't assigned to Tenantry in this tenant. An admin can assign you under Enterprise applications › Tenantry › Users and groups.";
        if (text.Contains("AADSTS700016", StringComparison.Ordinal))
            return "That application ID wasn't found in the tenant. Check both IDs; a brand-new registration can take a minute to appear.";
        if (text.Contains("AADSTS65001", StringComparison.Ordinal))
            return "Tenantry hasn't been granted consent in this tenant. Run the setup again, or grant admin consent in the portal.";
        if (text.Contains("AADSTS90002", StringComparison.Ordinal) || text.Contains("AADSTS900023", StringComparison.Ordinal))
            return "That tenant ID wasn't recognised.";
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
