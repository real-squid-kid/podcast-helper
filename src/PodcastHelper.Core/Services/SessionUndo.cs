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
            Pause = current.Pause is null ? (current.State == SessionState.Finished ? before.Pause : null)
                : current.Pause with { Started = Remap(current.Pause.Started) }
        };
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
}
