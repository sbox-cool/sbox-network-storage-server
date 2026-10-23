using System.Text.Json;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Storage;
/// <summary>In-memory workspace for module lifecycle tests (modules.json only).</summary>
public sealed class FakeWorkspaceStore : IWorkspaceStore
{
    private readonly Dictionary<string, JsonElement> _resources = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
    {
        if (!_resources.TryGetValue(Key(userId, projectId, resourcePath), out var element))
            return Task.FromResult<T?>(default);
        return Task.FromResult(element.Deserialize<T>());
    }

    public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
    {
        _resources[Key(userId, projectId, resourcePath)] = JsonSerializer.SerializeToElement(data);
        return Task.CompletedTask;
    }

    public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    private static string Key(long userId, string projectId, string resourcePath) => $"{userId}/{projectId}/{resourcePath}";
}

/// <summary>Recording audit log for module lifecycle tests.</summary>
public sealed class FakeAuditLogger : IAuditLogger
{
    public List<AuditLogRequest> Requests { get; } = [];
    public Task LogActionAsync(AuditLogRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }
}

/// <summary>Module lifecycle: install namespacing, conflicts, typed uninstall, upgrade holds.</summary>
public sealed class ModuleLifecycleTests
{
    private const long Owner = 1;
    private const string Project = "test-game";

    private static (InMemoryNetworkStorageStore Store, FakeWorkspaceStore Workspace, ModuleInstaller Installer, FakeAuditLogger Audit) Setup()
    {
        var store = new InMemoryNetworkStorageStore();
        var workspace = new FakeWorkspaceStore();
        var states = new ModuleStateStore(workspace);
        var audit = new FakeAuditLogger();
        var installer = new ModuleInstaller(store, workspace, states, audit, TimeProvider.System);
        return (store, workspace, installer, audit);
    }

    [Fact]
    public async Task Install_creates_prefixed_owned_definitions_and_values()
    {
        var (store, workspace, installer, audit) = Setup();
        var result = await installer.InstallAsync(Owner, Project, "economy", settings: null, default);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Installed);

        var collections = await store.ListCollectionsAsync(Project, default);
        Assert.Contains(collections, row => ModuleOwned(row, "economy_balance", "economy"));
        var endpoints = await store.ListEndpointsAsync(Project, default);
        Assert.Contains(endpoints, row => ModuleOwned(row, "economy_earn", "economy"));
        Assert.Contains(endpoints, row => ModuleOwned(row, "economy_spend", "economy"));

