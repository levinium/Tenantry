namespace Tenantry.Core.Directory;

/// <summary>One entry of Microsoft's directory audit log.</summary>
/// <param name="By">Who (or which app) did it.</param>
/// <param name="About">What it was done to, by name.</param>
/// <param name="Succeeded">False when the attempt failed.</param>
public sealed record AuditEvent(DateTimeOffset At, string Activity, string Category, bool Succeeded, string? By, string? About, string? Reason);

/// <summary>The last day each Microsoft 365 app saw them active (Microsoft's usage reports, a day or two behind).</summary>
/// <param name="ReportDate">The day the report covers up to.</param>
public sealed record AppActivity(DateOnly? ReportDate, IReadOnlyList<(string App, DateOnly? Last)> Apps);

/// <summary>The Activity tab's reads.</summary>
public interface IActivityRecords
{
    /// <summary>
    /// The directory audit log, newest first: changes made to them (<paramref name="byThem"/>
    /// false) or changes they made. Kept by Microsoft for 30 days (7 without Entra ID P1).
    /// </summary>
    Task<Available<IReadOnlyList<AuditEvent>>> GetAuditAsync(string userId, bool byThem, CancellationToken cancellationToken = default);

    /// <summary>Their last activity per app; a reason when the reports hide names or can't be read.</summary>
    Task<Available<AppActivity>> GetAppActivityAsync(string userPrincipalName, CancellationToken cancellationToken = default);
}
