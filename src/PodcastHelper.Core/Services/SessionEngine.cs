using System.Collections.Immutable;
using System.Globalization;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Core.Services;

// Однопоточный движок: вызывающий слой сериализует команды. UI-таймер только запрашивает снимок.
public sealed class SessionEngine
{
    private readonly IClock clock;
    private long lastTimestamp;
    private long sessionTicks;
    private long activeTicks;
    private DateTimeOffset nowUtc;
    private TimeSpan localOffset;
    private Session session;
    private readonly List<SessionEvent> automaticEvents = [];

    public bool IsClapDialogOpen { get; private set; }
    public bool IsOnAir => session.State == SessionState.Recording;
    public long MonotonicAnchor => lastTimestamp;
    public DateTimeOffset CalendarAnchorUtc => nowUtc;
    public long? TakeElapsedMs => session.Pause is { RecordingStopped: null } ? null : OpenTake is { } take
        ? checked(session.SessionElapsedMs - take.Started.SessionElapsedMs + take.ElapsedAdjustmentMs) : null;

    public SessionEngine(Session initialSession, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ModelValidation.Session(initialSession);
        if (initialSession.State != SessionState.Preparing || initialSession.SessionElapsedMs != 0
            || initialSession.ActiveElapsedMs != 0 || !initialSession.Takes.IsEmpty || !initialSession.Claps.IsEmpty
            || initialSession.Segments.Any(x => x.ActiveElapsedMs != 0 || x.ReserveUsedMs != 0)
            || initialSession.UnfundedOverrunMs != 0 || initialSession.Pause is not null)
            throw new InvalidOperationException("Use a new session. Recovery requires explicit reconciliation.");
        this.clock = clock;
        session = initialSession;
        lastTimestamp = clock.GetTimestamp();
        ReadCalendarTime();
    }

    public Session Refresh()
    {
        AdvanceTime();
        return session;
    }

    public EngineCheckpoint CaptureCheckpoint()
    {
        AdvanceTime();
        return new(session, (int)(sessionTicks % TimeSpan.TicksPerMillisecond),
            (int)(activeTicks % TimeSpan.TicksPerMillisecond), IsClapDialogOpen);
    }

    public static SessionEngine FromCheckpoint(EngineCheckpoint checkpoint, IClock clock)
    {
        ModelValidation.Session(checkpoint.Session);
        if (checkpoint.SessionFractionTicks is < 0 or >= 10000 || checkpoint.ActiveFractionTicks is < 0 or >= 10000)
            throw new ModelValidationException("Invalid fractional time counters.");
        return new SessionEngine(checkpoint, clock);
    }

    private SessionEngine(EngineCheckpoint checkpoint, IClock clock)
    {
        this.clock = clock;
        session = checkpoint.Session;
        sessionTicks = checked(session.SessionElapsedMs * TimeSpan.TicksPerMillisecond + checkpoint.SessionFractionTicks);
        activeTicks = checked(session.ActiveElapsedMs * TimeSpan.TicksPerMillisecond + checkpoint.ActiveFractionTicks);
        IsClapDialogOpen = checkpoint.ClapDialogOpen;
        lastTimestamp = clock.GetTimestamp();
        ReadCalendarTime();
    }

    // Точная транзакционная копия внутри одного процесса, без потери времени на запись.
    public SessionEngine Fork() => new(new EngineCheckpoint(session), clock)
    {
        sessionTicks = sessionTicks, activeTicks = activeTicks, lastTimestamp = lastTimestamp,
        nowUtc = nowUtc, localOffset = localOffset, IsClapDialogOpen = IsClapDialogOpen
    };

    public ImmutableArray<SessionEvent> DrainAutomaticEvents()
    {
        var result = automaticEvents.ToImmutableArray(); automaticEvents.Clear(); return result;
    }

    public TimerWarning TimerWarning => !session.Timer.Running ? TimerWarning.None : session.Timer.RemainingMs <= 5000
        ? TimerWarning.Red : session.Timer.RemainingMs <= 15000 ? TimerWarning.Yellow : TimerWarning.None;

