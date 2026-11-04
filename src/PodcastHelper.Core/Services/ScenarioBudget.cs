namespace PodcastHelper.Core.Services;

public sealed record ScenarioBalance(long TotalSeconds, long ReserveSeconds, long PlannedOverageSeconds);
public static class ScenarioBudget
{
    public static ScenarioBalance Analyze(int targetSeconds, IEnumerable<int> durations)
    {
        if (targetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetSeconds));
        long total = 0;
        foreach (int duration in durations)
        {
            if (duration <= 0) throw new ArgumentOutOfRangeException(nameof(durations));
            total = checked(total + duration);
        }
        return new(total, Math.Max(0, targetSeconds - total), Math.Max(0, total - targetSeconds));
    }
}
