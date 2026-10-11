using System.Text.Json;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Advisory authority check: lists game data a game client or an untrusted
/// host could still change. Pure function over precomputed facts; never on the
/// request path. Callers: the project overview, `sbox-ns doctor --project`
/// and the MCP read tool.
/// </summary>
public static class AuthorityAnalyzer
{
    public sealed record CollectionFact(string Name, string? AccessMode);

    public sealed record EndpointFact(string Slug, bool Enabled, bool RequiresSecretKey, JsonElement? Definition);

    public sealed record AuthorityFinding(string Id, string Target, string Detail, string Fix);

    private static readonly string[] ValidationStepTypes = ["condition", "assert"];
    private static readonly string[] WriteStepTypes = ["write", "delete"];

    public static IReadOnlyList<AuthorityFinding> Analyze(
        string? hostingProfile,
        IEnumerable<CollectionFact> collections,
        IEnumerable<EndpointFact> endpoints,
        bool secretKeyUsedOnDataPlane)
    {
        var profile = WorkspaceProjectProfiles.NormalizeHostingProfile(hostingProfile);
        var findings = new List<AuthorityFinding>();

        foreach (var collection in collections)
        {
            if (string.Equals(collection.AccessMode, "public", StringComparison.Ordinal))
            {
                findings.Add(new AuthorityFinding(
                    "direct-write-collection",
                    $"collection {collection.Name}",
                    $"Collection {collection.Name} allows direct reads and writes from game clients (accessMode: public).",
                    $"Set accessMode: endpoint on {collection.Name} and move every rule into endpoints."));
            }
        }

        foreach (var endpoint in endpoints)
        {
            if (!endpoint.Enabled) continue;
            if (endpoint.RequiresSecretKey && profile == "player-hosted")
            {
                findings.Add(new AuthorityFinding(
                    "unreachable-secret-endpoint",
                    $"endpoint {endpoint.Slug}",
                    $"Endpoint {endpoint.Slug} requires a secret key, but no trusted caller exists in a player-hosted project.",
                    $"Remove requiresSecretKey from {endpoint.Slug}, or move it to a dedicated project."));
            }
            if (endpoint.Definition is { } definition && WritesWithoutValidation(definition))
            {
                findings.Add(new AuthorityFinding(
                    "unvalidated-writes",
                    $"endpoint {endpoint.Slug}",
                    $"Endpoint {endpoint.Slug} writes player data with no condition or assert step guarding the input.",
                    $"Add a condition or assert step to {endpoint.Slug} that rejects out-of-range input before the write."));
            }
        }

        if (secretKeyUsedOnDataPlane && profile == "player-hosted")
        {
            findings.Add(new AuthorityFinding(
                "secret-key-on-data-plane",
                "secret API key",
                "A secret API key was used on game routes. Player-hosted games cannot hold a secret safely.",
                "Rotate the key, and ship only the public key in player-hosted games."));
        }

        return findings;
    }

    /// <summary>True when the definition writes or deletes with no condition/assert step.</summary>
    public static bool WritesWithoutValidation(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object) return false;
        if (!definition.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return false;
        var writes = false;
        var validates = false;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) continue;
            if (!step.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
            var name = type.GetString() ?? "";
            if (Array.IndexOf(WriteStepTypes, name) >= 0) writes = true;
            if (Array.IndexOf(ValidationStepTypes, name) >= 0) validates = true;
        }
        return writes && !validates;
    }
}
