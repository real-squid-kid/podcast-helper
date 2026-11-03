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

    public bool IsClapDialogOpen { get; private set; }
    public bool IsOnAir => session.State == SessionState.Recording;
    public long? TakeElapsedMs => session.Pause is { RecordingStopped: null } ? null : OpenTake is { } take
        ? session.SessionElapsedMs - take.Started.SessionElapsedMs : null;

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
    public SessionTransition ClapFromEnter()
    {
        bool first = session.State == SessionState.Ready;
        bool newTake = first || session.State == SessionState.AwaitingClap;
        if (!newTake && !(session.State == SessionState.Recording && IsClapDialogOpen))
            throw new InvalidOperationException("Press Enter in the clap dialog, or wait until the session is ready.");
        AdvanceTime();
        var events = ImmutableArray.CreateBuilder<SessionEvent>();
        if (newTake)
        {
            session = session with { State = SessionState.Recording, Pause = null, StartedUtc = session.StartedUtc ?? nowUtc };
            var takeId = Guid.NewGuid();
            var started = CaptureTime() with { TakeId = takeId, TakeElapsedMs = 0 };
            session = session with { Takes = session.Takes.Add(new Take(takeId, session.Takes.Length + 1, started)) };
            events.Add(new(Guid.NewGuid(), SessionEventType.TakeStarted, started));
        }
        var time = CaptureTime();
        var clap = new Clap(Guid.NewGuid(), OpenTake!.Id, time);
        session = session with { Claps = session.Claps.Add(clap) };
        events.Add(new(clap.Id, SessionEventType.Clap, time));
        if (first) events.Add(Event(SessionEventType.SegmentStarted));
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
        if (session.State == SessionState.Recording)
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
        }
        session = session with { SessionElapsedMs = sessionTicks / TimeSpan.TicksPerMillisecond,
            ActiveElapsedMs = activeTicks / TimeSpan.TicksPerMillisecond };
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
            take is null ? null : session.SessionElapsedMs - take.Started.SessionElapsedMs);
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