    public SessionTransition StartTimer(int seconds)
    {
        RequireRunningSession();
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Timer duration must be positive.");
        AdvanceTime();
        var events = ImmutableArray.CreateBuilder<SessionEvent>();
        if (session.Timer.Running) events.Add(Event(SessionEventType.TimerStopped, "replaced"));
        session = session with { Timer = new SessionTimer(seconds * 1000L, true, session.ActiveElapsedMs) };
        events.Add(Event(SessionEventType.TimerStarted, $"{seconds / 60:D2}:{seconds % 60:D2}"));
        return new(session, events.ToImmutable());
    }

    public SessionTransition StopTimer()
    {
        RequireRunningSession(); AdvanceTime();
        if (!session.Timer.Running) return Result();
        session = session with { Timer = session.Timer with { Running = false } };
        return Result(Event(SessionEventType.TimerStopped, "manual"));
    }

    public SessionTransition AdjustTimer(int seconds)
    {
        RequireRunningSession(); AdvanceTime();
        long value = Math.Max(0, checked(session.Timer.RemainingMs + seconds * 1000L));
        bool stopped = session.Timer.Running && value == 0;
        session = session with { Timer = session.Timer with { RemainingMs = value, Running = session.Timer.Running && value > 0 } };
        return stopped ? Result(Event(SessionEventType.TimerAdjusted, seconds.ToString(CultureInfo.InvariantCulture)), Event(SessionEventType.TimerStopped, "adjustedToZero"))
            : Result(Event(SessionEventType.TimerAdjusted, seconds.ToString(CultureInfo.InvariantCulture)));
    }

    public SessionTransition AddMoment(string categoryId, long dispatchDelayTicks = 0, DateTimeOffset? pressedUtc = null, TimeSpan? offset = null)
    {
        RequireState(SessionState.Recording); RequireNoClapDialog();
        var category = session.SettingsSnapshot.Categories.SingleOrDefault(x => x.Id == categoryId)
            ?? throw new ArgumentException("Unknown moment category.", nameof(categoryId));
        AdvanceTime();
        if (dispatchDelayTicks < 0 || dispatchDelayTicks > activeTicks) throw new ArgumentOutOfRangeException(nameof(dispatchDelayTicks));
        var time = CaptureTime();
        long atSession = (sessionTicks - dispatchDelayTicks) / TimeSpan.TicksPerMillisecond;
        long atActive = (activeTicks - dispatchDelayTicks) / TimeSpan.TicksPerMillisecond;
        long difference = session.ActiveElapsedMs - atActive;
        if (difference > Current.ActiveElapsedMs) throw new InvalidOperationException("The segment changed before this press was handled.");
        var local = (pressedUtc ?? time.UtcTimestamp).ToOffset(offset ?? time.LocalComputerTimestamp.Offset);
        time = time with { SessionElapsedMs = atSession, ActiveElapsedMs = atActive,
            SegmentActiveElapsedMs = time.SegmentActiveElapsedMs - difference, TakeElapsedMs = time.TakeElapsedMs - (time.SessionElapsedMs - atSession),
            UtcTimestamp = local.ToUniversalTime(), LocalComputerTimestamp = local,
            PredictedCameraTimecode = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":00" };
        var press = new MomentPress(time, session.SettingsSnapshot.MarkerOffsetSeconds, category with { });
        var last = session.Moments.LastOrDefault();
        bool merge = last is not null && atSession >= last.Presses[^1].Time.SessionElapsedMs
            && atSession - last.Presses[^1].Time.SessionElapsedMs <= 3000;
        if (merge)
        {
            var combined = last! with { Presses = last.Presses.Add(press) };
            session = session with { Moments = session.Moments.SetItem(session.Moments.Length - 1, combined) };
            return Result(new SessionEvent(Guid.NewGuid(), SessionEventType.MomentMerged, time, combined.Id.ToString("N")));
        }
        var moment = new Moment(Guid.NewGuid(), Math.Max(0, atSession - press.OffsetSeconds * 1000L), [press]);
        session = session with { Moments = session.Moments.Add(moment) };
        return Result(new SessionEvent(Guid.NewGuid(), SessionEventType.MomentAdded, time, moment.Id.ToString("N")));
    }

