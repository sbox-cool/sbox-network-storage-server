using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public abstract class ModuleUpgradeTests<TFactory>(TFactory factory) : IClassFixture<TFactory> where TFactory : SelfHostFactory
{
    [SkippableTheory]
    [InlineData("endpoint", false)]
    [InlineData("workflow", false)]
    [InlineData("query", false)]
    [InlineData("collection", false)]
    [InlineData("endpoint", true)]
    [InlineData("workflow", true)]
    [InlineData("query", true)]
    [InlineData("collection", true)]
    public async Task Upgrade_conflicts_with_owner_replacements_even_when_the_catalog_retired_the_name(string kind, bool approve)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<INetworkStorageStore>();
        var installer = services.GetRequiredService<ModuleInstaller>();
        var states = services.GetRequiredService<ModuleStateStore>();
        var installed = (await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", null, default)).Installed!;
        const string name = "economy_retired_owner_definition";
        var ownerDefinition = JsonSerializer.SerializeToElement(new { id = name, name, description = "Owner replacement" });
        switch (kind)
        {
            case "endpoint": await store.UpsertEndpointAsync(project.ProjectId, name, name, "POST", true, ownerDefinition, null, 10, default); break;
            case "workflow": await store.UpsertWorkflowAsync(project.ProjectId, name, name, ownerDefinition, null, 10, default); break;
            case "query": await store.UpsertQueryAsync(project.ProjectId, name, name, false, ownerDefinition, 10, default); break;
            case "collection": await store.UpsertCollectionAsync(project.ProjectId, name, name, "private", ownerDefinition, 10, default); break;
        }
        installed = kind switch
        {
            "endpoint" => installed with { Endpoints = [.. installed.Endpoints, name] },
            "workflow" => installed with { Workflows = [.. installed.Workflows, name] },
            "query" => installed with { Queries = [.. installed.Queries, name] },
            _ => installed with { Collections = [.. installed.Collections, name] },
        };
        await states.SaveAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId,
            new ModuleProjectState([installed with { Version = "0.0.0" }], []), default);
        var result = approve
            ? await installer.ApproveUpgradeAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", default)
            : await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", null, default);
        Assert.False(result.Ok);
        Assert.Contains(name, result.Error);
        var row = kind switch
        {
            "endpoint" => await store.ReadEndpointAsync(project.ProjectId, name, default),
            "workflow" => await store.ReadWorkflowAsync(project.ProjectId, name, default),
            "query" => await store.ReadQueryAsync(project.ProjectId, name, default),
            _ => await store.ReadCollectionAsync(project.ProjectId, name, default),
        };
        Assert.Equal("Owner replacement", row!.Value.GetProperty("definition_json").GetProperty("description").GetString());
        foreach (var endpoint in installed.Endpoints.Where(id => id != name))
            Assert.Equal("economy", (await store.ReadEndpointAsync(project.ProjectId, endpoint, default))!.Value.GetProperty("definition_json").GetProperty("module").GetString());
        Assert.Equal("0.0.0", (await states.LoadAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, default)).Modules.Single().Version);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_replacement_rolls_back_deleted_definitions_and_partial_new_writes(bool approve)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<INetworkStorageStore>();
        var installer = services.GetRequiredService<ModuleInstaller>();
        var states = services.GetRequiredService<ModuleStateStore>();
        var installed = (await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", null, default)).Installed!;
        await states.SaveAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId,
            new ModuleProjectState([installed with { Version = "0.0.0" }], []), default);
        var original = (await store.ReadEndpointAsync(project.ProjectId, installed.Endpoints[0], default))!.Value.GetProperty("definition_json").GetRawText();
        var trigger = "upgrade_fault_" + Guid.NewGuid().ToString("N");
        var projectLiteral = project.ProjectId.Replace("'", "''", StringComparison.Ordinal);
        DbConnection connection;
        string create;
        string drop;
        if (factory.Driver == StoreDriver.Postgres)
        {
            var options = StoreRegistration.BuildPostgresOptions(factory.Config);
            connection = new NpgsqlConnection(options.BuildConnectionString());
            var function = $"\"{options.Schema}\".\"{trigger}\"";
            create = $"CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.project_id = '{projectLiteral}' THEN RAISE EXCEPTION 'injected module upgrade write failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER \"{trigger}\" BEFORE INSERT ON \"{options.Schema}\".endpoints FOR EACH ROW EXECUTE FUNCTION {function}();";
            drop = $"DROP TRIGGER \"{trigger}\" ON \"{options.Schema}\".endpoints; DROP FUNCTION {function}();";
        }
        else
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StoreRegistration.SqlitePath(factory.Config), Pooling = false }.ToString());
            create = $"CREATE TRIGGER {trigger} BEFORE INSERT ON endpoints WHEN NEW.project_id = '{projectLiteral}' BEGIN SELECT RAISE(ABORT, 'injected module upgrade write failure'); END;";
            drop = $"DROP TRIGGER {trigger};";
        }
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = create;
            await command.ExecuteNonQueryAsync();
            try
            {
                await Assert.ThrowsAnyAsync<DbException>(async () =>
                {
                    if (approve) await installer.ApproveUpgradeAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", default);
                    else await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", null, default);
                });
            }
            finally { command.CommandText = drop; await command.ExecuteNonQueryAsync(); }
        }
        Assert.Equal(original, (await store.ReadEndpointAsync(project.ProjectId, installed.Endpoints[0], default))!.Value.GetProperty("definition_json").GetRawText());
        foreach (var collection in installed.Collections)
            Assert.Equal("economy", (await store.ReadCollectionAsync(project.ProjectId, collection, default))!.Value.GetProperty("definition_json").GetProperty("module").GetString());
        Assert.Equal("0.0.0", (await states.LoadAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, default)).Modules.Single().Version);
        var retry = approve
            ? await installer.ApproveUpgradeAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", default)
            : await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "economy", null, default);
        Assert.True(retry.Ok, retry.Error);
        Assert.Equal(installed.Version, retry.Installed!.Version);
    }
}

public sealed class SqliteModuleUpgradeTests(SqliteHostFactory factory) : ModuleUpgradeTests<SqliteHostFactory>(factory);
public sealed class PostgresModuleUpgradeTests(PostgresHostFactory factory) : ModuleUpgradeTests<PostgresHostFactory>(factory);
