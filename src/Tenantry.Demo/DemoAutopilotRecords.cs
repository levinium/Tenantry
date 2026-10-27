using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's Autopilot devices: most company Windows laptops, tagged by department, plus a few
/// new ones not yet set up and a couple without a profile.
/// </summary>
public sealed partial class DemoUserRecords : IAutopilotRecords
{
    List<AutopilotDevice>? _autopilot;

    public async Task<Available<IReadOnlyList<AutopilotDevice>>> GetAutopilotDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_autopilot is null)
        {
            var users = (await directory.GetUsersAsync(cancellationToken)).ToDictionary(u => u.Id, StringComparer.Ordinal);
            var devices = await directory.GetDevicesAsync(cancellationToken);
            var random = new Random(20261028);
            _autopilot = devices.Where(d => d.Platform == "Windows" && d.Ownership == DeviceFields.Company && d.Serial is not null)
                .Select(d =>
                {
                    var tag = d.UserId is { } id && users.TryGetValue(id, out var u) ? u.Department : "Shared";
                    var noProfile = random.NextDouble() < 0.05;
                    return new AutopilotDevice($"ap-{d.Key}", d.Serial!, d.Manufacturer, d.Model, tag,
                        noProfile ? null : "Standard Laptop", noProfile ? "Not Assigned" : "Assigned", "Enrolled",
                        d.LastCheckIn, d.ManagedId, d.DeviceId, d.UserPrincipalName, null);
                })
                .ToList();
            for (var i = 1; i <= 4; i++)
                _autopilot.Add(new AutopilotDevice($"ap-new-{i}", $"PF{random.Next(1000000, 9999999)}", "Lenovo", "ThinkPad T14", i == 4 ? null : "Operations",
                    "Standard Laptop", i == 4 ? "Pending" : "Assigned", "Not Contacted", null, null, null, null, "PO-1042"));
        }
        return new(_autopilot.ToArray());
    }

    /// <summary>Applies an Autopilot change to the demo's list; false when it isn't one.</summary>
    internal bool ApplyAutopilot(ChangeOp change)
    {
        switch (change)
        {
            case SetAutopilotGroupTag t when _autopilot is not null:
                var index = _autopilot.FindIndex(d => d.Id == t.Entry.Id);
                if (index >= 0)
                    _autopilot[index] = _autopilot[index] with { GroupTag = t.New };
                return true;
            case DeleteAutopilotDevice a:
                _autopilot?.RemoveAll(d => d.Id == a.Entry.Id);
                return true;
            default:
                return change is SetAutopilotGroupTag;
        }
    }
}
