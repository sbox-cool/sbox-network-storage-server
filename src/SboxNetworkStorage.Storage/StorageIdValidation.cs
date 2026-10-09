using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Storage;

/// <summary>
/// Provider-neutral validation for collection IDs and record keys at HTTP ingress.
/// Canonical patterns matching the production the store store
/// (<c>INetworkStorageStore.IdPattern/RecordKeyPattern</c> upstream): collection
/// (and project) IDs allow <c>[a-zA-Z0-9_-]</c> (1–128 chars); record keys
/// additionally allow <c>:</c> (1–256 chars) so composite keys such as
/// <c>{steamId}_{saveId}</c> and <c>player:1-2_3</c> keep working.
/// Ingress handlers MUST use these helpers instead of inlining their own
/// patterns so malformed client identifiers are rejected as 4xx before they
/// reach the store (where they surface as <see cref="ArgumentException"/> and
/// would otherwise be misclassified as backend failures).
/// </summary>
public static partial class StorageIdValidation
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.None, 100)]
    private static partial Regex CollectionIdPattern();

    [GeneratedRegex("^[a-zA-Z0-9_:-]{1,256}$", RegexOptions.None, 100)]
    private static partial Regex RecordKeyPattern();

    /// <summary>True when <paramref name="value"/> is a loadable collection ID.</summary>
    public static bool IsValidCollectionId(string? value)
        => !string.IsNullOrEmpty(value) && CollectionIdPattern().IsMatch(value);

    /// <summary>True when <paramref name="value"/> is a loadable record key.</summary>
    public static bool IsValidRecordKey(string? value)
        => !string.IsNullOrEmpty(value) && RecordKeyPattern().IsMatch(value);
}
