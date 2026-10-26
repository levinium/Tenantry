using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels.Workflows;

/// <summary>A warning sign found on a possibly compromised account, and the fix for it (ticked) when there is one.</summary>
public sealed partial class Finding(string person, string title, string detail, ChangeOp? fix, string fixText = "", bool isOn = true) : ObservableObject
{
    public string Person => person;
    public string Title => title;
    public string Detail => detail;
    public ChangeOp? Fix => fix;
    public bool HasFix => fix is not null;
    public string FixText => fixText;

    [ObservableProperty]
    public partial bool IsOn { get; set; } = fix is not null && isOn;
}

/// <summary>
/// Responding to a compromised account, for one person or several. It first checks each for the
/// usual signs of an attacker (mailbox rules that forward or delete mail, forwarding outside the
/// organization, sign-in methods or app consents added in the last week, sign-ins from new
/// countries) and offers a fix for each, ticked. The response itself is always offered, ticked:
/// a new password, MFA registered again, signed out everywhere. Blocking sign-in is offered
/// unticked. Everything goes to Pending changes.
/// </summary>
public sealed partial class CompromiseViewModel : ObservableObject
{
    const int ReadsAtOnce = 4;

    /// <summary>"Recent": methods, consents and new countries in this many days.</summary>
    public const int RecentDays = 7;

    /// <summary>Sign-ins are compared over this many days: the last week against the rest.</summary>
    const int SignInDays = 30;

    readonly IReadOnlyList<UserSummary> _people;
    readonly IUserRecords? _records;
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    readonly Func<DateTimeOffset> _now;
    readonly Dictionary<string, IReadOnlyList<AuthMethod>> _methods = new(StringComparer.Ordinal);

    public CompromiseViewModel(IReadOnlyList<UserSummary> people, IUserRecords? records, ChangePipeline pipeline, Action close,
        Func<DateTimeOffset>? now = null, bool checkNow = true)
    {
        _people = people;
        _records = records;
        _pipeline = pipeline;
        _close = close;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Steps =
        [
            new("password", "Reset the password", "A new password in this tenant's style, shown to you once after applying.", true),
            new("mustchange", "Ask them to change it when they next sign in", "They choose their own password once you've passed the new one on.", true),
            new("reregister", "Require MFA to be registered again", "Removes their sign-in methods (not the password), including any the attacker added.", true),
            new("signout", "Sign out everywhere", "Ends every session, including the attacker's.", true),
            new("block", "Block sign-in", "Until you've finished looking into it. Allow sign-in again from their page.", false),
        ];
        if (checkNow)
            _ = CheckAsync();
    }

    public string Heading => _people.Count == 1 ? $"Respond: {_people[0].DisplayName} may be compromised" : $"Respond: {_people.Count:N0} accounts may be compromised";

    public string? Subtitle => _people.Count == 1 ? null : string.Join(", ", _people.Take(6).Select(p => p.DisplayName)) + (_people.Count > 6 ? $" and {_people.Count - 6:N0} more" : "");

    public IReadOnlyList<WorkflowStep> Steps { get; }

    bool On(string key) => Steps.First(s => s.Key == key).IsOn;

    // ---- Checking -----------------------------------------------------------------------------

