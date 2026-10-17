using System.Globalization;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>Request headers the Network Storage library sends to identify itself (docs/game-client.md).</summary>
public static class ClientHeaders
{
    public const string LibraryVersion = "x-ns-library-version";
    public const string LibraryRevision = "x-ns-library-revision";
    public const string ClientType = "x-ns-client-type";
    public const string RevisionId = "x-ns-revision-id";
    public const string EngineVersion = "x-ns-engine-version";
}

/// <summary>Response headers the server adds so a library can tell where it stands (docs/game-client.md).</summary>
public static class CompatHeaders
{
    public const string ServerVersion = "x-ns-server-version";
    public const string LibraryStatus = "x-ns-library-status";
    public const string LibraryLatest = "x-ns-library-latest";
    public const string LibrarySunset = "x-ns-library-sunset";
}

/// <summary>
/// Who is calling: the library build, client type, game revision and engine build, as the request
/// reported them. Resolved once per request by <see cref="ClientIdentityContext"/>. A missing, malformed or
/// old-constant (<c>"1.0"</c>) library version is the <see cref="Legacy"/> bucket, never an error.
/// </summary>
/// <param name="LibraryVersion">Canonical SemVer without build metadata, or <see cref="Legacy"/>.</param>
/// <param name="Library">Parsed <paramref name="LibraryVersion"/>; null for legacy.</param>
/// <param name="LibraryRevision">s&amp;box package revision of the library (best effort).</param>
/// <param name="ClientType"><c>game</c>, <c>editor</c> or <c>dedicated</c>; null when absent or unrecognised.</param>
/// <param name="RevisionId">Game revision the client was built from (<c>x-ns-revision-id</c>).</param>
/// <param name="EngineVersion">s&amp;box engine build, at most 32 characters of <c>[0-9A-Za-z._+-]</c>; null otherwise.</param>
public sealed record ClientIdentity(
    string LibraryVersion,
    SemanticVersion? Library,
    long? LibraryRevision,
    string? ClientType,
    long? RevisionId,
    string? EngineVersion)
{
    public const string Legacy = "legacy";
    public const int MaxLibraryVersionLength = 64;
    public const int MaxEngineVersionLength = 32;

    // Strict SemVer 2.0.0 core, optional pre-release and build metadata. The shared SemanticVersion parser is
    // lenient (leading "v", missing parts), which would let garbage count as a real version.
    private static readonly Regex StrictSemVer = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EngineChars = new(@"^[0-9A-Za-z._+-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool IsLegacy => Library is null;

    public static ClientIdentity Unknown { get; } = new(Legacy, null, null, null, null, null);

    /// <summary>True for a well-formed SemVer library version. The old constant <c>"1.0"</c> is not one.</summary>
    public static bool TryParseLibraryVersion(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0, null);
        if (text is null) return false;
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > MaxLibraryVersionLength || !StrictSemVer.IsMatch(trimmed)) return false;
        return SemanticVersion.TryParse(trimmed, out version);
    }

    public static ClientIdentity FromHeaders(IHeaderDictionary headers)
    {
        var library = TryParseLibraryVersion(First(headers, ClientHeaders.LibraryVersion), out var version) ? version : null;
        return new ClientIdentity(
            library?.ToString() ?? Legacy,
            library,
            ParseLong(First(headers, ClientHeaders.LibraryRevision), nonNegative: true),
            ParseClientType(First(headers, ClientHeaders.ClientType)),
            ParseLong(First(headers, ClientHeaders.RevisionId), nonNegative: false),
            ParseEngineVersion(First(headers, ClientHeaders.EngineVersion)));
    }

    /// <summary>
    /// Fallback for old and new libraries that report the version only in a request body
    /// (<c>networkStorageVersion</c> on revision-init, <c>libraryVersion</c> on heartbeat and analytics).
    /// A valid header wins; an invalid or old-constant body value leaves the identity unchanged.
    /// </summary>
    public ClientIdentity WithBodyLibraryVersion(string? bodyValue)
        => IsLegacy && TryParseLibraryVersion(bodyValue, out var version) ? this with { LibraryVersion = version.ToString(), Library = version } : this;

    public static string? ParseClientType(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        return value is "game" or "editor" or "dedicated" ? value : null;
    }

    public static string? ParseEngineVersion(string? text)
    {
        var value = text?.Trim();
        return value is { Length: > 0 and <= MaxEngineVersionLength } && EngineChars.IsMatch(value) ? value : null;
    }

    private static string? First(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    private static long? ParseLong(string? text, bool nonNegative)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && (!nonNegative || value >= 0) ? value : null;
}

