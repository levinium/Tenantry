using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>What one row of the file will do.</summary>
public enum ImportOutcome
{
    New,
    Update,
    NoChange,
    Problem,
}

/// <summary>One row of the file, as planned: who, what will happen, and what's wrong.</summary>
public sealed record ImportRow(int Line, string SignInName, string Name, ImportOutcome Outcome, IReadOnlyList<string> Changes, IReadOnlyList<string> Problems,
    IReadOnlyList<ChangeOp> Ops)
{
    public string StatusText => Outcome switch
    {
        ImportOutcome.New => "New",
        ImportOutcome.Update => "Update",
        ImportOutcome.NoChange => "No Change",
        _ => "Needs Fixing",
    };

    public BadgeKind StatusKind => Outcome switch
    {
        ImportOutcome.New => BadgeKind.Success,
        ImportOutcome.Update => BadgeKind.Info,
        ImportOutcome.NoChange => BadgeKind.Neutral,
        _ => BadgeKind.Danger,
    };

    public string Details => string.Join("\n", Problems.Count > 0 ? Problems : Changes);
    public bool HasDetails => Details.Length > 0;
}

/// <summary>
/// Import from CSV, using Tenantry's template: each row creates a person (when the sign-in name is
/// new) or updates one (when it isn't): their details, manager, sign-in, password, licenses to add
/// and groups to join. Blank cells leave things as they are; nothing is removed. Every row is
/// checked and shown before anything goes to Pending Changes.
/// </summary>
public sealed partial class ImportUsersViewModel : ObservableObject
{
    /// <summary>The template's columns, in order. Only these are accepted.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        SignInColumn, "First Name", "Last Name", "Display Name", "Password", "Must Change Password", "Sign-In Allowed",
        "Department", "Job Title", "Office", "Mobile Phone", "Employee ID", "Manager", "Country", "Licenses", "Groups",
    ];

    const string SignInColumn = "Sign-In Name";

    /// <summary>Template columns that are Graph text properties, and their labels for Pending Changes.</summary>
    static readonly (string Column, string Property, string Label)[] TextColumns =
    [
        ("First Name", "givenName", "First Name"), ("Last Name", "surname", "Last Name"), ("Display Name", "displayName", "Display Name"),
        ("Department", "department", "Department"), ("Job Title", "jobTitle", "Job Title"), ("Office", "officeLocation", "Office"),
        ("Mobile Phone", "mobilePhone", "Mobile Phone"), ("Employee ID", "employeeId", "Employee ID"),
    ];

    readonly ChangePipeline _pipeline;
    readonly IUserRecords? _records;
    readonly IDirectoryIndex _index;
    readonly Action _close;

    public ImportUsersViewModel(ChangePipeline pipeline, IUserRecords? records, IDirectoryIndex index, Action close)
    {
        _pipeline = pipeline;
        _records = records;
        _index = index;
        _close = close;
    }

    public string ColumnsText => string.Join(", ", Columns);

    public string TemplateHelp =>
        $"Use Tenantry's template. Its columns: {ColumnsText}. Only Sign-In Name is required. Separate several licenses or groups with a semicolon (;). " +
        "Manager is a sign-in name; Country a two-letter code like US.";

    // ---- Template ---------------------------------------------------------------------------------

    /// <summary>Saves the template: just its column headers.</summary>
    [RelayCommand]
    async Task DownloadTemplate()
    {
        try
        {
            await Platform.SaveCsv("Tenantry users template.csv", Csv.Write([Columns]));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FileProblem = FileError("save the template", e);
        }
    }

    /// <summary>
    /// Why a file couldn't be opened or saved. A file open in Excel is the usual reason: Excel
    /// keeps it locked until it's closed there.
    /// </summary>
    internal static string FileError(string what, Exception e) => e is IOException or UnauthorizedAccessException
        ? $"Couldn't {what}: {Friendly.Message(e)} If it's open in Excel or another app, close it there and try again."
        : $"Couldn't {what}: {Friendly.Message(e)}";

    // ---- Reading the file -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    public partial string? FileName { get; private set; }

    public bool HasFile => FileName is not null;

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileProblem))]
    public partial string? FileProblem { get; private set; }

    public bool HasFileProblem => FileProblem is not null;

    public ObservableCollection<ImportRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [RelayCommand]
    async Task ChooseFile()
    {
        (string Name, string Text)? file;
        try
        {
            file = await Platform.OpenCsv();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FileProblem = FileError("open the file", e);
            return;
        }
        if (file is { } chosen)
            await LoadAsync(chosen.Name, chosen.Text);
    }

    /// <summary>Reads and plans a file (the file picker's, or a test's).</summary>
    public async Task LoadAsync(string name, string text)
    {
        FileName = name;
        FileProblem = null;
        Rows.Clear();
        IsReading = true;
        try
        {
            var rows = Csv.Read(text);
            if (rows.Count == 0)
            {
                FileProblem = "The file is empty.";
                return;
            }
            var headers = rows[0].Select(h => h.Trim()).ToArray();
            var unknown = headers.Where(h => h.Length > 0 && !Columns.Contains(h, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (unknown.Length > 0)
            {
                FileProblem = $"These columns aren't in Tenantry's template: {string.Join(", ", unknown)}. Use the template's column names: {ColumnsText}.";
                return;
            }
            if (!headers.Contains(SignInColumn, StringComparer.OrdinalIgnoreCase))
            {
                FileProblem = $"The file needs a \"{SignInColumn}\" column: it says who each row is.";
                return;
            }
            var data = rows.Skip(1).Select((cells, i) => (Line: i + 2, Cells: headers
                .Select((h, c) => (h, c < cells.Count ? cells[c].Trim() : ""))
                .Where(x => x.h.Length > 0)
                .GroupBy(x => x.h, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase))).ToArray();
            foreach (var row in await PlanAsync(data))
                Rows.Add(row);
            var counts = Rows.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
            Summary = string.Join(" · ", new[]
            {
                Count(counts, ImportOutcome.New, "new person", "new people"),
                Count(counts, ImportOutcome.Update, "update", "updates"),
                Count(counts, ImportOutcome.NoChange, "already as the file says", "already as the file says"),
                Count(counts, ImportOutcome.Problem, "row needs fixing", "rows need fixing"),
            }.OfType<string>());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FileProblem = $"Couldn't read the file: {Friendly.Message(e)}";
        }
        finally
        {
            IsReading = false;
            OnPropertyChanged(nameof(AddText));
            AddCommand.NotifyCanExecuteChanged();
        }
    }

    static string? Count(Dictionary<ImportOutcome, int> counts, ImportOutcome outcome, string one, string many) =>
        counts.GetValueOrDefault(outcome) switch { 0 => null, 1 => $"1 {one}", var n => $"{n} {many}" };

    // ---- Planning ---------------------------------------------------------------------------------

    async Task<IReadOnlyList<ImportRow>> PlanAsync(IReadOnlyList<(int Line, Dictionary<string, string> Cells)> data)
    {
        var domains = _records is null ? [] : await _records.GetDomainsAsync();
        var catalog = _records is null ? [] : await _records.GetLicenseCatalogAsync();
        var byAddress = new Dictionary<string, UserSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in _index.AllUsers)
        {
            byAddress.TryAdd(u.UserPrincipalName, u);
            if (u.Mail is { Length: > 0 } mail)
                byAddress.TryAdd(mail, u);
        }
        var groups = new Dictionary<string, List<GroupSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _index.AllGroups)
        {
            foreach (var key in new[] { g.DisplayName, g.Mail }.OfType<string>())
            {
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = [];
                list.Add(g);
            }
        }

        // Existing people's details beyond the list (office, phone…), read a few at a time, only for those the file updates.
        var existing = data.Select(r => byAddress.GetValueOrDefault(r.Cells.GetValueOrDefault(SignInColumn) ?? ""))
            .OfType<UserSummary>().DistinctBy(u => u.Id).ToArray();
        var details = _records is null || existing.Length == 0
            ? new Dictionary<string, UserDetails>()
            : await Workflows.Throttled.ReadAllAsync(existing, 6, (u, ct) => _records.GetDetailsAsync(u.Id, ct), _ => { }, CancellationToken.None);

        var seatsLeft = catalog.ToDictionary(s => s.SkuId, s => s.Available, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<ImportRow>();
        foreach (var (line, cells) in data)
        {
            string? Cell(string column) => cells.TryGetValue(column, out var v) && v.Length > 0 ? v : null;
            var upn = Cell(SignInColumn) ?? "";
            var problems = new List<string>();
            var changes = new List<string>();
            var ops = new List<ChangeOp>();
            if (upn.Length == 0)
            {
                plans.Add(new ImportRow(line, "", "", ImportOutcome.Problem, [], ["No sign-in name."], []));
                continue;
            }
            if (!seen.Add(upn))
                problems.Add($"{upn} is in the file more than once.");
            var person = byAddress.GetValueOrDefault(upn);

            // Yes/No columns.
            bool? YesNo(string column)
            {
                if (Cell(column) is not { } v)
                    return null;
                if (v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1")
                    return true;
                if (v.Equals("no", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0")
                    return false;
                problems.Add($"{column} should be Yes or No, not \"{v}\".");
                return null;
            }
            var mustChange = YesNo("Must Change Password") ?? true;
            var signInAllowed = YesNo("Sign-In Allowed");

            var password = Cell("Password");
            if (password is not null && TemporaryPassword.Problem(password) is { } weak)
                problems.Add($"Password: {weak}");

            string? country = null;
            if (Cell("Country") is { } countryText)
            {
                country = Countries.All.FirstOrDefault(c => string.Equals(c.Code, countryText, StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(c.Name, countryText, StringComparison.OrdinalIgnoreCase)).Code;
                if (country is null)
                    problems.Add($"Country \"{countryText}\" isn't a country Tenantry knows; use a two-letter code like US.");
            }

            UserSummary? manager = null;
            if (Cell("Manager") is { } managerText)
            {
                manager = byAddress.GetValueOrDefault(managerText);
                if (manager is null)
                    problems.Add($"Manager {managerText} isn't anyone in the directory yet (add them first, then import their team).");
            }

            var licenses = new List<LicenseSku>();
            foreach (var name in Split(Cell("Licenses")))
            {
                if (catalog.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } sku)
                    problems.Add($"There's no license called \"{name}\".");
                else if (person?.Licenses.Contains(sku.Name, StringComparer.OrdinalIgnoreCase) != true)
                    licenses.Add(sku);
            }

            var joins = new List<GroupSummary>();
            foreach (var name in Split(Cell("Groups")))
            {
                var found = groups.GetValueOrDefault(name) ?? [];
                if (found.Count == 0)
                    problems.Add($"There's no group called \"{name}\".");
                else if (found.Count > 1)
                    problems.Add($"More than one group is called \"{name}\"; use its email address.");
                else if (found[0].IsDynamic)
                    problems.Add($"{found[0].DisplayName} picks its members by a rule; people can't be added to it.");
                else if (found[0].SyncedFromOnPremises)
                    problems.Add($"{found[0].DisplayName} is synced from on-premises Active Directory; add people to it there.");
                else if (found[0].IsMailbox)
                    problems.Add($"{found[0].DisplayName} is a mailbox; give people access to it from its page instead.");
                else if (person is null || found[0].Members.All(m => m.Id != person.Id))
                    joins.Add(found[0]);
            }

            ImportOutcome outcome;
            string name1;
            if (person is null)
            {
                // A new person.
                var at = upn.IndexOf('@');
                var alias = at > 0 ? upn[..at] : "";
                var domain = at > 0 ? upn[(at + 1)..] : "";
                if (at <= 0 || alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
                    problems.Add($"\"{upn}\" isn't a sign-in name like pat.lee@{domains.FirstOrDefault() ?? "contoso.com"}.");
                else if (domains.Count > 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                    problems.Add($"{domain} isn't one of this tenant's domains.");
                name1 = Cell("Display Name") ?? $"{Cell("First Name")} {Cell("Last Name")}".Trim();
                if (name1.Length == 0)
                    problems.Add("Give a display name, or a first and last name.");
                var usage = country ?? RegionInfo.CurrentRegion.TwoLetterISORegionName;
                var text = TextColumns.Where(t => t.Property is not ("givenName" or "surname" or "displayName") && Cell(t.Column) is not null)
                    .ToDictionary(t => t.Property, t => Cell(t.Column)!, StringComparer.Ordinal);
                ops.Add(new CreateUser(new ChangeTarget("user", upn, name1, upn), Cell("First Name") ?? "", Cell("Last Name") ?? "", alias, usage,
                    signInAllowed ?? true, mustChange,
                    licenses.Select(l => new NewUserLicense(l.SkuId, l.Name)).ToArray(),
                    joins.Select(g => new NewUserGroup(new ChangeTarget("group", g.Id, g.DisplayName, g.Mail), g.IsExchangeManaged)).ToArray(),
                    manager?.Id, manager?.DisplayName, text, password));
                changes.Add(string.Join(" · ", new[]
                {
                    "Create",
                    licenses.Count == 0 ? null : string.Join(", ", licenses.Select(l => l.Name)),
                    joins.Count == 0 ? null : $"In {string.Join(", ", joins.Select(g => g.DisplayName))}",
                    manager is null ? null : $"Reports to {manager.DisplayName}",
                    password is null ? "Generated password" : "Password from the file",
                }.OfType<string>()));
                outcome = ImportOutcome.New;
            }
            else
            {
                // An update: only what the file gives and differs.
                name1 = person.DisplayName;
                var target = GroupAccess.Target(person);
                var known = details.GetValueOrDefault(person.Id);
                foreach (var (column, property, label) in TextColumns)
                {
                    if (Cell(column) is not { } value)
                        continue;
                    var old = property switch
                    {
                        "displayName" => person.DisplayName,
                        "department" => person.Department,
                        "jobTitle" => person.JobTitle,
                        _ => known?[property],
                    };
                    if (!string.Equals(old ?? "", value, StringComparison.Ordinal))
                    {
                        ops.Add(new SetUserText(target, property, label, old, value));
                        changes.Add($"{label}: {(string.IsNullOrEmpty(old) ? "(none)" : old)} → {value}");
                    }
                }
                if (country is not null && !string.Equals(known?["usageLocation"], country, StringComparison.OrdinalIgnoreCase))
                {
                    ops.Add(new SetUserText(target, "usageLocation", "Country", known?["usageLocation"], country));
                    changes.Add($"Country: {Countries.NameOf(known?["usageLocation"]) ?? "(none)"} → {Countries.NameOf(country)}");
                }
                if (manager is not null && known?.ManagerId != manager.Id)
                {
                    ops.Add(new SetManager(target, known?.ManagerId, known?.ManagerName, manager.Id, manager.DisplayName));
                    changes.Add($"Manager: {known?.ManagerName ?? "(none)"} → {manager.DisplayName}");
                }
                if (signInAllowed is { } allowed && allowed != person.AccountEnabled)
                {
                    ops.Add(new SetSignInAllowed(target, person.AccountEnabled, allowed));
                    changes.Add(allowed ? "Sign-in: allowed" : "Sign-in: blocked");
                }
                if (password is not null && person.Kind == UserKind.Guest)
                    problems.Add("Password: guests sign in with their own organization's account, so their password can't be set here.");
                else if (password is not null)
                {
                    ops.Add(new ResetPassword(target, mustChange, password));
                    changes.Add("Password: reset to the one in the file");
                }
                foreach (var sku in licenses)
                {
                    ops.Add(new SetLicenseAssigned(target, sku.SkuId, sku.Name, false, true));
                    changes.Add($"License: add {sku.Name}");
                }
                foreach (var g in joins)
                {
                    ops.Add(new SetGroupMembership(new ChangeTarget("group", g.Id, g.DisplayName, g.Mail), target, "user", GroupRole.Member, g.IsExchangeManaged, false, true));
                    changes.Add($"Join {g.DisplayName}");
                }
                outcome = ops.Count == 0 ? ImportOutcome.NoChange : ImportOutcome.Update;
            }

            // Seats, counted across the whole file.
            foreach (var sku in licenses)
            {
                if (seatsLeft.GetValueOrDefault(sku.SkuId) <= 0)
                    problems.Add($"No {sku.Name} licenses are left for this row.");
                else if (problems.Count == 0)
                    seatsLeft[sku.SkuId]--;
            }
            plans.Add(new ImportRow(line, upn, name1, problems.Count > 0 ? ImportOutcome.Problem : outcome, changes, problems, problems.Count > 0 ? [] : ops));
        }
        return plans;
    }

    static IEnumerable<string> Split(string? cell) =>
        (cell ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- Adding -----------------------------------------------------------------------------------

    int ReadyOps => Rows.Sum(r => r.Ops.Count);

    public string AddText => ReadyOps switch
    {
        0 => "Nothing to Add",
        1 => "Add 1 Change to Pending Changes",
        var n => $"Add {n} Changes to Pending Changes",
    };

    bool CanAdd => ReadyOps > 0 && !IsReading;

    /// <summary>Rows that need fixing are left out; the rest go to Pending Changes together.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        _pipeline.StageAll(Rows.SelectMany(r => r.Ops));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>
/// Add Members from CSV, on a group: a file with a "Sign-In Name" column (sign-in names or email
/// addresses). Each person not yet a member is added; nobody is removed.
/// </summary>
public sealed partial class ImportMembersViewModel(GroupSummary group, ChangePipeline pipeline, IDirectoryIndex index, Action close) : ObservableObject
{
    const string Column = "Sign-In Name";

    public string Explanation =>
        $"Choose a CSV file with a \"{Column}\" column: one sign-in name or email address per row. Everyone not already in {group.DisplayName} is added as a member.";

    [RelayCommand]
    async Task DownloadTemplate()
    {
        try
        {
            await Platform.SaveCsv("Tenantry members template.csv", Csv.Write([[Column]]));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FileProblem = ImportUsersViewModel.FileError("save the template", e);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    public partial string? FileName { get; private set; }

    public bool HasFile => FileName is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileProblem))]
    public partial string? FileProblem { get; private set; }

    public bool HasFileProblem => FileProblem is not null;

    public ObservableCollection<ImportRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [RelayCommand]
    async Task ChooseFile()
    {
        try
        {
            if (await Platform.OpenCsv() is { } file)
                Load(file.Name, file.Text);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FileProblem = ImportUsersViewModel.FileError("open the file", e);
        }
    }

    public void Load(string name, string text)
    {
        FileName = name;
        FileProblem = null;
        Rows.Clear();
        var rows = Csv.Read(text);
        var column = rows.Count == 0 ? -1 : rows[0].Select(h => h.Trim()).ToList().FindIndex(h => string.Equals(h, Column, StringComparison.OrdinalIgnoreCase));
        if (column < 0)
        {
            FileProblem = $"The file needs a \"{Column}\" column.";
            Changed();
            return;
        }
        var byAddress = new Dictionary<string, UserSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in index.AllUsers)
        {
            byAddress.TryAdd(u.UserPrincipalName, u);
            if (u.Mail is { Length: > 0 } mail)
                byAddress.TryAdd(mail, u);
        }
        var members = group.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var target = new ChangeTarget("group", group.Id, group.DisplayName, group.Mail);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (cells, i) in rows.Skip(1).Select((r, i) => (r, i)))
        {
            var address = column < cells.Count ? cells[column].Trim() : "";
            if (address.Length == 0)
                continue;
            var line = i + 2;
            if (byAddress.GetValueOrDefault(address) is not { } person)
                Rows.Add(new ImportRow(line, address, "", ImportOutcome.Problem, [], [$"{address} isn't anyone in the directory."], []));
            else if (group.IsExchangeManaged && string.IsNullOrEmpty(person.Mail) && person.Mailbox is null)
                Rows.Add(new ImportRow(line, address, person.DisplayName, ImportOutcome.Problem, [], [$"{person.DisplayName} has no email address, so Exchange can't add them to a distribution list."], []));
            else if (members.Contains(person.Id) || !seen.Add(person.Id))
                Rows.Add(new ImportRow(line, address, person.DisplayName, ImportOutcome.NoChange, ["Already a member"], [], []));
            else
                Rows.Add(new ImportRow(line, address, person.DisplayName, ImportOutcome.Update, [$"Add to {group.DisplayName}"], [],
                    [new SetGroupMembership(target, GroupAccess.Target(person), "user", GroupRole.Member, group.IsExchangeManaged, false, true)]));
        }
        var adds = Rows.Count(r => r.Outcome == ImportOutcome.Update);
        var problems = Rows.Count(r => r.Outcome == ImportOutcome.Problem);
        Summary = string.Join(" · ", new[]
        {
            adds == 0 ? null : adds == 1 ? "1 person to add" : $"{adds} people to add",
            Rows.Count(r => r.Outcome == ImportOutcome.NoChange) is var already and > 0 ? $"{already} already in" : null,
            problems == 0 ? null : problems == 1 ? "1 row needs fixing" : $"{problems} rows need fixing",
        }.OfType<string>());
        Changed();
    }

    void Changed()
    {
        OnPropertyChanged(nameof(AddText));
        AddCommand.NotifyCanExecuteChanged();
    }

    int Adds => Rows.Sum(r => r.Ops.Count);

    public string AddText => Adds switch
    {
        0 => "Nothing to Add",
        1 => "Add 1 Member to Pending Changes",
        var n => $"Add {n} Members to Pending Changes",
    };

    bool CanAdd => Adds > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        pipeline.StageAll(Rows.SelectMany(r => r.Ops));
        close();
    }

    [RelayCommand]
    void Cancel() => close();
}