    public SessionTransition RemoveMoment(Guid id)
    {
        RequireRunningSession(); AdvanceTime();
        int index = Array.FindIndex(session.Moments.ToArray(), x => x.Id == id);
        if (index < 0) throw new ArgumentException("Moment not found.", nameof(id));
        session = session with { Moments = session.Moments.RemoveAt(index) };
        return Result(Event(SessionEventType.MomentRemoved, id.ToString("N")));
    }

    public SessionTransition AddFutureSegment(Segment segment)
    {
        RequireRunningSession();
        ModelValidation.Scenario(new Scenario { Name = "Validation", ModifiedUtc = nowUtc, TargetSeconds = 1, Segments = [segment] });
        if (session.Segments.Any(x => x.Original.Id == segment.Id)) throw new ArgumentException("Segment ID already exists.");
        AdvanceTime();
        // Новая карточка сохраняет введённую длительность; нехватка плана видна как предупреждение, без скрытого сокращения.
        long fromReserve = Math.Min(ReserveMs, segment.OriginalSeconds * 1000L);
        SetReserve(ReserveMs - fromReserve);
        session = session with { Segments = session.Segments.Add(new(segment, segment.OriginalSeconds, 0)) };
        return Result(Event(SessionEventType.SegmentAdded, segment.Id.ToString("N")));
    }

    public SessionTransition RemoveFutureSegment(Guid id)
    {
        RequireRunningSession();
        int index = Array.FindIndex(session.Segments.ToArray(), x => x.Original.Id == id);
        if (index <= session.CurrentSegmentIndex) throw new InvalidOperationException("Only an upcoming segment can be removed without an explicit time reassignment.");
        AdvanceTime();
        long returned = session.Segments[index].BudgetSeconds * 1000L;
        long repaid = Math.Min(returned, session.UnfundedOverrunMs);
        session = session with { Segments = session.Segments.RemoveAt(index), UnfundedOverrunMs = session.UnfundedOverrunMs - repaid };
        SetReserve(checked(ReserveMs + returned - repaid));
        return Result(Event(SessionEventType.SegmentRemoved, id.ToString("N")));
    }

    public SessionTransition RemoveCurrentAndAdvanceConfirmed()
    {
        RequireRunningSession(); AdvanceTime();
        var removed = Event(SessionEventType.SegmentRemoved, "Elapsed time retained under the completed segment.");
        session = session with { Segments = session.Segments.SetItem(session.CurrentSegmentIndex, Current with { RemovedFromScenario = true }) };
        var transition = NextSegment();
        return transition with { Events = transition.Events.Insert(0, removed) };
    }

    public SessionTransition EditFutureSegment(Segment edited)
    {
        RequireRunningSession();
        int index = Array.FindIndex(session.Segments.ToArray(), x => x.Original.Id == edited.Id);
        if (index <= session.CurrentSegmentIndex) throw new InvalidOperationException("Only upcoming segments can be edited here.");
        ModelValidation.Scenario(new Scenario { Name = "Validation", ModifiedUtc = nowUtc, TargetSeconds = 1, Segments = [edited] });
        AdvanceTime();
        var previous = session.Segments[index];
        long delta = edited.OriginalSeconds * 1000L - previous.BudgetSeconds * 1000L;
        if (delta > 0) SetReserve(ReserveMs - Math.Min(ReserveMs, delta));
        else
        {
            long credit = -delta, repaid = Math.Min(credit, session.UnfundedOverrunMs);
            session = session with { UnfundedOverrunMs = session.UnfundedOverrunMs - repaid };
            SetReserve(checked(ReserveMs + credit - repaid));
        }
        session = session with { Segments = session.Segments.SetItem(index, previous with { Original = edited, BudgetSeconds = edited.OriginalSeconds }) };
        return Result(Event(SessionEventType.ScenarioEdited, edited.Id.ToString("N")));
    }

