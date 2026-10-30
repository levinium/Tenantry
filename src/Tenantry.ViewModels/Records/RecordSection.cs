using CommunityToolkit.Mvvm.ComponentModel;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// One tab of a record page (Account, Licenses & apps, …). In the preview pane each section is a
/// row with a one-line summary and a Manage button that opens the same section in a popup.
/// </summary>
public abstract partial class RecordSection : ObservableObject
{
    public abstract string Key { get; }

    public abstract string Title { get; }

    /// <summary>A FluentIcons name for the section's tab and row.</summary>
    public abstract string Icon { get; }

    /// <summary>One line for the preview's row ("2 licenses", "Forwarding on").</summary>
    [ObservableProperty]
    public partial string Summary { get; protected set; } = "";

    /// <summary>
    /// The list fields this section's summary shows (comma-separated keys), so its preview row is
    /// tinted when a record check finds one of them changed.
    /// </summary>
    public virtual string FreshKeys => "";

    /// <summary>Called when the section is first shown, to load anything it reads live.</summary>
    public virtual Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// The record changed (changes were applied and read back): sections that read live details
    /// read them again if they had already. The tabs themselves stay put.
    /// </summary>
    public virtual void RecordChanged(Tenantry.Core.Directory.UserSummary user)
    {
    }
}

/// <summary>A section that arrives in a later release: says when, and what it will hold.</summary>
public sealed class PlannedSection : RecordSection
{
    readonly string key;
    readonly string title;
    readonly string icon;
    readonly string release;
    readonly IReadOnlyList<string> features;

    public PlannedSection(string key, string title, string icon, string release, IReadOnlyList<string> features)
    {
        this.key = key;
        this.title = title;
        this.icon = icon;
        this.release = release;
        this.features = features;
        Summary = $"Coming in {release}";
    }

    public override string Key => key;
    public override string Title => title;
    public override string Icon => icon;

    public string Release => release;

    public IReadOnlyList<string> Features => features;

    public string Heading => $"{title} arrives in {release}";

}

/// <summary>The Overview tab: the record's own summary (health, sign-in, licenses, mail).</summary>
public sealed class OverviewSection(object record) : RecordSection
{
    public override string Key => "overview";
    public override string Title => "Overview";
    public override string Icon => "Home";

    /// <summary>The record whose summary this tab shows.</summary>
    public object Record => record;
}

/// <summary>A one-glance flag on a record ("Sign-in blocked", "Forwarding outside the organization").</summary>
public sealed record HealthChip(string Text, BadgeKind Kind, string Tip);

/// <summary>A popup over the window: a title and any record section (or other editor) inside.</summary>
/// <param name="Footnote">The line under the popup; editors remind that changes are staged, Settings needs none.</param>
public sealed record DialogViewModel(string Title, string? Subtitle, object Content, string? Footnote = DialogViewModel.Staged)
{
    public const string Staged = "Changes go to Pending Changes at the bottom of the window; nothing is sent until you apply them.";

    public bool HasFootnote => !string.IsNullOrEmpty(Footnote);
}
