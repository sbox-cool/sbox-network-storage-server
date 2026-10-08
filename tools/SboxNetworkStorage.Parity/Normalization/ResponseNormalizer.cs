using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Parity.Normalization;

/// <summary>
/// Turns a raw response into a deterministic, comparable shape: run-specific
/// values (project id, keys, captured ids) become placeholders; timestamps,
/// generated ids, correlation ids, durations and version hashes are masked;
/// object keys are sorted; arrays are sorted only at declared order-insensitive paths.
/// </summary>
public static partial class ResponseNormalizer
{
    /// <summary>Headers compared by value.</summary>
    private static readonly HashSet<string> ValueHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache-control", "allow", "www-authenticate", "location",
        "x-security-config-cache", "x-security-config-source",
        "x-sboxcool-route-owner", "x-sboxcool-sboxauth",
        "x-ratelimit-limit", "x-ratelimit-remaining",
    };

    /// <summary>Headers compared by presence only (their values are time- or version-dependent).</summary>
    private static readonly HashSet<string> PresenceHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "retry-after", "x-ratelimit-reset", "x-security-config-version", "etag",
    };

    /// <summary>Object keys whose values are always volatile (time, ids, durations, hashes).</summary>
    private static readonly HashSet<string> MaskedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "_timestamp", "serverTime", "serverTimeMs", "now", "lastSeen", "lastSeenUnix", "lastSeenUnixMs",
        "durationMs", "elapsedMs", "tookMs", "latencyMs", "executionMs", "totalMs", "scanMs", "queryMs", "processingMs",
        "sessionToken", "token", "signature", "versionHash", "configVersion", "contentHash", "sourceHash", "checksum", "etag",
        "_txId", "txId",
        // Per-run generated signing keys (security-config): fresh key material
        // on every server start must not fail cross-run comparison.
        "keyId", "publicKeyJwk", "publicKeyPem",
        "lastSyncedAtUnix", "revisionFirstSyncedAtUnix", "lastran",
    };

    public static NormalizedResponse Normalize(
        int status,
        string? contentType,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        string body,
        IReadOnlyList<KeyValuePair<string, string>> replacements,
        IReadOnlyList<string>? orderInsensitivePaths,
        IReadOnlyList<string>? maskPaths)
    {
        var normalizedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            var key = name.ToLowerInvariant();
            if (ValueHeaders.Contains(key))
            {
                normalizedHeaders[key] = ScrubString(string.Join(", ", values), replacements);
            }
            else if (PresenceHeaders.Contains(key))
            {
                normalizedHeaders[key] = "<present>";
            }
        }

        return new NormalizedResponse
        {
            Status = status,
            ContentType = NormalizeContentType(contentType),
            Headers = normalizedHeaders.Count == 0 ? null : normalizedHeaders,
            Body = NormalizeBody(body, replacements, orderInsensitivePaths, maskPaths),
        };
    }

    public static string? NormalizeContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? null : contentType.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private static JsonNode? NormalizeBody(
        string body,
        IReadOnlyList<KeyValuePair<string, string>> replacements,
        IReadOnlyList<string>? orderInsensitivePaths,
        IReadOnlyList<string>? maskPaths)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            var text = ScrubString(body.Length > 4000 ? body[..4000] + "…" : body, replacements);
            return new JsonObject { ["$text"] = text };
        }

        root = Scrub(root, replacements, parentKey: null);
        foreach (var path in maskPaths ?? [])
        {
            root = JsonPath.Parse(path).Apply(root, node => node is null ? null : JsonValue.Create("<masked>"));
        }

        foreach (var path in orderInsensitivePaths ?? [])
        {
            root = JsonPath.Parse(path).Apply(root, SortArray);
        }

        return SortKeys(root);
    }

    private static JsonNode? Scrub(JsonNode? node, IReadOnlyList<KeyValuePair<string, string>> replacements, string? parentKey)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (name, child) in obj.ToList())
                {
                    var scrubbedName = ScrubString(name, replacements);
                    for (var n = 2; result.ContainsKey(scrubbedName); n++)
                    {
                        scrubbedName = $"{ScrubString(name, replacements)}#{n}";
                    }

                    result[scrubbedName] = MaskedKeys.Contains(name) || IsTimeKey(name)
                        ? Mask(child, name)
                        : Scrub(child, replacements, name);
                }

                return result;
            case JsonArray array:
                return new JsonArray(array.Select(item => Scrub(item, replacements, parentKey)).ToArray());
            case JsonValue value:
                return value.GetValueKind() switch
                {
                    JsonValueKind.String => JsonValue.Create(ScrubString(value.GetValue<string>(), replacements)),
                    JsonValueKind.Number when IsEpochMillis(value) => JsonValue.Create("<epoch-ms>"),
                    _ => value.DeepClone(),
                };
            default:
                return null;
        }
    }

    /// <summary>Masks a volatile value but keeps null/boolean/structure so presence differences still surface.</summary>
    private static JsonNode? Mask(JsonNode? node, string key) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => value.DeepClone(),
        JsonValue value when value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length == 0 => JsonValue.Create(""),
        JsonArray array => new JsonArray(array.Select(item => Mask(item, key)).ToArray()),
        JsonObject => JsonValue.Create($"<masked:{key}:object>"),
        _ => JsonValue.Create($"<masked:{key}>"),
    };

    /// <summary><c>createdAt</c>, <c>expiresAt</c>, <c>lastRunAt</c>, ... — any camelCase key ending in "At".</summary>
    private static bool IsTimeKey(string name) =>
        name.Length > 2 && name.EndsWith("At", StringComparison.Ordinal) && char.IsLower(name[^3]);

    private static bool IsEpochMillis(JsonValue value) =>
        value.TryGetValue<double>(out var number) && number is >= 1_000_000_000_000 and < 10_000_000_000_000;

    public static string ScrubString(string text, IReadOnlyList<KeyValuePair<string, string>> replacements)
    {
        foreach (var (actual, placeholder) in replacements)
        {
            if (actual.Length > 0)
            {
                text = text.Replace(actual, placeholder, StringComparison.Ordinal);
            }
        }

        if (IsoTimestamp().IsMatch(text))
        {
            text = IsoTimestamp().Replace(text, "<timestamp>");
        }

        text = MaskedApiKey().Replace(text, "<masked-api-key>");
        text = RawApiKey().Replace(text, "<api-key>");
        text = ProjectId().Replace(text, "<project-id>");
        text = Guid().Replace(text, "<guid>");
        text = LongHex().Replace(text, "<hex>");
        text = Hex32().Replace(text, "<hex32>");
        return text;
    }

    private static JsonNode? SortArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return node;
        }

        var sorted = array
            .Select(item => SortKeys(item?.DeepClone()))
            .OrderBy(item => item?.ToJsonString() ?? "null", StringComparer.Ordinal)
            .ToArray();
        return new JsonArray(sorted);
    }

    private static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, SortKeys(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => SortKeys(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"sbox_(?:sk|ns|pk)_[A-Za-z0-9]{0,8}\.\.\.[A-Za-z0-9]{4}")]
    private static partial Regex MaskedApiKey();

    [GeneratedRegex(@"sbox_(?:sk|ns|pk)_[A-Za-z0-9_-]{16,}")]
    private static partial Regex RawApiKey();

    [GeneratedRegex(@"proj_[0-9a-f]{8,}")]
    private static partial Regex ProjectId();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-f]{40,}(?![0-9A-Za-z])")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-f]{32}(?![0-9A-Za-z])")]
    private static partial Regex Hex32();
}

public sealed class NormalizedResponse
{
    public int Status { get; set; }

    public string? ContentType { get; set; }

    public SortedDictionary<string, string>? Headers { get; set; }

    public JsonNode? Body { get; set; }
}