        var values = await store.ReadGameValuesAsync(Project, default);
        Assert.NotNull(values);
        var payload = values.Value.TryGetProperty("payload_json", out var nested) ? nested : values.Value;
        Assert.Contains(ValuesItems(payload), item => ItemId(item) == "economy_limits");
        Assert.Contains(audit.Requests, request => request.Action == "module.install");
        var state = await new ModuleStateStore(workspace).LoadAsync(Owner, Project, default);
        Assert.Contains(state.Modules, module => module.Id == "economy" && module.Enabled);
    }

    [Fact]
    public async Task Install_refuses_on_name_conflict()
    {
        var (store, _, installer, _) = Setup();
        await store.UpsertCollectionAsync(Project, "economy_balance", "economy_balance", "private",
            JsonSerializer.SerializeToElement(new { name = "economy_balance" }), 1, default);
        var preview = await installer.PreviewAsync(Owner, Project, "economy", default);
        Assert.NotNull(preview);
        Assert.Contains(preview.Conflicts, conflict => conflict.Name == "economy_balance");
        Assert.False(preview.CanInstall);
        var result = await installer.InstallAsync(Owner, Project, "economy", settings: null, default);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Reinstalling_the_same_version_is_stable()
    {
        var (store, _, installer, _) = Setup();
        Assert.True((await installer.InstallAsync(Owner, Project, "progression", settings: null, default)).Ok);
        var again = await installer.InstallAsync(Owner, Project, "progression", settings: null, default);
        Assert.True(again.Ok);
        Assert.Equal("1.0.0", again.Installed?.Version);
        Assert.Single(await store.ListCollectionsAsync(Project, default));
    }

    [Fact]
    public async Task Uninstall_deletes_records_beyond_ten_thousand_and_they_do_not_return_on_reinstall()
    {
        var (store, _, installer, _) = Setup();
        Assert.True((await installer.InstallAsync(Owner, Project, "economy", null, default)).Ok);
        for (var i = 0; i <= 10_000; i++)
            await store.UpsertRecordAsync(Project, "economy_balance", $"player{i:D5}",
                JsonSerializer.SerializeToElement(new { balance = i }), false, 1, default);
        await store.UpsertRecordAsync(Project, "unrelated", "keep", JsonSerializer.SerializeToElement(new { keep = true }), false, 1, default);

        Assert.True((await installer.UninstallAsync(Owner, Project, "economy", "economy", keepData: false, default)).Ok);
        Assert.Null(await store.ReadRecordAsync(Project, "economy_balance", "player10000", default));
        Assert.Empty(await store.ListRecordsAsync(Project, "economy_balance", default));
        Assert.True((await installer.InstallAsync(Owner, Project, "economy", null, default)).Ok);
        Assert.Null(await store.ReadRecordAsync(Project, "economy_balance", "player10000", default));
        Assert.NotNull(await store.ReadRecordAsync(Project, "unrelated", "keep", default));
    }

    [Fact]
    public async Task Disable_hides_hooks_panels_and_endpoints_while_keeping_data()
    {
        var (store, workspace, installer, _) = Setup();
        Assert.True((await installer.InstallAsync(Owner, Project, "economy", settings: null, default)).Ok);
        Assert.True(await installer.SetEnabledAsync(Owner, Project, "economy", enabled: false, default));
        var state = await new ModuleStateStore(workspace).LoadAsync(Owner, Project, default);
        Assert.True(ModuleStateStore.DisabledLookup(state)("economy"));
        Assert.NotEmpty(await store.ListCollectionsAsync(Project, default));
        Assert.True(await installer.SetEnabledAsync(Owner, Project, "economy", enabled: true, default));
        state = await new ModuleStateStore(workspace).LoadAsync(Owner, Project, default);
        Assert.False(ModuleStateStore.DisabledLookup(state)("economy"));
    }

    [Fact]
    public async Task Uninstall_needs_the_typed_name_and_keeps_or_deletes_data()
    {
        var (store, _, installer, _) = Setup();
        Assert.True((await installer.InstallAsync(Owner, Project, "inventory", settings: null, default)).Ok);
        await store.UpsertRecordAsync(Project, "inventory_state", "player-1",
            JsonSerializer.SerializeToElement(new { inventory = new object[] { } }), false, 1, default);

        var mistyped = await installer.UninstallAsync(Owner, Project, "inventory", "nope", keepData: true, default);
        Assert.False(mistyped.Ok);

        var kept = await installer.UninstallAsync(Owner, Project, "inventory", "inventory", keepData: true, default);
        Assert.True(kept.Ok);
        Assert.Contains(kept.KeptCollections, name => name == "inventory_state");
        var collections = await store.ListCollectionsAsync(Project, default);
        var ordinary = Assert.Single(collections);
        Assert.False(ModuleOwnership.IsOwned(DefinitionOf(ordinary)));
        Assert.NotEmpty(await store.ListRecordsAsync(Project, "inventory_state", default));

        // The kept names are ordinary now, so reinstalling refuses until they are resolved.
        var blocked = await installer.InstallAsync(Owner, Project, "inventory", settings: null, default);
        Assert.False(blocked.Ok);

        var fresh = Setup();
        Assert.True((await fresh.Installer.InstallAsync(Owner, Project, "inventory", settings: null, default)).Ok);
        var deleted = await fresh.Installer.UninstallAsync(Owner, Project, "inventory", "inventory", keepData: false, default);
        Assert.True(deleted.Ok);
        Assert.Empty(await fresh.Store.ListCollectionsAsync(Project, default));
        Assert.Empty(await fresh.Store.ListRecordsAsync(Project, "inventory_state", default));
    }

    [Fact]
    public void Upgrade_hold_triggers_only_on_new_permissions()
    {
        Assert.Empty(ModuleInstaller.NewPermissions(["read-player-data"], ["read-player-data"]));
        Assert.Equal(["block-requests"],
            ModuleInstaller.NewPermissions(["flag-players"], ["flag-players", "block-requests"]));
    }

    [Fact]
    public void Ownership_predicates_distinguish_owned_and_ordinary_definitions()
    {
        var owned = JsonSerializer.SerializeToElement(new { name = "economy_earn", module = "economy" });
        var ordinary = JsonSerializer.SerializeToElement(new { name = "earn" });
        Assert.True(ModuleOwnership.IsOwned(owned));
        Assert.Equal("economy", ModuleOwnership.OwnerOf(owned));
        Assert.False(ModuleOwnership.IsOwned(ordinary));
        Assert.True(ModuleOwnership.MustKeep(owned));
        Assert.False(ModuleOwnership.MustKeep(ordinary));
        Assert.Equal("economy_earn", ModuleOwnership.Namespaced("economy", "earn"));
    }

    [Fact]
    public void Values_merge_keeps_owned_items_across_full_replaces()
    {
        var existing = JsonSerializer.SerializeToElement(new
        {
            items = new object[]
            {
                new { id = "economy_limits", type = "group" },
                new { id = "custom", type = "group" },
            },
        });
        var incoming = JsonSerializer.SerializeToElement(new
        {
            items = new object[] { new { id = "custom", type = "group" } },
        });
        var merged = ModuleInstaller.MergeValuesPreservingOwned(existing, incoming, new HashSet<string>(["economy_limits"]));
        var ids = ValuesItems(merged).Select(ItemId).ToList();
        Assert.Contains("economy_limits", ids);
        Assert.Contains("custom", ids);
    }

    private static bool ModuleOwned(JsonElement row, string name, string module)
    {
        if (row.ValueKind != JsonValueKind.Object) return false;
        var display = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()
            : row.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return display == name && ModuleOwnership.OwnedBy(DefinitionOf(row), module);
    }

    private static JsonElement DefinitionOf(JsonElement row)
    {
        if (row.TryGetProperty("definition_json", out var definition))
        {
            if (definition.ValueKind == JsonValueKind.String)
                return JsonDocument.Parse(definition.GetString() ?? "{}").RootElement;
            return definition;
        }
        return row;
    }

    private static IEnumerable<JsonElement> ValuesItems(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private static string? ItemId(JsonElement item)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
}
