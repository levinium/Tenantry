using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A text field on a card that goes to Pending changes when you leave it (no button), showing the pending value until applied.</summary>
public sealed partial class AppField(string label, string placeholder, bool multiline, bool canEdit, Action<string> commit) : ObservableObject
{
    public string Label => label;
    public string Placeholder => placeholder;
    public bool Multiline => multiline;
    public bool CanEdit => canEdit;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    [RelayCommand]
    void Commit()
    {
        if (canEdit)
            commit(Text.Trim());
    }
}

/// <summary>Fields on a card (an app's name and notes).</summary>
public sealed record AppFieldsForm(IReadOnlyList<AppField> Fields);

/// <summary>
/// An Add button that opens a small form: a choice (a platform) and a line of text (an address),
/// checked before Add puts it in Pending changes.
/// </summary>
public sealed partial class AddEntryForm(string addText, string title, IReadOnlyList<EditorChoice> choices, string placeholder,
    Func<string, string, string?> problem, Action<string, string> add) : ObservableObject
{
    public string AddText => addText;
    public string FormTitle => title;
    public IReadOnlyList<EditorChoice> Choices => choices;
    public bool HasChoices => choices.Count > 1;
    public string Placeholder => placeholder;

    [ObservableProperty]
    public partial EditorChoice? Choice { get; set; } = choices.FirstOrDefault();

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [RelayCommand]
    void Open()
    {
        Close();
        IsOpen = true;
    }

    [RelayCommand]
    void Cancel() => Close();

    [RelayCommand]
    void Add()
    {
        var choice = Choice?.Value ?? "";
        Problem = Text.Trim().Length == 0 ? "Type it in first." : problem(choice, Text.Trim());
        if (Problem is not null)
            return;
        add(choice, Text.Trim());
        Close();
    }

    void Close()
    {
        Text = "";
        Problem = null;
        Choice = choices.FirstOrDefault();
        IsOpen = false;
    }
}

/// <summary>One permission an API offers, in the picker, with Add.</summary>
public sealed partial class ApiPermissionChoice(NamedPermission permission, Action<NamedPermission> add)
{
    public NamedPermission Permission => permission;
    public string Name => permission.Name;
    public string? Description => permission.Description;
    public bool HasDescription => !string.IsNullOrEmpty(permission.Description);
    public HealthChip Kind => permission.IsApplication
        ? new HealthChip("Application", BadgeKind.Warning, "The app acts on its own, with no one signed in")
        : new HealthChip("Delegated", BadgeKind.Neutral, "The app acts for whoever signs in, within what they can do");

    [RelayCommand]
    void Add() => add(permission);
}

/// <summary>
/// Add Permission: choose an API, search what it offers (delegated and application permissions),
/// and add one to what the app asks for. The API's list is read when it's chosen.
/// </summary>
public sealed partial class AddPermissionForm : ObservableObject
{
    readonly Func<string, CancellationToken, Task<IReadOnlyList<NamedPermission>>> _read;
    readonly Func<string, bool, bool> _has;
    readonly Action<string, string, NamedPermission> _add;
    IReadOnlyList<NamedPermission> _offered = [];
    CancellationTokenSource? _reading;

    /// <param name="has">Whether the app already asks for (or is adding) a permission: (permission ID, application?).</param>
    /// <param name="add">Adds it: (API app ID, API name, permission).</param>
    public AddPermissionForm(IReadOnlyList<EditorChoice> apis, Func<string, CancellationToken, Task<IReadOnlyList<NamedPermission>>> read, Func<string, bool, bool> has,
        Action<string, string, NamedPermission> add)
    {
        Apis = apis;
        _read = read;
        _has = has;
        _add = add;
        Api = apis.FirstOrDefault();
    }

    public IReadOnlyList<EditorChoice> Apis { get; }

    [ObservableProperty]
    public partial EditorChoice? Api { get; set; }

