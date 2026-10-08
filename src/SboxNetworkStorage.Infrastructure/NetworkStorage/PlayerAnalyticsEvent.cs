using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// A read-only view over a raw player-analytics event object (one element of an
/// <c>events/{steamId}/{date}.json</c> array). Wraps a <see cref="JsonElement"/>
/// so the engine can classify events (parity with the field accessors in
/// <c>controllers/storage-modules/insights-routes.js</c> /
/// <c>services/network-storage-player-analytics.js</c>) while preserving every
/// original key for the serialized output (the legacy code spreads
/// <c>{ ...event, journeyLabel }</c>).
/// </summary>
internal sealed class PlayerAnalyticsEvent
{
    private readonly JsonElement _element;

    private PlayerAnalyticsEvent(JsonElement element) => _element = element;

    public static PlayerAnalyticsEvent? From(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? new PlayerAnalyticsEvent(element) : null;

    public JsonElement Raw => _element;

    public string Type => Str("type");
    public string Category => Str("category");
    public string EndpointSlug => Str("endpointSlug");
    public string Label => Str("label");
    public string Ts => Str("ts");
    public string SessionId => Str("sessionId");
    public string SteamId => Str("steamId");
    public string Source => Str("source");
    public string CollectionId => Str("collectionId");
    public string Key => Str("key");
    public string? Id => _element.TryGetProperty("id", out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
        ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
        : null;

    /// <summary>Legacy <c>event.ok !== false</c> — true unless explicitly false.</summary>
    public bool OkNotFalse => !(_element.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.False);

    public bool OkIsFalse => _element.TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.False;

    public JsonElement Payload => _element.TryGetProperty("payload", out var v) && v.ValueKind == JsonValueKind.Object
        ? v
        : default;

    public bool HasPayload => Payload.ValueKind == JsonValueKind.Object;

    public long? TimestampMs
    {
        get
        {
            var ts = Ts;
            if (ts.Length == 0) return null;
            return DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? dto.ToUnixTimeMilliseconds()
                : null;
        }
    }

    public string Str(string key)
        => _element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Payload section as an object element, or default when absent/not an object.</summary>
    public static JsonElement ObjectSection(JsonElement parent, string key)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    public static string StrOf(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public static double? NumberOf(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out var d)
            ? d
            : null;

    /// <summary>
    /// Build a serializable map from the raw event plus extra/overridden keys, mirroring
    /// the legacy spread <c>{ ...event, journeyLabel }</c>. Extra entries override originals.
    /// </summary>
    public Dictionary<string, object?> ToMapWith(params (string Key, object? Value)[] extra)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in _element.EnumerateObject())
        {
            map[prop.Name] = prop.Value;
        }
        foreach (var (key, value) in extra)
        {
            map[key] = value;
        }
        return map;
    }
}
