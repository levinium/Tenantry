namespace Tenantry.Core.Changes;

/// <summary>Checks a password an admin typed. Generated ones come from the tenant's <see cref="PasswordStyle"/>.</summary>
public static class TemporaryPassword
{
    /// <summary>
    /// What's wrong with a password an admin typed, by Entra's default rules (8 to 256 characters,
    /// three of: upper case, lower case, digits, symbols; no spaces at the ends), or null when it
    /// passes. The tenant can be stricter; Microsoft's answer then shows when the change applies.
    /// </summary>
    public static string? Problem(string password)
    {
        if (password.Length < 8)
            return "Use at least 8 characters.";
        if (password.Length > 256)
            return "Use at most 256 characters.";
        if (password.Trim().Length != password.Length)
            return "Don't start or end with a space.";
        var kinds = new[] { password.Any(char.IsUpper), password.Any(char.IsLower), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) }.Count(k => k);
        return kinds < 3 ? "Use at least three of: upper-case letters, lower-case letters, numbers, symbols." : null;
    }
}