    public SessionEngine WithCheckpoint(EngineCheckpoint checkpoint)
    {
        var replacement = FromCheckpoint(checkpoint, clock);
        replacement.lastTimestamp = lastTimestamp;
        return replacement;
    }

    public EventTime ReadEventTime()
    {
        AdvanceTime();
        return CaptureTime();
    }

    public BudgetProjection GetBudgetProjection() => BudgetProjector.Project(Refresh());

    public SessionTransition Ready()
    {
        RequireState(SessionState.Preparing);
        AdvanceTime();
        session = session with { State = SessionState.Ready };
        return Result(Event(SessionEventType.Ready));
    }

    public SessionTransition OpenClapDialog()
    {
        if (session.State is not (SessionState.Recording or SessionState.Ready or SessionState.AwaitingClap)
            || IsClapDialogOpen) throw new InvalidOperationException("Clap is not available.");
        AdvanceTime();
        IsClapDialogOpen = true;
        return Result();
    }

    public SessionTransition CancelClapDialog()
    {
        if (!IsClapDialogOpen) throw new InvalidOperationException("No clap dialog is open.");
        AdvanceTime();
        IsClapDialogOpen = false;
        return Result();
    }

    // Единственная точка регистрации хлопушки. UI вызывает её только из обработчика физического Enter.
    public SessionTransition ClapFromEnter(long dispatchDelayTicks = 0, DateTimeOffset? pressedUtc = null, TimeSpan? pressedOffset = null)
    {
        bool first = session.State == SessionState.Ready;
        bool newTake = first || session.State == SessionState.AwaitingClap;
        if (!newTake && !(session.State == SessionState.Recording && IsClapDialogOpen))
            throw new InvalidOperationException("Press Enter in the clap dialog, or wait until the session is ready.");
        AdvanceTime();
        if (dispatchDelayTicks < 0 || !first && dispatchDelayTicks > sessionTicks) throw new ArgumentOutOfRangeException(nameof(dispatchDelayTicks));
        var localPress = (pressedUtc ?? nowUtc).ToOffset(pressedOffset ?? localOffset);
        long pressSessionMs = first ? 0 : (sessionTicks - dispatchDelayTicks) / TimeSpan.TicksPerMillisecond;
        var events = ImmutableArray.CreateBuilder<SessionEvent>();
        if (newTake)
        {
            session = session with { State = SessionState.Recording, Pause = null, StartedUtc = session.StartedUtc ?? localPress.ToUniversalTime() };
            var takeId = Guid.NewGuid();
            var started = CaptureTime() with { TakeId = takeId, TakeElapsedMs = 0, SessionElapsedMs = pressSessionMs,
                UtcTimestamp = localPress.ToUniversalTime(), LocalComputerTimestamp = localPress,
                PredictedCameraTimecode = localPress.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":00" };
            session = session with { Takes = session.Takes.Add(new Take(takeId, session.Takes.Length + 1, started)) };
            events.Add(new(Guid.NewGuid(), SessionEventType.TakeStarted, started));
        }
        var time = CaptureTime();
        if (newTake) time = session.Takes[^1].Started;
        else
        {
            long pressedActiveMs = (activeTicks - dispatchDelayTicks) / TimeSpan.TicksPerMillisecond;
            long activeDelayMs = session.ActiveElapsedMs - pressedActiveMs;
            if (activeDelayMs > Current.ActiveElapsedMs) throw new InvalidOperationException("The segment changed before this clap was handled.");
            time = time with { SessionElapsedMs = pressSessionMs, ActiveElapsedMs = pressedActiveMs,
                SegmentActiveElapsedMs = time.SegmentActiveElapsedMs - activeDelayMs, TakeElapsedMs = time.TakeElapsedMs - (time.SessionElapsedMs - pressSessionMs),
                UtcTimestamp = localPress.ToUniversalTime(), LocalComputerTimestamp = localPress,
                PredictedCameraTimecode = localPress.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":00" };
        }
        var clap = new Clap(Guid.NewGuid(), OpenTake!.Id, time);
        session = session with { Claps = session.Claps.Add(clap) };
        events.Add(new(clap.Id, SessionEventType.Clap, time));
        if (first) events.Add(new(Guid.NewGuid(), SessionEventType.SegmentStarted, time));
        if (newTake && dispatchDelayTicks > 0)
        {
            if (first)
            {
                sessionTicks = dispatchDelayTicks;
                session = session with { SessionElapsedMs = sessionTicks / TimeSpan.TicksPerMillisecond };
            }
            AccountRecordingDelta(dispatchDelayTicks);
        }
        IsClapDialogOpen = false;
        return new(session, events.ToImmutable());
    }

