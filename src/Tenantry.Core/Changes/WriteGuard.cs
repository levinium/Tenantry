namespace Tenantry.Core.Changes;

/// <summary>
/// While a tenant is in Test mode, only objects whose name, sign-in name or address starts with
/// <see cref="Marker"/> can be changed; anything else is refused before any call is made. Test
/// mode is on by default in development builds, and only a person can turn it off.
/// </summary>
public static class WriteGuard
{
    public const string Marker = "zz-tenantry-test-";

    /// <summary>
    /// Judged by address (sign-in name or email) when the object has one, since a display name
    /// is easy to change: renaming a real user "zz-tenantry-test-…" must not get past the guard.
    /// Objects without an address (a security group) are judged by name.
    /// </summary>
    public static bool IsTestObject(ChangeTarget target) =>
        (target.Address ?? target.Name).StartsWith(Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the change is refused, or null when it may go ahead.</summary>
    public static string? Refusal(ChangeOp op, bool testMode)
    {
        if (!testMode)
            return null;
        var real = op.Guarded.FirstOrDefault(t => !IsTestObject(t));
        return real is null
            ? null
            : $"Test mode is on: only objects whose sign-in name or address starts with {Marker} can be changed, and \"{real.Name}\" isn't one. Nothing was sent.";
    }
}
