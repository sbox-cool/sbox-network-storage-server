using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Keeps the Ns interfaces in ns.d.ts in step with the script API members the host registers.</summary>
public sealed class EndpointTypingsDeclarationTests
{
    // Each interface maps to the path prefix its members take in scripts: ns.records.get, ns.now, and so on.
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        ["Ns"] = "",
        ["NsRecords"] = "records.",
        ["NsValues"] = "values.",
        ["NsWorkflows"] = "workflows.",
    };

    [Fact]
    public void NsDeclarationsListExactlyThePublicHostMembers()
    {
        var declared = new List<string>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match block in Regex.Matches(EndpointTypings.StaticDeclarations(), @"^interface (\w+) \{\r?\n(.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            var name = block.Groups[1].Value;
            if (!Prefixes.TryGetValue(name, out var prefix))
                continue;
            found.Add(name);
            foreach (Match member in Regex.Matches(block.Groups[2].Value, @"^  (\w+)\(", RegexOptions.Multiline))
                declared.Add(prefix + member.Groups[1].Value);
        }

        Assert.Equal(Prefixes.Keys.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        // Equal lists show the missing and the extra members in the failure message.
        Assert.Equal(
            EndpointScriptHost.PublicApiNames.Order(StringComparer.Ordinal),
            declared.Order(StringComparer.Ordinal));
    }
}

public abstract class EndpointTypingsHttpTests<TFactory> : IDisposable where TFactory : SelfHostFactory, new()
{
    private readonly TFactory factory = new();
    protected EndpointTypingsHttpTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);
    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task StaticDeclarationsAreServedAsAnOwnerAsset()
    {
        using var anonymous = Client(factory);
        using var response = await anonymous.GetAsync("/owner-assets/endpoint-api/ns.d.ts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("interface NsRecords {", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task TypingsRequireOwnerAndReturnNotFoundForUnknownProjects()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Typings auth");
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync($"/dashboard/projects/{project.ProjectId}/resources/endpoint-typings")).StatusCode);
        using var owner = await LoggedInClientAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/dashboard/projects/missing/resources/endpoint-typings")).StatusCode);
    }

    [SkippableFact]
    public async Task ProjectTypingsDeclareItsCollectionsValuesWorkflowsAndGlobalExports()
    {
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Typings fixture");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}";

        await SaveYamlAsync(client, root + "/resources/collection", "player_stats",
            "id: player_stats\nname: Player stats\ncollectionType: player\nschema:\n  coins:\n    type: number\n");
        await SaveYamlAsync(client, root + "/resources/workflow", "grant_coins",
            "id: grant_coins\nname: Grant coins\nsteps: []\n");
        await SaveYamlAsync(client, root + "/resources/endpoint", "inventory", """
            id: inventory
            slug: inventory
            method: GET
            scope: global
            enabled: true
            runtime: javascript
            script: |
              export class Inventory { constructor(items) { this.items = items; } }
              export function check_inventory_has_item(items, item) { return items.indexOf(item) >= 0; }
              export const CAP = 10;
            """);
        var gameValuesPage = await client.GetStringAsync(root + "/resources/game-values");
        using var values = await client.PostAsync(root + "/resources/game-values",
            Form(("definition", "{\"items\":[{\"id\":\"rewards\",\"type\":\"group\",\"entries\":{\"dailyReward\":25,\"season\":\"autumn\"}}]}"),
                ("__RequestVerificationToken", Csrf(gameValuesPage))));
        Assert.Equal(HttpStatusCode.Redirect, values.StatusCode);

        using var response = await client.GetAsync(root + "/resources/endpoint-typings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        var typings = await response.Content.ReadAsStringAsync();

        Assert.Contains("interface NsRecords {", typings);
        Assert.Contains("  \"player_stats\": unknown;", typings);
        Assert.Contains("\"rewards\": { \"dailyReward\": unknown; \"season\": unknown; };", typings);
        Assert.Contains("\"grantCoins\"(input?: Record<string, unknown>): unknown;", typings);
        Assert.Contains("\"check_inventory_has_item\"(items: unknown, item: unknown): unknown;", typings);
        Assert.Contains("\"Inventory\": new (...args: unknown[]) => unknown;", typings);
        Assert.Contains("\"CAP\": unknown;", typings);
        Assert.Contains("\"inventory\": {", typings);
    }

    private static async Task SaveYamlAsync(HttpClient client, string url, string id, string yaml)
    {
        var page = await client.GetStringAsync(url + "/new?draft=" + id);
        using var saved = await client.PostAsync(url, Form(("definition", yaml), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
    }
}

public sealed class SqliteEndpointTypingsTests : EndpointTypingsHttpTests<SqliteHostFactory> { }
public sealed class PostgresEndpointTypingsTests : EndpointTypingsHttpTests<PostgresHostFactory> { }