    partial void OnApiChanged(EditorChoice? value)
    {
        if (IsOpen)
            _ = ReadAsync();
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Fill();

    /// <summary>Delegated, Application, or both.</summary>
    public static IReadOnlyList<EditorChoice> Kinds { get; } = [new("all", "Delegated and Application"), new("delegated", "Delegated"), new("application", "Application")];

    [ObservableProperty]
    public partial EditorChoice Kind { get; set; } = Kinds[0];

    partial void OnKindChanged(EditorChoice value) => Fill();

    public ObservableCollection<ApiPermissionChoice> Matches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The most shown at once; searching narrows the rest.</summary>
    public const int Shown = 40;

    [RelayCommand]
    async Task Open()
    {
        IsOpen = true;
        await ReadAsync();
    }

    [RelayCommand]
    void Close()
    {
        _reading?.Cancel();
        IsOpen = false;
        Search = "";
        Matches.Clear();
    }

    async Task ReadAsync()
    {
        if (Api is not { } api)
            return;
        _reading?.Cancel();
        var reading = _reading = new CancellationTokenSource();
        IsReading = true;
        Note = null;
        Matches.Clear();
        try
        {
            _offered = await _read(api.Value, reading.Token);
            if (reading.IsCancellationRequested)
                return;
            Fill();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _offered = [];
            Note = $"Couldn't read what {api.Label} offers: {Friendly.Message(e)}";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_reading, reading))
                IsReading = false;
        }
    }

    internal void Fill()
    {
        Matches.Clear();
        if (!IsOpen || IsReading && _offered.Count == 0)
            return;
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = _offered
            .Where(p => Kind.Value == "all" || (Kind.Value == "application") == p.IsApplication)
            .Where(p => !_has(p.Id, p.IsApplication))
            .Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || (p.Description?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToArray();
        foreach (var p in found.Take(Shown))
            Matches.Add(new ApiPermissionChoice(p, chosen => _add(Api!.Value, Api.Label, chosen)));
        Note = _offered.Count == 0 ? $"{Api?.Label} offers no permissions to ask for (or they couldn't be read)."
            : found.Length == 0 ? "Nothing matches (permissions it already asks for aren't listed)."
            : found.Length > Shown ? $"Showing {Shown} of {found.Length}: type to narrow them down." : null;
    }
}

/// <summary>A certificate read from a file: what Entra needs (the public part) and what the admin sees.</summary>
public sealed record CertificateFile(string Subject, string Thumbprint, string Base64, DateTimeOffset NotBefore, DateTimeOffset NotAfter)
{
    /// <summary>Reads a .cer/.crt (DER or PEM) or .pem file; a problem in words when it can't be used.</summary>
    public static (CertificateFile? File, string? Problem) Read(byte[] bytes, DateTimeOffset now)
    {
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            return (null, "That file holds a private key. Upload only the certificate (the public part); keep the private key where the app runs.");
        X509Certificate2 certificate;
        try
        {
            certificate = text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (CryptographicException)
        {
            return (null, "That isn't a certificate Tenantry can read. Use a .cer, .crt or .pem file (not a .pfx).");
        }
        using (certificate)
        {
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (notAfter <= now)
                return (null, $"That certificate expired on {notAfter.ToLocalTime():d MMM yyyy}.");
            return (new CertificateFile(certificate.Subject, certificate.Thumbprint, Convert.ToBase64String(certificate.RawData),
                new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero), notAfter), null);
        }
    }
}

/// <summary>Upload Certificate: choose a file; a usable certificate goes to Pending changes.</summary>
public sealed partial class CertificateForm(Func<DateTimeOffset> now, Action<CertificateFile> add) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    [RelayCommand]
    async Task Upload()
    {
        Problem = null;
        if (await Platform.OpenFile("Choose the Certificate (.cer, .crt or .pem)") is not { } file)
            return;
        Use(file.Bytes);
    }

    /// <summary>Reads the file's bytes and stages the certificate, or says why not.</summary>
    public void Use(byte[] bytes)
    {
        var (certificate, problem) = CertificateFile.Read(bytes, now());
        Problem = problem;
        if (certificate is not null)
            add(certificate);
    }
}
