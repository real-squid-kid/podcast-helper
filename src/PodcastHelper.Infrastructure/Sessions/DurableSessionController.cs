using System.Collections.Immutable;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Services;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.Infrastructure.Sessions;

// Единственная точка выполнения команд сессии: сначала durable journal, затем публикация состояния.
public sealed class DurableSessionController : IAsyncDisposable
{
    private readonly SessionJournalStore store;
    private readonly IClock clock;
    private readonly IDisposable lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task? checkpointLoop;
    private readonly List<JournalRecord> records;
    private SessionEngine engine;
    private DurableSession durable;
    private readonly string journalPath;
    private long lastSavedTimestamp;
    private long? finishedTimestamp;
    private bool disposed;
    private sealed record RedoFrame(SessionEngine Engine, ImmutableArray<UndoReference> UndoStack, Guid UndoId, long CreatedAt, long? FinishedAt);
    private RedoFrame? redo;
    public bool IsFaulted { get; private set; }
    public bool NeedsRecovery { get; private set; }
    public Exception? SnapshotError { get; private set; }
    public Exception? BackgroundError { get; private set; }
    public Guid SessionId => durable.Engine.Session.Id;

    private DurableSessionController(SessionJournalStore store, IClock clock, IDisposable lease,
        DurableSession state, IEnumerable<JournalRecord> records, string journalPath, bool needsRecovery)
    {
        this.store = store; this.clock = clock; this.lease = lease; this.records = records.ToList();
        durable = state; this.journalPath = journalPath; NeedsRecovery = needsRecovery;
        engine = SessionEngine.FromCheckpoint(state.Engine, clock);
        lastSavedTimestamp = clock.GetTimestamp();
    }

    public static async Task<DurableSessionController> CreateAsync(SessionJournalStore store, Session session, IClock clock, CancellationToken ct = default, bool runCheckpoints = true)
    {
        var lease = await store.LeaseAsync(session.Id, ct).ConfigureAwait(false);
        try
        {
            // Даже пустой/повреждённый старый фрагмент нельзя затереть новым Created.
            if (await store.HasJournalAsync(session.Id, ct).ConfigureAwait(false)) throw new InvalidOperationException("This session already has a journal.");
            var engine = new SessionEngine(session, clock);
            var state = new DurableSession(engine.CaptureCheckpoint(), []);
            var controller = new DurableSessionController(store, clock, lease, state, [], store.FirstPath(session.Id), false);
            await controller.RunAsync(async () =>
            {
                await controller.PersistAsync(engine, state, new(SessionCommandKind.Created), [], null, ct).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
            if (runCheckpoints) controller.StartCheckpoints();
            return controller;
        }
        catch { lease.Dispose(); throw; }
    }

    public static async Task<DurableSessionController> OpenAsync(SessionJournalStore store, Guid id, IClock clock, CancellationToken ct = default, bool runCheckpoints = true)
    {
        var lease = await store.LeaseAsync(id, ct).ConfigureAwait(false);
        try
        {
            var loaded = await store.ReadAsync(id, ct).ConfigureAwait(false);
            var controller = new DurableSessionController(store, clock, lease, loaded.State, loaded.Records, loaded.NextJournalPath,
                !loaded.State.Archived && !loaded.State.ResultsClosed && loaded.State.Engine.Session.State != SessionState.Finished);
            if (runCheckpoints) controller.StartCheckpoints();
            return controller;
        }
        catch { lease.Dispose(); throw; }
    }

    public Task<Session> ReadCurrentAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        if (NeedsRecovery || durable.Archived || IsFaulted) return durable.Engine.Session;
        var candidate = engine.Fork(); var checkpoint = candidate.CaptureCheckpoint(); var automatic = candidate.DrainAutomaticEvents();
        if (!automatic.IsEmpty)
            await PersistAsync(candidate, durable with { Engine = checkpoint }, new(SessionCommandKind.Automatic), automatic, null, ct).ConfigureAwait(false);
        else engine = candidate;
        return checkpoint.Session;
    }, ct);

