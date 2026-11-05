using Tenantry.ViewModels.Editing;

namespace Tenantry.Tests;

/// <summary>Adding several at once from an Add popup: ticks, Shift ranges, and one Add for them all.</summary>
public sealed class PickTicksTests
{
    static readonly PickOption[] People =
        [.. new[] { "Ada", "Bo", "Cy", "Di", "Ed" }.Select(n => new PickOption(n.ToLowerInvariant(), n, $"{n}@contoso.example", "user", null, n.ToLowerInvariant()))];

    static (PersonPicker Picker, List<PickOption> Added) NewPicker(bool multiple = true)
    {
        var added = new List<PickOption>();
        var picker = new PersonPicker("type", () => People.Where(p => added.All(a => a.Id != p.Id)), added.Add) { Multiple = multiple };
        picker.Browse();
        return (picker, added);
    }

    [Fact]
    public void A_click_ticks_and_Add_hands_over_everyone_ticked_in_order()
    {
        var (picker, added) = NewPicker();
        var picked = 0;
        picker.Picked += (_, _) => picked++;
        picker.Choose(picker.Rows[2]);
        picker.Choose(picker.Rows[0]);
        Assert.Empty(added);
        Assert.Equal("Add 2", picker.AddTickedText);
        Assert.True(picker.AddTickedCommand.CanExecute(null));

        picker.AddTickedCommand.Execute(null);

        Assert.Equal(["Cy", "Ada"], added.Select(a => a.Name));
        Assert.Equal(1, picked);
        Assert.False(picker.HasTicked);
        Assert.DoesNotContain(picker.Rows, r => r.Option.Name is "Ada" or "Cy");
    }

    [Fact]
    public void Shift_click_ticks_the_range_and_a_second_click_unticks()
    {
        var (picker, _) = NewPicker();
        picker.Choose(picker.Rows[1]);
        picker.Choose(picker.Rows[3], shift: true);
        Assert.Equal(["Bo", "Cy", "Di"], picker.Ticks.Ticked.Select(t => t.Name));
        Assert.All(picker.Rows.Skip(1).Take(3), r => Assert.True(r.IsTicked));

        picker.Choose(picker.Rows[2]);

        Assert.Equal(["Bo", "Di"], picker.Ticks.Ticked.Select(t => t.Name));
        Assert.False(picker.Rows[2].IsTicked);
    }

    [Fact]
    public void Ticks_stay_while_typing_narrows_the_list()
    {
        var (picker, added) = NewPicker();
        picker.Choose(picker.Rows[0]);
        picker.Query = "ed";
        Assert.Single(picker.Rows);
        picker.Choose(picker.Rows[0]);
        picker.Query = "";
        Assert.True(picker.Rows.Single(r => r.Option.Name == "Ada").IsTicked);

        picker.AddTickedCommand.Execute(null);

        Assert.Equal(["Ada", "Ed"], added.Select(a => a.Name));
    }

    [Fact]
    public void Closing_without_adding_forgets_the_ticks()
    {
        var (picker, added) = NewPicker();
        picker.Choose(picker.Rows[0]);
        picker.ClearTicks();
        Assert.False(picker.HasTicked);
        Assert.False(picker.AddTickedCommand.CanExecute(null));
        Assert.All(picker.Rows, r => Assert.False(r.IsTicked));
        Assert.Empty(added);
    }

    [Fact]
    public void Where_only_one_makes_sense_a_click_chooses_it_at_once()
    {
        var (picker, added) = NewPicker(multiple: false);
        picker.Choose(picker.Rows[1], shift: true);
        Assert.Equal(["Bo"], added.Select(a => a.Name));
        Assert.False(picker.HasTicked);
    }
}
