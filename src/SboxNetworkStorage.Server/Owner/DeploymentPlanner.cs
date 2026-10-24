using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// The stored state a plan is checked against: live rows, staged endpoint and collection overrides, whether
/// revision data exists, and the names installed modules own, per kind.
/// </summary>
internal sealed record DeploymentState(OwnerProjectResources Live, JsonObject Staged, bool HasRevisionData,
    IReadOnlyDictionary<string, HashSet<string>> Owned, JsonElement RevisionPolicy);

/// <summary>
/// Builds deployment plans. Planning is pure: it reads the state it is given, never writes, never executes
/// endpoints or scripts, and never calls out. References are static; dynamic or runtime-built references are
/// reported as unknown, not validated.
/// </summary>
internal static class DeploymentPlanner
{
    private const string GameValues = "game-values";
    private static readonly string[] RowKinds = ["collection", "endpoint", "workflow", "query"];
    private static readonly string[] WriteRank = ["collection", GameValues, "workflow", "query", "endpoint"];
    private static readonly string[] DeleteRank = ["endpoint", "workflow", "query", "collection"];

    private sealed record Upsert(ProposedUpsert Proposed, JsonElement? Compiled, string Action, string Publish);

    private sealed record Delete(ProposedDelete Proposed, bool Exists, string Action);

