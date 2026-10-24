using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>A batch of definition upserts and explicit deletes, as entered in the deployment console.</summary>
public sealed record DeploymentProposal(string Target, IReadOnlyList<ProposedUpsert> Upserts, IReadOnlyList<ProposedDelete> Deletes)
{
    public bool TargetsNext => Target == "next";
}

/// <summary>One definition to create or replace. The resource is the parsed definition as the owner wrote it.</summary>
public sealed record ProposedUpsert(string Kind, string Id, JsonElement Resource);

/// <summary>One definition to remove by id.</summary>
public sealed record ProposedDelete(string Kind, string Id);

/// <summary>Reads a proposal from YAML or JSON text. Unknown keys, duplicate kind/id pairs and empty batches are rejected.</summary>
internal static class DeploymentProposalReader
{
    private static readonly string[] DeletableKinds = ["collection", "endpoint", "workflow", "query"];
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

    /// <exception cref="ArgumentException">The proposal is empty or not a valid batch.</exception>
    /// <exception cref="JsonException">The proposal is not valid YAML or JSON.</exception>
    public static DeploymentProposal Parse(string? text, int maxPayloadBytes)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a deployment proposal first.");
        var root = OwnerYamlDefinitions.ParseDefinition(text, out _);
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("A proposal is a mapping with target, upserts and deletes.");
        RejectUnknown(root, "proposal", "target", "upserts", "deletes");

        var target = OwnerProjectResources.Text(root, "target") ?? "live";
        if (target is not ("live" or "next")) throw new ArgumentException("target must be live or next.");

        var seen = new HashSet<(string Kind, string Id)>();
        var upserts = new List<ProposedUpsert>();
        foreach (var item in Entries(root, "upserts"))
        {
            RejectUnknown(item, "upsert", "kind", "source");
            var kind = KindOf(item, "upsert");
            var source = OwnerProjectResources.Text(item, "source")
                ?? throw new ArgumentException($"Upsert for {kind} needs a source.");
            var resource = OwnerResourceSource.Parse(kind, source, null, maxPayloadBytes, out var resourceId);
            var id = kind == "game-values" ? "game-values" : resourceId
                ?? throw new ArgumentException($"Upsert for {kind} needs an id in its source.");
            Claim(seen, kind, id);
            upserts.Add(new ProposedUpsert(kind, id, resource));
        }

        var deletes = new List<ProposedDelete>();
        foreach (var item in Entries(root, "deletes"))
        {
            RejectUnknown(item, "delete", "kind", "id");
            var kind = KindOf(item, "delete");
            if (Array.IndexOf(DeletableKinds, kind) < 0) throw new ArgumentException($"Deployment plans cannot delete {kind} definitions.");
            var id = OwnerProjectResources.Text(item, "id") ?? "";
            if (!IdPattern.IsMatch(id)) throw new ArgumentException($"Delete for {kind} needs a valid id.");
            Claim(seen, kind, id);
            deletes.Add(new ProposedDelete(kind, id));
        }

        if (upserts.Count == 0 && deletes.Count == 0) throw new ArgumentException("Proposal has no changes.");
        return new DeploymentProposal(target, upserts, deletes);
    }

    private static List<JsonElement> Entries(JsonElement root, string name)
    {
        var entries = new List<JsonElement>();
        if (!root.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null) return entries;
        if (list.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{name} must be a list.");
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Each entry in {name} must be a mapping.");
            entries.Add(item);
        }
        return entries;
    }

    private static string KindOf(JsonElement item, string context)
    {
        var kind = OwnerProjectResources.Text(item, "kind");
        if (!OwnerResourceSource.IsKind(kind))
            throw new ArgumentException($"{context} kind must be one of: {string.Join(", ", OwnerResourceSource.Kinds)}.");
        return kind!;
    }

    private static void RejectUnknown(JsonElement item, string context, params string[] allowed)
    {
        foreach (var property in item.EnumerateObject())
            if (Array.IndexOf(allowed, property.Name) < 0) throw new ArgumentException($"Unknown {context} key '{property.Name}'.");
    }

    private static void Claim(HashSet<(string Kind, string Id)> seen, string kind, string id)
    {
        if (!seen.Add((kind, id))) throw new ArgumentException($"{kind} {id} appears more than once in this proposal.");
    }
}
