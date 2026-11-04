using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.Infrastructure.Sessions;

public sealed record SessionArchive(JournalReadResult Journal)
{
    public Session Session => Journal.State.Engine.Session;
    public string Status => Journal.State.Archived ? "Archived" : Session.State == SessionState.Finished ? "Finished" : "Unfinished";
}
public sealed record HistoryEntry(Guid Id, string Name, DateTimeOffset? CreatedUtc, string Status,
    long PlannedMs, long ActualMs, int Segments, int Moments, int Takes, string? Error = null, bool InUse = false);

public sealed class SessionHistoryStore(PortablePaths paths, IArchiveFileSystem files)
{
    private readonly SessionJournalStore journals = new(paths, files);
    public async Task<SessionArchive> ReadAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await journals.LeaseAsync(id, ct).ConfigureAwait(false);
        return new(await journals.ReadAsync(id, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<HistoryEntry>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<HistoryEntry>();
        foreach (string directory in await files.EnumerateDirectoriesAsync(paths.SessionsDirectory, ct).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty) continue;
            IDisposable lease;
            try { lease = await journals.LeaseAsync(id, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result.Add(new(id, "Session unavailable", null, "In use / unavailable", 0, 0, 0, 0, 0, "Close the other instance, or restore folder access, then refresh.", true)); continue; }
            using (lease)
            try
            {
                if (!await journals.HasJournalAsync(id, ct).ConfigureAwait(false)) continue;
                var archive = new SessionArchive(await journals.ReadAsync(id, ct).ConfigureAwait(false)); var s = archive.Session;
                result.Add(new(id, s.ScenarioSnapshot.Name, s.CreatedUtc, archive.Status, s.ScenarioSnapshot.TargetSeconds * 1000L,
                    s.SessionElapsedMs, s.Segments.Length, s.Moments.Length, s.Takes.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException or ModelValidationException)
            { result.Add(new(id, "Session could not be read", null, "Needs review", 0, 0, 0, 0, 0, "Restore this session from a backup, or delete it after reviewing the warning.")); }
        }
        return result.OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id).ToArray();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = await journals.LeaseAsync(id, ct).ConfigureAwait(false);
        await files.DeleteSessionDataAsync(paths.SessionDirectory(id), ct).ConfigureAwait(false);
    }
}
