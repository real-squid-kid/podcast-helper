using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PodcastHelper.Core.Validation;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.Infrastructure.Sessions;

internal static class JournalCodec
{
    internal const int Version = 1;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static JournalRecord Sign(JournalRecord value) => value with { Sha256 = Hash(Serialize(value with { Sha256 = "" })) };

    internal static JournalRecord Decode(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int n))
                throw new InvalidDocumentException("Journal record has no valid schema version.");
            if (n != Version) throw new UnsupportedSchemaException();
            var record = JsonSerializer.Deserialize<JournalRecord>(text, Options) ?? throw new InvalidDocumentException("Empty journal record.");
            if (record.Id == Guid.Empty || record.SessionId == Guid.Empty || record.Sequence <= 0 || record.UtcTimestamp == default
                || record.UtcTimestamp.Offset != TimeSpan.Zero || record.Command is null || !Enum.IsDefined(record.Command.Kind)
                || record.Events.IsDefault || record.Changes.IsDefault || record.PreviousHash is null || record.Sha256 is null
                || Sign(record).Sha256 != record.Sha256)
                throw new InvalidDocumentException("Journal record failed validation or integrity check.");
            return record;
        }
        catch (JsonException) { throw new InvalidDocumentException("Invalid journal JSON."); }
    }

    internal static ImmutableArray<PatchOperation> Diff(DurableSession? before, DurableSession after)
    {
        var changes = ImmutableArray.CreateBuilder<PatchOperation>();
        Walk(before is null ? null : JsonSerializer.SerializeToNode(before, Options), JsonSerializer.SerializeToNode(after, Options), []);
        return changes.ToImmutable();
        void Set(ImmutableArray<string> path, JsonNode? value) => changes.Add(new(path, false, JsonSerializer.SerializeToElement(value, Options)));
        void Walk(JsonNode? left, JsonNode? right, ImmutableArray<string> path)
        {
            if (JsonNode.DeepEquals(left, right)) return;
            if (left is JsonObject a && right is JsonObject b)
            {
                foreach (var key in a.Select(x => x.Key).Union(b.Select(x => x.Key)))
                {
                    var next = path.Add(key);
                    if (!b.ContainsKey(key)) changes.Add(new(next, true, null));
                    else if (!a.ContainsKey(key)) Set(next, b[key]);
                    else Walk(a[key], b[key], next);
                }
            }
            else if (left is JsonArray aa && right is JsonArray bb)
            {
                for (int i = 0; i < Math.Min(aa.Count, bb.Count); i++) Walk(aa[i], bb[i], path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                for (int i = aa.Count; i < bb.Count; i++) Set(path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), bb[i]);
                for (int i = aa.Count - 1; i >= bb.Count; i--) changes.Add(new(path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), true, null));
            }
            else Set(path, right);
        }
    }

    internal static DurableSession Apply(DurableSession? before, ImmutableArray<PatchOperation> changes)
    {
        JsonNode? node = before is null ? null : JsonSerializer.SerializeToNode(before, Options);
        try
        {
            foreach (var change in changes)
            {
                if (change.Path.IsDefault || change.Path.Any(x => x is null)) throw new InvalidDocumentException("Invalid journal patch path.");
                var value = change.Value is { } json ? JsonNode.Parse(json.GetRawText()) : null;
                if (change.Path.IsEmpty)
                {
                    if (before is not null || change.Remove) throw new InvalidDocumentException("Unexpected journal root replacement.");
                    node = value;
                    continue;
                }
                JsonNode? parent = node;
                foreach (string component in change.Path.Take(change.Path.Length - 1))
                    parent = Child(parent, component);
                string key = change.Path[^1];
                if (parent is JsonObject obj)
                {
                    if (change.Remove) { if (!obj.Remove(key)) throw new InvalidDocumentException("Missing journal patch property."); }
                    else obj[key] = value;
                }
                else if (parent is JsonArray array && int.TryParse(key, out int index) && index >= 0 && index <= array.Count)
                {
                    if (change.Remove) array.RemoveAt(index);
                    else if (index == array.Count) array.Add(value);
                    else array[index] = value;
                }
                else throw new InvalidDocumentException("Invalid journal patch target.");
            }
            var state = node?.Deserialize<DurableSession>(Options) ?? throw new InvalidDocumentException("Journal contains no session state.");
            Validate(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        { throw new InvalidDocumentException("Journal changes cannot be applied safely."); }
    }

    private static JsonNode? Child(JsonNode? node, string key) => node switch
    {
        JsonObject obj when obj.ContainsKey(key) => obj[key],
        JsonArray array when int.TryParse(key, out int i) && i >= 0 && i < array.Count => array[i],
        _ => throw new InvalidDocumentException("Invalid journal patch path.")
    };

    internal static void Validate(DurableSession state)
    {
        if (state.Engine is null || state.UndoStack.IsDefault || state.Engine.SessionFractionTicks is < 0 or >= 10000
            || state.Engine.ActiveFractionTicks is < 0 or >= 10000 || state.UndoStack.Any(x => x is null || x.CommandId == Guid.Empty
                || x.BeforeSequence < 1 || x.Sequence <= x.BeforeSequence || x.Kind is not (SessionCommandKind.NextSegment or SessionCommandKind.Reorder)))
            throw new InvalidDocumentException("Invalid durable session state.");
        ModelValidation.Session(state.Engine.Session);
    }
}
