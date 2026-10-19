using System.Globalization;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Updates.ReleaseStatus;

/// <summary>
/// The <c>release-status.json</c> document (schema 1) that the library repository publishes. The
/// library and engine sections describe the sbox.game library release, the latest s&amp;box engine
/// release and the library's <c>main</c>. The server section describes this server's own releases.
/// Every scalar is nullable: a missing or malformed field reads as null, never as a guessed value.
/// </summary>
public sealed record ReleaseStatusDocument(DateTimeOffset? GeneratedAt, ReleaseLibrary Library, ReleaseEngine Engine, ReleaseServer Server);

public sealed record ReleaseLibrary(LibraryPublished Published, LibraryMain Main, IReadOnlyList<PendingChange> Pending,
    DateTimeOffset? PendingSince, InstallMain InstallMain);

/// <summary>The release sbox.game serves to game developers.</summary>
public sealed record LibraryPublished(long? Revision, string? Version, string? Commit, DateTimeOffset? PublishedAt, Uri? Url);

public sealed record LibraryMain(string? Version, string? Commit, DateTimeOffset? CommittedAt, CiStatus Ci);

/// <summary><c>conclusion</c> is success, failure, pending or unknown. <c>Engine</c> is the s&amp;box build the CI ran against.</summary>
public sealed record CiStatus(string? Conclusion, Uri? RunUrl, string? Engine);

public sealed record PendingChange(int? Number, string? Title, DateTimeOffset? MergedAt, Uri? Url);

public sealed record InstallMain(Uri? ZipUrl, Uri? DocsUrl);

public sealed record ReleaseEngine(EngineRelease Latest, bool? LibraryBehind, int? DaysBehind);

public sealed record EngineRelease(string? Version, DateTimeOffset? Date, Uri? Url);

public sealed record ReleaseServer(ServerRelease LatestRelease, ServerMain Main, IReadOnlyList<PendingChange> Pending, DateTimeOffset? PendingSince);

public sealed record ServerRelease(string? Version, DateTimeOffset? PublishedAt, Uri? Url);

public sealed record ServerMain(string? Commit, DateTimeOffset? CommittedAt);

/// <summary>
/// Reads schema 1 tolerantly: unknown fields are ignored, a missing field is null, and any other
/// schema number is rejected so a future incompatible document never renders as if it were schema 1.
/// </summary>
public static class ReleaseStatusParser
{
    public const int SupportedSchema = 1;

    /// <summary>Most pending changes kept per list, the producer's cap as well.</summary>
    public const int MaxPending = 50;

    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="FormatException">The document is not an object or uses another schema.</exception>
    public static ReleaseStatusDocument Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The release status document is not a JSON object.");
        }

        var schema = Int(root, "schema");
        if (schema != SupportedSchema)
        {
            throw new FormatException($"Unsupported release status schema {schema?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}; this server reads schema {SupportedSchema}.");
        }

        var library = Child(root, "library");
        var published = Child(library, "published");
        var main = Child(library, "main");
        var ci = Child(main, "ci");
        var engine = Child(root, "engine");
        var latestEngine = Child(engine, "latest");
        var installMain = Child(library, "installMain");
        var server = Child(root, "server");
        var latestServer = Child(server, "latestRelease");
        var serverMain = Child(server, "main");

        return new ReleaseStatusDocument(
            Time(root, "generatedAt"),
            new ReleaseLibrary(
                new LibraryPublished(Long(published, "revision"), Text(published, "version", 64), Text(published, "commit", 64),
                    Time(published, "publishedAt"), Url(published, "url")),
                new LibraryMain(Text(main, "version", 64), Text(main, "commit", 64), Time(main, "committedAt"),
                    new CiStatus(Text(ci, "conclusion", 32), Url(ci, "runUrl"), Text(ci, "engine", 128))),
                Pending(library, "pending"),
                Time(library, "pendingSince"),
                new InstallMain(Url(installMain, "zipUrl"), Url(installMain, "docsUrl"))),
            new ReleaseEngine(
                new EngineRelease(Text(latestEngine, "version", 64), Time(latestEngine, "date"), Url(latestEngine, "url")),
                Bool(engine, "libraryBehind"),
                Int(engine, "daysBehind")),
            new ReleaseServer(
                new ServerRelease(Text(latestServer, "version", 64), Time(latestServer, "publishedAt"), Url(latestServer, "url")),
                new ServerMain(Text(serverMain, "commit", 64), Time(serverMain, "committedAt")),
                Pending(server, "pending"),
                Time(server, "pendingSince")));
    }

    private static IReadOnlyList<PendingChange> Pending(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var changes = new List<PendingChange>();
        foreach (var item in list.EnumerateArray())
        {
            if (changes.Count == MaxPending)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var change = new PendingChange(Int(item, "number"), Text(item, "title", 512), Time(item, "mergedAt"), Url(item, "url"));
            if (change is { Number: null, Title: null, MergedAt: null, Url: null })
            {
                continue;
            }

            changes.Add(change);
        }

        return changes;
    }

    /// <summary>Missing or non-object values yield an undefined element, whose scalar readers all return null.</summary>
    private static JsonElement Child(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? Text(JsonElement parent, string name, int maxLength)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > maxLength ? null : text;
    }

    private static long? Long(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? Int(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Bool(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static DateTimeOffset? Time(JsonElement parent, string name)
    {
        var text = Text(parent, name, 64);
        return text is not null
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    /// <summary>Only absolute http(s) links reach the page; anything else (javascript:, relative paths) is dropped.</summary>
    private static Uri? Url(JsonElement parent, string name)
    {
        var text = Text(parent, name, 2048);
        return text is not null
            && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }
}
