using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>Contents of the <c>release.json</c> asset attached to every GitHub Release.</summary>
public sealed record ReleaseInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("minUpgradableFrom")] string? MinUpgradableFrom,
    [property: JsonPropertyName("migrationRequired")] bool MigrationRequired,
    [property: JsonPropertyName("security")] bool Security,
    [property: JsonPropertyName("changelogUrl")] string? ChangelogUrl,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("prerelease")] bool Prerelease = false);

/// <summary>A <c>major.minor.patch[-prerelease]</c> version with SemVer precedence.</summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0, null);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('v', 'V');
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];
        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var pre = dash >= 0 ? value[(dash + 1)..] : null;
        var parts = core.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = int.TryParse(left[i], out var l);
            var rightNumeric = int.TryParse(right[i], out var r);
            var cmp = (leftNumeric, rightNumeric) switch
            {
                (true, true) => l.CompareTo(r),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i])
            };
            if (cmp != 0) return cmp;
        }

        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>
/// Resolves the latest release: the sboxcool.com feed first, then the GitHub
/// Releases API. Requests carry only the current version and platform.
/// </summary>
public sealed class ReleaseFeed(HttpClient http, EffectiveConfig config)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sbox-ns", BuildInfo.Version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({BuildInfo.RuntimeIdentifier})"));
        return client;
    }

    public string GitHubRepository => config.GetString("updates.github_repo");

    /// <summary>Latest release allowed by <c>updates.include_prereleases</c>, or a specific tag.</summary>
    public async Task<ReleaseInfo> GetReleaseAsync(string? specificVersion, CancellationToken ct)
    {
        var includePrereleases = config.GetBoolean("updates.include_prereleases");
        if (specificVersion is null && !includePrereleases)
        {
            try
            {
                var fromFeed = await http.GetFromJsonAsync<ReleaseInfo>(config.GetString("updates.feed_url"), JsonOptions, ct);
                if (fromFeed is not null && SemanticVersion.TryParse(fromFeed.Version, out _))
                {
                    return fromFeed;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Fall through to GitHub.
            }
        }

        return await GetFromGitHubAsync(specificVersion, includePrereleases, ct);
    }

    private async Task<ReleaseInfo> GetFromGitHubAsync(string? specificVersion, bool includePrereleases, CancellationToken ct)
    {
        var api = $"https://api.github.com/repos/{GitHubRepository}/releases";
        JsonElement release;
        if (specificVersion is not null)
        {
            var tag = specificVersion.StartsWith('v') ? specificVersion : "v" + specificVersion;
            release = await http.GetFromJsonAsync<JsonElement>($"{api}/tags/{Uri.EscapeDataString(tag)}", JsonOptions, ct);
        }
        else if (includePrereleases)
        {
            var releases = await http.GetFromJsonAsync<JsonElement>($"{api}?per_page=20", JsonOptions, ct);
            release = releases.EnumerateArray()
                .Where(r => !r.GetProperty("draft").GetBoolean())
                .Where(r => SemanticVersion.TryParse(r.GetProperty("tag_name").GetString(), out _))
                .OrderByDescending(r => { SemanticVersion.TryParse(r.GetProperty("tag_name").GetString(), out var v); return v; })
                .FirstOrDefault();
            if (release.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException($"No releases found in {GitHubRepository}.");
            }
        }
        else
        {
            release = await http.GetFromJsonAsync<JsonElement>($"{api}/latest", JsonOptions, ct);
        }

        var tagName = release.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("Release has no tag.");
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "release.json")
                {
                    var manifest = await http.GetFromJsonAsync<ReleaseInfo>(asset.GetProperty("browser_download_url").GetString()!, JsonOptions, ct);
                    if (manifest is not null)
                    {
                        return manifest;
                    }
                }
            }
        }

        return new ReleaseInfo(
            tagName.TrimStart('v'),
            MinUpgradableFrom: null,
            MigrationRequired: false,
            Security: false,
            ChangelogUrl: release.TryGetProperty("html_url", out var url) ? url.GetString() : null,
            PublishedAt: release.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String ? published.GetDateTimeOffset() : null,
            Prerelease: release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
    }

    /// <summary>Download URL of a named asset on the release tagged <c>v{version}</c>.</summary>
    public string AssetUrl(string version, string assetName)
        => $"https://github.com/{GitHubRepository}/releases/download/v{version.TrimStart('v')}/{assetName}";
}
