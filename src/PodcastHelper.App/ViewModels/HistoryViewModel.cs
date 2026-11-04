using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Windows.Input;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;
using PodcastHelper.Infrastructure.Export;
using PodcastHelper.Infrastructure.Sessions;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.App.ViewModels;

public sealed record HistoryItem(HistoryEntry Entry)
{
    public string Name => Entry.Name;
    public string Date => Entry.CreatedUtc?.ToLocalTime().ToString("dd MMM yyyy · HH:mm", CultureInfo.InvariantCulture) ?? "Date unavailable";
    public string Status => Entry.Status;
}
public sealed record HistoryDetail(string Time, string Title, string Detail, string Notes = "")
{
    public bool HasNotes => Notes.Length > 0;
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly SessionHistoryStore store;
    private readonly SessionExportService exporter;
    private readonly Session? openSession;
    private readonly List<AsyncRelayCommand> commands = [];
    private HistoryItem? selected;
    private SessionArchive? archive;
    private bool busy, closed;
    private int selectionVersion;
    private string message = "", section = "Segments", exportFolder;
    public bool ShowMouseHint { get; }
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) { Notify(nameof(NotBusy)); foreach (var command in commands) command.Refresh(); } } }
    public bool NotBusy => !IsBusy;
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public string ExportFolder { get => exportFolder; set => SetProperty(ref exportFolder, value); }
    public string[] Sections { get; } = ["Segments", "Moments", "System", "Takes", "Changes"];
    public string Section { get => section; set { if (SetProperty(ref section, value)) RefreshDetails(); } }
    public ObservableCollection<HistoryItem> Sessions { get; } = [];
    public ObservableCollection<HistoryDetail> Details { get; } = [];
    public string Heading => archive?.Session.ScenarioSnapshot.Name ?? selected?.Name ?? "Choose a session";
    public string Summary => archive is null ? selected?.Entry.Error ?? "" : $"{archive.Status} · {DisplayFormats.Time(archive.Session.SessionElapsedMs)} elapsed · {DisplayFormats.Time(archive.Session.ActiveElapsedMs)} active";
    public string Counts => archive is null ? "" : $"Plan {DisplayFormats.Time(archive.Session.ScenarioSnapshot.TargetSeconds * 1000L)} · {archive.Session.Segments.Length} segments · {archive.Session.Moments.Length} {(archive.Session.Moments.Length == 1 ? "moment" : "moments")} · {archive.Session.Takes.Length} {(archive.Session.Takes.Length == 1 ? "take" : "takes")}";
    public HistoryItem? Selected { get => selected; set { if (SetProperty(ref selected, value)) SelectionTask = LoadSelectionAsync(value); } }
    public Task SelectionTask { get; private set; } = Task.CompletedTask;
    public Func<PromptViewModel, Task<int?>>? RequestPrompt { get; set; }
    public Func<string, Task<string?>>? RequestFolder { get; set; }
    public Action<string>? OpenFolder { get; set; }
    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CsvCommand { get; }
    public ICommand XlsCommand { get; }
    public ICommand FolderCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CloseCommand { get; }
    public event Action? CloseRequested;

    public HistoryViewModel(SessionHistoryStore store, SessionExportService exporter, string exportFolder, bool mouse, Session? openSession = null)
    {
        this.store = store; this.exporter = exporter; this.exportFolder = exportFolder; this.openSession = openSession; ShowMouseHint = mouse;
        RefreshCommand = Make(RefreshAsync);
        DeleteCommand = Make(DeleteAsync, () => selected is { Entry.InUse: false });
        CsvCommand = Make(() => ExportAsync(SessionExportFormat.Csv), () => archive is not null);
        XlsCommand = Make(() => ExportAsync(SessionExportFormat.Xls), () => archive is not null);
        FolderCommand = Make(async () => { if (RequestFolder is not null && await RequestFolder(ExportFolder) is { } folder) ExportFolder = folder; });
        OpenFolderCommand = Make(() => { OpenFolder?.Invoke(ExportFolder); return Task.CompletedTask; });
        CloseCommand = new RelayCommand(() => { if (!IsBusy) { closed = true; selectionVersion++; CloseRequested?.Invoke(); } });
    }
    private AsyncRelayCommand Make(Func<Task> action, Func<bool>? allowed = null)
    {
        var command = new AsyncRelayCommand(async () =>
        {
            IsBusy = true;
            try { Message = ""; await action(); }
            catch (ExportLimitException ex) { Message = ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException or ModelValidationException or ArgumentException or InvalidOperationException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            { Message = "Could not complete this action. Check folder access and free space, then refresh or try another export folder. Saved sessions were not overwritten."; }
            finally { IsBusy = false; }
        }, () => !IsBusy && !closed && (allowed?.Invoke() ?? true)); commands.Add(command); return command;
    }
    public async Task RefreshAsync()
    {
        var rows = await Task.Run(() => store.ListAsync()); var previous = selected?.Entry.Id;
        selectionVersion++; archive = null; selected = null; Notify(nameof(Selected)); Sessions.Clear();
        foreach (var row in rows)
        {
            var visible = row.InUse && openSession?.Id == row.Id ? row with { Name = openSession.ScenarioSnapshot.Name, CreatedUtc = openSession.CreatedUtc,
                Status = "Open on main panel", Error = openSession.State == SessionState.Finished ? "Close Session on the main panel to browse its saved history here. You can export directly from the results screen."
                    : "Finish and close this session on the main panel to browse its saved history here." } : row;
            Sessions.Add(new(visible));
        }
        selected = Sessions.FirstOrDefault(x => x.Entry.Id == previous) ?? Sessions.FirstOrDefault(); Notify(nameof(Selected));
        SelectionTask = LoadSelectionAsync(selected); await SelectionTask;
        if (Sessions.Count == 0) Message = "No saved sessions yet. Finished sessions appear here without exporting.";
    }
    private async Task LoadSelectionAsync(HistoryItem? item)
    {
        int version = ++selectionVersion; archive = null; RefreshDetails();
        if (item is null || item.Entry.InUse || item.Entry.Error is not null) return;
        bool wasBusy = IsBusy; IsBusy = true;
        try
        {
            var loaded = await Task.Run(() => store.ReadAsync(item.Entry.Id));
            if (version != selectionVersion || closed) return;
            archive = loaded;
            Message = loaded.Journal.IncompleteTail ? "The last incomplete journal line was skipped. Export includes the last complete saved events."
                : loaded.Status == "Unfinished" ? "Unfinished session · showing the last saved clocks. Reopen PodcastHelper to review recovery." : "";
            RefreshDetails();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException or ModelValidationException)
        { if (version == selectionVersion) Message = "This session is unavailable. Close the other instance or restore its backup, then refresh."; }
        finally { if (version == selectionVersion) IsBusy = wasBusy; }
    }
    private async Task DeleteAsync()
    {
        if (selected is null || RequestPrompt is null) return;
        var item = selected;
        var prompt = new PromptViewModel("Delete this session permanently?", $"Remove “{item.Name}” and its saved history. This cannot be undone. Previously exported files will remain.", ShowMouseHint, "Keep session", "Delete session") { Icon = "trash" };
        if (await RequestPrompt(prompt) != 1) return;
        try { await Task.Run(() => store.DeleteAsync(item.Entry.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { await RefreshAsync(); Message = "Some session files could not be removed. Close other programs using them, restore folder access, then refresh and review what remains."; return; }
        await RefreshAsync(); Message = "Session deleted. Existing exports were kept.";
    }
    private async Task ExportAsync(SessionExportFormat format)
    {
        if (archive is null) return;
        string path = Path.Combine(Path.GetFullPath(ExportFolder), SessionExportService.FileName(archive.Session, format));
        Message = $"Saving {format.ToString().ToUpperInvariant()}…";
        await exporter.ExportAsync(archive, format, path); Message = $"{format.ToString().ToUpperInvariant()} saved";
    }
    private void RefreshDetails()
    {
        Notify(nameof(Heading)); Notify(nameof(Summary)); Notify(nameof(Counts)); foreach (var command in commands) command.Refresh(); Details.Clear();
        if (archive is null) return;
        var s = archive.Session;
        switch (Section)
        {
            case "Segments":
                foreach (var x in s.Segments.Select((segment, i) => (segment, i))) Details.Add(new(DisplayFormats.Time(x.segment.ActiveElapsedMs), $"{x.i + 1:00} · {x.segment.Original.Name}",
                    $"Planned {DisplayFormats.InputDuration(x.segment.Original.OriginalSeconds)} · budget {DisplayFormats.InputDuration(x.segment.BudgetSeconds)}{(x.segment.RemovedFromScenario ? " · removed" : "")}", x.segment.Original.DescriptionMarkdown));
                foreach (var x in s.ScenarioSnapshot.Segments.Concat(archive.Journal.Records.Where(x => x.Command.Segment is not null).Select(x => x.Command.Segment!))
                    .GroupBy(x => x.Id).Select(x => x.Last()).Where(x => s.Segments.All(segment => segment.Original.Id != x.Id)))
                    Details.Add(new("00:00:00", x.Name, "Removed before it started"));
                break;
            case "Moments":
                foreach (var x in s.Moments) Details.Add(new(SessionExportProjection.Time(x.SessionElapsedMs), string.Join(" · ", x.Categories.Select(c => c.Name)),
                    $"Pressed {SessionExportProjection.Time(x.Presses[0].Time.SessionElapsedMs)} · {x.Presses[0].OffsetSeconds}s back · {x.Presses.Length} {(x.Presses.Length == 1 ? "press" : "presses")}"));
                break;
            case "Takes":
                foreach (var x in s.Takes) Details.Add(new(SessionExportProjection.Time(x.Started.SessionElapsedMs), $"Take {x.Number:00}",
                    x.Ended is null ? "No saved end point" : $"Ended {SessionExportProjection.Time(x.Ended.SessionElapsedMs)} · {x.Ended.LocalComputerTimestamp:HH:mm:ss zzz}"));
                break;
            case "Changes":
                foreach (var x in archive.Journal.Records.Where(x => x.Command.Kind is SessionCommandKind.Reorder or SessionCommandKind.AddSegment or SessionCommandKind.RemoveSegment
                    or SessionCommandKind.RemoveCurrent or SessionCommandKind.EditSegment or SessionCommandKind.CorrectMetadata or SessionCommandKind.Undo or SessionCommandKind.Redo or SessionCommandKind.Recovered))
                    Details.Add(new(x.UtcTimestamp.ToString("HH:mm:ss 'UTC'"), x.Command.Kind == SessionCommandKind.CorrectMetadata ? "Recording status corrected" : SessionCommands.Label(x.Command.Kind), x.Command.Segment?.Name ?? (x.UndoOf is { } target
                        ? "Changes to " + SessionCommands.Label(archive.Journal.Records.First(r => r.Id == target).Command.Kind) : "Saved in session history")));
                break;
            default:
                foreach (var x in archive.Journal.Records.SelectMany(x => x.Events)) Details.Add(new(SessionExportProjection.Time(x.Time.SessionElapsedMs), EventName(x.Type),
                    $"{x.Time.LocalComputerTimestamp:HH:mm:ss zzz}{(EventDetail(x).Length == 0 ? "" : " · " + EventDetail(x))}"));
                break;
        }
    }
    private string EventDetail(SessionEvent item)
    {
        if (archive is null) return "";
        if (item.Type is SessionEventType.MomentAdded or SessionEventType.MomentMerged or SessionEventType.MomentRemoved) return "";
        if (item.Type == SessionEventType.TimerStopped) return item.Detail switch { "replaced" => "Replaced by a new timer", "sessionFinished" => "Session finished", "adjustedToZero" => "Reduced to zero", _ => "Stopped manually" };
        if (item.Type == SessionEventType.TimerAdjusted) return item.Detail + " seconds";
        if (item.Type == SessionEventType.PauseAnswered) return item.Detail == "Yes" ? "Recording stopped" : "Recording kept rolling";
        if (item.Type is SessionEventType.Undo or SessionEventType.Redo) return "Saved as a new history entry";
        if (item.Type == SessionEventType.SegmentReordered) return "Running order changed";
        if (Guid.TryParse(item.Detail, out var id)) return archive.Session.Segments.FirstOrDefault(x => x.Original.Id == id)?.Original.Name
            ?? archive.Session.ScenarioSnapshot.Segments.FirstOrDefault(x => x.Id == id)?.Name
            ?? archive.Journal.Records.LastOrDefault(x => x.Command.Segment?.Id == id)?.Command.Segment?.Name ?? "";
        return item.Type == SessionEventType.SessionFinished ? "Session saved" : item.Detail ?? "";
    }
    private static string EventName(SessionEventType type) => type switch
    {
        SessionEventType.TakeStarted => "Take started", SessionEventType.TakeEnded => "Take ended", SessionEventType.SegmentStarted => "Segment started", SessionEventType.SegmentFinished => "Segment finished",
        SessionEventType.TimerStarted => "Timer started", SessionEventType.TimerStopped => "Timer stopped", SessionEventType.TimerFinished => "Timer finished", SessionEventType.TimerAdjusted => "Timer adjusted",
        SessionEventType.SessionFinished => "Session finished", SessionEventType.PauseAnswered => "Recording status answered", SessionEventType.MomentAdded => "Moment added", SessionEventType.MomentMerged => "Moment grouped",
        SessionEventType.MomentRemoved => "Moment removed", SessionEventType.MetadataCorrected => "Recording status corrected", SessionEventType.ScenarioEdited => "Segment edited",
        SessionEventType.SegmentAdded => "Segment added", SessionEventType.SegmentRemoved => "Segment removed", SessionEventType.SegmentReordered => "Segments reordered", _ => type.ToString()
    };
}
