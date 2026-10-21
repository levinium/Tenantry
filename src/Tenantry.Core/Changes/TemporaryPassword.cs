using System.Security.Cryptography;

namespace Tenantry.Core.Changes;

/// <summary>
/// Temporary passwords that are strong and easy to read out or type: four groups of characters
/// with no look-alikes (no 0/O, 1/l/I), always meeting Entra's complexity rules.
/// </summary>
public static class TemporaryPassword
{
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
