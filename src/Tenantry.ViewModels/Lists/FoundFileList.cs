using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.FoundFileFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>Find Files' results: newest change first, with where each file is.</summary>
public static class FoundFileList
{
    static readonly ListQuery Newest = ListQuery.Empty with { Sort = [new SortLevel(F.Modified, Descending: true)] };

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        new("all", "All Found Files", Newest, BuiltIn: true, Pinned: true),
        new("largest", "Largest First", ListQuery.Empty with { Sort = [new SortLevel(F.Size, Descending: true)] }, BuiltIn: true, Pinned: true),
    ];

    public static ListDefinition<FoundFile> Definition(Func<FoundFile, object> preview) => new()
    {
        Key = "foundfiles",
        Title = "Found Files",
        Noun = "files",
        Fields = F.All,
        Id = f => $"{f.DriveId}/{f.Id}",
        Views = Views,
        SearchHint = "Narrow the results: names, folders, people…",
        Preview = preview,
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 240, DefaultWidth = 320,
                Text = f => f.Name, Subtitle = f => f.Path, Initials = f => F.TypeOf(f) is var t && t.Length > 0 ? t[..Math.Min(2, t.Length)].ToUpperInvariant() : "?" },
            new() { Key = F.Place, Header = "Site or OneDrive", Cell = CellKind.Text, MinWidth = 160, DefaultWidth = 220, Text = f => f.PlaceTitle },
            new() { Key = F.Type, Header = "Type", Cell = CellKind.Text, MinWidth = 100, DefaultWidth = 120, Text = F.TypeOf },
            new() { Key = F.Modified, Header = "Modified", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = f => f.Modified },
            new() { Key = F.ModifiedBy, Header = "Modified By", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, Text = f => f.ModifiedBy ?? f.ModifiedByEmail },
            new() { Key = F.Size, Header = "Size (MB)", Cell = CellKind.Number, MinWidth = 100, DefaultWidth = 110, Number = f => Math.Round(f.Size / 1_048_576.0, 1) },
            new() { Key = F.Created, Header = "Created", Cell = CellKind.Date, MinWidth = 130, DefaultWidth = 150, Date = f => f.Created, DefaultVisible = false },
            new() { Key = F.CreatedBy, Header = "Created By", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, Text = f => f.CreatedBy ?? f.CreatedByEmail, DefaultVisible = false },
        ],
    };
}