    public ObservableCollection<Finding> Findings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    [NotifyPropertyChangedFor(nameof(HasNoFindings))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    public partial string? Progress { get; private set; }

    public bool HasNoFindings => !IsChecking && Findings.Count == 0;

    public string NoFindingsText => $"No warning signs found: no mailbox rules that forward or delete mail, no forwarding outside the organization, " +
                                    $"and no sign-in methods, app consents or sign-in countries new in the last {RecentDays} days.";

    /// <summary>What couldn't be checked, and why.</summary>
    public ObservableCollection<string> CheckNotes { get; } = [];

    public bool HasCheckNotes => CheckNotes.Count > 0;

    sealed record Signs(List<Finding> Findings, List<string> Notes, IReadOnlyList<AuthMethod> Methods);

    public async Task CheckAsync(CancellationToken ct = default)
    {
        IsChecking = true;
        Progress = "Checking for warning signs…";
        try
        {
            var found = await Throttled.ReadAllAsync(_people, ReadsAtOnce, CheckPersonAsync,
                done => Progress = $"Checking for warning signs: {done:N0} of {_people.Count:N0}…", ct);
            Findings.Clear();
            CheckNotes.Clear();
            foreach (var person in _people)
            {
                var signs = found[person.Id];
                _methods[person.Id] = signs.Methods;
                foreach (var f in signs.Findings)
                    Findings.Add(f);
                foreach (var n in signs.Notes)
                    CheckNotes.Add(n);
            }
        }
        finally
        {
            IsChecking = false;
            Progress = null;
            OnPropertyChanged(nameof(HasCheckNotes));
        }
    }

    async Task<Signs> CheckPersonAsync(UserSummary person, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var notes = new List<string>();
        var target = GroupAccess.Target(person);
        var name = person.DisplayName;
        var since = _now() - TimeSpan.FromDays(RecentDays);
        IReadOnlyList<AuthMethod> methods = [];

        async Task Try(string what, Func<Task> read)
        {
            try
            {
                await read();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (notes)
                    notes.Add($"{name}: couldn't check {what} ({Friendly.Message(e)}).");
            }
        }

        var tasks = new List<Task>();
        // Mailbox forwarding, from the local copy.
        if (person.Mailbox is { ForwardingTo: { } to } mailbox)
        {
            var outside = mailbox.Forwarding == ForwardingKind.External;
            lock (findings)
                findings.Add(new Finding(name, outside ? "Forwarding mail outside the organization" : "Forwarding mail",
                    $"To {to}.", new SetForwarding(target, to, mailbox.KeepsCopyWhenForwarding, null, false, false), "Turn forwarding off", isOn: outside));
        }
        if (person.Mailbox is not null && _records is IMailboxContents contents)
            tasks.Add(Try("their mailbox rules", async () =>
            {
                foreach (var rule in await contents.GetInboxRulesAsync(person.Mailbox.PrimaryAddress ?? person.UserPrincipalName, ct))
                {
                    if (!rule.Enabled || !(rule.Forwards || rule.Deletes))
                        continue;
                    var what = rule.Forwards ? $"forwards mail to {string.Join(", ", rule.ForwardsTo)}" : "deletes mail";
                    lock (findings)
                        findings.Add(new Finding(name, $"Mailbox rule \"{rule.Name}\" {what}", rule.Description,
                            new SetInboxRuleEnabled(target, rule.Identity, rule.Name, true, false), "Turn this rule off"));
                }
            }));
        if (_records is ISecurityRecords security)
        {
            tasks.Add(Try("their sign-in methods", async () =>
            {
                methods = (await security.GetAuthMethodsAsync(person.Id, ct)).Where(m => m.Removable).ToArray();
                foreach (var m in methods.Where(m => m.Added >= since))
                    lock (findings)
                        findings.Add(new Finding(name, $"New sign-in method: {m.Kind}", $"Added {m.Added!.Value.ToLocalTime():d MMM, h:mm tt}{(m.Detail is { } d ? $" ({d})" : "")}.",
                            new RemoveAuthMethod(target, m.Id, m.Segment, m.Kind), "Remove this method"));
            }));
            tasks.Add(Try("their sign-ins", async () =>
            {
                var read = await security.GetSignInsAsync(person.Id, new SignInQuery(SignInDays, Result: SignInResult.Succeeded), ct);
                if (read.Unavailable is { } why)
                {
                    lock (notes)
                        notes.Add($"{name}: sign-in countries not checked. {why}");
                    return;
                }
                string? Country(SignInEvent s) => s.Location?.Split(", ").LastOrDefault() is { Length: > 0 } c ? c : null;
                var earlier = (read.Value ?? []).Where(s => s.When < since).Select(Country).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (earlier.Count == 0)
                    return; // nothing to compare with: every country would look new
                foreach (var g in (read.Value ?? []).Where(s => s.When >= since && Country(s) is { } c && !earlier.Contains(c)).GroupBy(s => Country(s)!))
                    lock (findings)
                        findings.Add(new Finding(name, $"Signed in from a new country: {g.Key}",
                            $"{g.Count()} sign-in{(g.Count() == 1 ? "" : "s")} in the last {RecentDays} days, the first {g.Min(s => s.When).ToLocalTime():d MMM, h:mm tt}" +
                            $"{(g.First().IpAddress is { } ip ? $" from {ip}" : "")}. Not seen in the {SignInDays - RecentDays} days before.", null));
            }));
            if (_records is IActivityRecords activity)
                tasks.Add(Try("their app consents", async () =>
                {
                    var consents = await security.GetAppConsentsAsync(person.Id, ct);
                    var audit = await activity.GetAuditAsync(person.Id, byThem: true, ct);
                    var recent = (audit.Value ?? []).Where(a => a.At >= since && a.Succeeded && a.Activity.Contains("Consent to application", StringComparison.OrdinalIgnoreCase))
                        .Select(a => a.About).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in consents.Where(c => recent.Contains(c.AppName)))
                        lock (findings)
                            findings.Add(new Finding(name, $"App given access recently: {c.AppName}", $"Can use: {string.Join(", ", c.Scopes.Take(6))}.",
                                new RevokeAppConsent(target, c.GrantId, c.AppName), "Take away the app's access"));
                }));
        }
        await Task.WhenAll(tasks);
        return new Signs(findings, notes, methods);
    }

    // ---- Staging ------------------------------------------------------------------------------

    /// <summary>The response for each person, then the fixes ticked for what was found.</summary>
    public WorkflowPlan Plan()
    {
        var changes = new List<ChangeOp>();
        foreach (var person in _people)
        {
            var target = GroupAccess.Target(person);
            if (On("block") && person.AccountEnabled)
                changes.Add(new SetSignInAllowed(target, true, false));
            if (On("password"))
                changes.Add(new ResetPassword(target, On("mustchange")));
            if (On("reregister"))
                changes.AddRange(_methods.GetValueOrDefault(person.Id, []).Select(m => new RemoveAuthMethod(target, m.Id, m.Segment, m.Kind)));
            if (On("signout"))
                changes.Add(new RevokeSessions(target));
        }
        // Fixes after the response; one already planned (a method removed by re-registering) isn't repeated.
        var slots = changes.Select(c => (c.Target.Id, c.Key)).ToHashSet();
        changes.AddRange(Findings.Where(f => f.IsOn && f.Fix is not null && slots.Add((f.Fix.Target.Id, f.Fix.Key))).Select(f => f.Fix!));
        return new WorkflowPlan(changes, []);
    }

    bool CanStage => !IsChecking;

    [RelayCommand(CanExecute = nameof(CanStage))]
    void Stage()
    {
        var plan = Plan();
        _pipeline.StageAll(plan.Changes);
        _close();
    }

    [RelayCommand]
    void Close() => _close();
}