/// <summary>The one place a <see cref="ClientIdentity"/> is resolved from an <see cref="HttpContext"/>.</summary>
public static class ClientIdentityContext
{
    private const string ItemKey = "sboxcool.client_identity";

    /// <summary>The identity of the current request, parsed from its headers on first use and cached.</summary>
    public static ClientIdentity Get(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is ClientIdentity identity) return identity;
        identity = ClientIdentity.FromHeaders(context.Request.Headers);
        context.Items[ItemKey] = identity;
        return identity;
    }

    /// <summary>Applies a library version found in the request body when the headers carried none.</summary>
    public static void ApplyBodyLibraryVersion(HttpContext context, string? bodyValue)
        => context.Items[ItemKey] = Get(context).WithBodyLibraryVersion(bodyValue);

    /// <summary>
    /// Reads the first of <paramref name="propertyNames"/> that is a string in a JSON object body and applies it with
    /// <see cref="ApplyBodyLibraryVersion(HttpContext, string?)"/>. A body of any other shape is ignored.
    /// </summary>
    public static void ApplyBodyLibraryVersion(HttpContext context, System.Text.Json.JsonElement? body, params string[] propertyNames)
    {
        if (body is not { ValueKind: System.Text.Json.JsonValueKind.Object } json) return;
        foreach (var name in propertyNames)
        {
            if (json.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                ApplyBodyLibraryVersion(context, value.GetString());
                return;
            }
        }
    }

    /// <summary>
    /// Applies the published-game revision from an already-parsed request body, for clients that do not send
    /// <c>x-ns-revision-id</c>. Only a positive numeric <c>revisionId</c> on a published game bundle
    /// (<c>isPublishedGameBundle: true</c>) counts: anything else is an editor or unpublished build whose
    /// revision is not the published game revision. A header revision wins; this only enriches analytics.
    /// </summary>
    public static void ApplyBodyGameRevision(HttpContext context, System.Text.Json.JsonElement? body)
    {
        if (body is not { ValueKind: System.Text.Json.JsonValueKind.Object } json) return;
        if (Get(context).RevisionId is not null) return;
        if (!json.TryGetProperty("isPublishedGameBundle", out var published)
            || published.ValueKind != System.Text.Json.JsonValueKind.True) return;
        if (json.TryGetProperty("revisionId", out var revision) && revision.ValueKind == System.Text.Json.JsonValueKind.Number
            && revision.TryGetInt64(out var gameRevision) && gameRevision > 0)
        {
            context.Items[ItemKey] = Get(context) with { RevisionId = gameRevision };
        }
    }

    /// <summary>
    /// The player the caller claims to be, from headers or the query string (best effort, self-reported).
    /// Only numeric Steam IDs count; anything else is ignored for player estimates. The request body is
    /// deliberately not read here: by the time analytics run the body stream is consumed.
    /// </summary>
    public static string? PlayerId(HttpContext context)
    {
        var request = context.Request;
        foreach (var candidate in new[]
        {
            request.Headers["x-steam-id"].FirstOrDefault(),
            request.Headers["x-sbox-steam-id"].FirstOrDefault(),
            request.Query["steamId"].FirstOrDefault(),
            request.Query["steamid"].FirstOrDefault(),
        })
        {
            if (candidate is { Length: > 0 and <= 20 } id && id.All(char.IsAsciiDigit)) return id;
        }

        return null;
    }
}
