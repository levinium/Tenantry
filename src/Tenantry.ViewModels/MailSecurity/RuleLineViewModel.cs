using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.MailFlow;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>
/// One line of the sentence ("the sender is…", "prepend the subject with…"): what it checks or does,
/// its value and any companion values, and what's wrong with them.
/// </summary>
public sealed partial class RuleLineViewModel : ObservableObject
{
    /// <summary>How each companion value is labelled beside its line.</summary>
    static readonly Dictionary<string, string> ExtraLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SetHeaderValue"] = "Value",
        ["HeaderContainsMessageHeader"] = "Header Name",
        ["HeaderMatchesMessageHeader"] = "Header Name",
        ["ApplyHtmlDisclaimerFallbackAction"] = "If the Disclaimer Can't Be Added",
        ["BetweenMemberOf2"] = "And Members Of",
        ["ManagerAddresses"] = "Is This Person",
        ["ADComparisonOperator"] = "Comparison",
        ["RejectMessageEnhancedStatusCode"] = "Status Code",
        ["RejectMessageReasonText"] = "Explanation",
        ["IncidentReportContent"] = "Include in the Report",
        ["ApplyRightsProtectionCustomizationTemplate"] = "Branding Template",
        ["SmtpRejectMessageRejectText"] = "Explanation",
    };

    readonly Action _changed;

    public RuleLineViewModel(RuleLine line, Action changed, Action<RuleLineViewModel> remove)
    {
        Line = line;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
        var def = line.Def;
        Main = def.ValueType == "none" ? null : new RuleValueEditor("", def.ValueType, def.Choices, true, line.Value, v =>
        {
            Line.Value = v;
            Edited();
        });
        Extras = def.Extras.Select(extra => new RuleValueEditor(ExtraLabel(extra.Parameter), extra.ValueType, extra.Choices, extra.Required,
            line.Extras.GetValueOrDefault(extra.Parameter), v =>
            {
                Line.Extras[extra.Parameter] = v;
                Edited();
            })).ToArray();
        // A switch-like part ("any attachment is password protected") is there or not; its value is always yes.
        if (def.ValueType == "none")
            line.Value = System.Text.Json.Nodes.JsonValue.Create(true);
    }

    static string ExtraLabel(string parameter) =>
        ExtraLabels.TryGetValue(parameter, out var label) ? label
            : Display.TitleCase(string.Concat(parameter.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString())));

    public RuleLine Line { get; }

    public RulePartDef Def => Line.Def;

    /// <summary>The line as part of the sentence ("the recipient is"); Mark chose sentence flow over Title Case here.</summary>
    public string Label => RuleCodec.Lower(Def.Label);

    /// <summary>The word joining it to the line before ("and", "or"); empty for the first line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnector))]
    public partial string Connector { get; set; } = "";

    public bool HasConnector => Connector.Length > 0;

    public string Group => Def.Group;

    public bool IsAdvanced => Def.Advanced;

    public string? Notes => Def.Notes;

    public bool HasNotes => !string.IsNullOrEmpty(Def.Notes);

    public RuleValueEditor? Main { get; }

    public bool HasMain => Main is not null;

    public IReadOnlyList<RuleValueEditor> Extras { get; }

    public bool HasExtras => Extras.Count > 0;

    public IRelayCommand RemoveCommand { get; }

    /// <summary>The first thing wrong with this line, for the summary of problems.</summary>
    public string? Problem => (Main?.Problem is { } p ? p : null)
                              ?? Extras.Select(e => e.Problem is { } x ? $"{e.Label}: {x}" : null).FirstOrDefault(x => x is not null);

    void Edited()
    {
        OnPropertyChanged(nameof(Problem));
        _changed();
    }
}

/// <summary>One choice in the add-a-part list.</summary>
public sealed record RulePickerItem(RulePartDef Def, bool IsUsed)
{
    /// <summary>As it will read in the sentence, capitalized as a choice in a list.</summary>
    public string Label => Def.Label;
    public bool IsAdvanced => Def.Advanced && Def.IsAvailable;
    public bool CanAdd => Def.IsAvailable && !IsUsed;

