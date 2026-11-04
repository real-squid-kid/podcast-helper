using System.Collections.Immutable;

namespace PodcastHelper.Core.Models;

public enum SessionState { Preparing, Ready, Recording, Paused, AwaitingClap, Finished }

public sealed record SessionSegment(Segment Original, int BudgetSeconds, long ActiveElapsedMs)
{
    public long ReserveUsedMs { get; init; }
    public bool RemovedFromScenario { get; init; }
}

// Только сохраняемые данные. Переходы состояний и расчёт времени выполняет SessionEngine.
// Монотонные timestamp не сохраняются между запусками процесса.
public sealed record Session
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Scenario ScenarioSnapshot { get; init; }
    public required AppSettings SettingsSnapshot { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public SessionState State { get; init; } = SessionState.Preparing;
    public ImmutableArray<SessionSegment> Segments { get; init; } = [];
    public int CurrentSegmentIndex { get; init; }
    public long ReserveSeconds { get; init; }
    public int ReserveSubsecondMs { get; init; }
    public long PlannedOverageSeconds { get; init; }
    public long AccountedOverrunSeconds { get; init; }
    public long UnfundedOverrunMs { get; init; }
    public long SessionElapsedMs { get; init; }
    public long ActiveElapsedMs { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public ImmutableArray<Take> Takes { get; init; } = [];
    public ImmutableArray<Clap> Claps { get; init; } = [];
    public PauseContext? Pause { get; init; }
    public SessionTimer Timer { get; init; } = new();
    public ImmutableArray<Moment> Moments { get; init; } = [];
}
