using System.Collections.Immutable;
using System.Text;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.Infrastructure.Sessions;

public sealed class SessionJournalStore(PortablePaths paths, IJournalFileSystem fileSystem)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly JsonDocumentStore snapshots = new(fileSystem);
    internal Task<IDisposable> LeaseAsync(Guid id, CancellationToken ct) => fileSystem.AcquireLeaseAsync(Path.Combine(paths.SessionDirectory(id), "session.lock"), ct);
    internal string FirstPath(Guid id) => Path.Combine(paths.SessionDirectory(id), "events-000001.jsonl");
    internal async Task<bool> HasJournalAsync(Guid id, CancellationToken ct) =>
        (await fileSystem.EnumerateFilesAsync(paths.SessionDirectory(id), "events-*.jsonl", ct).ConfigureAwait(false)).Count > 0
        || await fileSystem.ReadBytesAsync(SnapshotPath(id), ct).ConfigureAwait(false) is not null
        || await fileSystem.ReadBytesAsync(paths.SessionFile(id), ct).ConfigureAwait(false) is not null;
    private string SnapshotPath(Guid id) => Path.Combine(paths.SessionDirectory(id), "recovery.json");

    public async Task<JournalReadResult> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var files = (await fileSystem.EnumerateFilesAsync(paths.SessionDirectory(id), "events-*.jsonl", ct).ConfigureAwait(false))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var records = ImmutableArray.CreateBuilder<JournalRecord>();
        var seen = new Dictionary<Guid, string>();
        string previousHash = "";
        long sequence = 0;
        bool incomplete = false, lastIncomplete = false;
        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            if (Path.GetFileName(files[fileIndex]) != $"events-{fileIndex + 1:D6}.jsonl")
                throw new InvalidDocumentException("A journal fragment is missing or out of order.");
            byte[] bytes = await fileSystem.ReadBytesAsync(files[fileIndex], ct).ConfigureAwait(false) ?? throw new InvalidDocumentException("Journal fragment disappeared.");
            int start = 0;
            while (start < bytes.Length)
            {
                int end = Array.IndexOf(bytes, (byte)'\n', start);
                if (end < 0) break;
                string line;
                try { line = Utf8.GetString(bytes, start, end - start).TrimEnd('\r'); }
                catch (DecoderFallbackException) { throw new InvalidDocumentException("A complete journal record has invalid UTF-8."); }
                start = end + 1;
                var record = JournalCodec.Decode(line);
                if (record.SessionId != id) throw new InvalidDocumentException("Journal belongs to another session.");
                if (seen.TryGetValue(record.Id, out string? known))
                {
                    if (known != record.Sha256) throw new InvalidDocumentException("Duplicate journal ID has different contents.");
                    continue;
                }
                if (record.Sequence != sequence + 1 || record.PreviousHash != previousHash)
                    throw new InvalidDocumentException("Journal sequence or integrity chain is broken.");
                if (sequence == 0 && record.Command.Kind != SessionCommandKind.Created
                    || sequence > 0 && record.Command.Kind == SessionCommandKind.Created)
                    throw new InvalidDocumentException("Invalid journal creation record.");
                seen.Add(record.Id, record.Sha256);
                records.Add(record);
                sequence = record.Sequence;
                previousHash = record.Sha256;
            }
            lastIncomplete = start < bytes.Length;
            incomplete |= lastIncomplete;
        }
        if (records.Count == 0) throw new InvalidDocumentException("No complete session journal records were found.");
        RecoverySnapshot? snapshot = null;
        bool snapshotIgnored = false;
        try
        {
            snapshot = await snapshots.LoadAsync<RecoverySnapshot>(SnapshotPath(id), "recovery", ValidateSnapshot, ct).ConfigureAwait(false);
            if (snapshot is not null && (snapshot.Sequence > records.Count || snapshot.State.Engine.Session.Id != id
                || records[(int)snapshot.Sequence - 1].Sha256 != snapshot.JournalHash
                || records[(int)snapshot.Sequence - 1].UtcTimestamp != snapshot.LastSavedUtc))
            { snapshot = null; snapshotIgnored = true; }
        }
        catch (Exception ex) when (ex is InvalidDocumentException or ModelValidationException)
        { snapshotIgnored = true; }
        DurableSession? state = snapshot?.State;
        foreach (var record in records.Where(x => x.Sequence > (snapshot?.Sequence ?? 0)))
        {
            state = JournalCodec.Apply(state, record.Changes);
            if (state.Engine.Session.Id != id) throw new InvalidDocumentException("State changes reference another session.");
        }
        ValidateReferences(state!, records);
        string nextPath = files.Length == 0 ? FirstPath(id) : lastIncomplete
            ? Path.Combine(paths.SessionDirectory(id), $"events-{files.Length + 1:D6}.jsonl") : files[^1];
        return new(records.ToImmutable(), state!, incomplete, snapshotIgnored, nextPath);
    }

    public async Task<IReadOnlyList<SessionDiscovery>> DiscoverAsync(CancellationToken ct = default)
    {
        var result = new List<SessionDiscovery>();
        foreach (var directory in await fileSystem.EnumerateDirectoriesAsync(paths.SessionsDirectory, ct).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            IDisposable lease;
            try { lease = await LeaseAsync(id, ct).ConfigureAwait(false); }
            catch (IOException) { result.Add(new(id, "Session is in use or unavailable.", true, false, null, null)); continue; }
            catch (UnauthorizedAccessException) { result.Add(new(id, "Session access was denied.", false, false, null, null)); continue; }
            using (lease)
            try
            {
                var loaded = await ReadAsync(id, ct).ConfigureAwait(false);
                result.Add(new(id, null, false, loaded.State.Archived, loaded.State.Engine.Session.State, loaded.Records[^1].UtcTimestamp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException or ModelValidationException)
            { result.Add(new(id, ex is UnsupportedSchemaException ? "Unsupported session format." : "Session data could not be read.", false, false, null, null)); }
        }
        return result.OrderByDescending(x => x.LastSavedUtc).ToArray();
    }

    internal Task AppendAsync(string path, JournalRecord record, CancellationToken ct) =>
        fileSystem.AppendDurablyAsync(path, JournalCodec.Serialize(record) + "\n", ct);

    internal Task WriteSnapshotAsync(JournalRecord last, DurableSession state, CancellationToken ct) =>
        snapshots.SaveAsync(SnapshotPath(last.SessionId), "recovery", new RecoverySnapshot(last.Sequence, last.Sha256, last.UtcTimestamp, state),
            ValidateSnapshot, ct, repairCorrupt: true);

    internal static DurableSession StateAt(IEnumerable<JournalRecord> records, long sequence)
    {
        DurableSession? state = null;
        foreach (var record in records.TakeWhile(x => x.Sequence <= sequence)) state = JournalCodec.Apply(state, record.Changes);
        return state ?? throw new InvalidDocumentException("Undo source state is missing.");
    }

    private static void ValidateSnapshot(RecoverySnapshot snapshot)
    {
        if (snapshot.Sequence <= 0 || string.IsNullOrEmpty(snapshot.JournalHash) || snapshot.LastSavedUtc.Offset != TimeSpan.Zero || snapshot.State is null)
            throw new InvalidDocumentException("Invalid recovery snapshot.");
        JournalCodec.Validate(snapshot.State);
    }

    private static void ValidateReferences(DurableSession state, IEnumerable<JournalRecord> records)
    {
        var byId = records.ToDictionary(x => x.Id);
        if (state.UndoStack.Select(x => x.CommandId).Distinct().Count() != state.UndoStack.Length)
            throw new InvalidDocumentException("Duplicate Undo references.");
        foreach (var undo in state.UndoStack)
            if (!byId.TryGetValue(undo.CommandId, out var command) || command.Sequence != undo.Sequence || command.Command.Kind != undo.Kind
                || undo.BeforeSequence != undo.Sequence - 1) throw new InvalidDocumentException("Broken Undo reference.");
        foreach (var record in records)
            if (record.UndoOf is { } target && (!byId.TryGetValue(target, out var original) || original.Sequence >= record.Sequence
                || record.Command.Kind is not (SessionCommandKind.Undo or SessionCommandKind.Redo)
                || record.Command.Kind == SessionCommandKind.Redo && original.Command.Kind != SessionCommandKind.Undo)) throw new InvalidDocumentException("Invalid Undo audit link.");
    }
}
