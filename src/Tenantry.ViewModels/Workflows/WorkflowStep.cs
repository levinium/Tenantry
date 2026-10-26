using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Workflows;

/// <summary>One line of a workflow's checklist: what it does, and whether it's ticked.</summary>
public sealed partial class WorkflowStep(string key, string title, string detail, bool isOn) : ObservableObject
{
    public string Key => key;
    public string Title => title;
    public string Detail => detail;

    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;
}

/// <summary>A heading in a workflow's checklist and the steps under it.</summary>
public sealed record WorkflowSection(string Title, IReadOnlyList<WorkflowStep> Steps);

/// <summary>
/// What a workflow will stage: the changes in the order they apply, and notes for the admin
/// (steps that don't apply to someone, or something to know before applying).
/// </summary>
public sealed record WorkflowPlan(IReadOnlyList<ChangeOp> Changes, IReadOnlyList<string> Notes);
