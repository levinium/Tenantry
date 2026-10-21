using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// The Reset password popup: explains what happens, offers "must change at next sign-in", and
/// stages the reset. The temporary password appears once, in the tray, after applying.
/// </summary>
public sealed partial class ResetPasswordViewModel(ChangeTarget user, ChangePipeline pipeline, Action close) : ObservableObject
{
    public string Explanation =>
        $"{user.Name} gets a new temporary password, shown to you once after the change is applied so you can pass it on. " +
        "It is never stored. Their current password stops working at once.";

    [ObservableProperty]
    public partial bool MustChange { get; set; } = true;

    [RelayCommand]
    void Stage()
    {
        pipeline.Stage(new ResetPassword(user, MustChange));
        close();
    }

    [RelayCommand]
    void Cancel() => close();
}
