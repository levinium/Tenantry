using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Workflows;

/// <summary>Opens a workflow's popup for one person or several (record pages, the selection panel).</summary>
public interface IWorkflowLauncher
{
    void Offboard(IReadOnlyList<UserSummary> people);

    void RespondToCompromise(IReadOnlyList<UserSummary> people);
}
