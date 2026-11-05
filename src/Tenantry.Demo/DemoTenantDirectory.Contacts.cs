using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's mail contacts: people at partner and supplier organizations, a few of them on the
/// Partner Updates list. One is hidden from the address list and one comes from on-premises AD.
/// </summary>
public sealed partial class DemoTenantDirectory
{
    /// <summary>The distribution list the demo's contacts are on.</summary>
    public const string PartnersListId = "grp-partner-updates";

    List<ContactSummary>? _contacts;

    public Task<IReadOnlyList<ContactSummary>> GetContactsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ContactSummary>>((_contacts ??= GenerateContacts()).ToArray());

    public void AddContact(ContactSummary contact) => (_contacts ??= GenerateContacts()).Add(contact);

    /// <summary>Changes one contact (null removes it); false when there's none with that ID.</summary>
    public bool UpdateContact(string id, Func<ContactSummary, ContactSummary?> change)
    {
        var contacts = _contacts ??= GenerateContacts();
        var index = contacts.FindIndex(c => c.Id == id);
        if (index < 0)
            return false;
        if (change(contacts[index]) is { } changed)
            contacts[index] = changed;
        else
            contacts.RemoveAt(index);
        return true;
    }

    static List<ContactSummary> GenerateContacts() =>
    [
        new("obj-c-1", "Avery Quinn", "avery.quinn@fabrikam.example", "Avery", "Quinn", "Fabrikam", "Sales", "Account Manager", "+1 425 555 0141", null, Hidden: false),
        new("obj-c-2", "Jordan Ellis", "jordan.ellis@contoso-legal.example", "Jordan", "Ellis", "Contoso Legal", null, "Attorney", "+1 206 555 0177", "+1 206 555 0190", Hidden: false),
        new("obj-c-3", "Sam Patel", "sam.patel@tailspin.example", "Sam", "Patel", "Tailspin Toys", "Operations", "Operations Lead", null, "+44 20 7946 0321", Hidden: false),
        new("obj-c-4", "Riley Chen", "riley.chen@wingtip.example", "Riley", "Chen", "Wingtip Supplies", "Sales", "Sales Representative", "+1 312 555 0112", null, Hidden: true),
        new("obj-c-5", "Morgan Blake", "morgan.blake@adatum.example", "Morgan", "Blake", "A. Datum", "Audit", "Senior Auditor", null, null, Hidden: false),
        new("obj-c-6", "Casey Rivera", "casey.rivera@fourthcoffee.example", "Casey", "Rivera", "Fourth Coffee", null, "Events Manager", "+1 503 555 0168", null, Hidden: false),
        new("obj-c-7", "Litware Accounts", "accounts@litware.example", null, null, "Litware", "Finance", null, "+1 646 555 0105", null, Hidden: false),
        new("obj-c-8", "Drew Okafor", "drew.okafor@woodgrove.example", "Drew", "Okafor", "Woodgrove Bank", null, "Relationship Manager", "+1 617 555 0133", null,
            SyncedFromOnPremises: true, Hidden: false),
    ];

    /// <summary>Partner Updates: a list of outside contacts, with one person from inside.</summary>
    static GroupSummary PartnersList(DateTimeOffset created, UserSummary? insider) => new(PartnersListId, "Partner Updates", $"partners@{Domain}",
        "News for partner and supplier organizations.", GroupKind.Distribution, false, false, null, created,
        [
            .. new[] { 1, 2, 3, 4, 8 }.Select(i => new GroupMember($"obj-c-{i}", "orgContact")),
            .. insider is null ? Array.Empty<GroupMember>() : [new GroupMember(insider.Id, "user")],
        ]);
}
