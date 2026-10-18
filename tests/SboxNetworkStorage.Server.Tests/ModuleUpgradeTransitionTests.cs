using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Upgrade transitions against the real store (SQLite and PostgreSQL). An installed module is
/// aged in <c>modules.json</c> to look like an older release, then upgraded through
/// <see cref="ModuleInstaller"/>. Covers #128: customized settings survive both upgrade paths,
/// and an upgrade that collides with an operator's own definition is refused before anything is
/// deleted or rewritten.
/// </summary>
public abstract class ModuleUpgradeTransitionTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string Economy = "economy";
    private const string Integrity = "integrity";
    private const string OldVersion = "0.9.0";
    private const string CurrentVersion = "1.0.0";
    private readonly TFactory factory;

    protected ModuleUpgradeTransitionTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task Automatic_upgrade_keeps_customized_settings_and_drops_removed_ones()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
        var owner = NetworkStorageServices.LocalOwnerUserId;

        var install = await installer.InstallAsync(owner, project.ProjectId, Economy,
            new Dictionary<string, string> { ["starting_balance"] = "500" }, default);
        Assert.True(install.Ok, install.Error);
        await AgeAsync(states, owner, project.ProjectId, Economy, module => module with
        {
            Version = OldVersion,
            Settings = new Dictionary<string, string> { ["starting_balance"] = "500", ["retired_key"] = "gone" },
        });

        var upgrade = await installer.InstallAsync(owner, project.ProjectId, Economy, settings: null, default);

        Assert.True(upgrade.Ok, upgrade.Error);
        Assert.Equal(CurrentVersion, upgrade.Installed?.Version);
        Assert.Equal("500", upgrade.Installed!.Settings["starting_balance"]);
        Assert.DoesNotContain("retired_key", upgrade.Installed.Settings.Keys);
        var persisted = Assert.Single((await states.LoadAsync(owner, project.ProjectId, default)).Modules);
        Assert.Equal("500", persisted.Settings["starting_balance"]);
    }

    [SkippableFact]
    public async Task Automatic_upgrade_fills_settings_new_in_the_version_with_defaults()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
        var owner = NetworkStorageServices.LocalOwnerUserId;

        Assert.True((await installer.InstallAsync(owner, project.ProjectId, Economy, settings: null, default)).Ok);
        await AgeAsync(states, owner, project.ProjectId, Economy, module => module with
        {
            Version = OldVersion,
            Settings = new Dictionary<string, string>(),
        });

        var upgrade = await installer.InstallAsync(owner, project.ProjectId, Economy, settings: null, default);

        Assert.True(upgrade.Ok, upgrade.Error);
        Assert.Equal("0", upgrade.Installed!.Settings["starting_balance"]);
    }

    [SkippableFact]
    public async Task Automatic_upgrade_prefers_explicit_settings_over_saved_ones()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
        var owner = NetworkStorageServices.LocalOwnerUserId;

        Assert.True((await installer.InstallAsync(owner, project.ProjectId, Economy,
            new Dictionary<string, string> { ["starting_balance"] = "500" }, default)).Ok);
        await AgeAsync(states, owner, project.ProjectId, Economy, module => module with { Version = OldVersion });

        var upgrade = await installer.InstallAsync(owner, project.ProjectId, Economy,
            new Dictionary<string, string> { ["starting_balance"] = "900" }, default);

        Assert.True(upgrade.Ok, upgrade.Error);
        Assert.Equal("900", upgrade.Installed!.Settings["starting_balance"]);
    }

    [SkippableFact]
    public async Task Approved_upgrade_keeps_customized_settings()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
        var owner = NetworkStorageServices.LocalOwnerUserId;

        Assert.True((await installer.InstallAsync(owner, project.ProjectId, Integrity,
            new Dictionary<string, string> { ["max_calls_per_minute"] = "300" }, default)).Ok);
        await AgeAsync(states, owner, project.ProjectId, Integrity, module => module with
        {
            Version = OldVersion,
            Permissions = module.Permissions.Take(1).ToList(),
        });
        var held = await installer.InstallAsync(owner, project.ProjectId, Integrity, settings: null, default);
        Assert.False(held.Ok);
        Assert.Contains("approve", held.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var approved = await installer.ApproveUpgradeAsync(owner, project.ProjectId, Integrity, default);

        Assert.True(approved.Ok, approved.Error);
        Assert.Equal(CurrentVersion, approved.Installed!.Version);
        Assert.Equal("300", approved.Installed.Settings["max_calls_per_minute"]);
        var persisted = Assert.Single((await states.LoadAsync(owner, project.ProjectId, default)).Modules);
        Assert.Equal("300", persisted.Settings["max_calls_per_minute"]);
    }

    [SkippableFact]
    public async Task Automatic_upgrade_refuses_user_definition_collision_without_touching_anything()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var owner = NetworkStorageServices.LocalOwnerUserId;
        const string collision = "economy_spend";

        Assert.True((await installer.InstallAsync(owner, project.ProjectId, Economy, settings: null, default)).Ok);
        // The "old" release did not ship economy_spend, and the operator made their own endpoint with that name.
        await ReplaceWithOperatorEndpointAsync(store, project.ProjectId, collision);
        await AgeAsync(states, owner, project.ProjectId, Economy, module => module with
        {
            Version = OldVersion,
            Endpoints = module.Endpoints.Where(name => name != collision).ToList(),
        });

        var upgrade = await installer.InstallAsync(owner, project.ProjectId, Economy, settings: null, default);

        Assert.False(upgrade.Ok);
        Assert.Contains(collision, upgrade.Error ?? string.Empty);
        var state = Assert.Single((await states.LoadAsync(owner, project.ProjectId, default)).Modules);
        Assert.Equal(OldVersion, state.Version);
        Assert.DoesNotContain(collision, state.Endpoints);
        Assert.Contains("economy_earn", state.Endpoints);
        var earn = await store.ReadEndpointAsync(project.ProjectId, "economy_earn", default);
        Assert.NotNull(earn);
        Assert.True(ModuleOwnership.OwnedBy(DefinitionOf(earn!.Value), Economy));
        var operatorRow = await store.ReadEndpointAsync(project.ProjectId, collision, default);
        Assert.NotNull(operatorRow);
        Assert.Equal("operator-made", DefinitionOf(operatorRow!.Value).GetProperty("custom").GetString());
    }

    /// <summary>Rewrites the installed record so the workspace looks like an earlier release.</summary>
    private static async Task AgeAsync(ModuleStateStore states, long userId, string projectId, string moduleId,
        Func<InstalledModule, InstalledModule> change)
    {
        var state = await states.LoadAsync(userId, projectId, default);
        var modules = state.Modules.Select(module => module.Id == moduleId ? change(module) : module).ToList();
        await states.SaveAsync(userId, projectId, state with { Modules = modules }, default);
    }

    /// <summary>Removes the module-owned endpoint and stores an operator-written endpoint under the same name.</summary>
    private static async Task ReplaceWithOperatorEndpointAsync(INetworkStorageStore store, string projectId, string name)
    {
        await store.DeleteEndpointAsync(projectId, name, default);
        var definition = JsonSerializer.SerializeToElement(new { name, slug = name, custom = "operator-made" });
        await store.UpsertEndpointAsync(projectId, name, name, "POST", true, definition, null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), default);
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
}

public sealed class ModuleUpgradeTransitionTests_Sqlite(SqliteHostFactory factory)
    : ModuleUpgradeTransitionTests<SqliteHostFactory>(factory);

public sealed class ModuleUpgradeTransitionTests_Postgres(PostgresHostFactory factory)
    : ModuleUpgradeTransitionTests<PostgresHostFactory>(factory);
