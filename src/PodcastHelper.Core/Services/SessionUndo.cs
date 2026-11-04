using System.Collections.Immutable;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Core.Services;

public sealed record UndoAssessment(Guid CommandId, string Action, bool CanApply, bool RequiresConfirmation, string Consequences);

public static class SessionUndo
{
    public static Session UndoNext(Session before, Session current, long afterFinishMs = 0, long? activeAfterFinishMs = null)
    {
        if (before.Id != current.Id || before.Segments.Length != current.Segments.Length
            || !before.Segments.Select(x => x.Original.Id).ToHashSet().SetEquals(current.Segments.Select(x => x.Original.Id)))
            throw new InvalidOperationException("Segment structure changed. Review dependent edits before undoing this transition.");
        long extraActive = activeAfterFinishMs ?? (before.State == SessionState.Recording ? afterFinishMs : 0);
        long activeDelta = checked(current.ActiveElapsedMs - before.ActiveElapsedMs + extraActive);
        if (activeDelta < 0) throw new InvalidOperationException("Active time cannot move backwards.");
        int index = before.CurrentSegmentIndex;
        var original = before.Segments[index];
        long elapsed = checked(original.ActiveElapsedMs + activeDelta);
        long deltaOverrun = Math.Max(0, elapsed - original.BudgetSeconds * 1000L)
            - Math.Max(0, original.ActiveElapsedMs - original.BudgetSeconds * 1000L);
        long reserveBefore = checked(before.ReserveSeconds * 1000 + before.ReserveSubsecondMs);
        long used = Math.Min(reserveBefore, deltaOverrun);
        long reserve = reserveBefore - used;
        var segments = before.Segments.SetItem(index, original with { ActiveElapsedMs = elapsed, ReserveUsedMs = original.ReserveUsedMs + used });
        EventTime Remap(EventTime time) => time.ActiveElapsedMs < before.ActiveElapsedMs ? time : time with
        { SegmentId = original.Original.Id, SegmentActiveElapsedMs = checked(original.ActiveElapsedMs + time.ActiveElapsedMs - before.ActiveElapsedMs) };
        var oldClaps = before.Claps.Select(x => x.Id).ToHashSet();
        var oldTakes = before.Takes.ToDictionary(x => x.Id);
        var takes = current.Takes.Select(take =>
        {
            var started = oldTakes.ContainsKey(take.Id) ? take.Started : Remap(take.Started);
            var ended = take.Ended is null ? null : oldTakes.TryGetValue(take.Id, out var old) && old.Ended is not null ? take.Ended : Remap(take.Ended);
            if (current.State == SessionState.Finished && take == current.Takes[^1] && oldTakes.TryGetValue(take.Id, out var oldTake) && oldTake.Ended is null)
                ended = null;
            return take with { Started = started, Ended = ended };
        }).ToImmutableArray();
        var result = current with
        {
            State = current.State == SessionState.Finished ? before.State : current.State,
            FinishedUtc = null, CurrentSegmentIndex = index, Segments = segments,
            ActiveElapsedMs = checked(current.ActiveElapsedMs + extraActive),
            SessionElapsedMs = checked(current.SessionElapsedMs + afterFinishMs),
            ReserveSeconds = reserve / 1000, ReserveSubsecondMs = (int)(reserve % 1000),
            UnfundedOverrunMs = checked(before.UnfundedOverrunMs + deltaOverrun - used),
            AccountedOverrunSeconds = before.AccountedOverrunSeconds,
            Takes = takes,
            Claps = current.Claps.Select(x => oldClaps.Contains(x.Id) ? x : x with { Time = Remap(x.Time) }).ToImmutableArray(),
            Moments = current.Moments.Select(moment => moment with { Presses = moment.Presses.Select((press, i) =>
                before.Moments.FirstOrDefault(x => x.Id == moment.Id) is { } oldMoment && i < oldMoment.Presses.Length
                    ? press : press with { Time = Remap(press.Time) }).ToImmutableArray() }).ToImmutableArray(),
            Pause = current.Pause is null ? (current.State == SessionState.Finished ? before.Pause : null)
                : current.Pause with { Started = Remap(current.Pause.Started) }
        };
        // Финальный Next остановил таймер вместе с сессией. После отмены восстанавливаем
        // его ход так, словно перехода не было, включая активное время на экране итогов.
        if (current.State == SessionState.Finished) result = UndoTimer(before, result);
        ModelValidation.Session(result);
        return result;
    }

    public static Session UndoReorder(Session before, Session current)
    {
        var future = current.Segments.Skip(current.CurrentSegmentIndex + 1).ToDictionary(x => x.Original.Id);
        var order = before.Segments.Skip(before.CurrentSegmentIndex + 1).Select(x => x.Original.Id).ToArray();
        if (before.CurrentSegmentIndex != current.CurrentSegmentIndex || !order.ToHashSet().SetEquals(future.Keys))
            throw new InvalidOperationException("Segment structure changed. Review dependent edits before undoing the order.");
        var result = current with { Segments = current.Segments.Take(current.CurrentSegmentIndex + 1)
            .Concat(order.Select(x => future[x])).ToImmutableArray() };
        ModelValidation.Session(result);
        return result;
    }

    public static Session UndoTimer(Session before, Session current)
    {
        long remaining = before.Timer.Running ? Math.Max(0, before.Timer.RemainingMs - (current.ActiveElapsedMs - before.ActiveElapsedMs)) : before.Timer.RemainingMs;
        return current with { Timer = before.Timer with { RemainingMs = remaining, Running = before.Timer.Running && remaining > 0 } };
    }

    public static Session UndoMoments(Session before, Session current) => current with { Moments = before.Moments };

    public static Session UndoFutureEdit(Session before, Session current)
    {
        if (before.CurrentSegmentIndex != current.CurrentSegmentIndex)
            throw new InvalidOperationException("Undo the later segment transition first.");
        if (current.Segments.Any(x => !before.Segments.Any(y => y.Original.Id == x.Original.Id) && x.ActiveElapsedMs > 0))
            throw new InvalidOperationException("The added segment has recorded time. Undo its transition first.");
        var aligned = current with { Segments = before.Segments.Select(original =>
            current.Segments.FirstOrDefault(x => x.Original.Id == original.Original.Id) ?? original).ToImmutableArray() };
        return UndoNext(before, aligned);
    }
}
