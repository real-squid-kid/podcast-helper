namespace PodcastHelper.Core.Abstractions;

public interface IFileSystem
{
    // null означает отсутствие файла, но не ошибку доступа или повреждение.
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    // При сбое до замены исходный файл должен оставаться целым.
    Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken = default);
    Task CheckWriteAccessAsync(string directory, CancellationToken cancellationToken = default);
}
