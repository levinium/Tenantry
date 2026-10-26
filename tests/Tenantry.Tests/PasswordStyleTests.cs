using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;

namespace Tenantry.Tests;

/// <summary>The tenant's password style (Settings › Passwords) and passwords shown after a bulk reset.</summary>
public sealed class PasswordStyleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(12, PasswordLetters.LowerOnly, true, PasswordSymbols.EasyToType, false, "^[a-z0-9!@#$%&*?+=]{12}$")]
    [InlineData(10, PasswordLetters.UpperOnly, true, PasswordSymbols.None, true, "^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{2}$")]
    [InlineData(20, PasswordLetters.Mixed, true, PasswordSymbols.Any, false, @"^\S{20}$")]
    public void Passwords_follow_the_style_and_include_every_kind_chosen(int length, PasswordLetters letters, bool numbers, PasswordSymbols symbols, bool groups, string pattern)
    {
        var style = new PasswordStyle(length, letters, numbers, symbols, groups);
        Assert.Null(style.Problem);

        foreach (var password in Enumerable.Range(0, 200).Select(_ => style.Create()))
        {
            Assert.Matches(pattern, password);
            Assert.DoesNotContain(password, c => "0Oo1lI5S2Z".Contains(c));
            Assert.Null(TemporaryPassword.Problem(password));
            if (letters == PasswordLetters.LowerOnly)
                Assert.Contains(password, char.IsLower);
            if (numbers)
                Assert.Contains(password, char.IsDigit);
            if (symbols == PasswordSymbols.EasyToType)
                Assert.Contains(password, c => "!@#$%&*?+=".Contains(c));
        }
    }

    [Fact]
    public void Styles_Microsoft_365_would_refuse_say_what_to_change()
    {
        Assert.NotNull(new PasswordStyle(Letters: PasswordLetters.LowerOnly, Numbers: true, Groups: false).Problem);
        Assert.Null(new PasswordStyle(Letters: PasswordLetters.LowerOnly, Numbers: true, Groups: true).Problem);
        Assert.NotNull(new PasswordStyle(Letters: PasswordLetters.None, Numbers: false).Problem);
        Assert.NotNull(new PasswordStyle(Length: 6).Problem);
        Assert.Equal("16 characters: upper and lower case letters and numbers, in groups of four.", PasswordStyle.Default.Description);
    }

    [Fact]
    public void The_settings_screen_saves_good_styles_only_and_can_go_back_to_the_default()
    {
        var store = new MemoryPasswordStyleStore();
        var screen = new PasswordStyleViewModel(store);

        screen.Symbols = PasswordStyleViewModel.SymbolChoices.Single(c => c.Value == PasswordSymbols.EasyToType);
        screen.Length = 12;
        Assert.Equal(new PasswordStyle(12, Symbols: PasswordSymbols.EasyToType), store.Load());
        Assert.Matches("^.{14}$", screen.Example);

        screen.Letters = PasswordStyleViewModel.LetterChoices.Single(c => c.Value == PasswordLetters.None);
        screen.Numbers = false;
        screen.Symbols = PasswordStyleViewModel.SymbolChoices.Single(c => c.Value == PasswordSymbols.None);
        Assert.True(screen.HasProblem);
        Assert.Equal(new PasswordStyle(12, Symbols: PasswordSymbols.EasyToType), store.Load()); // the last good style is kept

        screen.UseDefaultCommand.Execute(null);
        Assert.Equal(PasswordStyle.Default, store.Load());
        Assert.True(screen.IsDefault);
    }

    sealed class Resets : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplyResult(ChangeOutcome.Applied, Reveal: "Pw-" + change.Target.Id));
    }

    [Fact]
    public async Task Several_new_passwords_can_be_copied_together()
    {
        var pipeline = new ChangePipeline(new Resets(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(true), "admin");
        var tray = new PendingChangesViewModel(pipeline, "Contoso", false, () => Task.CompletedTask);
        var copied = "";
        Platform.CopyText = text => copied = text;
        try
        {
            foreach (var id in new[] { "u1", "u2" })
                pipeline.Stage(new ResetPassword(new ChangeTarget("user", id, "zz-tenantry-test-" + id, $"zz-tenantry-test-{id}@contoso.example"), true));
            await tray.ApplyCommand.ExecuteAsync(null);
            if (tray.IsConfirming)
                await tray.ApplyCommand.ExecuteAsync(null);

            Assert.True(tray.HasManyReveals);
            tray.CopyAllRevealsCommand.Execute(null);
            Assert.Equal("zz-tenantry-test-u1\tzz-tenantry-test-u1@contoso.example\tPw-u1" + Environment.NewLine +
                         "zz-tenantry-test-u2\tzz-tenantry-test-u2@contoso.example\tPw-u2", copied);
            tray.DismissAllRevealsCommand.Execute(null);
            Assert.False(tray.HasReveals);
        }
        finally
        {
            Platform.CopyText = _ => { };
        }
    }
}