    public Task<ImmutableArray<SessionEvent>> ReadEventsAsync(CancellationToken ct = default) => RunAsync(() =>
        Task.FromResult(records.SelectMany(x => x.Events).ToImmutableArray()), ct);

    public Task<SessionArchive> CaptureArchiveAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        if (IsFaulted || NeedsRecovery || engine.CaptureCheckpoint().Session.State != SessionState.Finished)
            throw new InvalidOperationException("Finish and save this session before exporting results.");
        var snapshot = durable with { Engine = engine.CaptureCheckpoint() };
        return Task.FromResult(new SessionArchive(new(records.ToImmutableArray(), snapshot, false, false, journalPath)));
    }, ct);

    public Task<RecoveryPlan> GetRecoveryPlanAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        if (!NeedsRecovery) throw new InvalidOperationException("No recovery decision is pending.");
        return Task.FromResult(RecoveryPlanner.Plan(durable.Engine.Session, records[^1].UtcTimestamp, clock.UtcNow));
    }, ct);

    public Task<PersistenceResult> ExecuteAsync(SessionCommand command, CancellationToken ct = default)
    {
        if (command.Kind is SessionCommandKind.AddMoment or SessionCommandKind.Clap && command.InputTimestamp is null)
            command = command with { InputTimestamp = clock.GetTimestamp(), PressedUtc = clock.UtcNow, PressedOffset = clock.LocalNow.Offset };
        return RunAsync(async () =>
    {
        EnsureWritable();
        await RepairSnapshotAsync(ct).ConfigureAwait(false);
        var candidate = engine.Fork();
        SessionTransition transition = command.Kind switch
        {
            SessionCommandKind.Ready => candidate.Ready(), SessionCommandKind.OpenClap => candidate.OpenClapDialog(),
            SessionCommandKind.CancelClap => candidate.CancelClapDialog(), SessionCommandKind.Clap => candidate.ClapFromEnter(command.InputTimestamp is { } clapInput
                ? Math.Max(0, clock.GetElapsedTime(clapInput, clock.GetTimestamp()).Ticks) : 0, command.PressedUtc, command.PressedOffset),
            SessionCommandKind.Pause => candidate.Pause(),
            SessionCommandKind.AnswerPause when command.RecordingStopped.HasValue => candidate.AnswerPause(command.RecordingStopped.Value),
            SessionCommandKind.Resume => candidate.Resume(), SessionCommandKind.NextSegment => candidate.NextSegment(),
            SessionCommandKind.WrapUp => candidate.WrapUpConfirmed(), SessionCommandKind.Reorder => candidate.ReorderFutureSegments(command.Order),
            SessionCommandKind.TimerStart => candidate.StartTimer(command.Seconds), SessionCommandKind.TimerStop => candidate.StopTimer(),
            SessionCommandKind.TimerAdjust => candidate.AdjustTimer(command.Seconds),
            SessionCommandKind.AddMoment => candidate.AddMoment(command.CategoryId ?? "", command.InputTimestamp is { } input
                ? Math.Max(0, clock.GetElapsedTime(input, clock.GetTimestamp()).Ticks) : 0, command.PressedUtc, command.PressedOffset),
            SessionCommandKind.RemoveMoment => candidate.RemoveMoment(command.EntityId),
            SessionCommandKind.AddSegment when command.Segment is not null => candidate.AddFutureSegment(command.Segment),
            SessionCommandKind.EditSegment when command.Segment is not null => candidate.EditFutureSegment(command.Segment),
            SessionCommandKind.RemoveSegment => candidate.RemoveFutureSegment(command.EntityId), SessionCommandKind.RemoveCurrent => candidate.RemoveCurrentAndAdvanceConfirmed(),
            _ => throw new InvalidOperationException("Use the dedicated method for this command.")
        };
        var state = durable with { Engine = candidate.CaptureCheckpoint() };
        Guid id = Guid.NewGuid();
        if (SessionCommands.IsReversible(command.Kind) && !transition.Events.IsEmpty)
            state = state with { UndoStack = state.UndoStack.Add(new(id, records[^1].Sequence, records[^1].Sequence + 1, command.Kind)) };
        if (command.Kind == SessionCommandKind.WrapUp) state = state with { UndoStack = [] };
        var events = candidate.DrainAutomaticEvents().AddRange(transition.Events).OrderBy(x => x.Time.SessionElapsedMs).ToImmutableArray();
        var result = await PersistAsync(candidate, state, command, events, null, ct, id).ConfigureAwait(false);
        if (transition.Session.State == SessionState.Finished && command.Kind is SessionCommandKind.NextSegment or SessionCommandKind.RemoveCurrent)
            finishedTimestamp = candidate.MonotonicAnchor;
        return result;
    }, ct);
    }

    public Task<bool> CheckpointIfDueAsync(CancellationToken ct = default, bool force = false) => RunAsync(async () =>
    {
        if (NeedsRecovery || durable.Archived || durable.ResultsClosed || IsFaulted) return false;
        await RepairSnapshotAsync(ct).ConfigureAwait(false);
        if (durable.Engine.Session.State == SessionState.Finished) return false;
        var candidate = engine.Fork();
        var state = durable with { Engine = candidate.CaptureCheckpoint() };
        var automatic = candidate.DrainAutomaticEvents();
        if (!force && automatic.IsEmpty && clock.GetElapsedTime(lastSavedTimestamp, clock.GetTimestamp()) < TimeSpan.FromSeconds(10)) return false;
        await PersistAsync(candidate, state, new(automatic.IsEmpty ? SessionCommandKind.Checkpoint : SessionCommandKind.Automatic), automatic, null, ct).ConfigureAwait(false);
        return true;
    }, ct);

    public Task<UndoAssessment?> AssessUndoAsync(CancellationToken ct = default) => RunAsync(() => Task.FromResult(AssessUndo()), ct);

    public Task<PersistenceResult> UndoAsync(Guid expectedCommandId, bool confirmedConsequences = false, CancellationToken ct = default) => RunAsync(async () =>
    {
        EnsureWritable();
        await RepairSnapshotAsync(ct).ConfigureAwait(false);
        var assessment = AssessUndo() ?? throw new InvalidOperationException("There is no reversible command.");
        if (assessment.CommandId != expectedCommandId) throw new InvalidOperationException("The Undo action changed. Review it again.");
        if (!assessment.CanApply || assessment.RequiresConfirmation && !confirmedConsequences)
            throw new InvalidOperationException(assessment.Consequences);
        var reference = durable.UndoStack[^1];
        var before = SessionJournalStore.StateAt(records, reference.BeforeSequence).Engine.Session;
        var candidate = engine.Fork();
        var current = candidate.CaptureCheckpoint();
        var automaticBeforeUndo = candidate.DrainAutomaticEvents();
        var redoEngine = candidate.Fork();
        var redoStack = durable.UndoStack;
        var redoFinishedAt = finishedTimestamp;
        long extraSessionMs = 0, extraActiveMs = 0;
        int sessionFraction = current.SessionFractionTicks, activeFraction = current.ActiveFractionTicks;
        if (current.Session.State == SessionState.Finished)
        {
            if (finishedTimestamp is null) throw new InvalidOperationException("The results were reopened. This transition can no longer be undone.");
            long ticks = clock.GetElapsedTime(finishedTimestamp.Value, candidate.MonotonicAnchor).Ticks;
            extraSessionMs = (ticks + sessionFraction) / TimeSpan.TicksPerMillisecond;
            sessionFraction = (int)((ticks + sessionFraction) % TimeSpan.TicksPerMillisecond);
            if (before.State == SessionState.Recording)
            {
                extraActiveMs = (ticks + activeFraction) / TimeSpan.TicksPerMillisecond;
                activeFraction = (int)((ticks + activeFraction) % TimeSpan.TicksPerMillisecond);
            }
        }
        var restored = RestoreUndo(reference.Kind, before, current.Session, extraSessionMs, extraActiveMs);
        candidate = candidate.WithCheckpoint(current with { Session = restored, SessionFractionTicks = sessionFraction, ActiveFractionTicks = activeFraction });
        var undoEvent = new SessionEvent(Guid.NewGuid(), SessionEventType.Undo, candidate.ReadEventTime(), reference.CommandId.ToString("N"));
        var state = durable with { Engine = candidate.CaptureCheckpoint(), UndoStack = durable.UndoStack.RemoveAt(durable.UndoStack.Length - 1) };
        var result = await PersistAsync(candidate, state, new(SessionCommandKind.Undo), automaticBeforeUndo.AddRange(candidate.DrainAutomaticEvents()).Add(undoEvent), reference.CommandId, ct).ConfigureAwait(false);
        redo = new(redoEngine, redoStack, records[^1].Id, clock.GetTimestamp(), redoFinishedAt);
        finishedTimestamp = null;
        return result;
    }, ct);

    private long RedoRemainingMs => redo is null || IsFaulted || NeedsRecovery || durable.ResultsClosed || durable.Archived ? 0
        : Math.Max(0, (long)Math.Ceiling(10_000 - clock.GetElapsedTime(redo.CreatedAt, clock.GetTimestamp()).TotalMilliseconds));

    public Task<long> ReadRedoRemainingMsAsync(CancellationToken ct = default) => RunAsync(() => Task.FromResult(RedoRemainingMs), ct);

    public Task<PersistenceResult> RedoAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        EnsureWritable();
        if (redo is not { } frame || RedoRemainingMs == 0) throw new InvalidOperationException("The ten-second window to restore Undo has ended.");
        await RepairSnapshotAsync(ct).ConfigureAwait(false);
        var activeBranch = engine.Fork(); var activeCheckpoint = activeBranch.CaptureCheckpoint();
        var pending = activeBranch.DrainAutomaticEvents();
        var candidate = frame.Engine.Fork(); var checkpoint = candidate.CaptureCheckpoint();
        var originalBranchEvents = candidate.DrainAutomaticEvents();
        long undoSequence = records.First(x => x.Id == frame.UndoId).Sequence;
        bool SameFinish(SessionEvent a, SessionEvent b) => a.Type == SessionEventType.TimerFinished && b.Type == a.Type && a.Time.ActiveElapsedMs == b.Time.ActiveElapsedMs;
        var automatic = pending.AddRange(originalBranchEvents.Where(e =>
            !(activeCheckpoint.Session.Timer.StartedAtActiveMs == checkpoint.Session.Timer.StartedAtActiveMs && pending.Any(x => SameFinish(x, e)))
            && !records.Any(r => r.Sequence > undoSequence && r.Events.Any(x => SameFinish(x, e))
                && SessionJournalStore.StateAt(records, r.Sequence).Engine.Session.Timer.StartedAtActiveMs == checkpoint.Session.Timer.StartedAtActiveMs)));
        var restored = new SessionEvent(Guid.NewGuid(), SessionEventType.Redo, candidate.ReadEventTime(), frame.UndoId.ToString("N"));
        var state = durable with { Engine = checkpoint, UndoStack = frame.UndoStack };
        var result = await PersistAsync(candidate, state, new(SessionCommandKind.Redo), automatic.Add(restored), frame.UndoId, ct).ConfigureAwait(false);
        finishedTimestamp = frame.FinishedAt; redo = null;
        return result;
    }, ct);

    public Task<PersistenceResult> RecoverAsync(Session? corrected = null, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (!NeedsRecovery || IsFaulted) throw new InvalidOperationException("No recoverable session is pending.");
        var plan = RecoveryPlanner.Plan(durable.Engine.Session, records[^1].UtcTimestamp, clock.UtcNow);
        if (corrected is null && plan.RequiresConfirmation) throw new InvalidOperationException(plan.Warning ?? "Review the recording state before recovery.");
        var session = corrected is null ? RecoveryPlanner.Conservative(plan) : RecoveryPlanner.Reconcile(plan, corrected);
        var checkpoint = durable.Engine with { Session = session, ClapDialogOpen = false };
        var candidate = SessionEngine.FromCheckpoint(checkpoint, clock);
        // Восстановление становится явной зависимостью, которую оператор просматривает перед старым Undo.
        var recoveryEvent = new SessionEvent(Guid.NewGuid(), SessionEventType.Recovered, candidate.ReadEventTime(), "Operator time estimate; camera activity is not verified.");
        var state = durable with { Engine = candidate.CaptureCheckpoint() };
        var result = await PersistAsync(candidate, state, new(SessionCommandKind.Recovered), [recoveryEvent], null, ct).ConfigureAwait(false);
        NeedsRecovery = false;
        return result;
    }, ct);

    public Task<PersistenceResult> AbandonAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        if (IsFaulted || durable.Archived) throw new InvalidOperationException("This session is not writable.");
        var checkpoint = NeedsRecovery ? durable.Engine : engine.CaptureCheckpoint();
        var candidate = SessionEngine.FromCheckpoint(checkpoint, clock);
        var state = durable with { Engine = checkpoint, Archived = true, ArchiveReason = "Abandoned/Recovery declined", UndoStack = [], ResultsClosed = true };
        var result = await PersistAsync(candidate, state, new(SessionCommandKind.Abandoned), [], null, ct).ConfigureAwait(false);
        NeedsRecovery = false;
        return result;
    }, ct);

    public Task<PersistenceResult> CorrectPauseAnswerAsync(bool recordingStopped, CancellationToken ct = default) => RunAsync(async () =>
    {
        EnsureWritable(); await RepairSnapshotAsync(ct).ConfigureAwait(false);
        var candidate = engine.Fork(); var checkpoint = candidate.CaptureCheckpoint(); var s = checkpoint.Session;
        if (s.State != SessionState.Paused || s.Pause is not { RecordingStopped: not null } pause || s.Takes.IsEmpty)
            throw new InvalidOperationException("Only an answered current pause can be corrected here.");
        var take = s.Takes[^1];
        var corrected = s with { Pause = pause with { RecordingStopped = recordingStopped },
            Takes = s.Takes.SetItem(s.Takes.Length - 1, take with { Ended = recordingStopped ? pause.Started : null }) };
        candidate = candidate.WithCheckpoint(checkpoint with { Session = corrected });
        var audit = new SessionEvent(Guid.NewGuid(), SessionEventType.MetadataCorrected, candidate.ReadEventTime(), recordingStopped ? "Pause answer corrected: Yes" : "Pause answer corrected: No");
        return await PersistAsync(candidate, durable with { Engine = candidate.CaptureCheckpoint() }, new(SessionCommandKind.CorrectMetadata, recordingStopped), [audit], null, ct).ConfigureAwait(false);
    }, ct);

    public Task<PersistenceResult> CloseResultsAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        EnsureWritable();
        if (durable.Engine.Session.State != SessionState.Finished) throw new InvalidOperationException("Only finished results can be closed.");
        var candidate = engine.Fork();
        var checkpoint = candidate.CaptureCheckpoint();
        var result = await PersistAsync(candidate, durable with { Engine = checkpoint, ResultsClosed = true, UndoStack = [] }, new(SessionCommandKind.ResultsClosed), [], null, ct).ConfigureAwait(false);
        finishedTimestamp = null;
        return result;
    }, ct);

    private UndoAssessment? AssessUndo()
    {
        if (NeedsRecovery || IsFaulted || durable.Archived || durable.ResultsClosed || durable.UndoStack.IsEmpty) return null;
        var target = durable.UndoStack[^1];
        if (durable.Engine.Session.State == SessionState.Finished && finishedTimestamp is null) return null;
        var redone = records.Where(x => x.Command.Kind == SessionCommandKind.Redo && x.UndoOf.HasValue).Select(x => x.UndoOf!.Value).ToHashSet();
        var cancelled = records.Where(x => x.Command.Kind == SessionCommandKind.Undo && x.UndoOf.HasValue && !redone.Contains(x.Id)).Select(x => x.UndoOf!.Value).ToHashSet();
        var dependent = records.Where(x => x.Sequence > target.Sequence && x.Command.Kind is not (SessionCommandKind.Checkpoint or SessionCommandKind.Undo or SessionCommandKind.Redo or SessionCommandKind.Automatic)
            && !cancelled.Contains(x.Id)).Select(x => x.Command.Kind).Distinct().ToArray();
        try
        {
            var before = SessionJournalStore.StateAt(records, target.BeforeSequence).Engine.Session;
            var current = engine.Fork().Refresh();
            _ = RestoreUndo(target.Kind, before, current, 0, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PodcastHelper.Core.Validation.ModelValidationException or OverflowException)
        {
            return new(target.CommandId, target.Kind.ToString(), false, true,
                "Dependent time or segment edits prevent a safe Undo. Review those edits first. No saved actions have been removed.");
        }
        return new(target.CommandId, SessionCommands.Label(target.Kind), true, dependent.Length > 0,
            dependent.Length == 0 ? "Elapsed time and budgets will be recalculated. Original journal entries will be retained."
                : $"Later actions ({string.Join(", ", dependent)}) will be retained. Their segment references may be reassigned. Confirm before undoing.");
    }

    private static Session RestoreUndo(SessionCommandKind kind, Session before, Session current, long extraSession, long extraActive) => kind switch
    {
        SessionCommandKind.NextSegment or SessionCommandKind.RemoveCurrent => SessionUndo.UndoNext(before, current, extraSession, extraActive),
        SessionCommandKind.Reorder => SessionUndo.UndoReorder(before, current),
        SessionCommandKind.TimerStart or SessionCommandKind.TimerStop or SessionCommandKind.TimerAdjust => SessionUndo.UndoTimer(before, current),
        SessionCommandKind.AddMoment or SessionCommandKind.RemoveMoment => SessionUndo.UndoMoments(before, current),
        SessionCommandKind.AddSegment or SessionCommandKind.RemoveSegment or SessionCommandKind.EditSegment => SessionUndo.UndoFutureEdit(before, current),
        _ => throw new InvalidOperationException("This command cannot be undone.")
    };

    private async Task<PersistenceResult> PersistAsync(SessionEngine candidate, DurableSession state, SessionCommand command,
        ImmutableArray<SessionEvent> events, Guid? undoOf, CancellationToken ct, Guid? id = null)
    {
        JournalCodec.Validate(state);
        var record = JournalCodec.Sign(new(JournalCodec.Version, id ?? Guid.NewGuid(), state.Engine.Session.Id,
            records.Count == 0 ? 1 : records[^1].Sequence + 1, candidate.CalendarAnchorUtc, command, events,
            JournalCodec.Diff(records.Count == 0 ? null : durable, state), undoOf, records.Count == 0 ? "" : records[^1].Sha256, ""));
        try { await store.AppendAsync(journalPath, record, ct).ConfigureAwait(false); }
        catch { IsFaulted = true; throw; } // Неизвестно, дошёл ли Flush: повторять команду в этом writer нельзя.
        engine = candidate; durable = state; records.Add(record); lastSavedTimestamp = candidate.MonotonicAnchor;
        if (command.Kind is not (SessionCommandKind.Checkpoint or SessionCommandKind.Automatic or SessionCommandKind.Undo or SessionCommandKind.Redo)) redo = null;
        bool saved = true;
        try { await store.WriteSnapshotAsync(record, state, ct).ConfigureAwait(false); SnapshotError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException or OperationCanceledException)
        { SnapshotError = ex; saved = false; }
        return new(new(state.Engine.Session, events), saved);
    }

    private async Task RepairSnapshotAsync(CancellationToken ct)
    {
        if (SnapshotError is null) return;
        await store.WriteSnapshotAsync(records[^1], durable, ct).ConfigureAwait(false);
        SnapshotError = null;
    }

    private void EnsureWritable()
    {
        if (IsFaulted || NeedsRecovery || durable.Archived || durable.ResultsClosed) throw new InvalidOperationException("Resolve session storage or recovery before using commands.");
    }

    private Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct) => Task.Run(async () =>
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }, ct);

    private void StartCheckpoints() => checkpointLoop = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await CheckpointIfDueAsync(stopping.Token).ConfigureAwait(false);
                    if (SnapshotError is null && !IsFaulted) BackgroundError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { BackgroundError = ex; }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { BackgroundError = ex; }
    });

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        if (checkpointLoop is not null) await checkpointLoop.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; lease.Dispose(); } }
        finally { gate.Release(); }
        stopping.Dispose();
    }
}
