namespace Tenantry.Core.Changes;

/// <summary>
/// A mail contact change, made through Exchange (Graph can only read contacts). The target is the
/// contact (kind "contact"), judged by the write guard on its outside address. Hiding a contact
/// from the address list uses <see cref="SetHiddenFromAddressList"/>, and lists it joins or leaves
/// <see cref="SetGroupMembership"/>, as for people.
/// </summary>
public abstract record ContactChange(ChangeTarget Contact) : ChangeOp
{
    public const string Kind = "contact";

    public override ChangeTarget Target => Contact;
}

/// <summary>
/// Makes a mail contact (New-MailContact), then hides it and puts it on its lists in the same step.
/// The write guard checks the new address and every list.
/// </summary>
/// <param name="Contact">Kind "contact": Id and Address are the outside address until it's made.</param>
public sealed record CreateContact(ChangeTarget Contact, string? FirstName, string? LastName, string? Company, bool Hidden,
    IReadOnlyList<NewUserGroup> Lists) : ContactChange(Contact)
{
    public override string Key => "create";
    public override string Label => $"New Contact {Contact.Name}";
    public override string Before => "None";

    public override string After => string.Join(" · ", new[]
    {
        Contact.Address,
        Company,
        Hidden ? "Hidden from the address list" : null,
        Lists.Count switch { 0 => null, 1 => $"On {Lists[0].Group.Name}", var n => $"On {n} lists" },
    }.Where(s => !string.IsNullOrEmpty(s)));

    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
    public override IReadOnlyList<ChangeTarget> Guarded => [Contact, .. Lists.Select(l => l.Group)];
}

/// <summary>
/// Sets one of a contact's details. <see cref="Parameter"/> is the Exchange parameter: Set-MailContact
/// takes DisplayName and ExternalEmailAddress; Set-Contact the rest (FirstName, LastName, Company,
/// Department, Title, Phone, MobilePhone). Changing where its mail goes is a caution change.
/// </summary>
public sealed record SetContactText(ChangeTarget Contact, string Parameter, string ParameterLabel, string? Old, string? New) : ContactChange(Contact)
{
    public const string Address = "ExternalEmailAddress";

    /// <summary>The parameters Set-MailContact takes; the others go to Set-Contact.</summary>
    public static readonly string[] MailContactParameters = ["DisplayName", Address];

    public override string Key => "contact:" + Parameter;
    public override string Label => ParameterLabel;
    public override string Before => Show(Old);
    public override string After => Show(New);
    public override bool IsCaution => Parameter == Address;
    public override bool IsNoChange => string.Equals(Old?.Trim() ?? "", New?.Trim() ?? "", StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetContactText e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes a mail contact (Remove-MailContact): it leaves every list and can't be restored. A caution change.</summary>
public sealed record DeleteContact(ChangeTarget Contact, string Detail) : ContactChange(Contact)
{
    public override string Key => "delete";
    public override string Label => "Delete Contact";
    public override string Before => Detail;
    public override string After => "Deleted (it leaves every list, and can't be restored)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
