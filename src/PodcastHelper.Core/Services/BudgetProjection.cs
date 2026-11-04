using System.Collections.Immutable;
using PodcastHelper.Core.Models;

namespace PodcastHelper.Core.Services;

public enum SegmentWarning { None, Yellow, Red, Overdue, Overscheduled }

public sealed record BudgetProjection(
    long PersonalRemainingMs,
    long ReserveRemainingMs,
    long EffectiveRemainingMs,
    long UnfundedOverrunMs,
    long UncoveredAfterDistributionMs,
    ImmutableArray<int> FutureBudgets,
    long ProposedCutSeconds,
    SegmentWarning Warning,
    bool ReserveInUse)
{
    public long PlannedExcessMs { get; init; }
}

public static class BudgetProjector
{
    public static BudgetProjection Project(Session session)
    {
        var current = session.Segments[session.CurrentSegmentIndex];
        long own = Math.Max(0, current.BudgetSeconds * 1000L - current.ActiveElapsedMs);
        long reserve = checked(session.ReserveSeconds * 1000 + session.ReserveSubsecondMs);
        long secondsToCut = session.UnfundedOverrunMs / 1000 + (session.UnfundedOverrunMs % 1000 > 0 ? 1 : 0);
        var future = session.Segments.Skip(session.CurrentSegmentIndex + 1).Select(x => x.BudgetSeconds).ToArray();
        var distribution = BudgetDistributor.Distribute(future, secondsToCut, session.SettingsSnapshot.OverrunDistribution);
        long uncovered = Math.Max(0, session.UnfundedOverrunMs - distribution.CoveredSeconds * 1000);
        long effective = checked(own + reserve);
        var settings = session.SettingsSnapshot;
        bool yellow = effective <= settings.SegmentWarningMinutes * 60_000L;
        bool red = effective <= settings.SegmentWrapUpMinutes * 60_000L;
        SegmentWarning warning = uncovered > 0 ? SegmentWarning.Overscheduled
            : session.UnfundedOverrunMs > 0 || (own == 0 && reserve == 0) ? SegmentWarning.Overdue
            : yellow && red ? settings.SegmentWrapUpMinutes <= settings.SegmentWarningMinutes ? SegmentWarning.Red : SegmentWarning.Yellow
            : red ? SegmentWarning.Red : yellow ? SegmentWarning.Yellow
            : SegmentWarning.None;
        long planTotalMs = checked(session.ActiveElapsedMs + reserve + (session.State == SessionState.Finished ? 0 : own)
            + future.Sum(x => x * 1000L) - session.UnfundedOverrunMs);
        return new(own, reserve, effective, session.UnfundedOverrunMs, uncovered, distribution.Budgets,
            distribution.CoveredSeconds, warning, current.ReserveUsedMs > 0 && reserve > 0)
        { PlannedExcessMs = Math.Max(0, planTotalMs - session.ScenarioSnapshot.TargetSeconds * 1000L) };
    }
}
