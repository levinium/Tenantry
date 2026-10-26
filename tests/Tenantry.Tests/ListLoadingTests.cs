using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.Tests;

/// <summary>A list shows it's busy while a search or refresh the admin asked for is reading, and stays quiet for background reloads.</summary>
public sealed class ListLoadingTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record Row(string Id, string Name);

    static ListDefinition<Row> Definition => new()
    {
        Key = "rows",
        Title = "Rows",
        Noun = "rows",
        Id = r => r.Id,
        Fields = [new() { Key = "name", Label = "Name", Kind = FieldKind.Text, Searchable = true, Text = r => r.Name }],
        Columns = [new() { Key = "name", Header = "Name", Cell = CellKind.Primary, Text = r => r.Name, Initials = r => "R" }],
    };

    [Fact]
    public async Task A_search_shows_the_wheel_until_its_results_arrive_and_a_background_reload_does_not()
    {
        var reading = new TaskCompletionSource<IReadOnlyList<Row>>();
        var list = DataListViewModel.Create(Definition, _ => reading.Task, _ => { });

        var search = list.ReloadAsync(Ct, busyText: "Searching…");
        Assert.True(list.IsLoading);
        Assert.Equal("Searching…", list.LoadingText);
        Assert.False(list.IsEmpty);

        reading.SetResult([new Row("1", "One")]);
        await search;
        Assert.False(list.IsLoading);
        Assert.Equal(1, list.Total);

        reading = new TaskCompletionSource<IReadOnlyList<Row>>();
        var quiet = list.ReloadAsync(Ct);
        Assert.False(list.IsLoading);
        reading.SetResult([]);
        await quiet;
    }

    [Fact]
    public async Task A_failed_search_clears_the_wheel_and_says_why()
    {
        var list = DataListViewModel.Create(Definition, _ => Task.FromException<IReadOnlyList<Row>>(new InvalidOperationException("Exchange is busy.")), _ => { });

        await list.ReloadAsync(Ct, busyText: "Searching…");

        Assert.False(list.IsLoading);
        Assert.NotNull(list.LoadError);
    }
}
