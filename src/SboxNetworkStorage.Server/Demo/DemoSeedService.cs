using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Demo;

/// <summary>Creates clearly generated sample rows in an empty, dedicated SQLite database.</summary>
public sealed class DemoSeedService(IServiceScopeFactory scopes, EffectiveConfig config) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!config.GetBoolean("adminpanel.demo_read_only")) return;
        if (config.GetString("database.provider") != "sqlite")
            throw new InvalidOperationException("The public demo requires its own SQLite database, not a shared PostgreSQL installation.");
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var admin = scope.ServiceProvider.GetRequiredService<INetworkStorageStoreAdmin>();
        await admin.EnsureSchemaCompatibleAsync(ct);
        var marker = Path.Combine(config.DataDirectory, "demo-project.json");
        string project;
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(marker, ct));
            project = json.RootElement.GetProperty("projectId").GetString()!;
            if (await store.ReadProjectAsync(project, ct) is null || (await admin.CountUsageAsync(0, ct)).Projects != 1)
                throw new InvalidOperationException("The demo marker does not match an isolated demo database.");
        }
        else
        {
            if ((await admin.CountUsageAsync(0, ct)).Projects != 0)
                throw new InvalidOperationException("Refusing to expose an existing database as a public demo. Use a new data directory.");
            var result = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().CreateProjectAsync(
                1, "Island survival demo", "Generated sample data. No real players or production credentials.", true, true, "player", "", ct);
            project = result.ProjectId;
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { projectId = project }), ct);
        }
        var timestamp = DateTimeOffset.UtcNow;
        var now = timestamp.ToUnixTimeMilliseconds();
        var month = timestamp.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var day = timestamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await Collection("players", "Player progress", "player", new { level = new { type = "number" }, coins = new { type = "number" } });
        await Collection("inventory", "Player inventory", "player", new { items = new { type = "array" } });
        await Collection("world", "World state", "global", new { season = new { type = "number" } });
        string[] names = ["Demo Scout", "Demo Builder", "Demo Miner", "Demo Ranger", "Demo Farmer", "Demo Sailor"];
        for (var i = 0; i < names.Length; i++)
        {
            var player = $"demo-player-{i + 1:000}";
            await store.UpsertRecordAsync(project, "players", player, JsonSerializer.SerializeToElement(new {
                displayName = names[i], level = 7 + i * 3, coins = 120 + i * 87,
                position = new { x = i * 12.5, y = 0, z = i * -4.25 },
                quests = new[] { new { id = "first-shelter", completed = true }, new { id = "find-the-harbor", completed = i > 2 } },
                sampleData = true
            }), false, 1, ct);
            await store.UpsertRecordAsync(project, "inventory", player, JsonSerializer.SerializeToElement(new {
                slots = 20, items = new[] { new { id = "wood", amount = 12 + i }, new { id = "stone", amount = 5 + i * 2 } }, sampleData = true
            }), false, 1, ct);
            await store.UpsertPlayerProfileAsync(project, player, names[i], false, null, now - i * 900000, now - i * 900000,
                null, null, 3600 + i * 1200, 3 + i, "quest-completed", "save-progress", "{}", now, ct);
            await store.InsertPlayerAnalyticsEventV2Async(project, player, now - i * 900000, $"demo-event-{i}", "quest-completed",
                "progress", "First shelter", "save-progress", "players", JsonSerializer.SerializeToElement(new { quest = "first-shelter", sampleData = true }), ct);
        }
        await store.UpsertGlobalRecordAsync(project, "world", "current-season", JsonSerializer.SerializeToElement(new {
            season = 3, weather = "clear", settlements = new[] { new { name = "Harbor", population = 18 }, new { name = "Hill camp", population = 7 } }, sampleData = true
        }), 1, ct);
        await store.UpsertEndpointAsync(project, "demo-save-progress", "save-progress", "POST", true,
            JsonSerializer.SerializeToElement(new { id = "demo-save-progress", name = "Save progress", slug = "save-progress", method = "POST", enabled = true,
                steps = new[] { new { type = "respond", body = new { saved = true } } }, notes = "Illustrative definition. Public demo execution is disabled." }), null, 1, ct);
        await store.UpsertGameValuesAsync(project, JsonSerializer.SerializeToElement(new { xpMultiplier = 1.25, startingCoins = 100, maximumPartySize = 4 }), null, 1, ct);
        await store.UpsertWorkflowAsync(project, "demo-season-reward", "Season reward",
            JsonSerializer.SerializeToElement(new { id = "demo-season-reward", name = "Season reward",
                steps = new[] { new { type = "respond", body = new { reward = 100 } } }, notes = "Sample workflow; execution is disabled in the public demo." }), null, 1, ct);
        await store.UpsertQueryAsync(project, "demo-experienced-players", "Experienced players", false,
            JsonSerializer.SerializeToElement(new { id = "demo-experienced-players", name = "Experienced players", collectionId = "players",
                filters = new[] { new { field = "level", @operator = "gte", value = 10 } }, limit = 50 }), 1, ct);
        await store.InsertStorageErrorAsync(project, now, "demo-error-validation", "Sample validation error: level must be a number.",
            null, "demo-fixture", "/v3/endpoints/demo/save-progress", "warning", ct);
        await store.InsertStorageRequestLogAsync(project, now, "POST", "/v3/endpoints/demo/save-progress", 200, 14, "demo-runtime", ct);
        if (await store.ReadProjectUsageMonthlyAsync(project, month, ct) is null)
            await store.IncrementProjectUsageAsync(project, month, day, "save-progress",
                new UsageDelta(840, 540, 300, 300, 102400, 204800, 2, 11760, 840, 8192), ct);
        await store.InsertAuditLogAsync(project, now, "demo-fixture-created", "demo-operator", "demo.seed", "{\"kind\":\"sample\"}",
            "{}", "{\"message\":\"Generated demo dataset created\"}", "{}", ct);
        return;

        Task Collection(string id, string name, string type, object schema) => store.UpsertCollectionAsync(project, id, name, "public",
            JsonSerializer.SerializeToElement(new { id, name, collectionType = type, schema, maxRecords = 20, allowRecordDelete = true }), 1, ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
