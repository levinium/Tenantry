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
        // Some Microsoft services answer a missing permission with a web page instead of an error
        // (threat submissions did, seen live): say what it means rather than show the page.
        if (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
            return text.Contains("401", StringComparison.Ordinal) || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                ? "Microsoft refused: Tenantry hasn't been given the permission this needs. The banner at the top offers to grant the missing permissions (a Global or Cloud Application Administrator approves)."
                : "Microsoft answered with a web page instead of data. Try again in a minute.";
        // Intune answers a missing permission with a JSON blob ("Application is not authorized to perform this operation"): say what to do.
        if (text.Contains("not authorized to perform this operation", StringComparison.OrdinalIgnoreCase))
            return "Tenantry hasn't been given the permission this needs. The banner at the top offers to grant the missing permissions (a Global or Cloud Application Administrator approves).";
        // Intune's other errors wrap a short message in JSON with support IDs: keep the message.
        if (System.Text.RegularExpressions.Regex.Match(text, @"""Message"":\s*""(?<m>[^""]+?)( - Operation ID[^""]*)?""") is { Success: true } intune)
            return intune.Groups["m"].Value.Trim() is { Length: > 0 } m && m != "An error has occurred"
                ? m : "Intune refused it without saying why. Check the values, or try it in Microsoft's admin center to see the reason.";
        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
