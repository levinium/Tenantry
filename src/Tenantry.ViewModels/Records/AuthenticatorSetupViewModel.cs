using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// Setting up Microsoft Authenticator for someone, with them (in person or on a call). Microsoft
/// doesn't let an admin register the app for a person: they scan its QR code themselves. So this
/// makes a Temporary Access Pass straight away (they're waiting to use it), then shows a QR code
/// their phone opens Security info with, the pass, and the steps. The pass is shown once and never kept.
/// </summary>
public sealed partial class AuthenticatorSetupViewModel(string name, string signInName,
    Func<int, bool, Task<ApplyResult>> createPass, Action close) : ObservableObject
{
    /// <summary>Microsoft's own short link to Security info (mysignins.microsoft.com/security-info).</summary>
    public const string SecurityInfoLink = "https://aka.ms/mysecurityinfo";

    public string Link => SecurityInfoLink;

    public string Intro =>
        $"Microsoft doesn't let an admin add the Authenticator app for someone: {name} scans its QR code on their own phone. " +
        "Tenantry makes them a Temporary Access Pass to sign in with, then shows the steps and a code their phone can scan to get started.";

    public string SignInName => signInName;

    public static IReadOnlyList<string> Lifetimes { get; } = ["1 Hour", "8 Hours", "1 Day"];

    [ObservableProperty]
    public partial string Lifetime { get; set; } = "1 Hour";

    [ObservableProperty]
    public partial bool OneUse { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(CreatePassCommand))]
    public partial string? Pass { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreatePassCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Before the pass: what happens, and how long the pass lasts.</summary>
    public bool IsStarting => Pass is null;

    /// <summary>The pass is made: the QR code, the pass and the steps.</summary>
    public bool IsReady => Pass is not null;

    public string PassNote => $"Valid {Lifetime.ToLowerInvariant()}{(OneUse ? ", for one sign-in" : "")}. Shown here once and never stored; it's gone when this closes.";

    public IReadOnlyList<string> Steps =>
    [
        "On their phone, install Microsoft Authenticator from the App Store or Google Play.",
        "Scan the QR code with the phone's camera. It opens Microsoft's Security info page.",
        $"Sign in as {signInName}. When asked, enter the Temporary Access Pass above.",
        "Choose Add sign-in method, then Microsoft Authenticator, and follow along. On the phone, tap \"Pair your account to the app by clicking this link\".",
        "Approve the test notification in Authenticator. That's it: it shows in their sign-in methods.",
    ];

    [ObservableProperty]
    public partial bool Copied { get; private set; }

    bool CanCreate => !IsWorking && Pass is null;

    /// <summary>Makes the pass now (guarded by Test mode and logged; the pass itself isn't logged).</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    async Task CreatePass()
    {
        IsWorking = true;
        Problem = null;
        try
        {
            var minutes = Lifetime switch { "8 Hours" => 480, "1 Day" => 1440, _ => 60 };
            var result = await createPass(minutes, OneUse);
            if (result.Succeeded && result.Reveal is { } pass)
                Pass = pass;
            else
                Problem = result.Succeeded ? "Microsoft 365 made the pass but didn't return it. Make another from the Temporary Access Pass card."
                    : result.Detail ?? "The pass wasn't made.";
        }
        finally
        {
            IsWorking = false;
        }
        OnPropertyChanged(nameof(PassNote));
    }

    [RelayCommand]
    void CopyPass()
    {
        if (Pass is null)
            return;
        Platform.CopyText(Pass);
        Copied = true;
    }

    [RelayCommand]
    void Close()
    {
        Pass = null;
        close();
    }
}
