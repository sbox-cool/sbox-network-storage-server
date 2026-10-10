namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// The revision a request targets. The editor Sync Tool and the game runtime select the staged
/// ("next") revision with the <c>x-ns-publish-target</c> header or the <c>revisionTarget</c> query
/// parameter; every other value, or neither, targets the live revision.
/// </summary>
public static class NetworkStoragePublishTarget
{
    public const string HeaderName = "x-ns-publish-target";
    public const string QueryName = "revisionTarget";
    public const string Live = "live";
    public const string Next = "next";

    /// <summary>True when the header or the query parameter is <c>next</c> (case-insensitive).</summary>
    public static bool IsNext(string? header, string? query) => IsNextValue(header) || IsNextValue(query);

    private static bool IsNextValue(string? value)
        => value is not null && string.Equals(value.Trim(), Next, StringComparison.OrdinalIgnoreCase);
}
