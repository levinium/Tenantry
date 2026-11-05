using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using F = Tenantry.Core.Directory.ContactFields;

namespace Tenantry.ViewModels.Lists;

/// <summary>Mail contacts: outside addresses in the address list, with the lists they're on.</summary>
public static class ContactList
{
    static readonly ListQuery ByName = ListQuery.Empty with { Sort = [new SortLevel(F.Name)] };

    static readonly HashSet<string> PinnedByDefault = ["all", "on-lists", "not-on-lists", "hidden"];

    static SavedView View(string id, string name, params Condition[] where) =>
        new(id, name, ByName with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: PinnedByDefault.Contains(id));

    public static IReadOnlyList<SavedView> Views { get; } =
    [
        View("all", "All Contacts"),
        View("on-lists", "On a List", new NumberCondition(F.Lists, 1, null)),
        // Often left over: a contact no list uses any more.
        View("not-on-lists", "Not on Any List", new NumberCondition(F.Lists, null, 0)),
        View("hidden", "Hidden from the Address List", new ChoiceCondition(F.AddressList, [F.HiddenText], [])),
        View("synced", "Synced from On-Premises AD", new ChoiceCondition(F.Source, [F.OnPremises], [])),
    ];

    public static (string Text, BadgeKind Kind) AddressListBadge(ContactSummary c) => c.Hidden switch
    {
        true => (F.HiddenText, BadgeKind.Warning),
        false => (F.Shown, BadgeKind.Neutral),
        null => (F.NotRead, BadgeKind.Neutral),
    };

    public static ListDefinition<ContactSummary> Definition(Func<ContactSummary, object> preview) => new()
    {
        Key = "contacts",
        Title = "Contacts",
        Noun = "contacts",
        Fields = F.All,
        Id = c => c.Id,
        Views = Views,
        SearchHint = "Search names, addresses, companies… or try domain:fabrikam.example lists:0",
        Preview = preview,
        Actions =
        [
            new RowAction<ContactSummary>("Open in Exchange Admin Center", "Globe", _ => Platform.OpenUrl("https://admin.exchange.microsoft.com/#/contacts")),
        ],
        Columns =
        [
            new() { Key = F.Name, Header = "Name", Cell = CellKind.Primary, MinWidth = 220, DefaultWidth = 260,
                Text = c => c.DisplayName,
                Subtitle = c => c.Email,
                Initials = c => Display.Initials(c.DisplayName) },
            new() { Key = F.Company, Header = "Company", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 180, Text = c => c.Company },
            new() { Key = F.Lists, Header = "Lists", Cell = CellKind.Number, MinWidth = 80, DefaultWidth = 90, Number = c => c.Lists.Count },
            new() { Key = F.AddressList, Header = "Address List", Cell = CellKind.Badge, MinWidth = 120, DefaultWidth = 130, Badge = AddressListBadge },
            new() { Key = F.JobTitle, Header = "Job Title", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, Text = c => c.JobTitle },
            new() { Key = F.Phone, Header = "Phone", Cell = CellKind.Text, MinWidth = 130, DefaultWidth = 150, Text = c => c.Phone ?? c.Mobile },
            new() { Key = F.Email, Header = "Email", Cell = CellKind.Text, MinWidth = 200, DefaultWidth = 240, DefaultVisible = false, Text = c => c.Email },
            new() { Key = F.Domain, Header = "Domain", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, DefaultVisible = false, Text = c => F.DomainOf(c.Email) },
            new() { Key = F.Department, Header = "Department", Cell = CellKind.Text, MinWidth = 130, DefaultWidth = 150, DefaultVisible = false, Text = c => c.Department },
            new() { Key = F.Source, Header = "Source", Cell = CellKind.Text, MinWidth = 120, DefaultWidth = 140, DefaultVisible = false,
                Text = c => c.SyncedFromOnPremises ? F.OnPremises : F.Cloud },
        ],
    };
}
