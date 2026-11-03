namespace PodcastHelper.Core.Abstractions;

public interface IJournalFileSystem : IFileSystem
{
    Task<byte[]?> ReadBytesAsync(string path, CancellationToken ct = default);
    Task AppendDurablyAsync(string path, string text, CancellationToken ct = default);
    Task<IDisposable> AcquireLeaseAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EnumerateFilesAsync(string directory, string pattern, CancellationToken ct = default);
    Task<IReadOnlyList<string>> EnumerateDirectoriesAsync(string directory, CancellationToken ct = default);
}