    /// <summary>Why it can't be added, or what it does.</summary>
    public string Tip => IsUsed ? "Already in this rule" : Def.UnavailableReason ?? Def.Notes ?? Def.Label;

    public string? Status => IsUsed ? "In Use" : Def.UnavailableReason is not null ? "Not Available" : null;
    public bool HasStatus => Status is not null;
}

/// <summary>A heading in the add-a-part list ("The Sender") and its choices.</summary>
public sealed record RulePickerGroup(string Title, IReadOnlyList<RulePickerItem> Items);

/// <summary>
/// The add-a-condition (or exception, or action) list: everything Exchange Online offers, grouped as
/// the admin center groups it, searchable. Advanced ones are marked, not hidden; ones that can't work
/// in Exchange Online are listed, greyed, with the reason.
/// </summary>
public sealed partial class RulePartPickerViewModel : ObservableObject
{
    readonly RulePartKind _kind;
    readonly Func<RulePartKind, IReadOnlySet<string>> _used;
    readonly Action<RulePartKind, RulePartDef> _add;

    public RulePartPickerViewModel(RulePartKind kind, Func<RulePartKind, IReadOnlySet<string>> used, Action<RulePartKind, RulePartDef> add)
    {
        _kind = kind;
        _used = used;
        _add = add;
        Groups = Build();
    }

    public string Title => _kind switch
    {
        RulePartKind.Condition => "Add a Condition",
        RulePartKind.Exception => "Add an Exception",
        _ => "Add an Action",
    };

    public string SearchHint => _kind == RulePartKind.Action ? "Search actions (redirect, disclaimer, header…)" : "Search conditions (sender, subject, attachment…)";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial IReadOnlyList<RulePickerGroup> Groups { get; private set; }

    public bool HasNoMatches => Groups.Count == 0;

    partial void OnSearchChanged(string value) => Groups = Build();

    /// <summary>Re-reads which parts the rule already has.</summary>
    public void Refresh() => Groups = Build();

