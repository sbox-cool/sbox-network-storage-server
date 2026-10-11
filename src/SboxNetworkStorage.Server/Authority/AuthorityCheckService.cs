using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Application.NetworkStorage.AuthorityAnalyzer;

namespace SboxNetworkStorage.Server.Authority;

/// <summary>
/// Gathers authority-check facts (definitions, keys, recent request log) and
/// runs <see cref="AuthorityAnalyzer"/>. Shared by the project overview, the
/// CLI (<c>doctor --project</c>, <c>project authority</c>) and MCP. Advisory
/// only: never on the request path.
/// </summary>
public static class AuthorityCheckService
{
    public sealed record AuthorityCheckResult(string Profile, IReadOnlyList<AuthorityFinding> Findings);

    private static readonly string[] GameRoutePrefixes =
    [
        "/v3/endpoints/", "/v1/endpoints/",
        "/v3/storage/", "/v1/storage/", "/api/storage/",
        "/v3/queries/", "/v1/queries/",
        "/v3/values/", "/v1/values/",
    ];

    public static async Task<AuthorityCheckResult?> RunAsync(
        INetworkStorageProjectService projects,
        INetworkStorageStore store,
        long owner,
        string projectId,
        CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(owner, projectId, ct);
        if (access is null) return null;
        var resources = await projects.GetProjectResourcesAsync(owner, projectId, ct);
        var keys = await TryAsync(() => projects.GetProjectKeysAsync(owner, projectId, ct), ct)
            ?? (IReadOnlyList<ApiKeyInfo>)[];
        var log = await TryAsync(() => store.ListStorageRequestLogAsync(projectId, 200, ct), ct)
            ?? (IReadOnlyList<JsonElement>)[];
        return AnalyzeLoaded(access.Project.HostingProfile, resources, keys, log);
    }

    /// <summary>Analyze already-loaded project data (dashboard overview path).</summary>
    public static AuthorityCheckResult AnalyzeLoaded(
        string? hostingProfile,
        NetworkStorageProjectResources? resources,
        IReadOnlyList<ApiKeyInfo> keys,
        IReadOnlyList<JsonElement> logRows)
    {
        var collections = (resources?.Collections ?? [])
            .Select(c => new CollectionFact(c.Name, c.AccessMode))
            .ToList();
        var endpoints = (resources?.Endpoints ?? [])
            .Select(e => new EndpointFact(e.Slug, e.Enabled, e.RequiresSecretKey, e.Definition ?? StepsOnly(e.Steps)))
            .ToList();

        var secretIds = new HashSet<string>(keys
            .Where(k => string.Equals(k.KeyType, "secret", StringComparison.Ordinal) && k.KeyIdentifier is not null)
            .Select(k => k.KeyIdentifier!), StringComparer.Ordinal);
        var secretUsed = false;
        if (secretIds.Count > 0)
        {
            foreach (var row in logRows)
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("api_key_identifier", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (!secretIds.Contains(id.GetString() ?? "")) continue;
                if (!row.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                var route = path.GetString() ?? "";
                if (GameRoutePrefixes.Any(prefix => route.StartsWith(prefix, StringComparison.Ordinal))) { secretUsed = true; break; }
            }
        }
        var findings = Analyze(hostingProfile, collections, endpoints, secretUsed);
        return new AuthorityCheckResult(
            Domain.Workspace.WorkspaceProjectProfiles.NormalizeHostingProfile(hostingProfile),
            findings);
    }

    /// <summary>Stored endpoints keep their steps as a list; the analyzer reads a definition object.</summary>
    private static JsonElement? StepsOnly(IReadOnlyList<object>? steps)
        => steps is null ? null : JsonSerializer.SerializeToElement(new { steps });

    private static async Task<T?> TryAsync<T>(Func<Task<T>> load, CancellationToken ct)
    {
        try
        {
            return await load();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return default;
        }
    }
}
