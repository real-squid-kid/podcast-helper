using PodcastHelper.Core.Abstractions;

namespace PodcastHelper.Infrastructure.Storage;

public interface IArchiveFileSystem : IJournalFileSystem
{
    Task WriteBytesAtomicallyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    // Только собственные файлы сессии. Экспорты, чужие файлы и session.lock сохраняются.
    // session.lock открыт до конца удаления: другой процесс не может стать писателем.
    Task DeleteSessionDataAsync(string directory, CancellationToken ct = default);
}
