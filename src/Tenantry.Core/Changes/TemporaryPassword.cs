using System.Security.Cryptography;

namespace Tenantry.Core.Changes;

/// <summary>
/// Temporary passwords that are strong and easy to read out or type: four groups of characters
/// with no look-alikes (no 0/O, 1/l/I), always meeting Entra's complexity rules.
/// </summary>
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

    const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    const string Lower = "abcdefghijkmnopqrstuvwxyz";
    const string Digits = "23456789";

    /// <summary>For example "Kp7r-Xw3n-Bq9t-Hm4s": 16 random characters plus dashes.</summary>
    public static string Create()
    {
        var groups = new string[4];
        for (var g = 0; g < 4; g++)
        {
            // Each group: upper, lower, digit, lower, so every class is always present.
            groups[g] = string.Concat(
                Pick(Upper), Pick(Lower), Pick(Digits), Pick(Lower));
        }
        return string.Join('-', groups);
    }

    static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}
