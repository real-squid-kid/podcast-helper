using System.Collections.Immutable;
using System.Text.Json;
using PodcastHelper.Core.Models;

namespace PodcastHelper.Infrastructure.Sessions;

public enum SessionCommandKind { Created, Ready, OpenClap, CancelClap, Clap, Pause, AnswerPause, Resume, NextSegment, WrapUp, Reorder, Checkpoint, Undo, Recovered, Abandoned, ResultsClosed,
    Automatic, TimerStart, TimerStop, TimerAdjust, AddMoment, RemoveMoment, AddSegment, RemoveSegment, EditSegment, RemoveCurrent, CorrectMetadata, Redo }
public sealed record SessionCommand(SessionCommandKind Kind, bool? RecordingStopped = null, ImmutableArray<Guid> Order = default)
{
    public ImmutableArray<Guid> Order { get; init; } = Order.IsDefault ? [] : Order;
    public int Seconds { get; init; }
    public string? CategoryId { get; init; }
    public Guid EntityId { get; init; }
    public Segment? Segment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public long? InputTimestamp { get; init; }
    public DateTimeOffset? PressedUtc { get; init; }
    public TimeSpan? PressedOffset { get; init; }
}
public static class SessionCommands
{
    public static bool IsReversible(SessionCommandKind kind) => kind is SessionCommandKind.NextSegment or SessionCommandKind.Reorder
        or SessionCommandKind.TimerStart or SessionCommandKind.TimerStop or SessionCommandKind.TimerAdjust or SessionCommandKind.AddMoment
        or SessionCommandKind.RemoveMoment or SessionCommandKind.AddSegment or SessionCommandKind.RemoveSegment or SessionCommandKind.EditSegment or SessionCommandKind.RemoveCurrent;
    public static string Label(SessionCommandKind kind) => kind switch
    {
        SessionCommandKind.NextSegment => "Next segment", SessionCommandKind.Reorder => "Reorder segments", SessionCommandKind.TimerStart => "Start timer",
        SessionCommandKind.TimerStop => "Stop timer", SessionCommandKind.TimerAdjust => "Adjust timer", SessionCommandKind.AddMoment => "Add moment",
        SessionCommandKind.RemoveMoment => "Remove moment", SessionCommandKind.AddSegment => "Add segment", SessionCommandKind.RemoveSegment => "Remove segment",
        SessionCommandKind.EditSegment => "Edit segment", SessionCommandKind.RemoveCurrent => "Remove current segment", _ => kind.ToString()
    };
}
public sealed record UndoReference(Guid CommandId, long BeforeSequence, long Sequence, SessionCommandKind Kind);
public sealed record DurableSession(EngineCheckpoint Engine, ImmutableArray<UndoReference> UndoStack, bool Archived = false, string? ArchiveReason = null, bool ResultsClosed = false);
public sealed record PatchOperation(ImmutableArray<string> Path, bool Remove, JsonElement? Value);
public sealed record JournalRecord(int SchemaVersion, Guid Id, Guid SessionId, long Sequence, DateTimeOffset UtcTimestamp,
    SessionCommand Command, ImmutableArray<SessionEvent> Events, ImmutableArray<PatchOperation> Changes, Guid? UndoOf, string PreviousHash, string Sha256);
public sealed record RecoverySnapshot(long Sequence, string JournalHash, DateTimeOffset LastSavedUtc, DurableSession State);
public sealed record JournalReadResult(ImmutableArray<JournalRecord> Records, DurableSession State, bool IncompleteTail, bool SnapshotIgnored, string NextJournalPath);
public sealed record PersistenceResult(SessionTransition Transition, bool SnapshotSaved);
public sealed record SessionDiscovery(Guid SessionId, string? Error, bool InUse, bool Archived, SessionState? State, DateTimeOffset? LastSavedUtc);
