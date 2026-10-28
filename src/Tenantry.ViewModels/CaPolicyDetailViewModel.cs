using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>A choice of a policy's state, as the page's switch shows it.</summary>
public sealed record CaStateChoice(string State, string Label, string Detail)
{
    public static IReadOnlyList<CaStateChoice> All { get; } =
    [
        new("enabled", "On", "Enforced: sign-ins must meet it."),
        new("enabledForReportingButNotEnforced", "Report-Only", "Only recorded in the sign-in log: what it would have done."),
        new("disabled", "Off", "Does nothing."),
    ];
}

/// <summary>
/// A Conditional Access policy's page (8b): the policy in plain words, line by line, with its state
/// switch (On, Report-Only, Off) and Delete, and the JSON Entra keeps for it. Changes go to Pending
/// changes; once applied the page shows the policy as read again.
/// </summary>
public sealed partial class CaPolicyDetailViewModel : TabViewModel, IDisposable
{
    readonly ChangePipeline? _pipeline;
    readonly Action<CaPolicy>? _edit;
    bool _filling;

    /// <param name="edit">Opens the policy in the editor; null when it can't be edited.</param>
    public CaPolicyDetailViewModel(CaPolicyRow row, ChangePipeline? pipeline, Availability canEdit, Action<CaPolicy>? edit = null)
    {
        _pipeline = pipeline;
        _edit = edit;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        Row = row;
        Show(row);
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public CaPolicyRow Row { get; private set; }
    public CaPolicy Policy => Row.Policy;

    public bool CanEdit { get; }
    public string EditReason { get; }

    public override string Title => Policy.Name;

    public string StateText => CaList.StateBadge(Policy).Text;
    public BadgeKind StateKind => CaList.StateBadge(Policy).Kind;

    public string Dates => string.Join(" · ", new[]
    {
        Policy.Created is { } c ? $"Created {Display.Exact(c)}" : null,
        Policy.Modified is { } m ? $"changed {Display.Exact(m)}" : null,
    }.OfType<string>());

    public IReadOnlyList<SummaryLine> Lines => [.. Row.Lines.Select(l => new SummaryLine(l.Label, l.Text))];

    /// <summary>The policy as Entra keeps it, indented, for anyone who wants every detail.</summary>
    public string Json => JsonSerializer.Serialize(JsonDocument.Parse(Policy.Json).RootElement, new JsonSerializerOptions { WriteIndented = true });

    ChangeTarget Target => new("capolicy", Policy.Id, Policy.Name, null);

    [ObservableProperty]
    public partial CaStateChoice? State { get; set; }

    /// <summary>Choosing a state stages it (choosing the current one again takes the change back out).</summary>
    partial void OnStateChanged(CaStateChoice? value)
    {
        if (_filling || value is null || !CanEdit)
            return;
        _pipeline!.Stage(new SetCaPolicyState(Target, Policy.State, value.State));
    }

    public bool CanEditPolicy => CanEdit && _edit is not null;

    [RelayCommand]
    void EditPolicy() => _edit?.Invoke(Policy);

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        _pipeline!.Stage(new DeleteCaPolicy(Target, $"{StateText}; {Row.Requires}"));
    }

    [RelayCommand]
    void OpenInAdminCenter() => Platform.OpenUrl($"https://entra.microsoft.com/#view/Microsoft_AAD_ConditionalAccess/PolicyBlade/policyId/{Policy.Id}");

    /// <summary>What's waiting in Pending changes for this policy.</summary>
    public string? PendingText => _pipeline?.Pending.OfType<CaPolicyChange>().Where(c => c.Policy.Id == Policy.Id).Select(c => $"{c.Label} → {c.After}")
        .ToArray() is { Length: > 0 } labels ? "Pending: " + string.Join(", ", labels) : null;

    public bool HasPending => PendingText is not null;

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
        // Taking the change out (Undo in Pending changes) puts the switch back.
        if (!_pipeline!.Pending.OfType<SetCaPolicyState>().Any(c => c.Policy.Id == Policy.Id) && State?.State != Policy.State)
            SetState(Policy.State);
    }

    void SetState(string state)
    {
        _filling = true;
        State = CaStateChoice.All.FirstOrDefault(c => c.State == state);
        _filling = false;
    }

    void Show(CaPolicyRow row)
    {
        Row = row;
        SetState(row.Policy.State);
        foreach (var name in new[] { nameof(Row), nameof(Policy), nameof(Title), nameof(StateText), nameof(StateKind), nameof(Dates), nameof(Lines), nameof(Json),
                     nameof(PendingText), nameof(HasPending) })
            OnPropertyChanged(name);
    }

    /// <summary>Shows the policy as read again (after a refresh or applied changes).</summary>
    public void Update(CaPolicyRow row) => Show(row);

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
