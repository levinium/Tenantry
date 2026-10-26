using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// The Convert mailbox popup. To shared: explains what changes, warns when the mailbox will still
/// need a license (over 50 GB, an archive, a litigation hold), and offers to remove their licenses
/// and block sign-in in the same step. Back to a user mailbox: needs a license with Exchange.
/// Everything goes to Pending changes, conversion first so the mailbox is shared before it loses
/// its license.
/// </summary>
public sealed partial class ConvertMailboxViewModel : ObservableObject
{
    /// <summary>Shared mailboxes over this need a license.</summary>
    public const long FreeSharedLimit = 50L * 1024 * 1024 * 1024;

    readonly UserSummary _user;
    readonly IUserRecords? _records;
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    IReadOnlyList<LicenseAssignment> _direct = [];

    public ConvertMailboxViewModel(UserSummary user, IUserRecords? records, ChangePipeline pipeline, Action close)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _close = close;
        _ = LoadAsync();
    }

    ChangeTarget Target => new("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);

    public bool ToShared => _user.Mailbox?.Type != "Shared";

    public string Explanation => ToShared
        ? $"{_user.DisplayName}'s mailbox becomes a shared mailbox: its mail, calendar and contacts stay, and the people you give access to " +
          "(Groups & access) can open it. A shared mailbox needs no license up to 50 GB."
        : $"{_user.DisplayName}'s shared mailbox becomes a user mailbox again, one person's own. It needs a license that includes Exchange Online.";

    public string ActionText => ToShared ? "Convert to a Shared Mailbox" : "Convert to a User Mailbox";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; } = true;

    public bool IsReady => !IsLoading;

    /// <summary>Reasons the shared mailbox would still need a license.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    public partial bool RemoveLicenses { get; set; } = true;

    [ObservableProperty]
    public partial bool BlockSignIn { get; set; } = true;

    [ObservableProperty]
    public partial string RemoveLicensesText { get; private set; } = "Also Remove Their Licenses";

    [ObservableProperty]
    public partial bool HasLicensesToRemove { get; private set; }

    [ObservableProperty]
    public partial string? GroupLicensesText { get; private set; }

    public bool HasGroupLicenses => GroupLicensesText is not null;

    partial void OnGroupLicensesTextChanged(string? value) => OnPropertyChanged(nameof(HasGroupLicenses));

    async Task LoadAsync()
    {
        try
        {
            if (_records is null)
                return;
            var licensing = _records.GetLicensingAsync(_user.Id);
            var mail = _user.Mailbox is not null ? _records.GetMailAsync(_user.Id, _user.Mailbox.PrimaryAddress ?? _user.UserPrincipalName) : null;
            var assignments = (await licensing).Assignments;
            _direct = assignments.Where(a => a.IsDirect).ToArray();
            var inherited = assignments.Where(a => !a.IsDirect).ToArray();
            HasLicensesToRemove = _direct.Count > 0;
            RemoveLicensesText = _direct.Count switch
            {
                0 => "They have no licenses assigned directly",
                1 => $"Also Remove Their License ({_direct[0].Name})",
                _ => $"Also Remove Their {_direct.Count} Licenses ({string.Join(", ", _direct.Select(a => a.Name))})",
            };
            if (inherited.Length > 0)
                GroupLicensesText = $"Licenses from groups stay until they leave the group: {string.Join(", ", inherited.Select(a => $"{a.Name} (from {a.AssignedByGroupName})"))}.";
            if (mail is not null && ToShared)
            {
                var details = await mail;
                var warnings = new List<string>();
                if (details.UsedBytes >= FreeSharedLimit)
                    warnings.Add($"It holds {MailDetails.Size(details.UsedBytes.Value)}: over 50 GB, a shared mailbox needs a license.");
                if (details.Settings?.ArchiveOn == true)
                    warnings.Add("It has an online archive, which needs a license on a shared mailbox.");
                if (details.Settings?.HoldOn == true)
                    warnings.Add("It's on litigation hold, which needs a license on a shared mailbox.");
                Warnings = warnings;
                // Removing the license would break what it still needs: leave the choice to the admin.
                if (warnings.Count > 0)
                    RemoveLicenses = false;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Warnings = [$"Couldn't read their licenses and mailbox size ({Friendly.Message(e)}). Check them before removing licenses."];
            RemoveLicenses = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    void Convert()
    {
        var convert = new ConvertMailbox(Target, !ToShared, ToShared);
        var changes = new List<ChangeOp> { convert };
        // Only once it's shared: a user mailbox without a license is deleted after 30 days.
        if (ToShared && RemoveLicenses)
            changes.AddRange(_direct.Select(a => new SetLicenseAssigned(Target, a.SkuId, a.Name, true, false) { DependsOn = convert.Key }));
        if (ToShared && BlockSignIn && _user.AccountEnabled)
            changes.Add(new SetSignInAllowed(Target, true, false) { DependsOn = convert.Key });
        _pipeline.StageAll(changes);
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}
