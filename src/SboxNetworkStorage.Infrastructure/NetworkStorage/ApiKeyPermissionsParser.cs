using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Parses the API-key <c>permissions</c> (jsonb) column into a string map.
///
/// Both legacy and current writes persist the permission map *double-encoded*:
/// the dictionary is <see cref="JsonSerializer"/>-serialized to text and then
/// stored as a jsonb <em>string</em> value, so the column holds the literal
/// <c>"{\"endpoints\":\"rwx\"}"</c> rather than the object
/// <c>{"endpoints":"rwx"}</c>. Reading that back and deserializing straight into
/// <see cref="Dictionary{TKey, TValue}"/> throws
/// <see cref="JsonException"/> ("could not be converted ... Path: $") because the
/// root token is a String, not an Object — which previously returned HTTP 500 for
/// the whole Network Storage project page.
///
/// This reader accepts every shape we may encounter and never throws:
/// <list type="bullet">
///   <item>SQL/JSON null or blank -&gt; <see langword="null"/></item>
///   <item>a JSON object -&gt; the map (forward-compatible if writes are fixed)</item>
///   <item>a JSON string wrapping a JSON object (the real data) -&gt; the unwrapped map</item>
///   <item>anything else or malformed JSON -&gt; <see langword="null"/></item>
/// </list>
/// </summary>
public static class ApiKeyPermissionsParser
{
    public static Dictionary<string, string>? Parse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return FromElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an already-decoded <see cref="JsonElement"/> (e.g. a legacy store
    /// <c>permissions_json</c> column) without re-serializing it. Accepts the same
    /// shapes as <see cref="Parse(string?)"/> and never throws: a wrapped/malformed
    /// value yields <see langword="null"/> instead of dropping the whole key set.
    /// </summary>
    public static Dictionary<string, string>? Parse(JsonElement element)
    {
        try
        {
            return FromElement(element);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? FromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return ToStringMap(element);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            // Double-encoded: the string value is itself JSON text.
            var inner = element.GetString();
            if (string.IsNullOrWhiteSpace(inner))
            {
                return null;
            }

            using var innerDocument = JsonDocument.Parse(inner);
            return innerDocument.RootElement.ValueKind == JsonValueKind.Object
                ? ToStringMap(innerDocument.RootElement)
                : null;
        }

        return null;
    }

    private static Dictionary<string, string> ToStringMap(JsonElement objectElement)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in objectElement.EnumerateObject())
        {
            // Permission values are scalar strings ("rwx"); coerce any other shape
            // to its raw JSON text so an unexpected value can never throw here.
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return map;
    }
}
