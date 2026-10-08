using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Input validation identical to the production ScyllaDB store (same patterns and messages).</summary>
internal static partial class StoreValidation
{
    /// <summary>Production default for the maximum accepted JSON payload (64 KiB of UTF-8).</summary>
    public const int DefaultMaxPayloadBytes = 64 * 1024;

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,128}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-zA-Z0-9_:-]{1,256}$")]
    private static partial Regex RecordKeyPattern();

    public static void Id(string value)
    {
        if (string.IsNullOrEmpty(value) || !IdPattern().IsMatch(value))
            throw new ArgumentException($"Invalid ID '{value}': must match ^[a-zA-Z0-9_-]{{1,128}}$.");
    }

    public static void RecordKey(string value)
    {
        if (string.IsNullOrEmpty(value) || !RecordKeyPattern().IsMatch(value))
            throw new ArgumentException($"Invalid record key '{value}': must match ^[a-zA-Z0-9_:-]{{1,256}}$.");
    }

    /// <summary>Collection IDs in the reserved <c>__sbox_</c> namespace are rejected on create.</summary>
    public static void NonReservedCollectionId(string collectionId)
    {
        if (collectionId.StartsWith("__sbox_", StringComparison.Ordinal))
            throw new ArgumentException($"Collection ID '{collectionId}' uses a reserved system namespace (__sbox_). Choose a different ID.");
    }

    public static string Serialize(JsonElement element, string resourceType, int maxPayloadBytes)
    {
        var str = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(str) > maxPayloadBytes)
            throw new ArgumentException($"Payload exceeds {maxPayloadBytes} bytes for {resourceType}.");
        return str;
    }

    /// <summary>
    /// ScyllaDB rejects <c>LIMIT</c> values that are not strictly positive; the
    /// relational drivers fail the same way instead of silently returning nothing.
    /// </summary>
    public static void Limit(int limit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "LIMIT must be strictly positive.");
    }

    /// <summary>First non-empty string among the camelCase/snake_case property names, or null.</summary>
    public static string? ReadOptionalString(JsonElement element, string camelName, string snakeName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return NonEmptyString(element, camelName) ?? NonEmptyString(element, snakeName);
    }

    private static string? NonEmptyString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String && prop.GetString() is { Length: > 0 } value
            ? value
            : null;
}
