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
    bool ReserveInUse);

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
        SegmentWarning warning = uncovered > 0 ? SegmentWarning.Overscheduled
            : session.UnfundedOverrunMs > 0 || (own == 0 && reserve == 0) ? SegmentWarning.Overdue
            : effective <= Math.Min(settings.SegmentWarningMinutes, settings.SegmentWrapUpMinutes) * 60_000L ? SegmentWarning.Red
            : effective <= Math.Max(settings.SegmentWarningMinutes, settings.SegmentWrapUpMinutes) * 60_000L ? SegmentWarning.Yellow
            : SegmentWarning.None;
        return new(own, reserve, effective, session.UnfundedOverrunMs, uncovered, distribution.Budgets,
            distribution.CoveredSeconds, warning, current.ReserveUsedMs > 0 && reserve > 0);
    }
}
