using System.Globalization;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Core.Services;

public enum RecoveryStatus { Recording, PausedTakeContinues, PausedTakeStopped, AwaitingClap }
public sealed record RecoveryEdits(long SessionElapsedMs, int SegmentIndex, long SegmentActiveElapsedMs, long TakeElapsedMs, RecoveryStatus Status)
{
    public long? TimerRemainingMs { get; init; }
}

public static class RecoveryEditor
{
    public static Session Apply(RecoveryPlan plan, RecoveryEdits edits, DateTimeOffset nowUtc, TimeSpan localOffset)
    {
        var s = plan.LastKnown;
        if (edits.SessionElapsedMs < 0 || edits.SegmentActiveElapsedMs < 0 || edits.TakeElapsedMs < 0
            || edits.SegmentIndex < 0 || edits.SegmentIndex >= s.Segments.Length || !Enum.IsDefined(edits.Status))
            throw new ModelValidationException("Enter non-negative times and select a segment and recording state.");
        if (s.StartedUtc is null)
        {
            if (edits.SessionElapsedMs != 0 || edits.SegmentActiveElapsedMs != 0 || edits.TakeElapsedMs != 0)
                throw new ModelValidationException("An unstarted session must keep zero elapsed times.");
            return s;
        }
        if (s.Takes.IsEmpty) throw new ModelValidationException("No take is available for recovery.");
        var segment = s.Segments[edits.SegmentIndex];
        long active = checked(s.ActiveElapsedMs + edits.SegmentActiveElapsedMs - segment.ActiveElapsedMs);
        long oldOverrun = Math.Max(0, segment.ActiveElapsedMs - segment.BudgetSeconds * 1000L);
        long newOverrun = Math.Max(0, edits.SegmentActiveElapsedMs - segment.BudgetSeconds * 1000L);
        long reserve = checked(s.ReserveSeconds * 1000 + s.ReserveSubsecondMs), debt = s.UnfundedOverrunMs;
        long used = segment.ReserveUsedMs;
        if (newOverrun >= oldOverrun)
        {
            long fromReserve = Math.Min(reserve, newOverrun - oldOverrun);
            reserve -= fromReserve; used += fromReserve; debt = checked(debt + newOverrun - oldOverrun - fromReserve);
        }
        else
        {
            long credit = oldOverrun - newOverrun, repaid = Math.Min(debt, credit);
            debt -= repaid; reserve = checked(reserve + credit - repaid); used = Math.Min(used, newOverrun);
        }
        var last = s.Takes[^1];
        if (edits.TakeElapsedMs > edits.SessionElapsedMs || edits.SessionElapsedMs < last.Started.SessionElapsedMs)
            throw new ModelValidationException("Take Time must fit within Session Time.");
        long adjustment = edits.TakeElapsedMs - (edits.SessionElapsedMs - last.Started.SessionElapsedMs);
        var utc = nowUtc.ToUniversalTime(); var local = utc.ToOffset(localOffset);
        var stamp = new EventTime(utc, local, local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ":00", s.SettingsSnapshot.CameraFps,
            edits.SessionElapsedMs, active, segment.Original.Id, edits.SegmentActiveElapsedMs, last.Id, edits.TakeElapsedMs)
        { TakeClockAdjustmentMs = adjustment };
        bool stopped = edits.Status is RecoveryStatus.PausedTakeStopped or RecoveryStatus.AwaitingClap;
        long timerRemaining = edits.TimerRemainingMs ?? (s.Timer.Running ? Math.Max(0, checked(s.Timer.RemainingMs - (active - s.ActiveElapsedMs))) : s.Timer.RemainingMs);
        if (timerRemaining < 0) throw new ModelValidationException("Timer remaining time must not be negative.");
        var result = s with
        {
            SessionElapsedMs = edits.SessionElapsedMs, ActiveElapsedMs = active, CurrentSegmentIndex = edits.SegmentIndex,
            Segments = s.Segments.SetItem(edits.SegmentIndex, segment with { ActiveElapsedMs = edits.SegmentActiveElapsedMs, ReserveUsedMs = used }),
            ReserveSeconds = reserve / 1000, ReserveSubsecondMs = (int)(reserve % 1000), UnfundedOverrunMs = debt,
            Takes = s.Takes.SetItem(s.Takes.Length - 1, last with { ElapsedAdjustmentMs = adjustment, Ended = stopped ? stamp : null }),
            State = edits.Status switch { RecoveryStatus.Recording => SessionState.Recording, RecoveryStatus.AwaitingClap => SessionState.AwaitingClap, _ => SessionState.Paused },
            Pause = edits.Status == RecoveryStatus.Recording ? null : new PauseContext(stamp, stopped), FinishedUtc = null,
            Timer = s.Timer with { RemainingMs = timerRemaining, Running = s.Timer.Running && timerRemaining > 0 }
        };
        return RecoveryPlanner.Reconcile(plan, result);
    }
}
