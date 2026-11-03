using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Core.Services;

public sealed record RecoveryPlan(Session LastKnown, DateTimeOffset LastSavedUtc, long EstimatedClosedMs, bool RequiresConfirmation,
    bool ClockMovedBackwards, string? Warning)
{
    public string Heading => "While PodcastHelper was closed:";
}

public static class RecoveryPlanner
{
    public const string RecoveryWarning = "Session could be beyond recovery. Try to recover last session or start anew?";

    public static RecoveryPlan Plan(Session session, DateTimeOffset lastSavedUtc, DateTimeOffset nowUtc)
    {
        ModelValidation.Session(session);
        long interval = checked((nowUtc - lastSavedUtc).Ticks / TimeSpan.TicksPerMillisecond);
        bool question = interval > 180_000 || interval < 0 || session.State == SessionState.Recording || session.Pause?.RecordingStopped is null && session.State == SessionState.Paused;
        return new(session, lastSavedUtc, Math.Max(0, interval), question, interval < 0, interval > 180_000 ? RecoveryWarning : null);
    }

    // Автоматически оценивается только Session Time. Активное время и факт работы камер не додумываются.
    public static Session Conservative(RecoveryPlan plan)
    {
        var s = plan.LastKnown;
        if (s.State is SessionState.Preparing or SessionState.Ready or SessionState.Finished) return s;
        return s with { SessionElapsedMs = checked(s.SessionElapsedMs + plan.EstimatedClosedMs) };
    }

    // Явно подтверждённый оператором согласованный снимок; исходные события останутся в журнале.
    public static Session Reconcile(RecoveryPlan plan, Session corrected)
    {
        ModelValidation.Session(corrected);
        var source = plan.LastKnown.ScenarioSnapshot;
        var scenario = corrected.ScenarioSnapshot;
        if (corrected.Id != plan.LastKnown.Id || corrected.CreatedUtc != plan.LastKnown.CreatedUtc
            || scenario.Id != source.Id || scenario.Name != source.Name || scenario.TargetSeconds != source.TargetSeconds
            || scenario.ModifiedUtc != source.ModifiedUtc || !scenario.Segments.SequenceEqual(source.Segments))
            throw new ModelValidationException("Recovery must preserve the session identity and scenario.");
        if (!plan.LastKnown.Claps.All(x => corrected.Claps.Any(y => y.Id == x.Id))
            || !plan.LastKnown.Takes.All(x => corrected.Takes.Any(y => y.Id == x.Id)))
            throw new ModelValidationException("Recovery must retain existing takes and claps.");
        return corrected;
    }
}
