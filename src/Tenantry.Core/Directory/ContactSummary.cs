using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>
/// A mail contact: someone outside the organization with an entry in the address list, so they
/// can be put on distribution lists and found in Outlook. Entra keeps the name and details;
/// Exchange keeps the address mail goes to and whether the contact is hidden.
/// </summary>
/// <param name="Id">Entra's object ID (what groups list as a member).</param>
/// <param name="Email">Where mail to the contact goes.</param>
/// <param name="Hidden">Hidden from the address list; null until Exchange has been read.</param>
/// <param name="ListIds">The groups and lists the contact is a direct member of (filled in from the groups).</param>
public sealed record ContactSummary(
    string Id,
    string DisplayName,
    string? Email,
    string? FirstName = null,
    string? LastName = null,
    string? Company = null,
    string? Department = null,
    string? JobTitle = null,
    string? Phone = null,
    string? Mobile = null,
    bool SyncedFromOnPremises = false,
    bool? Hidden = null,
    IReadOnlyList<string>? ListIds = null)
{
    public IReadOnlyList<string> Lists => ListIds ?? [];
}

/// <summary>The Contacts list's fields.</summary>
public static class ContactFields
{
    public const string Name = "name";
    public const string Email = "email";
    public const string Domain = "domain";
    public const string Company = "company";
    public const string Department = "department";
    public const string JobTitle = "title";
    public const string Phone = "phone";
    public const string Lists = "lists";
    public const string AddressList = "addresslist";
    public const string Source = "source";

    public const string Shown = "Shown";
    public const string HiddenText = "Hidden";
    public const string NotRead = "Not Read";
    public const string Cloud = "Cloud";
    public const string OnPremises = "On-Premises AD";

    public static string AddressListText(ContactSummary c) => c.Hidden switch { true => HiddenText, false => Shown, null => NotRead };

    /// <summary>The part after the @ ("fabrikam.example"), for grouping contacts by organization.</summary>
    public static string? DomainOf(string? email) => email?.IndexOf('@') is >= 0 and var at ? email[(at + 1)..].ToLowerInvariant() : null;

    public static IReadOnlyList<Field<ContactSummary>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Text = c => c.DisplayName },
        new() { Key = Email, Label = "Email", Kind = FieldKind.Text, Searchable = true, Aliases = ["mail", "address"], EmptyLabel = "No Address", Text = c => c.Email },
        new() { Key = Domain, Label = "Domain", Kind = FieldKind.Choice, Searchable = true, EmptyLabel = "No Address", Text = c => DomainOf(c.Email) },
        new() { Key = Company, Label = "Company", Kind = FieldKind.Choice, Searchable = true, Aliases = ["organization", "org"], EmptyLabel = "No Company", Text = c => c.Company },
        new() { Key = Department, Label = "Department", Kind = FieldKind.Choice, Searchable = true, Aliases = ["dept"], EmptyLabel = "No Department", Text = c => c.Department },
        new() { Key = JobTitle, Label = "Job Title", Kind = FieldKind.Text, Searchable = true, Aliases = ["jobtitle", "job"], Text = c => c.JobTitle },
        new() { Key = Phone, Label = "Phone", Kind = FieldKind.Text, Searchable = true, Aliases = ["mobile"], Text = c => c.Phone ?? c.Mobile },
        new() { Key = Lists, Label = "Lists", Kind = FieldKind.Number, Aliases = ["groups", "memberof"], Number = c => c.Lists.Count },
        new()
        {
            Key = AddressList, Label = "Address List", Kind = FieldKind.Choice, Aliases = ["hidden", "gal"],
            ChoiceOrder = [Shown, HiddenText, NotRead], Text = AddressListText,
        },
        new() { Key = Source, Label = "Source", Kind = FieldKind.Choice, Aliases = ["synced"], Text = c => c.SyncedFromOnPremises ? OnPremises : Cloud },
    ];
}
