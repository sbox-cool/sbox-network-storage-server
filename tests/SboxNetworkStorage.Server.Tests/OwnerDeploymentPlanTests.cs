using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using SboxNetworkStorage.Storage;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Owner deployment plans (issue #232): preview classification, reference blocking, atomic apply and the console.</summary>
public abstract class OwnerDeploymentPlanTests<TFactory> : IDisposable where TFactory : SelfHostFactory, new()
{
    private const string PlayersUpsert = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: players
              name: players
              collectionType: player
              schema:
                coins:
                  type: number
        """;

    private const string PlayersChanged = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: players
              name: players
              collectionType: player
              schema:
                coins:
                  type: number
                level:
                  type: number
        """;

    private const string PlayersAndScores = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: players
              name: players
              collectionType: player
              schema:
                coins:
                  type: number
          - kind: query
            source: |
              id: scores
              name: Scores
              type: count
              sources:
                - collectionId: players
              config:
                limit: 20
        """;

    private const string MissingReferenceQuery = """
        target: live
        upserts:
          - kind: query
            source: |
              id: scores
              name: Scores
              type: count
              sources:
                - collectionId: missing_players
              config:
                limit: 20
        """;

    private const string DeletePlayers = """
        target: live
        deletes:
          - kind: collection
            id: players
        """;

    private const string SetupWorkflow = """
        target: live
        upserts:
          - kind: workflow
            source: |
              id: setup
              name: Setup
              steps: []
        """;

    private const string SetupWorkflowAndDeletePlayers = """
        target: live
        upserts:
          - kind: workflow
            source: |
              id: setup
              name: Setup
              steps: []
        deletes:
          - kind: collection
            id: players
        """;

    private const string DynamicEndpoint = """
        target: live
        upserts:
          - kind: endpoint
            source: |
              id: rates-health
              slug: rates-health
              method: GET
              enabled: true
              response:
                rate: "values[rates]"
        """;

    private const string NextTarget = """
        target: next
        upserts:
          - kind: collection
            source: |
              id: players
              name: players
              collectionType: player
              schema:
                coins:
                  type: number
          - kind: workflow
            source: |
              id: setup
              name: Setup
              steps: []
        """;

    private const string OwnedStats = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: owned_stats
              name: owned_stats
              collectionType: player
              schema:
                coins:
                  type: number
        """;

    private const string OwnedStatsChanged = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: owned_stats
              name: owned_stats
              collectionType: player
              schema:
                coins:
                  type: number
                level:
                  type: number
        """;

    private const string OtherStats = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: other_stats
              name: other_stats
              collectionType: player
              schema:
                coins:
                  type: number
        """;

    private const string RollbackBatch = """
        target: live
        upserts:
          - kind: collection
            source: |
              id: rollback_players
              name: rollback_players
              collectionType: player
              schema:
                coins:
                  type: number
          - kind: endpoint
            source: |
              id: deploy-boom
              slug: deploy-boom
              method: GET
              enabled: true
              response:
                ok: true
        """;

    private readonly TFactory factory = new();

    protected OwnerDeploymentPlanTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);

    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task PreviewClassifiesCreateUpdateAndUnchangedWithoutWriting()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Preview");
        var id = project.ProjectId;

        var created = await PreviewAsync(id, PlayersUpsert);
        Assert.False(created.Blocked);
        var item = Assert.Single(created.Items);
        Assert.Equal("create", item.Action);
        Assert.Equal("collection", item.Kind);
        Assert.Equal("live", item.Publish);
        Assert.False(await ExistsAsync(id, "collection", "players"), "Preview must not write.");

        await ApplyNowAsync(id, PlayersUpsert);
        Assert.Equal("unchanged", Assert.Single((await PreviewAsync(id, PlayersUpsert)).Items).Action);
        Assert.Equal("update", Assert.Single((await PreviewAsync(id, PlayersChanged)).Items).Action);
    }

    [SkippableFact]
    public async Task SameBatchReferencesResolveAgainstTheResultingSet()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Same batch");
        var id = project.ProjectId;

        var plan = await PreviewAsync(id, PlayersAndScores);
        Assert.False(plan.Blocked, string.Join("; ", plan.Issues.Select(issue => issue.Message)));
        var query = plan.Items.Single(item => item.Kind == "query");
        Assert.Contains(query.References, reference => reference.Kind == "collection" && reference.Id == "players" && reference.Status == "resolved");

        var result = await ApplyAsync(id, PlayersAndScores, plan.Hash);
        Assert.Equal(DeploymentOutcome.Applied, result.Outcome);
        Assert.True(await ExistsAsync(id, "collection", "players"));
        Assert.True(await ExistsAsync(id, "query", "scores"));
    }

    [SkippableFact]
    public async Task MissingStaticReferenceBlocksApproval()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Missing reference");
        var id = project.ProjectId;

        var plan = await PreviewAsync(id, MissingReferenceQuery);
        Assert.True(plan.Blocked);
        Assert.Contains(plan.Issues, issue => issue.Severity == PlanSeverity.Blocker);
        var query = plan.Items.Single(item => item.Kind == "query");
        Assert.Contains(query.References, reference => reference.Id == "missing_players" && reference.Status == "missing");

        var result = await ApplyAsync(id, MissingReferenceQuery, plan.Hash);
        Assert.Equal(DeploymentOutcome.Blocked, result.Outcome);
        Assert.False(await ExistsAsync(id, "query", "scores"));
    }

    [SkippableFact]
    public async Task DynamicReferencesAreUnknownAndDoNotBlock()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Dynamic reference");
        var id = project.ProjectId;

        var plan = await PreviewAsync(id, DynamicEndpoint);
        Assert.False(plan.Blocked);
        var endpoint = Assert.Single(plan.Items);
        Assert.Contains(endpoint.References, reference => reference.Status == "unknown");

        var result = await ApplyAsync(id, DynamicEndpoint, plan.Hash);
        Assert.Equal(DeploymentOutcome.Applied, result.Outcome);
    }

    [SkippableFact]
    public async Task DeletingAReferencedCollectionBlocksTheWholePlan()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Dependent delete");
        var id = project.ProjectId;
        await ApplyNowAsync(id, PlayersAndScores);

        var plan = await PreviewAsync(id, DeletePlayers);
        Assert.True(plan.Blocked);
        Assert.Contains(plan.Items, item => item.Action == "delete" && item.Kind == "collection" && item.Id == "players");
        Assert.Contains(plan.Issues, issue => issue.Severity == PlanSeverity.Blocker);

        var result = await ApplyAsync(id, DeletePlayers, plan.Hash);
        Assert.Equal(DeploymentOutcome.Blocked, result.Outcome);
        Assert.True(await ExistsAsync(id, "collection", "players"));
        Assert.True(await ExistsAsync(id, "query", "scores"));
    }

    [SkippableFact]
    public async Task ApplyCommitsUpsertsAndDeletesTogether()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Upserts and deletes");
        var id = project.ProjectId;
        await ApplyNowAsync(id, PlayersUpsert);

        var plan = await PreviewAsync(id, SetupWorkflowAndDeletePlayers);
        Assert.False(plan.Blocked, string.Join("; ", plan.Issues.Select(issue => issue.Message)));

        var result = await ApplyAsync(id, SetupWorkflowAndDeletePlayers, plan.Hash);
        Assert.Equal(DeploymentOutcome.Applied, result.Outcome);
        Assert.True(await ExistsAsync(id, "workflow", "setup"));
        Assert.False(await ExistsAsync(id, "collection", "players"));
    }

    [SkippableFact]
    public async Task ModuleOwnedDefinitionsAreRetainedAndNotWritten()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Module owned");
        var id = project.ProjectId;
        await ApplyNowAsync(id, OwnedStats);
        await OwnCollectionAsync(id, "owned_stats");
        var before = await CollectionRowsAsync(id);

        var plan = await PreviewAsync(id, OwnedStatsChanged);
        Assert.False(plan.Blocked);
        Assert.Equal("module-retained", Assert.Single(plan.Items).Action);

        var result = await ApplyAsync(id, OwnedStatsChanged, plan.Hash);
        Assert.Equal(DeploymentOutcome.Applied, result.Outcome);
        Assert.Equal(before, await CollectionRowsAsync(id));
    }

    [SkippableFact]
    public async Task NextTargetLabelsFallbackAndLiveOnlyKinds()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Next target");
        var id = project.ProjectId;

        var plan = await PreviewAsync(id, NextTarget);
        Assert.Equal("next", plan.Target);
        Assert.False(plan.Blocked);
        var collection = plan.Items.Single(item => item.Kind == "collection");
        Assert.Equal(plan.HasRevisionData ? "staged" : "live-fallback", collection.Publish);
        Assert.Equal("live", plan.Items.Single(item => item.Kind == "workflow").Publish);
    }

    [SkippableFact]
    public async Task ApplyRollsBackEveryWriteWhenALaterWriteFails()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Rollback");
        var id = project.ProjectId;
        await InstallEndpointFailureAsync();

        var plan = await PreviewAsync(id, RollbackBatch);
        Assert.False(plan.Blocked);

        // The existing sync writer catches the trigger error and returns a refused write.
        var result = await ApplyAsync(id, RollbackBatch, plan.Hash);
        Assert.Equal(DeploymentOutcome.Failed, result.Outcome);
        Assert.False(await ExistsAsync(id, "collection", "rollback_players"), "The earlier collection write must roll back.");
        Assert.False(await ExistsAsync(id, "endpoint", "deploy-boom"));
    }

    [SkippableFact]
    public async Task ConsoleRefusesAStalePlanWithoutWriting()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Stale plan");
        var id = project.ProjectId;
        var root = $"/dashboard/projects/{id}/deployment";
        using var client = await LoggedInClientAsync(factory);
        var page = await client.GetStringAsync(root);

        using var preview = await client.PostAsync(root + "/preview", Form(("proposal", PlayersUpsert), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var hash = PlanHash(await preview.Content.ReadAsStringAsync());

        // The live project changes after the plan was previewed.
        await ApplyNowAsync(id, OtherStats);

        using var apply = await client.PostAsync(root + "/apply", Form(("proposal", PlayersUpsert), ("planHash", hash), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        Assert.False(await ExistsAsync(id, "collection", "players"));
    }

    [SkippableFact]
    public async Task ConsolePreviewAndApproveRoundTrip()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Console round trip");
        var id = project.ProjectId;
        var root = $"/dashboard/projects/{id}/deployment";
        using var client = await LoggedInClientAsync(factory);
        var page = await client.GetStringAsync(root);

        using var preview = await client.PostAsync(root + "/preview", Form(("proposal", PlayersUpsert), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.False(await ExistsAsync(id, "collection", "players"), "Preview must not write.");
        var hash = PlanHash(await preview.Content.ReadAsStringAsync());

        using var apply = await client.PostAsync(root + "/apply", Form(("proposal", PlayersUpsert), ("planHash", hash), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        Assert.EndsWith("/deployment", apply.Headers.Location!.ToString());
        Assert.True(await ExistsAsync(id, "collection", "players"));
    }

    [SkippableFact]
    public async Task GlobalAndImporterCanChangeExportsInOneAtomicBatch()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Coordinated globals");
        const string original = """
            target: live
            upserts:
              - kind: endpoint
                source: |
                  id: pricing
                  slug: pricing
                  enabled: true
                  scope: global
                  runtime: javascript
                  script: |
                    export function oldPrice() { return 3; }
              - kind: endpoint
                source: |
                  id: quote
                  slug: quote
                  enabled: true
                  method: GET
                  runtime: javascript
                  script: |
                    import { oldPrice } from "globals/pricing";
                    export default function handler(req, ns) { return { price: oldPrice() }; }
            """;
        await ApplyNowAsync(project.ProjectId, original);
        var replacement = original.Replace("oldPrice", "newPrice");
        var plan = await PreviewAsync(project.ProjectId, replacement);
        Assert.False(plan.Blocked, string.Join("; ", plan.Issues.Select(issue => issue.Message)));
        Assert.Equal(DeploymentOutcome.Applied, (await ApplyAsync(project.ProjectId, replacement, plan.Hash)).Outcome);
        var resources = await InScopeAsync(sp => OwnerProjectResources.LoadAsync(sp.GetRequiredService<INetworkStorageStore>(), project.ProjectId, CancellationToken.None));
        var global = resources.GlobalExports(factory.Services.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>());
        Assert.Contains(global["pricing"].Exports, export => export.Name == "newPrice");
        Assert.DoesNotContain(global["pricing"].Exports, export => export.Name == "oldPrice");
    }

    [SkippableFact]
    public async Task OmittedModuleValuesRemainInThePlanAndCommittedPayload()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Module values");
        var id = project.ProjectId;
        await InScopeAsync(async sp =>
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                items = new[] { new { id = "module_rates", type = "group", values = new { bonus = 3 } } },
            });
            await sp.GetRequiredService<INetworkStorageStore>().UpsertGameValuesAsync(id, payload, null, 1, CancellationToken.None);
            var module = new InstalledModule("rates", "1.0.0", true, new Dictionary<string, string>(),
                [], [], [], [], [], ["module_rates"], "2026-10-24T00:00:00Z");
            await sp.GetRequiredService<ModuleStateStore>().SaveAsync(OwnerProjectScope.Owner, id,
                new ModuleProjectState([module], []), CancellationToken.None);
            return true;
        });
        const string proposal = """
            target: live
            upserts:
              - kind: game-values
                source: |
                  items: []
            """;
        var plan = await PreviewAsync(id, proposal);
        Assert.False(plan.Blocked);
        Assert.Contains(plan.Items, item => item.Action == "module-retained" && item.Kind == "value" && item.Id == "module_rates");
        Assert.Equal(DeploymentOutcome.Applied, (await ApplyAsync(id, proposal, plan.Hash)).Outcome);
        var resources = await InScopeAsync(sp => OwnerProjectResources.LoadAsync(sp.GetRequiredService<INetworkStorageStore>(), id, CancellationToken.None));
        Assert.Contains(resources.ValueItems(), item => item.Id == "module_rates");
    }

    [SkippableFact]
    public async Task StagedCollectionsStayOutOfLiveAndResolveLaterStagedReferences()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Staged dependency");
        var id = project.ProjectId;
        await SetRevisionAsync(id);
        await ApplyNowAsync(id, NextTarget);
        Assert.False(await ExistsAsync(id, "collection", "players"));
        Assert.True(await ExistsAsync(id, "workflow", "setup"));
        const string endpoint = """
            target: next
            upserts:
              - kind: endpoint
                source: |
                  id: next-read
                  slug: next-read
                  method: GET
                  enabled: true
                  steps:
                    - id: load
                      type: read
                      collection: players
                      key: "{{steamId}}"
                  response:
                    ok: true
            """;
        var plan = await PreviewAsync(id, endpoint);
        Assert.False(plan.Blocked, string.Join("; ", plan.Issues.Select(issue => issue.Message)));
        Assert.Contains(Assert.Single(plan.Items).References,
            reference => reference.Kind == "collection" && reference.Id == "players" && reference.Status == "resolved");
        Assert.Equal(DeploymentOutcome.Applied, (await ApplyAsync(id, endpoint, plan.Hash)).Outcome);
        Assert.False(await ExistsAsync(id, "endpoint", "next-read"));
        var staged = await InScopeAsync(sp => RevisionOverrides.ReadAsync(sp.GetRequiredService<IWorkspaceStore>(), OwnerProjectScope.Owner, id, CancellationToken.None));
        Assert.Contains(RevisionOverrides.Items(staged, RevisionOverrides.EndpointsSection), entry => entry.Key == "next-read");
        Assert.Contains(RevisionOverrides.Items(staged, RevisionOverrides.CollectionsSection), entry => entry.Key == "players");
    }

    [SkippableFact]
    public async Task LiveQueriesCannotUseACollectionCreatedOnlyInNext()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Mixed targets");
        var id = project.ProjectId;
        await SetRevisionAsync(id);
        var proposal = PlayersAndScores.Replace("target: live", "target: next");
        var plan = await PreviewAsync(id, proposal);
        Assert.True(plan.Blocked);
        Assert.Contains(plan.Items.Single(item => item.Kind == "query").References,
            reference => reference.Kind == "collection" && reference.Id == "players" && reference.Status == "missing");
        Assert.Equal(DeploymentOutcome.Blocked, (await ApplyAsync(id, proposal, plan.Hash)).Outcome);
        Assert.False(await ExistsAsync(id, "query", "scores"));
    }

    [SkippableFact]
    public async Task RevisionPolicyChangesInvalidateApproval()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Policy stale");
        var id = project.ProjectId;
        var plan = await PreviewAsync(id, PlayersUpsert);
        await InScopeAsync(async sp =>
        {
            var workspace = sp.GetRequiredService<IWorkspaceStore>();
            var projects = await workspace.GetUserProjectsAsync(OwnerProjectScope.Owner, CancellationToken.None);
            await workspace.SaveUserProjectsAsync(OwnerProjectScope.Owner,
                projects.Select(project => project.Id == id ? project with { RevisionServerEnforcementEnabled = true } : project).ToList(),
                CancellationToken.None);
            return true;
        });
        Assert.Equal(DeploymentOutcome.Stale, (await ApplyAsync(id, PlayersUpsert, plan.Hash)).Outcome);
        Assert.False(await ExistsAsync(id, "collection", "players"));
    }

    [SkippableFact]
    public async Task UpdatingACollectionShowsItsStillResolvedDependents()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Affected dependent");
        await ApplyNowAsync(project.ProjectId, PlayersAndScores);
        var plan = await PreviewAsync(project.ProjectId, PlayersChanged);
        Assert.False(plan.Blocked);
        Assert.Contains(plan.Dependents, dependent => dependent.Kind == "query" && dependent.Id == "scores"
            && dependent.ResourceKind == "collection" && dependent.ResourceId == "players" && dependent.Status == "resolved");
    }

    [SkippableFact]
    public async Task LiteralValueNamesDoNotBecomeFalseMissingReferenceBlockers()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Literal values");
        var literal = DynamicEndpoint.Replace("values[rates]", "Example values.absent");
        Assert.False((await PreviewAsync(project.ProjectId, literal)).Blocked);
        var template = DynamicEndpoint.Replace("values[rates]", "{{values.absent}}");
        var plan = await PreviewAsync(project.ProjectId, template);
        Assert.True(plan.Blocked);
        Assert.Contains(Assert.Single(plan.Items).References, reference => reference.Kind == "value"
            && reference.Id == "absent" && reference.Status == "missing");
    }

    private Task<bool> SetRevisionAsync(string projectId) => InScopeAsync(async sp =>
    {
        await sp.GetRequiredService<IWorkspaceStore>().PutProjectResourceAsync(OwnerProjectScope.Owner, projectId,
            "game-package.json", new { currentRevisionId = 1 }, CancellationToken.None);
        return true;
    });

    private static string PlanHash(string html)
    {
        var match = Regex.Match(html, "name=\"planHash\"[^>]*value=\"([0-9A-F]{64})\"");
        Assert.True(match.Success, "Expected an approved plan hash in the console page.");
        return match.Groups[1].Value;
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<DeploymentPlan> PreviewAsync(string projectId, string proposal) => InScopeAsync(async sp =>
    {
        var store = sp.GetRequiredService<INetworkStorageStore>();
        var parsed = DeploymentProposalReader.Parse(proposal, store.MaxPayloadBytes);
        return await OwnerDeployment.PreviewAsync(store, sp.GetRequiredService<IWorkspaceStore>(),
            sp.GetRequiredService<ManagementMutationHandler>(), projectId, parsed,
            sp.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>(), CancellationToken.None);
    });

    private Task<DeploymentApplyResult> ApplyAsync(string projectId, string proposal, string hash) => InScopeAsync(async sp =>
    {
        var store = sp.GetRequiredService<INetworkStorageStore>();
        var parsed = DeploymentProposalReader.Parse(proposal, store.MaxPayloadBytes);
        return await OwnerDeployment.ApplyAsync(store, sp.GetRequiredService<IWorkspaceStore>(),
            sp.GetRequiredService<ManagementMutationHandler>(), sp.GetRequiredService<IAuditLogger>(), projectId, parsed, hash,
            sp.GetRequiredService<IReadOnlyList<IEndpointScriptRuntime>>(), CancellationToken.None);
    });

    private async Task<DeploymentApplyResult> ApplyNowAsync(string projectId, string proposal)
    {
        var plan = await PreviewAsync(projectId, proposal);
        var result = await ApplyAsync(projectId, proposal, plan.Hash);
        Assert.Equal(DeploymentOutcome.Applied, result.Outcome);
        return result;
    }

    private Task<bool> ExistsAsync(string projectId, string kind, string id) => InScopeAsync(async sp =>
    {
        var store = sp.GetRequiredService<INetworkStorageStore>();
        var ct = CancellationToken.None;
        return kind switch
        {
            "collection" => await store.ReadCollectionAsync(projectId, id, ct) is not null,
            "endpoint" => await store.ReadEndpointAsync(projectId, id, ct) is not null,
            "workflow" => await store.ReadWorkflowAsync(projectId, id, ct) is not null,
            _ => await store.ReadQueryAsync(projectId, id, ct) is not null,
        };
    });

    private Task<List<string>> CollectionRowsAsync(string projectId) => InScopeAsync(async sp =>
    {
        var resources = await OwnerProjectResources.LoadAsync(sp.GetRequiredService<INetworkStorageStore>(), projectId, CancellationToken.None);
        return resources.Rows("collection").Select(row => row.GetRawText()).ToList();
    });

    private async Task OwnCollectionAsync(string projectId, string collectionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var module = new InstalledModule("stats-module", "1.0.0", true, new Dictionary<string, string>(), [], [collectionId], [], [], [], [], "2025-01-01T00:00:00Z");
        await scope.ServiceProvider.GetRequiredService<ModuleStateStore>().SaveAsync(OwnerProjectScope.Owner, projectId, new ModuleProjectState([module], []), CancellationToken.None);
    }

    private async Task InstallEndpointFailureAsync()
    {
        if (factory is PostgresHostFactory)
        {
            var schema = factory.Config.GetString("database.postgres.schema");
            await using var connection = new NpgsqlConnection(factory.Config.GetString("database.postgres.connection_string"));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE OR REPLACE FUNCTION "{schema}".fail_deployment_endpoint() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.endpoint_id = 'deploy-boom' THEN RAISE EXCEPTION 'injected endpoint failure'; END IF;
                    RETURN NEW;
                END;
                $$;
                DROP TRIGGER IF EXISTS fail_deployment_endpoint ON "{schema}".endpoints;
                CREATE TRIGGER fail_deployment_endpoint BEFORE INSERT ON "{schema}".endpoints
                    FOR EACH ROW EXECUTE FUNCTION "{schema}".fail_deployment_endpoint();
                """;
            await command.ExecuteNonQueryAsync();
            return;
        }

        var path = factory.Config.GetString("database.sqlite.path");
        await using var sqlite = new SqliteConnection($"Data Source={path};Pooling=False");
        await sqlite.OpenAsync();
        await using var sqliteCommand = sqlite.CreateCommand();
        sqliteCommand.CommandText = """
            CREATE TRIGGER fail_deployment_endpoint BEFORE INSERT ON endpoints WHEN NEW.endpoint_id = 'deploy-boom'
            BEGIN SELECT RAISE(ABORT, 'injected endpoint failure'); END
            """;
        await sqliteCommand.ExecuteNonQueryAsync();
    }
}

public sealed class SqliteOwnerDeploymentPlanTests : OwnerDeploymentPlanTests<SqliteHostFactory> { }

public sealed class PostgresOwnerDeploymentPlanTests : OwnerDeploymentPlanTests<PostgresHostFactory> { }
