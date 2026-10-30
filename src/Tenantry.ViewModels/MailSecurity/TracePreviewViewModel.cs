using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One step of a traced message, as the preview lists it.</summary>
public sealed record TraceStepRow(string When, string Event, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// A message from the trace results in the preview pane, with every step it went through (read when
/// shown). Moving on to another row cancels the read, so arrowing through results doesn't pile them up.
/// </summary>
public sealed partial class TracePreviewViewModel : ObservableObject, IDisposable
{
    readonly IMailSecurity? _records;
    readonly CancellationTokenSource _lifetime = new();

    public TracePreviewViewModel(TracedMessage message, IMailSecurity? records)
    {
        Message = message;
        _records = records;
        _ = LoadAsync();
    }

    public TracedMessage Message { get; }

    public string Subject => Message.Subject;
    public string Sender => Message.Sender;
    public string Recipient => Message.Recipient;
    public string StatusText => MailSecurityLists.StatusText(Message.Status);
    public string ReceivedText => Message.Received.ToLocalTime().ToString("d MMM yyyy, h:mm:ss tt");

    public ObservableCollection<TraceStepRow> Steps { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>Read, and Exchange had no steps for it (yet).</summary>
    [ObservableProperty]
    public partial bool HasNoSteps { get; private set; }

    async Task LoadAsync()
    {
        if (_records is null)
            return;
        IsLoading = true;
        try
        {
            foreach (var e in await _records.TraceDetailAsync(Message, _lifetime.Token))
                Steps.Add(new TraceStepRow(e.Date.ToLocalTime().ToString("h:mm:ss tt"), e.Event, e.Detail.Trim()));
            HasNoSteps = Steps.Count == 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Moved on to another row.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read its steps: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose() => _lifetime.Cancel();
}
