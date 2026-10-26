using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>OneDrive changes (4d-1), through the SharePoint admin site.</summary>
public sealed partial class GraphChangeExecutor
{
    const string NoSharePoint = "SharePoint isn't connected for this tenant.";

    async Task<ApplyResult> SetOneDriveAccessAsync(SetOneDriveAccess change, CancellationToken cancellationToken)
    {
        if (sharePoint is null)
            return new ApplyResult(ChangeOutcome.Failed, NoSharePoint);
        try
        {
            await sharePoint.SetSiteAdminAsync(change.SiteUrl, change.Login, change.New, cancellationToken);
        }
        catch (SharePointException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Message);
        }
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var admins = await sharePoint.GetSiteAdministratorsAsync(change.SiteId, cancellationToken);
            if (admins.Any(a => string.Equals(a.LoginName, change.Login, StringComparison.OrdinalIgnoreCase)) == change.New)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "SharePoint accepted the change but still shows the old access; it usually appears within a minute.");
    }
}