    public SessionTransition Pause()
    {
        RequireState(SessionState.Recording);
        RequireNoClapDialog();
        AdvanceTime();
        var time = CaptureTime();
        session = session with { State = SessionState.Paused, Pause = new PauseContext(time) };
        return Result(new SessionEvent(Guid.NewGuid(), SessionEventType.Pause, time));
    }

    public SessionTransition AnswerPause(bool recordingStopped)
    {
        RequireState(SessionState.Paused);
        if (session.Pause is not { RecordingStopped: null } pause)
            throw new InvalidOperationException("The pause question has already been answered.");
        AdvanceTime();
        var events = ImmutableArray.CreateBuilder<SessionEvent>();
        if (recordingStopped) events.Add(CloseTake(pause.Started));
        session = session with { Pause = pause with { RecordingStopped = recordingStopped } };
        events.Add(Event(SessionEventType.PauseAnswered, recordingStopped ? "Yes" : "No"));
        return new(session, events.ToImmutable());
    }

    public SessionTransition Resume()
    {
        RequireState(SessionState.Paused);
        if (session.Pause?.RecordingStopped is not bool stopped)
            throw new InvalidOperationException("Answer whether recording was stopped before resuming.");
        AdvanceTime();
        session = session with { State = stopped ? SessionState.AwaitingClap : SessionState.Recording,
            Pause = stopped ? session.Pause : null };
        return Result(Event(SessionEventType.Resume));
    }

    public SessionTransition NextSegment()
    {
        RequireRunningSession();
        AdvanceTime();
        var finished = Event(SessionEventType.SegmentFinished);
        ReturnUnusedBudget();
        SettleOverrun();
        if (session.CurrentSegmentIndex == session.Segments.Length - 1)
            return Finish(finished, "nextSegment");
        session = session with { CurrentSegmentIndex = session.CurrentSegmentIndex + 1 };
        return Result(finished, Event(SessionEventType.SegmentStarted));
    }

    // Вызов только после мышиного подтверждения в UI. Горячая клавиша этому действию не назначается.
    public SessionTransition WrapUpConfirmed()
    {
        RequireRunningSession();
        AdvanceTime();
        var finished = Event(SessionEventType.SegmentFinished);
        ReturnUnusedBudget();
        SettleOverrun();
        return Finish(finished, "wrapUp");
    }

    public SessionTransition ReorderFutureSegments(IReadOnlyList<Guid> orderedIds)
    {
        RequireRunningSession();
        ArgumentNullException.ThrowIfNull(orderedIds);
        var future = session.Segments.Skip(session.CurrentSegmentIndex + 1).ToDictionary(x => x.Original.Id);
        if (orderedIds.Count != future.Count || orderedIds.Distinct().Count() != future.Count || orderedIds.Any(x => !future.ContainsKey(x)))
            throw new ArgumentException("Provide every future segment ID exactly once.", nameof(orderedIds));
        AdvanceTime();
        session = session with { Segments = session.Segments.Take(session.CurrentSegmentIndex + 1)
            .Concat(orderedIds.Select(x => future[x])).ToImmutableArray() };
        return Result(Event(SessionEventType.SegmentReordered, string.Join(",", orderedIds)));
    }

