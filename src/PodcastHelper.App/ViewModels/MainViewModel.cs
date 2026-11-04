using System.IO;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows.Input;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Services;
using PodcastHelper.Core.Validation;
using PodcastHelper.Infrastructure.Sessions;
using PodcastHelper.Infrastructure.Storage;
using PodcastHelper.Infrastructure.Time;

namespace PodcastHelper.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private enum AlertLevel { None, Yellow, Red }
    private AlertLevel alertLevel;
    private string alertAdvice = "";
    private long redoRemainingMs;
    private bool sessionStorageAlert;
    private readonly PortableDataStore dataStore;
    private readonly SessionJournalStore? journalStore;
    private readonly PortablePaths? paths;
    private readonly IClock clock;
    private readonly List<AsyncRelayCommand> commands = [];
    private readonly HashSet<Guid> heardSounds = [];
    private ImmutableArray<SessionEvent> events = [];
    private string statusMessage = "Loading settings...";
    private bool isInitialized, isBusy, polling;
    private Session? current;
    private Scenario? selectedScenario;
    private SegmentRow? selectedSegment;
    private TimelineRow? selectedLogRow;
    private string logFilter = "All", lastAction = "No action yet";
    private UndoAssessment? undo;
    public DurableSessionController? Controller { get; private set; }
    public Func<PromptViewModel, Task<int?>>? RequestPrompt { get; set; }
    public Func<ScenarioEditorViewModel, Task<Scenario?>>? RequestEditor { get; set; }
    public Func<SettingsViewModel, Task<AppSettings?>>? RequestSettings { get; set; }
    public event Action? FullScreenRequested;
    public event Action<AppSettings>? TimerEnded;
    public event Action? UndoApplied;
    public string StatusMessage { get => statusMessage; private set { alertLevel = AlertLevel.None; alertAdvice = ""; SetProperty(ref statusMessage, value); RefreshAlert(); } }
    private bool BudgetAlertIsCritical => WarningActive && Budget?.Warning is SegmentWarning.Red or SegmentWarning.Overdue or SegmentWarning.Overscheduled;
    private bool ShowingStatusAlert => alertLevel == AlertLevel.Red || alertLevel == AlertLevel.Yellow && !BudgetAlertIsCritical;
    public string AlertText => ShowingStatusAlert ? StatusMessage : WarningText.Length > 0 ? WarningText : PlanNotice;
    public string AlertAdvice => ShowingStatusAlert ? alertAdvice : !WarningActive
        ? PlannedExcess > 0 ? "Review the scenario timing, or continue with the extra time." : "" : Budget?.Warning switch
    { SegmentWarning.Overscheduled => "Finish with Wrap up, or shorten the upcoming segments.", SegmentWarning.Overdue or SegmentWarning.Red => "Bring this segment to a close, then select Next segment.", SegmentWarning.Yellow => "Prepare to move to the next segment.", _ => "" };
    public bool AlertVisible => AlertText.Length > 0;
    public bool AlertRed => alertLevel == AlertLevel.Red || BudgetAlertIsCritical;
    public bool AlertYellow => !AlertRed && (alertLevel == AlertLevel.Yellow || WarningActive || PlannedExcess > 0);
    public bool AnimateRedAlert => AlertRed && !ReduceAnimations;
    public bool AnimateYellowAlert => AlertYellow && !ReduceAnimations;
    public bool CanRetryStartup => IsStandby && alertLevel == AlertLevel.Red && !IsBusy;
    public bool CanRedo => Usable && redoRemainingMs > 0 && !IsBusy;
    public string RedoLabel => $"Redo · {(redoRemainingMs + 999) / 1000}s";
    private void RefreshAlert()
    { foreach (string name in new[] { nameof(AlertText), nameof(AlertAdvice), nameof(AlertVisible), nameof(AlertRed), nameof(AlertYellow), nameof(AnimateRedAlert), nameof(AnimateYellowAlert), nameof(CanRetryStartup) }) Notify(name); }
    private void SetAlert(string text, bool red, string advice)
    {
        var level = red ? AlertLevel.Red : AlertLevel.Yellow;
        if (statusMessage == text && alertLevel == level && alertAdvice == advice) return;
        statusMessage = text; alertLevel = level; alertAdvice = advice;
        Notify(nameof(StatusMessage)); RefreshAlert();
    }
    public bool IsInitialized { get => isInitialized; private set => SetProperty(ref isInitialized, value); }
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) { RefreshCommands(); Notify(nameof(CanWrap)); Notify(nameof(CanRetryStartup)); Notify(nameof(CanRedo)); } } }
    public AppSettings? Settings { get; private set; }
    public Session? CurrentSession => current;
    public bool SystemAnimationsEnabled { get; set; } = true;
    public bool ReduceAnimations => Settings?.Animations == AnimationPreference.Reduced || Settings?.Animations == AnimationPreference.System && !SystemAnimationsEnabled;
    public bool IsOnAir => current?.State == SessionState.Recording;
    public bool IsFinished => current?.State == SessionState.Finished;
    public bool IsStandby => Controller is null;
    public bool HasSession => Controller is not null;
    public string IdleState => IsOnAir ? "" : DisplayState;
    public bool CanCorrectPause => current?.State == SessionState.Paused && current.Pause?.RecordingStopped is not null;
    public string RecordingChoiceLabel => current?.Pause?.RecordingStopped == true ? "Recording stopped" : "Recording kept rolling";
    public string ReserveCaption => Budget?.ReserveInUse == true ? "Using reserve" : "Reserve";
    public string PauseIcon => PauseLabel == "Pause" ? "pause" : PauseLabel == "Resume" ? "play" : "question";
    private bool checkingRecovery;
    public bool CheckingRecovery { get => checkingRecovery; set { if (SetProperty(ref checkingRecovery, value)) { RefreshCommands(); Notify(nameof(CanWrap)); } } }
    public bool CanWrap => !IsBusy && !CheckingRecovery && Operable;
    public string DisplayState => current?.State switch { SessionState.Recording => "ON AIR", SessionState.Ready => "READY", SessionState.Paused => "PAUSED", SessionState.AwaitingClap => "AWAITING CLAP", SessionState.Finished => "FINISHED", _ => "STANDBY" };
    public string SessionTime => DisplayFormats.Time(current?.SessionElapsedMs ?? 0);
    public string TakeTime => current is null || current.Pause is { RecordingStopped: null } || current.Takes.LastOrDefault()?.Ended is not null || current.Takes.IsEmpty
        ? "--:--:--" : DisplayFormats.Time(current.SessionElapsedMs - current.Takes[^1].Started.SessionElapsedMs + current.Takes[^1].ElapsedAdjustmentMs);
    public string TakeNumber => current?.Takes.LastOrDefault() is { } take ? $"TAKE {take.Number:00}" : "TAKE --";
    public string SegmentTitle => !IsInitialized ? StatusMessage == "Loading settings..." ? "Starting…" : "Cannot load application data"
        : current?.Segments[current.CurrentSegmentIndex].Original.Name ?? selectedScenario?.Segments[0].Name ?? "Choose a scenario";
    public string DescriptionMarkdown => !IsInitialized ? StatusMessage : current?.Segments[current.CurrentSegmentIndex].Original.DescriptionMarkdown ?? selectedScenario?.Segments[0].DescriptionMarkdown ?? "Create a scenario with the **+** button, or choose one from the list.";
    public BudgetProjection? Budget => current is null ? null : BudgetProjector.Project(current);
    private ScenarioBalance? PreviewBalance => selectedScenario is null ? null : ScenarioBudget.Analyze(selectedScenario.TargetSeconds, selectedScenario.Segments.Select(x => x.OriginalSeconds));
    public string PersonalTime => DisplayFormats.Minutes(Budget?.PersonalRemainingMs ?? selectedScenario?.Segments[0].OriginalSeconds * 1000L ?? 0);
    public string ReserveTime => DisplayFormats.Minutes(Budget?.ReserveRemainingMs ?? PreviewBalance?.ReserveSeconds * 1000L ?? 0);
    public string NextSegmentTitle => current is null ? selectedScenario?.Segments.ElementAtOrDefault(1)?.Name ?? "—" : current.CurrentSegmentIndex + 1 < current.Segments.Length
        ? current.Segments[current.CurrentSegmentIndex + 1].Original.Name : "Last segment";
    public string NextSegmentEstimate => Budget is null || IsFinished ? "" : $"≈ {(Budget.EffectiveRemainingMs + 999) / 1000} s";
    private long PlannedExcess => Budget?.PlannedExcessMs ?? PreviewBalance?.PlannedOverageSeconds * 1000L ?? 0;
    public string PlanNotice => PlannedExcess > 0 ? $"{DisplayFormats.Minutes(PlannedExcess)} over target" : "";
    public string WarningText => current?.State == SessionState.Ready ? "Press Enter on the first clap"
        : current?.State == SessionState.AwaitingClap ? "Press Enter on the new clap"
        : current?.State is SessionState.Recording or SessionState.Paused ? Budget?.Warning switch
    {
        SegmentWarning.Overscheduled => "OVERSCHEDULED, WRAP IT UP", SegmentWarning.Overdue => $"Wrap up the segment. Overdue {DisplayFormats.Minutes(Budget.UnfundedOverrunMs)}",
        SegmentWarning.Red => $"Wrap up the segment. {DisplayFormats.Minutes(Budget.EffectiveRemainingMs)} remaining",
        SegmentWarning.Yellow => $"Segment ending. {DisplayFormats.Minutes(Budget.EffectiveRemainingMs)} remaining", _ => ""
    } : "";
    public string WarningColor => current?.State is SessionState.Ready or SessionState.AwaitingClap ? "#EAD49A" : Budget?.Warning is SegmentWarning.Red or SegmentWarning.Overdue or SegmentWarning.Overscheduled ? "#FF6559" : "#FFD166";
    public bool WarningVisible => WarningText.Length > 0;
    public bool WarningActive => current?.State is SessionState.Recording or SessionState.Paused && WarningVisible;
    public bool Overscheduled => Budget?.Warning == SegmentWarning.Overscheduled && WarningActive;
    public string TimerTime => DisplayFormats.Minutes(current?.Timer.RemainingMs ?? 0);
    public string TimerColor => current?.Timer.Running != true ? "#879186" : current.Timer.RemainingMs <= 5000 ? "#FF6559" : current.Timer.RemainingMs <= 15000 ? "#FFD166" : "#B3EC96";
    public string TimerStatus => current?.Timer.Running == true ? current.State == SessionState.Recording ? "RUNNING" : "PAUSED" : "STOPPED";
    public string Preset1Label => DisplayFormats.InputDuration(SessionSettings.TimerPresetSeconds[0]);
    public string Preset2Label => DisplayFormats.InputDuration(SessionSettings.TimerPresetSeconds[1]);
    public string Preset3Label => DisplayFormats.InputDuration(SessionSettings.TimerPresetSeconds[2]);
    private AppSettings SessionSettings => current?.SettingsSnapshot ?? Settings ?? new AppSettings();
    public string PauseLabel => current?.State is null or SessionState.Preparing or SessionState.Ready or SessionState.Recording ? "Pause" : current?.Pause?.RecordingStopped is null && current?.State == SessionState.Paused ? "Recording stopped?" : "Resume";
    public string LastAction => $"Last action: {lastAction}";
    public string UndoLabel => undo is null ? "Undo" : $"Undo · {undo.Action}";
    public string UndoTooltip => undo is { CanApply: false } ? "Undo is blocked by later segment changes. Undo the newer changes first." : UndoLabel;
    public string ResultsSummary => current is null ? "" : $"{SessionTime} elapsed · {DisplayFormats.Time(current.ActiveElapsedMs)} active · {current.Takes.Length} {(current.Takes.Length == 1 ? "take" : "takes")} · {current.Moments.Length} {(current.Moments.Length == 1 ? "moment" : "moments")}";
    public void DetachAfterExit() => Controller = null;
    public ObservableCollection<Scenario> Scenarios { get; } = [];
    public ObservableCollection<SegmentRow> SegmentRows { get; } = [];
    public ObservableCollection<TimelineRow> TimelineRows { get; } = [];
    public ObservableCollection<MomentButton> MomentButtons { get; } = [];
    public string[] LogFilters { get; } = ["All", "Moments", "System"];
    public Scenario? SelectedScenario { get => selectedScenario; set { if (SetProperty(ref selectedScenario, value)) { SyncStandbySegments(); RefreshPresentation(); } } }
    public SegmentRow? SelectedSegment { get => selectedSegment; set { if (SetProperty(ref selectedSegment, value)) RefreshCommands(); } }
    public TimelineRow? SelectedLogRow { get => selectedLogRow; set { if (SetProperty(ref selectedLogRow, value)) RefreshCommands(); } }
    public string LogFilter { get => logFilter; set { if (SetProperty(ref logFilter, value)) RebuildTimeline(); } }
    public ICommand NewScenarioCommand { get; }
    public ICommand EditScenarioCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ReadyCommand { get; }
    public ICommand ClapCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CorrectPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand RetryStartupCommand { get; }
    public ICommand TimerPreset1Command { get; }
    public ICommand TimerPreset2Command { get; }
    public ICommand TimerPreset3Command { get; }
    public ICommand TimerStopCommand { get; }
    public ICommand TimerPlusCommand { get; }
    public ICommand TimerMinusCommand { get; }
    public ICommand TimerCustomCommand { get; }
    public ICommand DeleteMomentCommand { get; }
    public ICommand AddSegmentCommand { get; }
    public ICommand EditSegmentCommand { get; }
    public ICommand RemoveSegmentCommand { get; }
    public ICommand SegmentUpCommand { get; }
    public ICommand SegmentDownCommand { get; }
    public ICommand CloseSessionCommand { get; }
    public ICommand SaveScenarioChangesCommand { get; }
    public ICommand FullScreenCommand { get; }

    public MainViewModel(PortableDataStore dataStore, SessionJournalStore? journalStore = null, PortablePaths? paths = null, IClock? clock = null)
    {
        this.dataStore = dataStore; this.journalStore = journalStore; this.paths = paths; this.clock = clock ?? new SystemClock();
        NewScenarioCommand = Make(() => EditSourceAsync(true), () => IsInitialized && IsStandby);
        EditScenarioCommand = Make(() => EditSourceAsync(false), () => IsInitialized && IsStandby && SelectedScenario is not null);
        SettingsCommand = Make(EditSettingsAsync, () => IsInitialized);
        ReadyCommand = Make(StartSessionAsync, () => IsInitialized && IsStandby && SelectedScenario is not null && journalStore is not null);
        ClapCommand = Make(OpenClapAsync, () => Usable && current?.State is SessionState.Ready or SessionState.Recording or SessionState.AwaitingClap);
        PauseCommand = Make(PauseOrResumeAsync, () => Usable && current?.State is SessionState.Recording or SessionState.Paused);
        CorrectPauseCommand = Make(CorrectPauseAsync, () => Usable && current?.State == SessionState.Paused && current.Pause?.RecordingStopped is not null);
        NextCommand = Make(() => CommitAsync(new(SessionCommandKind.NextSegment)), () => Operable);
        UndoCommand = Make(UndoAsync, () => Usable && undo?.CanApply == true);
        RedoCommand = Make(async () => { if (Controller is null) return; await Controller.RedoAsync(); lastAction = "Redo"; await PollAsync(); }, () => Usable && redoRemainingMs > 0);
        RetryStartupCommand = Make(async () => { Scenarios.Clear(); SelectedScenario = null; await InitializeAsync(); }, () => IsStandby && alertLevel == AlertLevel.Red);
        TimerPreset1Command = Make(() => CommitAsync(new(SessionCommandKind.TimerStart) { Seconds = SessionSettings.TimerPresetSeconds[0] }), () => Operable);
        TimerPreset2Command = Make(() => CommitAsync(new(SessionCommandKind.TimerStart) { Seconds = SessionSettings.TimerPresetSeconds[1] }), () => Operable);
        TimerPreset3Command = Make(() => CommitAsync(new(SessionCommandKind.TimerStart) { Seconds = SessionSettings.TimerPresetSeconds[2] }), () => Operable);
        TimerStopCommand = Make(() => CommitAsync(new(SessionCommandKind.TimerStop)), () => Operable && current?.Timer.Running == true);
        TimerPlusCommand = Make(() => CommitAsync(new(SessionCommandKind.TimerAdjust) { Seconds = 15 }), () => Operable);
        TimerMinusCommand = Make(() => CommitAsync(new(SessionCommandKind.TimerAdjust) { Seconds = -15 }), () => Operable);
        TimerCustomCommand = Make(CustomTimerAsync, () => Operable);
        DeleteMomentCommand = Make(() => CommitAsync(new(SessionCommandKind.RemoveMoment) { EntityId = SelectedLogRow!.MomentId!.Value }), () => Operable && SelectedLogRow?.MomentId is not null);
        AddSegmentCommand = Make(() => EditUpcomingAsync(true), () => Operable);
        EditSegmentCommand = Make(() => EditUpcomingAsync(false), () => Operable && SelectedSegment?.Index > current!.CurrentSegmentIndex);
        RemoveSegmentCommand = Make(RemoveSegmentAsync, () => Operable && SelectedSegment?.Index >= current!.CurrentSegmentIndex);
        SegmentUpCommand = Make(() => MoveUpcomingAsync(-1), () => Operable && SelectedSegment?.Index > current!.CurrentSegmentIndex + 1);
        SegmentDownCommand = Make(() => MoveUpcomingAsync(1), () => Operable && SelectedSegment?.Index > current!.CurrentSegmentIndex && SelectedSegment.Index < current.Segments.Length - 1);
        CloseSessionCommand = Make(CloseSessionAsync, () => Usable && IsFinished);
        SaveScenarioChangesCommand = Make(SaveScenarioChangesAsync, () => Usable && IsFinished);
        FullScreenCommand = new RelayCommand(() => FullScreenRequested?.Invoke());
    }
    private bool Usable => Controller is not null && !Controller.IsFaulted && !Controller.NeedsRecovery && Controller.SnapshotError is null;
    private bool Operable => Usable && current?.State is SessionState.Recording or SessionState.Paused or SessionState.AwaitingClap
        && current.Pause is not { RecordingStopped: null };
    private AsyncRelayCommand Make(Func<Task> action, Func<bool> canExecute)
    {
        var command = new AsyncRelayCommand(() => RunActionAsync(action), () => !IsBusy && !CheckingRecovery && canExecute()); commands.Add(command); return command;
    }
    public void ReportSessionStatus(string message, bool isError = false, string? advice = null)
    {
        if (message == "Saved") StatusMessage = message;
        else SetAlert(message, isError, advice ?? (message.Contains("sound", StringComparison.OrdinalIgnoreCase)
            ? "Select Windows or Off for the timer sound in Settings, or choose a valid MP3." : "Check the recording status and clocks before continuing."));
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Доступ к диску и JSON не занимают UI-поток, включая синхронную часть файловых API.
            Settings = await Task.Run(() => dataStore.InitializeSettingsAsync());
            IsInitialized = true;
            var catalog = await dataStore.ListScenariosAsync();
            foreach (var scenario in catalog.Scenarios) Scenarios.Add(scenario);
            SelectedScenario = Scenarios.FirstOrDefault(); RebuildMomentButtons();
            if (catalog.Unreadable > 0) SetAlert("Some scenarios could not be read. Their files were retained.", false, "Choose another scenario, or restore the affected scenario from a backup.");
            else StatusMessage = "Settings loaded. Choose a scenario or create one.";
        }
        catch (UnsupportedSchemaException)
        {
            IsInitialized = false; SetAlert("Settings use an unsupported format version. The file was not changed.", true, "Open this folder with the newer PodcastHelper version that saved it.");
        }
        catch (Exception ex) when (ex is InvalidDocumentException or ModelValidationException)
        {
            IsInitialized = false; SetAlert("Settings are damaged or invalid. The file was not changed.", true, "Restore the settings from your backup, then select Try again. Keep the original file for recovery.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            IsInitialized = false; SetAlert("Cannot access application data. Use a writable application folder.", true, "Move the entire PodcastHelper folder to a location you can write to, then reopen it. If you fixed permissions, select Try again.");
        }
        RefreshCommands(); RefreshPresentation();
    }

    public async Task AttachControllerAsync(DurableSessionController controller)
    {
        sessionStorageAlert = false;
        Controller = controller; events = await controller.ReadEventsAsync();
        foreach (var item in events) heardSounds.Add(item.Id);
        current = await controller.ReadCurrentAsync(); undo = await controller.AssessUndoAsync();
        redoRemainingMs = await controller.ReadRedoRemainingMsAsync();
        var last = events.LastOrDefault(x => x.Type != SessionEventType.TimerFinished);
        if (last is not null)
        {
            var moment = current.Moments.LastOrDefault();
            lastAction = last.Type is SessionEventType.MomentAdded or SessionEventType.MomentMerged && moment is not null
                ? "Moment – " + string.Join(", ", moment.Categories.Select(x => x.Name)) : EventLabel(last);
        }
        RebuildMomentButtons(); SyncSegments(); RebuildTimeline(); RefreshPresentation();
    }
    public async Task PollAsync()
    {
        if (polling || Controller is null) return;
        polling = true;
        try
        {
            current = await Controller.ReadCurrentAsync();
            redoRemainingMs = await Controller.ReadRedoRemainingMsAsync();
            var latest = await Controller.ReadEventsAsync();
            bool changed = latest.Length != events.Length;
            if (changed)
            {
                foreach (var item in latest)
                    if (heardSounds.Add(item.Id) && item.Type == SessionEventType.TimerFinished) TimerEnded?.Invoke(SessionSettings);
                events = latest; undo = await Controller.AssessUndoAsync(); RebuildTimeline();
            }
            if (Controller.IsFaulted || Controller.SnapshotError is not null || Controller.BackgroundError is not null)
            {
                sessionStorageAlert = true;
                SetAlert("Session saving failed. Your last saved events are retained.", true, "Free disk space and restore write access to the application folder. Reopen PodcastHelper to recover the session before continuing.");
            }
            else if (sessionStorageAlert)
            {
                sessionStorageAlert = false;
                SetAlert("Saving is working again.", false, "Review the clocks, then continue.");
            }
            SyncSegments(); RefreshPresentation();
        }
        catch (Exception ex) { await ReportFailureAsync(ex); }
        finally { polling = false; }
    }
    private async Task RunActionAsync(Func<Task> action)
    {
        if (IsBusy || CheckingRecovery) return; IsBusy = true;
        try { await action(); }
        catch (Exception ex) { await ReportFailureAsync(ex); }
        finally { IsBusy = false; await PollAsync(); RefreshCommands(); Notify(nameof(LastAction)); }
    }
    private async Task ReportFailureAsync(Exception ex)
    {
        if (ex is ModelValidationException or FormatException or InvalidOperationException)
            SetAlert(ex.Message, false, "Check the selected action and current recording status, then try again.");
        else SetAlert("The action could not be saved. Previously saved events are retained.", true, "Check free disk space and write permissions. Reopen PodcastHelper and recover the session.");
        if (paths is null) return;
        try { Directory.CreateDirectory(paths.LogsDirectory); await File.AppendAllTextAsync(Path.Combine(paths.LogsDirectory, "operations.log"), $"{DateTimeOffset.UtcNow:O} {ex.GetType().Name}\n"); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
    private async Task CommitAsync(SessionCommand command)
    {
        if (Controller is null) return;
        var result = await Controller.ExecuteAsync(command);
        current = result.Transition.Session; lastAction = SessionCommands.Label(command.Kind);
        if (command.Kind == SessionCommandKind.AddMoment) lastAction = "Moment – " + SessionSettings.Categories.First(x => x.Id == command.CategoryId).Name;
        if (result.SnapshotSaved) StatusMessage = "Saved";
        else SetAlert("Event saved. The recovery snapshot could not be updated.", true, "Free disk space and restore write access to the application folder before continuing.");
        await PollAsync();
    }
    private Task<int?> PromptAsync(PromptViewModel vm) => RequestPrompt?.Invoke(vm) ?? Task.FromResult<int?>(null);
    private async Task EditSourceAsync(bool create)
    {
        if (RequestEditor is null) return;
        var edited = await RequestEditor(new(create ? null : SelectedScenario, Settings?.HasMacropad == true, heading: create ? "New scenario" : "Edit scenario"));
        if (edited is null) return;
        await dataStore.SaveScenarioAsync(edited); UpsertScenario(edited); StatusMessage = "Scenario saved.";
    }
    private void UpsertScenario(Scenario scenario)
    {
        var previous = Scenarios.FirstOrDefault(x => x.Id == scenario.Id); if (previous is not null) Scenarios.Remove(previous);
        Scenarios.Add(scenario); SelectedScenario = scenario;
    }
    private async Task EditSettingsAsync()
    {
        if (Settings is null || RequestSettings is null) return;
        var vm = new SettingsViewModel(Settings);
        if (paths is not null && Directory.Exists(paths.SoundsDirectory))
            foreach (string file in await Task.Run(() => Directory.GetFiles(paths.SoundsDirectory, "*.mp3"))) vm.AvailableSounds.Add(Path.GetFileName(file));
        var edited = await RequestSettings(vm); if (edited is null) return;
        await dataStore.SaveSettingsAsync(edited); Settings = edited; RebuildMomentButtons(); RefreshPresentation(); StatusMessage = "Settings saved. Active session settings are retained.";
    }
    private async Task StartSessionAsync()
    {
        if (SelectedScenario is null || Settings is null || journalStore is null) return;
        var created = await DurableSessionController.CreateAsync(journalStore, new SessionFactory(clock).Create(SelectedScenario, Settings), clock);
        await AttachControllerAsync(created); await CommitAsync(new(SessionCommandKind.Ready)); StatusMessage = "Ready. Press Enter on the first clap.";
    }
    public Task PhysicalEnterAsync()
    {
        var command = new SessionCommand(SessionCommandKind.Clap) { InputTimestamp = clock.GetTimestamp(), PressedUtc = clock.UtcNow, PressedOffset = clock.LocalNow.Offset };
        return RunActionAsync(async () => { if (current?.State is SessionState.Ready or SessionState.AwaitingClap) await CommitAsync(command); });
    }
    private async Task OpenClapAsync()
    {
        await CommitAsync(new(SessionCommandKind.OpenClap));
        var prompt = OperatorPrompts.Clap(Settings?.HasMacropad == true, current!.State, current.Takes.Length);
        int? selected = await PromptAsync(prompt);
        await CommitAsync(new(selected == 1 ? SessionCommandKind.Clap : SessionCommandKind.CancelClap)
            { InputTimestamp = prompt.EnterTimestamp, PressedUtc = prompt.EnterUtc, PressedOffset = prompt.EnterOffset });
    }
    private async Task PauseOrResumeAsync()
    {
        if (current?.State == SessionState.Recording) await CommitAsync(new(SessionCommandKind.Pause));
        if (current?.State == SessionState.Paused && current.Pause?.RecordingStopped is null)
        {
            int? selected = await PromptAsync(OperatorPrompts.Pause(Settings?.HasMacropad == true));
            if (selected is not null) await CommitAsync(new(SessionCommandKind.AnswerPause, selected == 0));
            return;
        }
        await CommitAsync(new(SessionCommandKind.Resume));
        if (current?.State == SessionState.AwaitingClap) await OpenClapAsync();
    }
    public Task WrapUpFromMouseAsync() => RunActionAsync(async () =>
    {
        if (!Operable) return;
        int? result = await PromptAsync(OperatorPrompts.Finish(Settings?.HasMacropad == true, current!.Takes.Length));
        if (result == 1) await CommitAsync(new(SessionCommandKind.WrapUp));
    });
    private async Task UndoAsync()
    {
        if (Controller is null) return;
        var assessment = await Controller.AssessUndoAsync(); if (assessment is null) return;
        if (!assessment.CanApply) { SetAlert("Undo is not available for this action.", false, "Undo the later segment changes first. All saved actions have been kept."); return; }
        await Controller.UndoAsync(assessment.CommandId, true); lastAction = $"Undo – {assessment.Action}"; await PollAsync(); UndoApplied?.Invoke();
    }
    private async Task CorrectPauseAsync()
    {
        if (Controller is null) return;
        int? answer = await PromptAsync(OperatorPrompts.Pause(Settings?.HasMacropad == true, true, current?.Pause?.RecordingStopped));
        if (answer is 1 or 2) { await Controller.CorrectPauseAnswerAsync(answer == 1); lastAction = "Recording status changed"; await PollAsync(); }
    }
    private async Task CustomTimerAsync()
    {
        var prompt = OperatorPrompts.Timer(Settings?.HasMacropad == true, current?.Timer.Running == true);
        if (await PromptAsync(prompt) == 1) await CommitAsync(new(SessionCommandKind.TimerStart) { Seconds = prompt.DurationSeconds() });
    }
    private async Task EditUpcomingAsync(bool add)
    {
        if (current is null || RequestEditor is null) return;
        var segment = add ? new Segment { Name = "New segment", OriginalSeconds = 300 } : SelectedSegment!.Segment;
        var source = new Scenario { Name = "Upcoming segment", TargetSeconds = segment.OriginalSeconds, ModifiedUtc = clock.UtcNow, Segments = [segment] };
        var result = await RequestEditor(new(source, Settings?.HasMacropad == true, false, add ? "Add segment" : "Edit segment")); if (result is null) return;
        await CommitAsync(new(add ? SessionCommandKind.AddSegment : SessionCommandKind.EditSegment) { Segment = result.Segments[0] });
    }
    private async Task RemoveSegmentAsync()
    {
        if (current is null || SelectedSegment is null) return;
        if (SelectedSegment.Index == current.CurrentSegmentIndex)
        {
            bool last = current.CurrentSegmentIndex == current.Segments.Length - 1;
            string spent = DisplayFormats.Minutes(current.Segments[current.CurrentSegmentIndex].ActiveElapsedMs);
            int? choice = await PromptAsync(OperatorPrompts.Remove(Settings?.HasMacropad == true, SelectedSegment.Label,
                $"The {spent} already spent on this segment stays in session history. " + (last ? "This is the last segment, so removing it will finish the session." : $"Continue with “{NextSegmentTitle}”."), last ? "Remove & finish" : "Remove & continue"));
            if (choice == 1) await CommitAsync(new(SessionCommandKind.RemoveCurrent));
        }
        else if (await PromptAsync(OperatorPrompts.Remove(Settings?.HasMacropad == true, SelectedSegment.Label,
            $"Return its {SelectedSegment.Duration} to reserve for the other segments.", "Remove segment")) == 1)
            await CommitAsync(new(SessionCommandKind.RemoveSegment) { EntityId = SelectedSegment.Id });
    }
    private async Task MoveUpcomingAsync(int delta)
    {
        if (current is null || SelectedSegment is null) return;
        var order = current.Segments.Skip(current.CurrentSegmentIndex + 1).Select(x => x.Original.Id).ToList();
        int from = order.IndexOf(SelectedSegment.Id), to = from + delta;
        if (from < 0 || to < 0 || to >= order.Count) return;
        (order[from], order[to]) = (order[to], order[from]); await CommitAsync(new(SessionCommandKind.Reorder, Order: order.ToImmutableArray()));
    }
    private async Task SaveScenarioChangesAsync()
    {
        if (current is null || RequestEditor is null) return;
        var surviving = current.Segments.Where(x => !x.RemovedFromScenario).Select(x => x.Original with
        { OriginalSeconds = x.ActiveElapsedMs > 0 ? (int)Math.Min(int.MaxValue, Math.Max(1, (x.ActiveElapsedMs + 999) / 1000)) : x.Original.OriginalSeconds }).ToImmutableArray();
        var proposal = current.ScenarioSnapshot with { Segments = surviving };
        var edited = await RequestEditor(new(proposal, Settings?.HasMacropad == true, heading: "Review scenario changes")); if (edited is null) return;
        if (await PromptAsync(OperatorPrompts.SaveScenario(Settings?.HasMacropad == true, edited.Name)) != 1) return;
        await dataStore.SaveScenarioAsync(edited); UpsertScenario(edited); StatusMessage = "Scenario changes saved after confirmation.";
    }
    private async Task CloseSessionAsync()
    {
        if (Controller is null) return;
        await Controller.CloseResultsAsync(); await Controller.DisposeAsync(); Controller = null; current = null; events = []; undo = null;
        SegmentRows.Clear(); TimelineRows.Clear(); RebuildMomentButtons(); SyncStandbySegments(); lastAction = "Close Session"; RefreshPresentation(); StatusMessage = "Session saved.";
    }
    private void RebuildMomentButtons()
    {
        foreach (var previous in MomentButtons) if (previous.Command is AsyncRelayCommand old) commands.Remove(old);
        MomentButtons.Clear();
        foreach (var category in SessionSettings.Categories)
            MomentButtons.Add(new(category.Id, category.Name, DisplayFormats.Icon(category.Icon), Make(() => CommitAsync(new(SessionCommandKind.AddMoment) { CategoryId = category.Id }), () => Usable && current?.State == SessionState.Recording)));
    }
    private void SyncStandbySegments()
    {
        if (current is not null) return;
        SegmentRows.Clear();
        if (selectedScenario is null) return;
        for (int i = 0; i < selectedScenario.Segments.Length; i++)
        { var segment = selectedScenario.Segments[i]; SegmentRows.Add(new(new SessionSegment(segment, segment.OriginalSeconds, 0), i, 0, false)); }
        SelectedSegment = SegmentRows.FirstOrDefault();
    }
    private void SyncSegments()
    {
        if (current is null) return;
        var projection = Budget!;
        SessionSegment Displayed(int index) => index <= current.CurrentSegmentIndex ? current.Segments[index]
            : current.Segments[index] with { BudgetSeconds = projection.FutureBudgets[index - current.CurrentSegmentIndex - 1] };
        if (!SegmentRows.Select(x => x.Id).SequenceEqual(current.Segments.Select(x => x.Original.Id)))
        {
            Guid? selected = SelectedSegment?.Id; SegmentRows.Clear();
            for (int i = 0; i < current.Segments.Length; i++) SegmentRows.Add(new(Displayed(i), i, current.CurrentSegmentIndex, IsFinished));
            SelectedSegment = SegmentRows.FirstOrDefault(x => x.Id == selected) ?? SegmentRows[current.CurrentSegmentIndex];
        }
        else for (int i = 0; i < SegmentRows.Count; i++) SegmentRows[i].Update(Displayed(i), i, current.CurrentSegmentIndex, IsFinished);
    }
    private void RebuildTimeline()
    {
        TimelineRows.Clear(); if (current is null) return;
        var rows = new List<(long Time, TimelineRow Row)>();
        if (LogFilter != "System") foreach (var moment in current.Moments)
            rows.Add((moment.SessionElapsedMs, new(moment.Id, "Moment", DisplayFormats.Time(moment.SessionElapsedMs), moment.Categories.Length > 1 ? "◉◉" : DisplayFormats.Icon(moment.Categories[0].Icon),
                string.Join(", ", moment.Categories.Select(x => x.Name)), $"Pressed {DisplayFormats.Time(moment.Presses[0].Time.SessionElapsedMs)} · offset {moment.Presses[0].OffsetSeconds}s")));
        if (LogFilter != "Moments") foreach (var item in events.Where(x => x.Type is not (SessionEventType.MomentAdded or SessionEventType.MomentMerged or SessionEventType.MomentRemoved)))
            rows.Add((item.Time.SessionElapsedMs, new(null, "System", DisplayFormats.Time(item.Time.SessionElapsedMs), "▪", EventLabel(item), EventDetail(item))));
        foreach (var row in rows.OrderByDescending(x => x.Time).Select(x => x.Row)) TimelineRows.Add(row);
    }
    private static string EventLabel(SessionEvent e) => e.Type switch
    {
        SessionEventType.TimerStarted => "Timer started: " + e.Detail, SessionEventType.TimerStopped => "Timer stopped", SessionEventType.TimerFinished => "Timer finished",
        SessionEventType.TakeStarted => "Take started", SessionEventType.TakeEnded => "Take ended", SessionEventType.SegmentStarted => "Segment started", SessionEventType.SegmentFinished => "Segment finished",
        SessionEventType.SessionFinished => "Session finished", SessionEventType.PauseAnswered => e.Detail == "Yes" ? "Recording stopped" : "Recording kept rolling",
        SessionEventType.SegmentReordered => "Segments reordered", SessionEventType.TimerAdjusted => "Timer adjusted", SessionEventType.MomentRemoved => "Moment removed",
        SessionEventType.SegmentAdded => "Segment added", SessionEventType.SegmentRemoved => "Segment removed", SessionEventType.ScenarioEdited => "Segment edited",
        SessionEventType.MetadataCorrected => "Recording status changed", SessionEventType.Recovered => "Session recovered", SessionEventType.Redo => "Redo", _ => e.Type.ToString()
    };
    private string EventDetail(SessionEvent e) => e.Type switch
    {
        SessionEventType.Clap => current?.Takes.FirstOrDefault(x => x.Id == e.Time.TakeId) is { } take ? $"Take {take.Number:00}" : "",
        SessionEventType.SegmentStarted or SessionEventType.SegmentFinished => current?.Segments.FirstOrDefault(x => x.Original.Id == e.Time.SegmentId)?.Original.Name ?? "",
        SessionEventType.TimerStopped => e.Detail switch { "replaced" => "Replaced by a new timer", "sessionFinished" => "Session finished", "adjustedToZero" => "Reduced to zero", _ => "Stopped manually" },
        SessionEventType.TimerAdjusted => e.Detail + " seconds",
        _ => ""
    };
    private void RefreshCommands() { foreach (var command in commands) command.Refresh(); }
    private void RefreshPresentation()
    {
        RefreshAlert(); Notify(nameof(CanRedo)); Notify(nameof(RedoLabel));
        Notify(nameof(UndoTooltip));
        Notify(nameof(CanWrap));
        foreach (string name in new[] { nameof(HasSession), nameof(IdleState), nameof(CanCorrectPause), nameof(RecordingChoiceLabel), nameof(ReserveCaption), nameof(PauseIcon), nameof(WarningVisible) }) Notify(name);
        foreach (string name in new[] { nameof(CurrentSession), nameof(IsOnAir), nameof(IsFinished), nameof(IsStandby), nameof(DisplayState), nameof(SessionTime), nameof(TakeTime), nameof(TakeNumber), nameof(SegmentTitle), nameof(DescriptionMarkdown), nameof(PersonalTime), nameof(ReserveTime), nameof(NextSegmentTitle), nameof(NextSegmentEstimate), nameof(PlanNotice), nameof(WarningText), nameof(WarningColor), nameof(WarningActive), nameof(Overscheduled), nameof(TimerTime), nameof(TimerColor), nameof(TimerStatus), nameof(Preset1Label), nameof(Preset2Label), nameof(Preset3Label), nameof(PauseLabel), nameof(LastAction), nameof(UndoLabel), nameof(ResultsSummary), nameof(ReduceAnimations) }) Notify(name);
        RefreshCommands();
    }
}
