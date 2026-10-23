using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// The Reset password popup: a generated password (shown once, in the tray, after applying) or one
/// the admin types, checked against Entra's rules as they type; plus "must change at next sign-in".
/// A typed password lives only in this popup and the staged change, never in the change log.
/// </summary>
public sealed partial class ResetPasswordViewModel(ChangeTarget user, ChangePipeline pipeline, Action close) : ObservableObject
{
    public string Explanation =>
        $"{user.Name}'s current password stops working when the change is applied. A generated password is shown to you once afterwards, " +
        "so you can pass it on; it's never stored.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Generate))]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    public partial bool TypeOwn { get; set; }

    public bool Generate
    {
        get => !TypeOwn;
        set => TypeOwn = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial bool MustChange { get; set; } = true;

    /// <summary>What Entra would refuse, said before it's applied; nothing until they start typing.</summary>
    public string? Problem => TypeOwn && Password.Length > 0 ? TemporaryPassword.Problem(Password) : null;

    public bool HasProblem => Problem is not null;

    bool CanStage => !TypeOwn || (Password.Length > 0 && Problem is null);

    [RelayCommand(CanExecute = nameof(CanStage))]
    void Stage()
    {
        pipeline.Stage(new ResetPassword(user, MustChange, TypeOwn ? Password : null));
        Password = "";
        close();
    }

    [RelayCommand]
    void Cancel()
    {
        Password = "";
        close();
    }
}
