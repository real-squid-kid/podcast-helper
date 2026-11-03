using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Infrastructure.Storage;

public sealed class UnsupportedSchemaException() : Exception("This file uses an unsupported format version.");
public sealed class InvalidDocumentException(string message) : Exception(message);

public sealed class JsonDocumentStore(IFileSystem fileSystem)
{
    public const int CurrentSchemaVersion = 1;
    public Task CheckWriteAccessAsync(string directory, CancellationToken ct = default) => fileSystem.CheckWriteAccessAsync(directory, ct);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private sealed record Envelope
    {
        public required string Kind { get; init; }
        public required int SchemaVersion { get; init; }
        public required JsonElement Data { get; init; }
        public required string Sha256 { get; init; }
    }

    public async Task<T?> LoadAsync<T>(string path, string kind, Action<T> validate, CancellationToken cancellationToken = default) where T : class
    {
        string? text = await fileSystem.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (text is null) return null;
        return Decode(text, kind, validate);
    }

    public async Task SaveAsync<T>(string path, string kind, T value, Action<T> validate, CancellationToken cancellationToken = default, bool repairCorrupt = false) where T : class
    {
        validate(value);
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Не затирать неизвестный формат или повреждённые данные даже явным Save.
            try { _ = await LoadAsync(path, kind, validate, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (repairCorrupt && (ex is InvalidDocumentException or ModelValidationException))
            { /* Восстановимый snapshot пересоздаётся из целого журнала; неизвестную схему не затираем. */ }
            var data = JsonSerializer.SerializeToElement(value, Options);
            var envelope = new Envelope { Kind = kind, SchemaVersion = CurrentSchemaVersion, Data = data, Sha256 = Hash(data) };
            await fileSystem.WriteTextAtomicallyAsync(path, JsonSerializer.Serialize(envelope, Options), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static T Decode<T>(string text, string kind, Action<T> validate) where T : class
    {
        try
        {
            // Версию проверяем до десериализации остальных полей будущего формата.
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int schema))
                throw new InvalidDocumentException("The file has no valid format version.");
            if (schema != CurrentSchemaVersion) throw new UnsupportedSchemaException();
            var envelope = JsonSerializer.Deserialize<Envelope>(text, Options)
                ?? throw new InvalidDocumentException("The file is empty.");
            if (envelope.Kind != kind) throw new InvalidDocumentException("The file has an unexpected document type.");
            if (!string.Equals(envelope.Sha256, Hash(envelope.Data), StringComparison.Ordinal))
                throw new InvalidDocumentException("The file failed its integrity check.");
            var value = envelope.Data.Deserialize<T>(Options) ?? throw new InvalidDocumentException("The file contains no data.");
            validate(value);
            return value;
        }
        catch (JsonException) { throw new InvalidDocumentException("The file contains invalid JSON data."); }
    }

    private static string Hash(JsonElement data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, Options))));
}
