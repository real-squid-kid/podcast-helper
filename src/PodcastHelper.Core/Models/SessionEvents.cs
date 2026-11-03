using System.Collections.Immutable;

namespace PodcastHelper.Core.Models;

public sealed record EventTime(
    DateTimeOffset UtcTimestamp,
    DateTimeOffset LocalComputerTimestamp,
    string PredictedCameraTimecode,
    decimal Fps,
    long SessionElapsedMs,
    long ActiveElapsedMs,
    Guid SegmentId,
    long SegmentActiveElapsedMs,
    Guid? TakeId,
    long? TakeElapsedMs)
{
    public long TakeClockAdjustmentMs { get; init; }
}

public sealed record Take(Guid Id, int Number, EventTime Started, EventTime? Ended = null)
{
    public long ElapsedAdjustmentMs { get; init; }
}
public sealed record Clap(Guid Id, Guid TakeId, EventTime Time);
public sealed record PauseContext(EventTime Started, bool? RecordingStopped = null);

public enum SessionEventType
{
    Ready, Clap, TakeStarted, TakeEnded, Pause, PauseAnswered, Resume,
    SegmentStarted, SegmentFinished, SegmentReordered, SessionFinished, Undo, Recovered
}

public sealed record SessionEvent(Guid Id, SessionEventType Type, EventTime Time, string? Detail = null);
public sealed record SessionTransition(Session Session, ImmutableArray<SessionEvent> Events);
