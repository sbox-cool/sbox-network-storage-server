using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Module controls on the live request path: a disabled module's endpoints
/// deny even when the caller reports an old revision, and player blocks
/// enforce write/all scopes with secret-key exemption.
/// </summary>
public sealed class ModuleSecurityHttpTests(SqliteHostFactory factory) : IClassFixture<SqliteHostFactory>
{
    private const string Player = "76561198000000001";

    [SkippableFact]
    public async Task Disabled_module_endpoint_denies_despite_old_revision_header()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Module Gates");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
            Assert.True((await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId,
                project.ProjectId, "economy", null, CancellationToken.None)).Ok);
            Assert.True(await installer.SetEnabledAsync(NetworkStorageServices.LocalOwnerUserId,
                project.ProjectId, "economy", enabled: false, CancellationToken.None));
        }
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{project.ProjectId}/economy_get_balance");
        request.Headers.Add("x-api-key", project.PublicKey);
        request.Headers.Add("x-steam-id", Player);
        request.Headers.Add("x-ns-revision-id", "41");
        request.Content = JsonContent.Create(new { });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ENDPOINT_DISABLED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Blocked_player_gets_403_on_writes_reads_succeed_secret_key_exempt()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Module Blocks");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
            Assert.True((await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId,
                project.ProjectId, "economy", null, CancellationToken.None)).Ok);
        }
        await factory.Services.GetRequiredService<PlayerBlockService>().BlockAsync(
            project.ProjectId, Player, PlayerBlockService.WritesScope, "demo cheat", expiresAtUnixMs: null,
            "owner-dashboard", CancellationToken.None);

        using var client = factory.CreateClient();
        using var earn = await PostAsync(client, project.ProjectId, project.PublicKey, "economy_earn", new { amount = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, earn.StatusCode);
        using var earnBody = JsonDocument.Parse(await earn.Content.ReadAsStringAsync());
        Assert.Equal("PLAYER_BLOCKED", earnBody.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var balance = await PostAsync(client, project.ProjectId, project.PublicKey, "economy_get_balance", new { });
        Assert.Equal(HttpStatusCode.OK, balance.StatusCode);

        using var secret = await PostAsync(client, project.ProjectId, project.SecretKey, "economy_earn", new { amount = 10 }, secret: true);
        Assert.Equal(HttpStatusCode.OK, secret.StatusCode);
    }

    [SkippableFact]
    public async Task Absent_and_off_response_mode_write_responses_are_identical()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var absent = await factory.CreateProjectAsync("Response mode absent");
        var off = await factory.CreateProjectAsync("Response mode off");
        await InstallModuleAsync(factory, absent.ProjectId, "economy", null);
        await InstallModuleAsync(factory, absent.ProjectId, "integrity", CallRateSettings(null));
        await InstallModuleAsync(factory, off.ProjectId, "economy", null);
        await InstallModuleAsync(factory, off.ProjectId, "integrity", CallRateSettings("off"));
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        await SeedCountedFlagsAsync(store, absent.ProjectId);
        await SeedCountedFlagsAsync(store, off.ProjectId);

        using var client = factory.CreateClient();
        var absentResponses = await EarnTwiceAsync(client, absent.ProjectId, absent.PublicKey);
        var offResponses = await EarnTwiceAsync(client, off.ProjectId, off.PublicKey);

        Assert.Equal(offResponses, absentResponses);
    }

    [SkippableFact]
    public async Task Dry_run_records_would_apply_and_leaves_the_write_response_unchanged()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var off = await factory.CreateProjectAsync("Response dry run off");
        var dry = await factory.CreateProjectAsync("Response dry run dry");
        await InstallModuleAsync(factory, off.ProjectId, "economy", null);
        await InstallModuleAsync(factory, off.ProjectId, "integrity", CallRateSettings("off"));
        await InstallModuleAsync(factory, dry.ProjectId, "economy", null);
        await InstallModuleAsync(factory, dry.ProjectId, "integrity", CallRateSettings("dry-run"));
        var store = factory.Services.GetRequiredService<IModuleDataStore>();
        await SeedCountedFlagsAsync(store, off.ProjectId);
        await SeedCountedFlagsAsync(store, dry.ProjectId);

        using var client = factory.CreateClient();
        var offResponses = await EarnTwiceAsync(client, off.ProjectId, off.PublicKey);
        var dryResponses = await EarnTwiceAsync(client, dry.ProjectId, dry.PublicKey);

        Assert.Equal(offResponses, dryResponses);
        Assert.All(dryResponses, response => Assert.Equal(HttpStatusCode.OK, response.Status));

        // Four seeded counted flags plus the one flag fired by the second call.
        var flags = await WaitForCallRateFlagsAsync(store, dry.ProjectId, expected: 5);
        Assert.Equal(5, flags.Count);
        var wouldApply = Evidence(flags[0]).GetProperty("wouldApply");
        Assert.Equal("block-all", wouldApply.GetProperty("stage").GetString());
    }

    private static Dictionary<string, string> CallRateSettings(string? mode)
    {
        var settings = new Dictionary<string, string> { ["max_calls_per_minute"] = "1" };
        if (mode is not null) settings["response_mode"] = mode;
        return settings;
    }

    private static async Task InstallModuleAsync(SelfHostFactory host, string projectId, string moduleId,
        IReadOnlyDictionary<string, string>? settings)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
        Assert.True((await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId, projectId, moduleId,
            settings, CancellationToken.None)).Ok);
    }

    private static async Task SeedCountedFlagsAsync(IModuleDataStore store, string projectId)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var minutesAgo = 10; minutesAgo <= 40; minutesAgo += 10)
            await store.InsertFlagAsync(projectId, Guid.NewGuid().ToString("N"), nowMs - minutesAgo * 60_000L,
                Player, IntegrityDetectors.CallRate, "medium", "open", null, null, CancellationToken.None);
    }

    /// <summary>Two economy_earn writes from the same player; each response is (status, body with the project id masked).</summary>
    private static async Task<List<(HttpStatusCode Status, string Body)>> EarnTwiceAsync(HttpClient client,
        string projectId, string apiKey)
    {
        var responses = new List<(HttpStatusCode Status, string Body)>();
        for (var call = 0; call < 2; call++)
        {
            using var response = await PostAsync(client, projectId, apiKey, "economy_earn", new { amount = 10 });
            var body = await response.Content.ReadAsStringAsync();
            responses.Add((response.StatusCode, body.Replace(projectId, "<project>")));
        }
        return responses;
    }

    /// <summary>Polls the newest-first call-rate history until it holds the expected count or the bound passes.</summary>
    private static async Task<IReadOnlyList<JsonElement>> WaitForCallRateFlagsAsync(IModuleDataStore store,
        string projectId, int expected)
    {
        IReadOnlyList<JsonElement> flags = [];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            flags = await store.ListFlagsAsync(projectId, null, IntegrityDetectors.CallRate, null, Player, 20,
                CancellationToken.None);
            if (flags.Count >= expected) break;
            await Task.Delay(50);
        }
        return flags;
    }

    private static JsonElement Evidence(JsonElement flag)
    {
        var raw = flag.GetProperty("evidence_json");
        return raw.ValueKind == JsonValueKind.String ? JsonDocument.Parse(raw.GetString()!).RootElement : raw;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string projectId, string apiKey,
        string slug, object body, bool secret = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{projectId}/{slug}");
        request.Headers.Add("x-api-key", apiKey);
        if (!secret) request.Headers.Add("x-steam-id", Player);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