    IReadOnlyList<RulePickerGroup> Build()
    {
        var used = _used(_kind);
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var groups = RuleCatalog.Instance.For(_kind)
            .Where(d => words.All(w => d.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || d.Group.Contains(w, StringComparison.OrdinalIgnoreCase)
                                       || d.Parameter.Contains(w, StringComparison.OrdinalIgnoreCase) || (d.Notes?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .GroupBy(d => d.Group)
            .Select(g => new RulePickerGroup(Heading(g.Key),
                g.OrderBy(d => !d.IsAvailable).ThenBy(d => d.Advanced).Select(d => new RulePickerItem(d, used.Contains(Key(d)))).ToArray()))
            .ToArray();
        // Unfiltered, a heading over a single choice is noise: lone ones gather under "Other" at the end.
        var lone = groups.Where(g => g.Items.Count == 1).ToArray();
        if (words.Length > 0 || lone.Length < 2)
            return groups;
        return [.. groups.Where(g => g.Items.Count > 1), new RulePickerGroup("Other", [.. lone.SelectMany(g => g.Items)])];
    }

    /// <summary>A part's identity in a rule: its parameter, plus where a disclaimer goes.</summary>
    public static string Key(RulePartDef def) => def.Parameter + string.Concat(def.Fixed.Values);

    static string Heading(string group) => Display.TitleCase(group);

    /// <summary>Raised after a part is added, so the list that showed it can close.</summary>
    public event EventHandler? Chosen;

    [RelayCommand]
    void Choose(RulePickerItem item)
    {
        if (!item.CanAdd)
            return;
        _add(_kind, item.Def);
        Search = "";
        Chosen?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A starting point for a new rule.</summary>
public sealed record RuleStarter(string Title, string Description, string Icon, Func<RuleDraft> Build);

/// <summary>The starter templates New Rule offers. Every one starts in Test mode, with blanks left for what only the admin knows.</summary>
public static class RuleStarters
{
    static RuleLine Line(RulePartKind kind, string parameter, System.Text.Json.Nodes.JsonNode? value, string? location = null)
    {
        var def = RuleCatalog.Instance.For(kind).First(d => d.Parameter == parameter && (location is null || d.Fixed.GetValueOrDefault("ApplyHtmlDisclaimerLocation") == location));
        return new RuleLine(def, kind) { Value = value };
    }

    static RuleDraft Draft(string name, RuleLine[] conditions, RuleLine[] actions, RuleLine[]? exceptions = null)
    {
        var draft = new RuleDraft { Name = name };
        draft.Conditions.AddRange(conditions);
        draft.Actions.AddRange(actions);
        draft.Exceptions.AddRange(exceptions ?? []);
        return draft;
    }

    const RulePartKind When = RulePartKind.Condition, Then = RulePartKind.Action;

    public static IReadOnlyList<RuleStarter> All { get; } =
    [
        new("Blank Rule", "Start from nothing and add what you need.", "DocumentAdd", () => new RuleDraft()),
        new("Warn About Outside Senders", "Put a warning at the top of mail from outside the organization.", "Warning", () =>
        {
            var warning = Line(Then, "ApplyHtmlDisclaimerText",
                "<p style=\"background:#fff4ce;border:1px solid #c19c00;padding:6px\">This message came from outside the organization. Don't open links or attachments unless you trust the sender.</p>",
                "Prepend");
            warning.Extras["ApplyHtmlDisclaimerFallbackAction"] = "Wrap";
            return Draft("Outside sender warning", [Line(When, "FromScope", "NotInOrganization")], [warning]);
        }),
        new("Block Risky Attachments", "Reject mail carrying file types often used for malware.", "DocumentProhibited", () =>
            Draft("Block risky attachments",
                [Line(When, "AttachmentExtensionMatchesWords", new System.Text.Json.Nodes.JsonArray("exe", "js", "vbs", "scr", "bat", "cmd", "ps1", "jar", "msi", "hta", "iso"))],
                [Line(Then, "RejectMessageReasonText", "This type of attachment isn't accepted. Please send it another way.")])),
        new("Add a Disclaimer", "Add text to the end of mail sent outside the organization.", "TextAddT", () =>
        {
            var disclaimer = Line(Then, "ApplyHtmlDisclaimerText", "<p>This message is confidential and meant only for the people it was sent to.</p>", "Append");
            disclaimer.Extras["ApplyHtmlDisclaimerFallbackAction"] = "Wrap";
            return Draft("Outgoing disclaimer", [Line(When, "FromScope", "InOrganization"), Line(When, "SentToScope", "NotInOrganization")], [disclaimer]);
        }),
        new("Trust a Partner's Mail", "Skip spam filtering for a partner's domain. Use only for domains you control or fully trust.", "ShieldCheckmark", () =>
            Draft("Trusted partner", [Line(When, "SenderDomainIs", null)], [Line(Then, "SetSCL", "-1")])),
        new("Copy Mail to Someone", "Send a hidden copy of mail for certain people to someone else.", "MailCopy", () =>
            Draft("Copy mail", [Line(When, "SentTo", null)], [Line(Then, "BlindCopyTo", null)])),
        new("Approve Mail to a Group", "Hold mail sent to a group until someone approves it.", "PersonAvailable", () =>
            Draft("Approve mail to group", [Line(When, "SentToMemberOf", null)], [Line(Then, "ModerateMessageByUser", null)])),
        new("Block Forwarding Outside", "Reject mail that's automatically forwarded outside the organization.", "ArrowForward", () =>
            Draft("Block outside auto-forwarding", [Line(When, "MessageTypeMatches", "AutoForward"), Line(When, "SentToScope", "NotInOrganization")],
                [Line(Then, "RejectMessageReasonText", "Automatic forwarding outside the organization isn't allowed.")])),
    ];
}
