using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.PowerShell;

namespace Tenantry.ViewModels;

/// <summary>
/// "Show PowerShell": the commands that do what a change does (or a batch of them), to copy or
/// to open in the console, ready to read and run.
/// </summary>
/// <param name="openInConsole">Puts the script in the console's box; null where there's no console.</param>
public sealed partial class PowerShellScriptViewModel(PsScript script, Action<string>? openInConsole) : ObservableObject
{
    public string Text => script.Full;

    public string Runs => $"Runs with {ChangeScripts.ServiceNames(script.Needs)}.";

    /// <summary>Outside Tenantry's console (already signed in), these come first.</summary>
    public string Elsewhere => ChangeScripts.ConnectCommands(script.Needs) is { Count: > 0 } connect
        ? $"Tenantry's console is already signed in. Anywhere else, sign in first with {string.Join(", then ", connect)}."
        : "";

    public bool HasElsewhere => Elsewhere.Length > 0;

    public bool CanOpenInConsole => openInConsole is not null;

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    [RelayCommand]
    void Copy()
    {
        Platform.CopyText(Text);
        Copied = true;
    }

    [RelayCommand]
    void OpenInConsole() => openInConsole?.Invoke(Text);
}
