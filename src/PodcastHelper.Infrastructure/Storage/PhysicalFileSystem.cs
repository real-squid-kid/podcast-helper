using System.Text;
using PodcastHelper.Core.Abstractions;

namespace PodcastHelper.Infrastructure.Storage;

public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            return Utf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (DecoderFallbackException) { throw new InvalidDocumentException("The file is not valid UTF-8."); }
    }

    public async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("An absolute file path is required.", nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Utf8.GetBytes(content), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Временный файл находится на том же томе. Исходник не удаляется перед заменой.
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task CheckWriteAccessAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, $".write-check.{Guid.NewGuid():N}.tmp");
        await using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