    private Take? OpenTake => session.Takes.LastOrDefault(x => x.Ended is null);
    private SessionSegment Current => session.Segments[session.CurrentSegmentIndex];
    private long ReserveMs => checked(session.ReserveSeconds * 1000 + session.ReserveSubsecondMs);

    private void AdvanceTime()
    {
        long timestamp = clock.GetTimestamp();
        long deltaTicks = clock.GetElapsedTime(lastTimestamp, timestamp).Ticks;
        if (deltaTicks < 0) throw new InvalidOperationException("The monotonic clock moved backwards. Review the session time.");
        ReadCalendarTime();
        lastTimestamp = timestamp;
        if (deltaTicks == 0) return;
        if (session.State is SessionState.Preparing or SessionState.Ready or SessionState.Finished) return;
        sessionTicks = checked(sessionTicks + deltaTicks);
        session = session with { SessionElapsedMs = sessionTicks / TimeSpan.TicksPerMillisecond };
        if (session.State == SessionState.Recording) AccountRecordingDelta(deltaTicks);
    }

    private void AccountRecordingDelta(long deltaTicks)
    {
            long previousActiveMs = activeTicks / TimeSpan.TicksPerMillisecond;
            activeTicks = checked(activeTicks + deltaTicks);
            var current = Current;
            // Округление общее для активной шкалы: доли миллисекунды не теряются
            // и не создают дополнительный резерв при частых переходах сегментов.
            long elapsedMs = checked(current.ActiveElapsedMs + activeTicks / TimeSpan.TicksPerMillisecond - previousActiveMs);
            long budgetMs = current.BudgetSeconds * 1000L;
            long overrunDeltaMs = Math.Max(0, elapsedMs - budgetMs) - Math.Max(0, current.ActiveElapsedMs - budgetMs);
            long fromReserve = Math.Min(ReserveMs, overrunDeltaMs);
            SetReserve(ReserveMs - fromReserve);
            session = session with
            {
                UnfundedOverrunMs = checked(session.UnfundedOverrunMs + overrunDeltaMs - fromReserve),
                Segments = session.Segments.SetItem(session.CurrentSegmentIndex, current with
                { ActiveElapsedMs = elapsedMs, ReserveUsedMs = current.ReserveUsedMs + fromReserve })
            };
        session = session with { ActiveElapsedMs = activeTicks / TimeSpan.TicksPerMillisecond };
        if (session.Timer.Running)
        {
            long elapsed = session.ActiveElapsedMs - (activeTicks - deltaTicks) / TimeSpan.TicksPerMillisecond;
            long remaining = Math.Max(0, session.Timer.RemainingMs - elapsed);
            long overshoot = Math.Max(0, elapsed - session.Timer.RemainingMs);
            session = session with { Timer = session.Timer with { RemainingMs = remaining, Running = remaining > 0 } };
            if (remaining == 0)
            {
                var time = CaptureTime();
                var utc = time.UtcTimestamp.AddMilliseconds(-overshoot);
                var local = utc.ToOffset(time.LocalComputerTimestamp.Offset);
                time = time with { SessionElapsedMs = time.SessionElapsedMs - overshoot, ActiveElapsedMs = time.ActiveElapsedMs - overshoot,
                    SegmentActiveElapsedMs = time.SegmentActiveElapsedMs - overshoot, TakeElapsedMs = time.TakeElapsedMs - overshoot,
                    UtcTimestamp = utc, LocalComputerTimestamp = local, PredictedCameraTimecode = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":00" };
                automaticEvents.Add(new(Guid.NewGuid(), SessionEventType.TimerFinished, time));
            }
        }
    }

    private void ReadCalendarTime()
    {
        nowUtc = clock.UtcNow.ToUniversalTime();
        localOffset = clock.LocalNow.Offset;
    }

