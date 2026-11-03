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

    public Task<Session> ReadCurrentAsync(CancellationToken ct = default) => RunAsync(() =>
        Task.FromResult(NeedsRecovery || durable.Archived ? durable.Engine.Session : engine.Refresh()), ct);

    public Task<RecoveryPlan> GetRecoveryPlanAsync(CancellationToken ct = default) => RunAsync(() =>
    {
        if (!NeedsRecovery) throw new InvalidOperationException("No recovery decision is pending.");
        return Task.FromResult(RecoveryPlanner.Plan(durable.Engine.Session, records[^1].UtcTimestamp, clock.UtcNow));
    }, ct);

    public Task<PersistenceResult> ExecuteAsync(SessionCommand command, CancellationToken ct = default) => RunAsync(async () =>
    {
        EnsureWritable();
        await RepairSnapshotAsync(ct).ConfigureAwait(false);
        var candidate = engine.Fork();
        SessionTransition transition = command.Kind switch
        {
            SessionCommandKind.Ready => candidate.Ready(), SessionCommandKind.OpenClap => candidate.OpenClapDialog(),
            SessionCommandKind.CancelClap => candidate.CancelClapDialog(), SessionCommandKind.Clap => candidate.ClapFromEnter(),
            SessionCommandKind.Pause => candidate.Pause(),
            SessionCommandKind.AnswerPause when command.RecordingStopped.HasValue => candidate.AnswerPause(command.RecordingStopped.Value),
            SessionCommandKind.Resume => candidate.Resume(), SessionCommandKind.NextSegment => candidate.NextSegment(),
            SessionCommandKind.WrapUp => candidate.WrapUpConfirmed(), SessionCommandKind.Reorder => candidate.ReorderFutureSegments(command.Order),
            _ => throw new InvalidOperationException("Use the dedicated method for this command.")
        };
        var state = durable with { Engine = candidate.CaptureCheckpoint() };
        Guid id = Guid.NewGuid();
        if (command.Kind is SessionCommandKind.NextSegment or SessionCommandKind.Reorder)
            state = state with { UndoStack = state.UndoStack.Add(new(id, records[^1].Sequence, records[^1].Sequence + 1, command.Kind)) };
        if (command.Kind == SessionCommandKind.WrapUp) state = state with { UndoStack = [] };
        var result = await PersistAsync(candidate, state, command, transition.Events, null, ct, id).ConfigureAwait(false);
        if (transition.Session.State == SessionState.Finished && command.Kind == SessionCommandKind.NextSegment)
            finishedTimestamp = candidate.MonotonicAnchor;
        return result;
    }, ct);

    public Task<bool> CheckpointIfDueAsync(CancellationToken ct = default, bool force = false) => RunAsync(async () =>
    {
        if (NeedsRecovery || durable.Archived || durable.ResultsClosed || IsFaulted) return false;
        await RepairSnapshotAsync(ct).ConfigureAwait(false);
        if (durable.Engine.Session.State == SessionState.Finished) return false;
        if (!force && clock.GetElapsedTime(lastSavedTimestamp, clock.GetTimestamp()) < TimeSpan.FromSeconds(10)) return false;
        var candidate = engine.Fork();
        var state = durable with { Engine = candidate.CaptureCheckpoint() };
        await PersistAsync(candidate, state, new(SessionCommandKind.Checkpoint), [], null, ct).ConfigureAwait(false);
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
        var restored = reference.Kind == SessionCommandKind.NextSegment
            ? SessionUndo.UndoNext(before, current.Session, extraSessionMs, extraActiveMs)
            : SessionUndo.UndoReorder(before, current.Session);
        candidate = candidate.WithCheckpoint(current with { Session = restored, SessionFractionTicks = sessionFraction, ActiveFractionTicks = activeFraction });
        var undoEvent = new SessionEvent(Guid.NewGuid(), SessionEventType.Undo, candidate.ReadEventTime(), reference.CommandId.ToString("N"));
        var state = durable with { Engine = candidate.CaptureCheckpoint(), UndoStack = durable.UndoStack.RemoveAt(durable.UndoStack.Length - 1) };
        var result = await PersistAsync(candidate, state, new(SessionCommandKind.Undo), [undoEvent], reference.CommandId, ct).ConfigureAwait(false);
        finishedTimestamp = null;
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
        var cancelled = records.Where(x => x.UndoOf.HasValue).Select(x => x.UndoOf!.Value).ToHashSet();
        var dependent = records.Where(x => x.Sequence > target.Sequence && x.Command.Kind is not (SessionCommandKind.Checkpoint or SessionCommandKind.Undo)
            && !cancelled.Contains(x.Id)).Select(x => x.Command.Kind).Distinct().ToArray();
        try
        {
            var before = SessionJournalStore.StateAt(records, target.BeforeSequence).Engine.Session;
            var current = engine.Fork().Refresh();
            _ = target.Kind == SessionCommandKind.NextSegment ? SessionUndo.UndoNext(before, current) : SessionUndo.UndoReorder(before, current);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PodcastHelper.Core.Validation.ModelValidationException or OverflowException)
        {
            return new(target.CommandId, target.Kind.ToString(), false, true,
                "Dependent time or segment edits prevent a safe Undo. Review those edits first. No saved actions have been removed.");
        }
        return new(target.CommandId, target.Kind == SessionCommandKind.NextSegment ? "Next segment" : "Reorder segments", true, dependent.Length > 0,
            dependent.Length == 0 ? "Elapsed time and budgets will be recalculated. Original journal entries will be retained."
                : $"Later actions ({string.Join(", ", dependent)}) will be retained. Their segment references may be reassigned. Confirm before undoing.");
    }

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