    public static DeploymentPlan Plan(DeploymentProposal proposal, DeploymentState state, IReadOnlyList<IEndpointScriptRuntime> runtimes)
    {
        var issues = new List<PlanIssue>();
        var upserts = proposal.Upserts.Select(upsert => Classify(upsert, proposal.TargetsNext, state, runtimes)).ToList();
        var deletes = new List<Delete>();
        foreach (var delete in proposal.Deletes)
        {
            var row = FindRow(state.Live, delete.Kind, delete.Id);
            var owned = IsOwned(state.Owned, delete.Kind, delete.Id, row is null ? null : NameOf(delete.Kind, row.Value));
            if (owned)
            {
                deletes.Add(new Delete(delete, row is not null, "module-retained"));
                continue;
            }
            if (row is null) issues.Add(new PlanIssue(PlanSeverity.Blocker, delete.Kind, delete.Id,
                $"There is no live {delete.Kind} {delete.Id} to delete."));
            deletes.Add(new Delete(delete, row is not null, "delete"));
        }

        // What the project would hold after this plan: the basis for every reference check.
        var writes = upserts
            .Where(upsert => (upsert.Action is "create" or "update") && upsert.Proposed.Kind != GameValues && upsert.Publish != "staged")
            .Select(upsert => (Kind: upsert.Proposed.Kind, Id: upsert.Proposed.Id, Definition: upsert.Compiled!.Value))
            .ToList();
        var removals = deletes
            .Where(delete => delete.Action == "delete" && delete.Exists)
            .Select(delete => (delete.Proposed.Kind, delete.Proposed.Id))
            .ToList();
        var gameValuesUpsert = upserts.FirstOrDefault(upsert => upsert.Proposed.Kind == GameValues && (upsert.Action is "create" or "update"));
        var resultingGameValues = gameValuesUpsert?.Compiled ?? state.Live.GameValues;
        var liveAfter = state.Live.WithChanges(writes, removals, resultingGameValues);
        var stagedAfter = (JsonObject)state.Staged.DeepClone();
        foreach (var upsert in upserts.Where(upsert => upsert.Publish == "staged" && upsert.Action is "create" or "update"))
        {
            var section = upsert.Proposed.Kind == "endpoint" ? RevisionOverrides.EndpointsSection : RevisionOverrides.CollectionsSection;
            if (stagedAfter[section] is not JsonObject entries) stagedAfter[section] = entries = new JsonObject();
            var definition = upsert.Compiled!.Value;
            entries[NameOf(upsert.Proposed.Kind, definition) ?? upsert.Proposed.Id] = JsonNode.Parse(definition.GetRawText());
        }
        var nextBefore = OverlayStaged(state.Live, state.Staged, state.Owned);
        var nextAfter = OverlayStaged(liveAfter, stagedAfter, state.Owned);
        var items = new List<PlanItem>();

        foreach (var upsert in upserts)
        {
            var kind = upsert.Proposed.Kind;
            var id = upsert.Proposed.Id;
            var references = new List<PlanReference>();
            var resulting = upsert.Publish == "staged" ? nextAfter : liveAfter;
            var resultingIndex = Index(resulting);
            if (upsert.Action is "create" or "update" or "invalid")
            {
                var resourceId = kind == GameValues ? null : id;
                var diagnostics = OwnerResourceSource.ValidateSave(kind, upsert.Proposed.Resource, resourceId, resulting, runtimes, out _);
                foreach (var diagnostic in diagnostics)
                    issues.Add(new PlanIssue(diagnostic.IsError ? PlanSeverity.Blocker : PlanSeverity.Warning, kind, id, Describe(diagnostic)));
                if (upsert.Action == "invalid" && !diagnostics.Any(diagnostic => diagnostic.IsError))
                    issues.Add(new PlanIssue(PlanSeverity.Blocker, kind, id, "The definition could not be compiled."));
            }
            if (upsert.Compiled is { } definition && upsert.Action is "create" or "update")
            {
                foreach (var reference in DeploymentDependencies.Read(kind, definition, runtimes))
                {
                    var status = ReferenceStatus(reference, resultingIndex);
                    references.Add(new PlanReference(reference.Kind, reference.Id, reference.Path, status));
                    if (status == "missing") issues.Add(new PlanIssue(PlanSeverity.Blocker, kind, id,
                        $"References {reference.Kind} {reference.Id}, which is not defined after this plan."));
                }
            }
            items.Add(new PlanItem(upsert.Action, kind, id, upsert.Publish, references));
        }

        var changed = new HashSet<(string Kind, string Id)>();
        foreach (var upsert in upserts.Where(upsert => upsert.Action is "create" or "update"))
        {
            changed.Add((upsert.Proposed.Kind, upsert.Proposed.Id));
            if (upsert.Compiled is { } definition && NameOf(upsert.Proposed.Kind, definition) is { } name)
                changed.Add((upsert.Proposed.Kind, name));
            var previous = FindRow(state.Live, upsert.Proposed.Kind, upsert.Proposed.Id);
            if (previous is { } row && NameOf(upsert.Proposed.Kind, row) is { } oldName)
                changed.Add((upsert.Proposed.Kind, oldName));
        }
        foreach (var removal in removals)
        {
            changed.Add(removal);
            if (FindRow(state.Live, removal.Kind, removal.Id) is { } row && NameOf(removal.Kind, row) is { } name)
                changed.Add((removal.Kind, name));
        }
        if (gameValuesUpsert is not null)
        {
            changed.UnionWith(state.Live.ValueItems().Select(item => ("value", item.Id)));
            changed.UnionWith(liveAfter.ValueItems().Select(item => ("value", item.Id)));
        }
        var dependents = new List<PlanDependent>();
        ScanDependents(state.Live, liveAfter, "live", changed, runtimes, issues, dependents);
        if (state.HasRevisionData) ScanDependents(nextBefore, nextAfter, "next", changed, runtimes, issues, dependents);

        foreach (var delete in deletes)
            items.Add(new PlanItem(delete.Action, delete.Proposed.Kind, delete.Proposed.Id, "live", []));
        if (upserts.FirstOrDefault(upsert => upsert.Proposed.Kind == GameValues) is { } gameValuesProposal
            && state.Owned.TryGetValue(GameValues, out var ownedValues))
        {
            var incoming = gameValuesProposal.Proposed.Resource;
            var incomingIds = incoming.TryGetProperty("items", out var entries) && entries.ValueKind == JsonValueKind.Array
                ? entries.EnumerateArray().Select(item => OwnerProjectResources.Text(item, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in state.Live.ValueItems().Where(value => ownedValues.Contains(value.Id) && !incomingIds.Contains(value.Id)))
                items.Add(new PlanItem("module-retained", "value", value.Id, "live", []));
        }

        var hash = Hash(proposal, state, upserts, deletes);
        return new DeploymentPlan(proposal.Target, hash, items, issues.Distinct().ToArray(), state.HasRevisionData,
            dependents.Distinct().ToArray(), state.RevisionPolicy);
    }

    /// <summary>Upserts in apply order: collections, game values, workflows, queries, then endpoints in dependency order.</summary>
    public static IReadOnlyList<PlanItem> WriteOrder(DeploymentPlan plan)
    {
        var writes = plan.Items.Where(item => item.Action is "create" or "update").ToList();
        var endpoints = SortEndpoints(writes.Where(item => item.Kind == "endpoint").ToList());
        return [.. writes.Where(item => item.Kind != "endpoint").OrderBy(item => Array.IndexOf(WriteRank, item.Kind)), .. endpoints];
    }

    /// <summary>Explicit deletes: endpoints, then workflows, queries, and collections last.</summary>
    public static IReadOnlyList<PlanItem> DeleteOrder(DeploymentPlan plan) =>
        plan.Items.Where(item => item.Action == "delete").OrderBy(item => Array.IndexOf(DeleteRank, item.Kind)).ToList();

    private static Upsert Classify(ProposedUpsert upsert, bool targetsNext, DeploymentState state, IReadOnlyList<IEndpointScriptRuntime> runtimes)
    {
        var resourceId = upsert.Kind == GameValues ? null : upsert.Id;
        OwnerResourceSource.ValidateSave(upsert.Kind, upsert.Resource, resourceId, state.Live, runtimes, out var compiled);
        JsonElement? compiledDefinition = compiled.ValueKind == JsonValueKind.Object ? compiled : null;
        var publish = PublishFor(upsert.Kind, targetsNext, state);
        var owned = upsert.Kind != GameValues && IsOwned(state.Owned, upsert.Kind, upsert.Id, NameOf(upsert.Kind, compiledDefinition ?? upsert.Resource));
        if (owned) return new Upsert(upsert, compiledDefinition, "module-retained", publish);
        if (compiledDefinition is null) return new Upsert(upsert, null, "invalid", publish);
        if (upsert.Kind == GameValues && state.Owned.TryGetValue(GameValues, out var ownedValues))
            compiledDefinition = ModuleInstaller.MergeValuesPreservingOwned(state.Live.GameValues, compiledDefinition.Value, ownedValues);

        var current = CurrentDefinition(upsert.Kind, upsert.Id, publish, state);
        var action = current is null ? "create"
            : Canonical(current.Value) == Canonical(compiledDefinition.Value) ? "unchanged"
            : "update";
        return new Upsert(upsert, compiledDefinition, action, publish);
    }

    private static string PublishFor(string kind, bool targetsNext, DeploymentState state)
    {
        if (!targetsNext || kind is not ("endpoint" or "collection")) return "live";
        return state.HasRevisionData ? "staged" : "live-fallback";
    }

    private static JsonElement? CurrentDefinition(string kind, string id, string publish, DeploymentState state)
    {
        if (kind == GameValues) return state.Live.GameValues;
        if (publish == "staged")
        {
            var effective = OverlayStaged(state.Live, state.Staged, state.Owned);
            var stagedRow = FindRow(effective, kind, id);
            return stagedRow is null ? null : OwnerProjectResources.Column(stagedRow.Value, "definition_json");
        }
        var row = FindRow(state.Live, kind, id);
        return row is null ? null : OwnerProjectResources.Column(row.Value, "definition_json");
    }


    private static JsonElement? FindRow(OwnerProjectResources live, string kind, string id)
    {
        foreach (var row in live.Rows(kind))
            if (OwnerProjectResources.Text(row, kind + "_id") == id) return row;
        return null;
    }

    private static bool IsOwned(IReadOnlyDictionary<string, HashSet<string>> owned, string kind, string id, string? name) =>
        owned.TryGetValue(kind, out var names) && (names.Contains(id) || name is not null && names.Contains(name));

    private static string NameField(string kind) => kind == "endpoint" ? "slug" : "name";

    private static string? NameOf(string kind, JsonElement definition) => OwnerProjectResources.Text(definition, NameField(kind));

    private static string? Str(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string Describe(DefinitionDiagnostic diagnostic) =>
        string.IsNullOrEmpty(diagnostic.Path) || diagnostic.Path == "/" ? diagnostic.Message : $"{diagnostic.Message} ({diagnostic.Path})";

    private static Dictionary<string, HashSet<string>> Index(OwnerProjectResources set)
    {
        var collections = new HashSet<string>(set.ExistingIds("collection"), StringComparer.OrdinalIgnoreCase);
        collections.UnionWith(set.Rows("collection").Select(row => OwnerProjectResources.Text(row, "name")).OfType<string>());
        var endpoints = set.ExistingIds("endpoint");
        endpoints.UnionWith(set.Rows("endpoint").Select(row => OwnerProjectResources.Text(row, "slug")).OfType<string>());
        return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["collection"] = collections,
            ["endpoint"] = endpoints,
            ["workflow"] = set.ExistingIds("workflow"),
            ["query"] = set.ExistingIds("query"),
            ["value"] = set.ValueItems().Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
        };
    }

    private static bool Contains(IReadOnlyDictionary<string, HashSet<string>> index, DeploymentDependencies.Reference reference) =>
        index.TryGetValue(reference.Kind, out var ids) && ids.Contains(reference.Id);

    private static string ReferenceStatus(DeploymentDependencies.Reference reference, IReadOnlyDictionary<string, HashSet<string>> index)
    {
        if (reference.Dynamic || reference.Kind == "unknown") return "unknown";
        return Contains(index, reference) ? "resolved" : "missing";
    }

    private static List<PlanItem> SortEndpoints(IReadOnlyList<PlanItem> endpoints)
    {
        var ids = endpoints.Select(endpoint => endpoint.Id).ToHashSet(StringComparer.Ordinal);
        var dependencies = endpoints.ToDictionary(endpoint => endpoint.Id, endpoint => endpoint.References
            .Where(reference => reference.Kind == "endpoint" && reference.Status == "resolved" && reference.Id != endpoint.Id && ids.Contains(reference.Id))
            .Select(reference => reference.Id)
            .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<PlanItem>();
        while (ordered.Count < endpoints.Count)
        {
            var next = endpoints.FirstOrDefault(endpoint => !done.Contains(endpoint.Id) && dependencies[endpoint.Id].All(done.Contains));
            if (next is null)
            {
                // A reference cycle cannot be ordered; keep input order for the rest.
                ordered.AddRange(endpoints.Where(endpoint => !done.Contains(endpoint.Id)));
                break;
            }
            ordered.Add(next);
            done.Add(next.Id);
        }
        return ordered;
    }

    private static OwnerProjectResources OverlayStaged(OwnerProjectResources live, JsonObject staged,
        IReadOnlyDictionary<string, HashSet<string>> owned)
    {
        var writes = new List<(string Kind, string Id, JsonElement Definition)>();
        foreach (var kind in new[] { "collection", "endpoint" })
        {
            var section = kind == "endpoint" ? RevisionOverrides.EndpointsSection : RevisionOverrides.CollectionsSection;
            var nameField = NameField(kind);
            foreach (var (key, definition) in RevisionOverrides.Items(staged, section))
            {
                var name = Str(definition, nameField) ?? key;
                var row = live.Rows(kind).FirstOrDefault(row => OwnerProjectResources.Text(row, nameField) == name);
                var liveId = row.ValueKind == JsonValueKind.Object ? OwnerProjectResources.Text(row, kind + "_id") : null;
                if (IsOwned(owned, kind, liveId ?? Str(definition, "id") ?? name, name)) continue;
                var existing = row.ValueKind == JsonValueKind.Object ? OwnerProjectResources.Column(row, "definition_json") : null;
                var merged = RevisionOverrides.MergeOverLive(existing, definition);
                var id = Str(merged, "id") ?? liveId ?? name;
                writes.Add((kind, id, JsonSerializer.SerializeToElement(merged)));
            }
        }
        return live.WithChanges(writes, [], live.GameValues);
    }

    private static void ScanDependents(OwnerProjectResources before, OwnerProjectResources after, string target,
        HashSet<(string Kind, string Id)> changed, IReadOnlyList<IEndpointScriptRuntime> runtimes,
        List<PlanIssue> issues, List<PlanDependent> dependents)
    {
        var beforeIndex = Index(before);
        var afterIndex = Index(after);
        foreach (var kind in RowKinds)
        {
            foreach (var row in after.Rows(kind))
            {
                var id = OwnerProjectResources.Text(row, kind + "_id");
                if (id is null || OwnerProjectResources.Column(row, "definition_json") is not { ValueKind: JsonValueKind.Object } definition)
                    continue;
                foreach (var reference in DeploymentDependencies.Read(kind, definition, runtimes))
                {
                    var status = ReferenceStatus(reference, afterIndex);
                    if (reference.Dynamic || changed.Contains((reference.Kind, reference.Id)))
                        dependents.Add(new PlanDependent(kind, id, target, reference.Kind, reference.Id, status));
                    if (status != "missing") continue;
                    issues.Add(Contains(beforeIndex, reference)
                        ? new PlanIssue(PlanSeverity.Blocker, kind, id,
                            $"References {reference.Kind} {reference.Id}, which this plan removes from {target}.")
                        : new PlanIssue(PlanSeverity.Warning, kind, id,
                            $"References {reference.Kind} {reference.Id}, which is already missing from {target}."));
                }
            }
        }
    }

    private static string Hash(DeploymentProposal proposal, DeploymentState state, IReadOnlyList<Upsert> upserts, IReadOnlyList<Delete> deletes)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["target"] = proposal.Target,
            ["upserts"] = upserts.Select(upsert => new Dictionary<string, object?>
            {
                ["kind"] = upsert.Proposed.Kind,
                ["id"] = upsert.Proposed.Id,
                ["action"] = upsert.Action,
                ["definition"] = upsert.Compiled ?? upsert.Proposed.Resource,
            }).ToList(),
            ["deletes"] = deletes.Select(delete => new Dictionary<string, object?>
            {
                ["kind"] = delete.Proposed.Kind,
                ["id"] = delete.Proposed.Id,
                ["action"] = delete.Action,
            }).ToList(),
            ["live"] = RowKinds.ToDictionary(kind => kind, kind => (object?)state.Live.Rows(kind)),
            ["gameValues"] = state.Live.GameValues,
            ["staged"] = state.Staged,
            ["hasRevisionData"] = state.HasRevisionData,
            ["revisionPolicy"] = state.RevisionPolicy,
            ["owned"] = state.Owned.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal),
        };
        var canonical = Canonical(JsonSerializer.SerializeToElement(envelope));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Canonical JSON text: object keys sorted ordinally, no insignificant whitespace.</summary>
    private static string Canonical(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
