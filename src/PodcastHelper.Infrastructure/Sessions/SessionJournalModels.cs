using System.Collections.Immutable;
using System.Text.Json;
using PodcastHelper.Core.Models;

namespace PodcastHelper.Infrastructure.Sessions;

public enum SessionCommandKind { Created, Ready, OpenClap, CancelClap, Clap, Pause, AnswerPause, Resume, NextSegment, WrapUp, Reorder, Checkpoint, Undo, Recovered, Abandoned, ResultsClosed }
public sealed record SessionCommand(SessionCommandKind Kind, bool? RecordingStopped = null, ImmutableArray<Guid> Order = default)
{
    public ImmutableArray<Guid> Order { get; init; } = Order.IsDefault ? [] : Order;
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
