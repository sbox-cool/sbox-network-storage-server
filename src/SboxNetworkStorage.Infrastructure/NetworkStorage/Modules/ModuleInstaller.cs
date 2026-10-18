using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>What installing a module would add, and what stands in the way.</summary>
public sealed record ModuleDefinitionPreview(string Kind, string Name);
public sealed record ModuleConflict(string Kind, string Name, string Reason);
public sealed record ModulePreview(
    CatalogModule Module,
    IReadOnlyList<ModuleDefinitionPreview> Definitions,
    IReadOnlyList<ModuleConflict> Conflicts,
    bool CanInstall);

public sealed record ModuleInstallResult(bool Ok, string? Error, InstalledModule? Installed);
public sealed record ModuleUninstallResult(bool Ok, string? Error, IReadOnlyList<string> KeptCollections);

/// <summary>
/// The module lifecycle: preview, install, enable/disable, uninstall and
/// upgrades. Definitions are written into the live definition tables with a
/// <c>module</c> ownership field under the module's <c>&lt;id&gt;_</c> prefix;
/// only this class writes owned definitions. Upgrades that add permissions are
/// held on the old version until the owner approves them.
/// </summary>
public sealed class ModuleInstaller(
    INetworkStorageStore store,
    IWorkspaceStore workspace,
    ModuleStateStore states,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>Compiles every authored definition of a catalog module, stamping ownership.</summary>
    public static bool TryCompileModule(CatalogModule module, out IReadOnlyList<JsonElement> compiled, out string? error)
    {
        var list = new List<JsonElement>();
        foreach (var source in module.Definitions)
        {
            if (!NetworkStorageSourceResourceCompiler.TryCompile(source.Source, source.Kind, out var definition, out var compileError))
            {
                compiled = [];
                error = $"Definition '{source.LocalId}' ({source.Kind}) failed to compile: {compileError}";
                return false;
            }
            list.Add(StampOwnership(definition, module.Id));
        }
        compiled = list;
        error = null;
        return true;
    }

    /// <summary>Stamps the ownership field; the compiled name must already carry the module prefix.</summary>
    public static JsonElement StampOwnership(JsonElement compiled, string moduleId)
    {
        var node = JsonNode.Parse(compiled.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("Module definitions must be JSON objects.");
        node[ModuleOwnership.Field] = moduleId;
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString())!;
    }

    /// <summary>Merges incoming game-values items over the existing payload, keeping owned items the incoming doc drops.</summary>
    public static JsonElement MergeValuesPreservingOwned(JsonElement? existingPayload, JsonElement incomingPayload, IReadOnlySet<string> ownedItemIds)
    {
        if (ownedItemIds.Count == 0) return incomingPayload;
        var incomingItems = ValuesItems(incomingPayload);
        var incomingIds = new HashSet<string>(incomingItems.Select(item => ItemId(item) ?? string.Empty), StringComparer.Ordinal);
        var kept = new List<JsonElement>();
        if (existingPayload.HasValue)
        {
            foreach (var item in ValuesItems(existingPayload.Value))
            {
                var id = ItemId(item);
                if (id is not null && ownedItemIds.Contains(id) && !incomingIds.Contains(id))
                    kept.Add(item);
            }
        }
        if (kept.Count == 0) return incomingPayload;
        var merged = JsonNode.Parse(incomingPayload.GetRawText())?.AsObject();
        if (merged is null) return incomingPayload;
        if (merged["items"] is not JsonArray items)
        {
            items = new JsonArray();
            merged["items"] = items;
        }
        foreach (var item in kept) items.Add(JsonNode.Parse(item.GetRawText()));
        return JsonSerializer.Deserialize<JsonElement>(merged.ToJsonString())!;
    }

    private static IEnumerable<JsonElement> ValuesItems(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray() : [];

    private static string? ItemId(JsonElement item)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    public async Task<ModulePreview?> PreviewAsync(long userId, string projectId, string moduleId, CancellationToken ct)
    {
        var module = ModuleCatalog.Load().FirstOrDefault(candidate => candidate.Id == moduleId);
        if (module is null || !module.Available) return null;
        if (!TryCompileModule(module, out var compiled, out _)) return null;
        var conflicts = await FindConflictsAsync(projectId, module, compiled, excludeOwner: moduleId, ct);
        var definitions = compiled.Select((definition, index) => new ModuleDefinitionPreview(
            module.Definitions[index].Kind, module.Definitions[index].LocalId)).ToList();
        return new ModulePreview(module, definitions, conflicts, conflicts.Count == 0);
    }

    public async Task<ModuleInstallResult> InstallAsync(long userId, string projectId, string moduleId,
        IReadOnlyDictionary<string, string>? settings, CancellationToken ct)
    {
        var module = ModuleCatalog.Load().FirstOrDefault(candidate => candidate.Id == moduleId);
        if (module is null) return new ModuleInstallResult(false, "Unknown module.", null);
        if (!module.Available) return new ModuleInstallResult(false, $"The module is unavailable: {module.UnavailableReason}", null);
        if (!TryCompileModule(module, out var compiled, out var compileError))
            return new ModuleInstallResult(false, compileError, null);
        var prefix = ModuleOwnership.Prefix(moduleId);
        for (var index = 0; index < compiled.Count; index++)
        {
            var definition = compiled[index];
            if (module.Definitions[index].Kind == "game-values")
            {
                if (!ValuesItems(definition).Select(ItemId).Where(id => id is not null).Cast<string>().All(id => id.StartsWith(prefix, StringComparison.Ordinal)))
                    return new ModuleInstallResult(false, $"The catalog game-values items are not namespaced under '{prefix}'.", null);
                continue;
            }
            if (!NameCarriesPrefix(definition, prefix))
                return new ModuleInstallResult(false, $"The catalog definition is not namespaced under '{prefix}'.", null);
            if (RequiresSecretKey(definition))
                return new ModuleInstallResult(false, "Starter-kit definitions must work with a public key.", null);
        }
        var state = await states.LoadAsync(userId, projectId, ct);
        var existing = state.Modules.FirstOrDefault(installed => installed.Id == moduleId);
        if (existing is not null)
        {
            if (ModuleCatalog.CompareVersions(existing.Version, module.Version) >= 0)
                return new ModuleInstallResult(true, null, existing);
            return await UpgradeAsync(userId, projectId, state, existing, module, compiled, settings, ct);
        }
        var conflicts = await FindConflictsAsync(projectId, module, compiled, excludeOwner: moduleId, ct);
        if (conflicts.Count > 0) return ConflictFailure(conflicts);
        var installed = await WriteDefinitionsAsync(userId, projectId, module, compiled,
            ResolveSettings(module, null, settings), ct);
        await states.SaveAsync(userId, projectId, new ModuleProjectState([.. state.Modules, installed], state.PendingUpgrades), ct);
        await AuditAsync(userId, projectId, "module.install",
            new { module = moduleId, version = module.Version, definitions = installed.Endpoints.Count + installed.Collections.Count + installed.Workflows.Count + installed.Queries.Count }, ct);
        return new ModuleInstallResult(true, null, installed);
    }

    public async Task<bool> SetEnabledAsync(long userId, string projectId, string moduleId, bool enabled, CancellationToken ct)
    {
        var state = await states.LoadAsync(userId, projectId, ct);
        var index = state.Modules.ToList().FindIndex(installed => installed.Id == moduleId);
        if (index < 0) return false;
        var modules = state.Modules.ToList();
        var current = modules[index];
        if (current.Enabled == enabled) return true;
        modules[index] = current with { Enabled = enabled };
        await states.SaveAsync(userId, projectId, new ModuleProjectState(modules, state.PendingUpgrades), ct);
        await AuditAsync(userId, projectId, enabled ? "module.enable" : "module.disable", new { module = moduleId }, ct);
        return true;
    }

    public async Task<ModuleUninstallResult> UninstallAsync(long userId, string projectId, string moduleId,
        string typedName, bool keepData, CancellationToken ct)
    {
        if (!string.Equals(typedName, moduleId, StringComparison.Ordinal))
            return new ModuleUninstallResult(false, "Type the module id to confirm uninstall.", []);
        var state = await states.LoadAsync(userId, projectId, ct);
        var installed = state.Modules.FirstOrDefault(candidate => candidate.Id == moduleId);
        if (installed is null) return new ModuleUninstallResult(false, "The module is not installed.", []);
        var kept = new List<string>();
        foreach (var name in installed.Endpoints)
        {
            var row = await store.ReadEndpointAsync(projectId, name, ct);
            if (row.HasValue && ModuleOwnership.OwnedBy(DefinitionOf(row.Value), moduleId))
            {
                if (keepData) await ConvertToOrdinaryEndpointAsync(projectId, name, row.Value, ct);
                else await store.DeleteEndpointAsync(projectId, name, ct);
            }
        }
        foreach (var name in installed.Workflows)
        {
            var row = await store.ReadWorkflowAsync(projectId, name, ct);
            if (row.HasValue && ModuleOwnership.OwnedBy(DefinitionOf(row.Value), moduleId))
            {
                if (keepData) await ConvertToOrdinaryWorkflowAsync(projectId, name, row.Value, ct);
                else await store.DeleteWorkflowAsync(projectId, name, ct);
            }
        }
        foreach (var name in installed.Queries)
        {
            var row = await store.ReadQueryAsync(projectId, name, ct);
            if (row.HasValue && ModuleOwnership.OwnedBy(DefinitionOf(row.Value), moduleId))
            {
                if (keepData) await ConvertToOrdinaryQueryAsync(projectId, name, row.Value, ct);
                else await store.DeleteQueryAsync(projectId, name, ct);
            }
        }
        foreach (var name in installed.Collections)
        {
            var row = await store.ReadCollectionAsync(projectId, name, ct);
            if (row.HasValue && ModuleOwnership.OwnedBy(DefinitionOf(row.Value), moduleId))
            {
                if (keepData)
                {
                    await ConvertToOrdinaryCollectionAsync(projectId, name, row.Value, ct);
                    kept.Add(name);
                }
                else
                {
                    await DeleteCollectionDataAsync(projectId, name, ct);
                    await store.DeleteCollectionAsync(projectId, name, ct);
                }
            }
        }
        if (installed.ValuesItems.Count > 0)
        {
            var payload = await ReadValuesPayloadAsync(projectId, ct);
            if (payload.HasValue)
            {
                var pruned = RemoveValuesItems(payload.Value, installed.ValuesItems);
                await store.UpsertGameValuesAsync(projectId, pruned, versionHash: null, NextVersion(), ct);
            }
        }
        await states.SaveAsync(userId, projectId,
            new ModuleProjectState(state.Modules.Where(candidate => candidate.Id != moduleId).ToList(),
                state.PendingUpgrades.Where(pending => pending.Id != moduleId).ToList()), ct);
        await AuditAsync(userId, projectId, "module.uninstall",
            new { module = moduleId, keepData, keptCollections = kept }, ct);
        return new ModuleUninstallResult(true, null, kept);
    }

    /// <summary>
    /// Applies a held upgrade after the owner approves its new permissions. The
    /// installed module keeps running its previous version until this runs.
    /// </summary>
    public async Task<ModuleInstallResult> ApproveUpgradeAsync(long userId, string projectId, string moduleId, CancellationToken ct)
    {
        var module = ModuleCatalog.Load().FirstOrDefault(candidate => candidate.Id == moduleId);
        if (module is null || !module.Available) return new ModuleInstallResult(false, "Unknown module.", null);
        var state = await states.LoadAsync(userId, projectId, ct);
        var installed = state.Modules.FirstOrDefault(candidate => candidate.Id == moduleId);
        if (installed is null) return new ModuleInstallResult(false, "The module is not installed.", null);
        if (!TryCompileModule(module, out var compiled, out var compileError))
            return new ModuleInstallResult(false, compileError, null);
        var conflicts = await FindConflictsAsync(projectId, module, compiled, excludeOwner: moduleId, ct);
        if (conflicts.Count > 0) return ConflictFailure(conflicts);
        await DeleteOwnedDefinitionsAsync(projectId, installed, keepValues: false, ct);
        var upgraded = await WriteDefinitionsAsync(userId, projectId, module, compiled,
            ResolveSettings(module, installed.Settings, explicitValues: null), ct);
        upgraded = installed.Enabled ? upgraded : upgraded with { Enabled = false };
        var modules = state.Modules.Select(candidate => candidate.Id == moduleId ? upgraded : candidate).ToList();
        var pending = state.PendingUpgrades.Where(candidate => candidate.Id != moduleId).ToList();
        await states.SaveAsync(userId, projectId, new ModuleProjectState(modules, pending), ct);
        await AuditAsync(userId, projectId, "module.upgrade",
            new { module = moduleId, from = installed.Version, to = module.Version }, ct);
        return new ModuleInstallResult(true, null, upgraded);
    }

    private async Task<ModuleInstallResult> UpgradeAsync(long userId, string projectId, ModuleProjectState state,
        InstalledModule installed, CatalogModule module, IReadOnlyList<JsonElement> compiled,
        IReadOnlyDictionary<string, string>? settings, CancellationToken ct)
    {
        var newPermissions = NewPermissions(installed.Permissions, module.Permissions);
        if (newPermissions.Count == 0)
        {
            var conflicts = await FindConflictsAsync(projectId, module, compiled, excludeOwner: module.Id, ct);
            if (conflicts.Count > 0) return ConflictFailure(conflicts);
            await DeleteOwnedDefinitionsAsync(projectId, installed, keepValues: false, ct);
            var upgraded = await WriteDefinitionsAsync(userId, projectId, module, compiled,
                ResolveSettings(module, installed.Settings, settings), ct);
            upgraded = installed.Enabled ? upgraded : upgraded with { Enabled = false };
            var modules = state.Modules.Select(candidate => candidate.Id == module.Id ? upgraded : candidate).ToList();
            await states.SaveAsync(userId, projectId, new ModuleProjectState(modules, state.PendingUpgrades), ct);
            await AuditAsync(userId, projectId, "module.upgrade",
                new { module = module.Id, from = installed.Version, to = module.Version }, ct);
            return new ModuleInstallResult(true, null, upgraded);
        }
        var pending = state.PendingUpgrades.Where(candidate => candidate.Id != module.Id).ToList();
        pending.Add(new PendingModuleUpgrade(module.Id, module.Version, module.Permissions));
        await states.SaveAsync(userId, projectId, new ModuleProjectState(state.Modules, pending), ct);
        await AuditAsync(userId, projectId, "module.upgrade.held",
            new { module = module.Id, from = installed.Version, to = module.Version, newPermissions }, ct);
        return new ModuleInstallResult(false,
            $"Version {module.Version} needs new permissions ({string.Join(", ", newPermissions)}). The installed version {installed.Version} keeps running until you approve.", null);
    }

    /// <summary>Catalog permissions not yet granted to the installed module. Non-empty holds the upgrade.</summary>
    internal static IReadOnlyList<string> NewPermissions(IReadOnlyList<string> granted, IReadOnlyList<string> requested)
        => requested.Where(permission => !granted.Contains(permission, StringComparer.Ordinal)).ToList();

    private async Task<IReadOnlyList<ModuleConflict>> FindConflictsAsync(string projectId, CatalogModule module,
        IReadOnlyList<JsonElement> compiled, string excludeOwner, CancellationToken ct)
    {
        var conflicts = new List<ModuleConflict>();
        var live = await ReadLiveNamesAsync(projectId, ct);
        for (var index = 0; index < compiled.Count; index++)
        {
            var kind = module.Definitions[index].Kind;
            var name = module.Definitions[index].LocalId;
            if (kind == "game-values") continue;
            if (!live.TryGetValue(kind, out var names) || !names.Contains(name)) continue;
            var existing = await ReadLiveDefinitionAsync(projectId, kind, name, ct);
            if (existing.HasValue && ModuleOwnership.OwnedBy(existing.Value, excludeOwner)) continue;
            conflicts.Add(new ModuleConflict(kind, name, "An existing definition already uses this name."));
        }
        return conflicts;
    }

    private async Task<Dictionary<string, HashSet<string>>> ReadLiveNamesAsync(string projectId, CancellationToken ct)
    {
        // Both ids and display names: a live row collides when either matches
        // the module's namespaced id (module installs use id == display name).
        static IEnumerable<string> Both(JsonElement row, string idColumn, string nameColumn)
        {
            yield return NameOf(row, idColumn);
            yield return NameOf(row, nameColumn);
        }
        var live = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        live["collection"] = new HashSet<string>(
            (await store.ListCollectionsAsync(projectId, ct)).SelectMany(row => Both(row, "collection_id", "name")), StringComparer.Ordinal);
        live["endpoint"] = new HashSet<string>(
            (await store.ListEndpointsAsync(projectId, ct)).SelectMany(row => Both(row, "endpoint_id", "slug")), StringComparer.Ordinal);
        live["workflow"] = new HashSet<string>(
            (await store.ListWorkflowsAsync(projectId, ct)).SelectMany(row => Both(row, "workflow_id", "name")), StringComparer.Ordinal);
        live["query"] = new HashSet<string>(
            (await store.ListQueriesAsync(projectId, ct)).SelectMany(row => Both(row, "query_id", "name")), StringComparer.Ordinal);
        live["collection"].Remove(string.Empty);
        live["endpoint"].Remove(string.Empty);
        live["workflow"].Remove(string.Empty);
        live["query"].Remove(string.Empty);
        return live;
    }

    private static string NameOf(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private async Task<JsonElement?> ReadLiveDefinitionAsync(string projectId, string kind, string name, CancellationToken ct)
    {
        var row = kind switch
        {
            "collection" => await store.ReadCollectionAsync(projectId, name, ct),
            "endpoint" => await store.ReadEndpointAsync(projectId, name, ct),
            "workflow" => await store.ReadWorkflowAsync(projectId, name, ct),
            "query" => await store.ReadQueryAsync(projectId, name, ct),
            _ => null,
        };
        return row.HasValue ? DefinitionOf(row.Value) : null;
    }

    private static JsonElement DefinitionOf(JsonElement row)
    {
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("definition_json", out var definition))
        {
            if (definition.ValueKind == JsonValueKind.String)
            {
                try { return JsonDocument.Parse(definition.GetString() ?? "null").RootElement; }
                catch (JsonException) { return row; }
            }
            return definition;
        }
        return row;
    }

    private async Task<InstalledModule> WriteDefinitionsAsync(long userId, string projectId, CatalogModule module,
        IReadOnlyList<JsonElement> compiled, Dictionary<string, string> settings, CancellationToken ct)
    {
        var collections = new List<string>();
        var endpoints = new List<string>();
        var workflows = new List<string>();
        var queries = new List<string>();
        var valuesItems = new List<string>();
        var version = NextVersion();
        for (var index = 0; index < compiled.Count; index++)
        {
            var kind = module.Definitions[index].Kind;
            var localId = module.Definitions[index].LocalId;
            var definition = compiled[index];
            switch (kind)
            {
                case "collection":
                    await store.UpsertCollectionAsync(projectId, localId, localId,
                        Text(definition, "visibility") ?? "private", definition, version, ct);
                    collections.Add(localId);
                    break;
                case "endpoint":
                    await store.UpsertEndpointAsync(projectId, localId, localId,
                        Text(definition, "method") ?? "POST",
                        !IsFalse(definition, "enabled"), definition,
                        Text(definition, "versionHash"), version, ct);
                    await ManagementProjectObjects.RecordVersionAsync(
                        store, userId, projectId, "endpoint", localId, definition, "module-install", version, ct);
                    endpoints.Add(localId);
                    break;
                case "workflow":
                    await store.UpsertWorkflowAsync(projectId, localId, localId, definition, Text(definition, "versionHash"), version, ct);
                    workflows.Add(localId);
                    break;
                case "query":
                    await store.UpsertQueryAsync(projectId, localId, localId,
                        IsTrue(definition, "requiresSecretKey"), definition, version, ct);
                    queries.Add(localId);
                    break;
                case "game-values":
                    valuesItems.AddRange(await MergeValuesItemsAsync(projectId, definition, version, ct));
                    break;
            }
        }
        return new InstalledModule(module.Id, module.Version, true,
            settings, module.Permissions,
            collections, endpoints, workflows, queries, valuesItems,
            time.GetUtcNow().ToString("o"));
    }

    private async Task<IReadOnlyList<string>> MergeValuesItemsAsync(string projectId, JsonElement moduleValues, long version, CancellationToken ct)
    {
        var ids = ValuesItems(moduleValues).Select(ItemId).Where(id => id is not null).Cast<string>().ToList();
        if (ids.Count == 0) return [];
        var payload = await ReadValuesPayloadAsync(projectId, ct);
        var merged = payload.HasValue ? MergeValuesItems(payload.Value, moduleValues) : moduleValues;
        await store.UpsertGameValuesAsync(projectId, merged, versionHash: null, version, ct);
        return ids;
    }

    private static JsonElement MergeValuesItems(JsonElement existing, JsonElement incoming)
    {
        var merged = JsonNode.Parse(existing.GetRawText())?.AsObject();
        if (merged is null) return incoming;
        if (merged["items"] is not JsonArray items)
        {
            items = new JsonArray();
            merged["items"] = items;
        }
        var incomingIds = new HashSet<string>(
            ValuesItems(incoming).Select(ItemId).Where(id => id is not null).Cast<string>(), StringComparer.Ordinal);
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index] is JsonObject current
                && current["id"]?.GetValue<string>() is { } id && incomingIds.Contains(id))
                items.RemoveAt(index);
        }
        foreach (var item in ValuesItems(incoming)) items.Add(JsonNode.Parse(item.GetRawText()));
        return JsonSerializer.Deserialize<JsonElement>(merged.ToJsonString())!;
    }

    private static JsonElement RemoveValuesItems(JsonElement payload, IReadOnlyList<string> ids)
    {
        var removed = new HashSet<string>(ids, StringComparer.Ordinal);
        var node = JsonNode.Parse(payload.GetRawText())?.AsObject();
        if (node is null) return payload;
        if (node["items"] is not JsonArray items) return payload;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index] is JsonObject current
                && current["id"]?.GetValue<string>() is { } id && removed.Contains(id))
                items.RemoveAt(index);
        }
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString())!;
    }

    private async Task<JsonElement?> ReadValuesPayloadAsync(string projectId, CancellationToken ct)
    {
        var row = await store.ReadGameValuesAsync(projectId, ct);
        if (!row.HasValue || row.Value.ValueKind != JsonValueKind.Object) return null;
        if (!row.Value.TryGetProperty("payload_json", out var payload)) return row;
        if (payload.ValueKind == JsonValueKind.String)
        {
            try { return JsonDocument.Parse(payload.GetString() ?? "null").RootElement; }
            catch (JsonException) { return null; }
        }
        return payload;
    }

    private async Task DeleteOwnedDefinitionsAsync(string projectId, InstalledModule installed, bool keepValues, CancellationToken ct)
    {
        foreach (var name in installed.Endpoints) await store.DeleteEndpointAsync(projectId, name, ct);
        foreach (var name in installed.Workflows) await store.DeleteWorkflowAsync(projectId, name, ct);
        foreach (var name in installed.Queries) await store.DeleteQueryAsync(projectId, name, ct);
        foreach (var name in installed.Collections) await store.DeleteCollectionAsync(projectId, name, ct);
        if (!keepValues && installed.ValuesItems.Count > 0)
        {
            var payload = await ReadValuesPayloadAsync(projectId, ct);
            if (payload.HasValue)
                await store.UpsertGameValuesAsync(projectId, RemoveValuesItems(payload.Value, installed.ValuesItems), versionHash: null, NextVersion(), ct);
        }
    }

    private async Task DeleteCollectionDataAsync(string projectId, string collectionId, CancellationToken ct)
    {
        var keys = (await store.ListRecordsAsync(projectId, collectionId, ct))
            .Select(row => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("record_key", out var key)
                && key.ValueKind == JsonValueKind.String ? key.GetString() : null)
            .Where(key => key is not null)
            .Cast<string>()
            .Take(10_000)
            .ToList();
        foreach (var key in keys)
        {
            await store.DeleteLedgerEntriesAsync(projectId, collectionId, key, ct);
            await store.DeleteRecordAsync(projectId, collectionId, key, ct);
        }
    }

    private async Task ConvertToOrdinaryEndpointAsync(string projectId, string name, JsonElement row, CancellationToken ct)
    {
        var definition = StripOwnership(DefinitionOf(row));
        await store.UpsertEndpointAsync(projectId, name, NameOr(definition, "slug", name),
            Text(definition, "method") ?? "POST", !IsFalse(definition, "enabled"),
            definition, Text(definition, "versionHash"), NextVersion(), ct);
    }

    private async Task ConvertToOrdinaryWorkflowAsync(string projectId, string name, JsonElement row, CancellationToken ct)
    {
        var definition = StripOwnership(DefinitionOf(row));
        await store.UpsertWorkflowAsync(projectId, name, NameOr(definition, "name", name), definition, Text(definition, "versionHash"), NextVersion(), ct);
    }

    private async Task ConvertToOrdinaryQueryAsync(string projectId, string name, JsonElement row, CancellationToken ct)
    {
        var definition = StripOwnership(DefinitionOf(row));
        await store.UpsertQueryAsync(projectId, name, NameOr(definition, "name", name),
            IsTrue(definition, "requiresSecretKey"), definition, NextVersion(), ct);
    }

    private async Task ConvertToOrdinaryCollectionAsync(string projectId, string name, JsonElement row, CancellationToken ct)
    {
        var definition = StripOwnership(DefinitionOf(row));
        await store.UpsertCollectionAsync(projectId, name, NameOr(definition, "name", name),
            Text(definition, "visibility") ?? "private", definition, NextVersion(), ct);
    }

    private static JsonElement StripOwnership(JsonElement definition)
    {
        var node = JsonNode.Parse(definition.GetRawText())?.AsObject() ?? new JsonObject();
        node.Remove(ModuleOwnership.Field);
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString())!;
    }

    private static bool NameCarriesPrefix(JsonElement definition, string prefix)
    {
        foreach (var name in new[] { "slug", "name", "id" })
        {
            if (definition.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && text.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool RequiresSecretKey(JsonElement definition)
        => IsTrue(definition, "requiresSecretKey");

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string NameOr(JsonElement element, string name, string fallback)
        => Text(element, name) ?? fallback;

    private static bool IsTrue(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static bool IsFalse(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.False;

    /// <summary>
    /// Settings for a module version: explicit operator values win, then values already installed,
    /// then catalog defaults. Blank values count as absent, and keys the catalog no longer declares
    /// are dropped. Other values are stored as entered; each consumer falls back to its own default
    /// when a value does not parse.
    /// </summary>
    internal static Dictionary<string, string> ResolveSettings(CatalogModule module,
        IReadOnlyDictionary<string, string>? installedValues, IReadOnlyDictionary<string, string>? explicitValues)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var schema in module.Settings)
            settings[schema.Key] = Pick(explicitValues, schema.Key) ?? Pick(installedValues, schema.Key) ?? schema.Default;
        return settings;
    }

    private static string? Pick(IReadOnlyDictionary<string, string>? values, string key)
        => values is not null && values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static ModuleInstallResult ConflictFailure(IReadOnlyList<ModuleConflict> conflicts)
        => new(false,
            $"Conflicts with existing definitions: {string.Join(", ", conflicts.Select(conflict => $"{conflict.Kind} {conflict.Name}"))}. Resolve them and try again.", null);

    private long NextVersion() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private Task AuditAsync(long userId, string projectId, string action, object summary, CancellationToken ct)
        => audit.LogActionAsync(new AuditLogRequest(projectId, userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            action, new { id = userId, type = "owner-dashboard" }, new { id = projectId, type = "project" },
            summary, Before: null, After: null), ct);
}