    private EventTime CaptureTime()
    {
        var take = OpenTake;
        var local = nowUtc.ToOffset(localOffset);
        return new(nowUtc, local, local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":00",
            session.SettingsSnapshot.CameraFps, session.SessionElapsedMs, session.ActiveElapsedMs,
            Current.Original.Id, Current.ActiveElapsedMs, take?.Id,
            take is null ? null : checked(session.SessionElapsedMs - take.Started.SessionElapsedMs + take.ElapsedAdjustmentMs))
        { TakeClockAdjustmentMs = take?.ElapsedAdjustmentMs ?? 0 };
    }

    private SessionEvent Event(SessionEventType type, string? detail = null) => new(Guid.NewGuid(), type, CaptureTime(), detail);
    private SessionTransition Result(params SessionEvent[] events) => new(session, events.ToImmutableArray());

    private SessionEvent CloseTake(EventTime time)
    {
        var take = OpenTake ?? throw new InvalidOperationException("No take is open.");
        int index = session.Takes.IndexOf(take);
        session = session with { Takes = session.Takes.SetItem(index, take with { Ended = time }) };
        return new(Guid.NewGuid(), SessionEventType.TakeEnded, time);
    }

    private void SetReserve(long milliseconds) => session = session with
    { ReserveSeconds = milliseconds / 1000, ReserveSubsecondMs = (int)(milliseconds % 1000) };

    private void ReturnUnusedBudget()
    {
        long unused = Math.Max(0, Current.BudgetSeconds * 1000L - Current.ActiveElapsedMs);
        long debtRepaid = Math.Min(unused, session.UnfundedOverrunMs);
        session = session with { UnfundedOverrunMs = session.UnfundedOverrunMs - debtRepaid };
        SetReserve(checked(ReserveMs + unused - debtRepaid));
    }

    private void SettleOverrun()
    {
        var projection = BudgetProjector.Project(session);
        long payment = projection.ProposedCutSeconds * 1000;
        long debtRepaid = Math.Min(payment, session.UnfundedOverrunMs);
        var segments = session.Segments.ToBuilder();
        for (int i = 0; i < projection.FutureBudgets.Length; i++)
        {
            int index = session.CurrentSegmentIndex + 1 + i;
            segments[index] = segments[index] with { BudgetSeconds = projection.FutureBudgets[i] };
        }
        session = session with { Segments = segments.ToImmutable(), UnfundedOverrunMs = session.UnfundedOverrunMs - debtRepaid,
            AccountedOverrunSeconds = checked(session.AccountedOverrunSeconds + projection.ProposedCutSeconds) };
        // При сокращении целой секунды оставшиеся миллисекунды сохраняются в резерве.
        SetReserve(checked(ReserveMs + payment - debtRepaid));
    }

    private SessionTransition Finish(SessionEvent segmentFinished, string reason)
    {
        var events = ImmutableArray.CreateBuilder<SessionEvent>();
        events.Add(segmentFinished);
        if (session.Timer.Running)
        {
            session = session with { Timer = session.Timer with { Running = false } };
            events.Add(Event(SessionEventType.TimerStopped, "sessionFinished"));
        }
        if (OpenTake is not null) events.Add(CloseTake(CaptureTime()));
        session = session with { State = SessionState.Finished, FinishedUtc = nowUtc, Pause = null };
        events.Add(Event(SessionEventType.SessionFinished, reason));
        return new(session, events.ToImmutable());
    }

    private void RequireState(SessionState required)
    {
        if (session.State != required) throw new InvalidOperationException($"This command requires the {required} state.");
    }

    private void RequireNoClapDialog()
    {
        if (IsClapDialogOpen) throw new InvalidOperationException("Close the clap dialog before using another command.");
    }

    private void RequireRunningSession()
    {
        RequireNoClapDialog();
        if (session.State is not (SessionState.Recording or SessionState.Paused or SessionState.AwaitingClap)
            || session.Pause is { RecordingStopped: null })
            throw new InvalidOperationException("This command requires an active session and an answered pause question.");
    }
}
