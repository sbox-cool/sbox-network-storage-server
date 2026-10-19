using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Owner;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

/// <summary>
/// Every shipped endpoint template, in both runtimes, passes save-time validation and runs against a fixture
/// project. A template without a scenario below fails the test, so adding a template means adding its scenario.
/// </summary>
public sealed class EndpointTemplatesTests
{
    private const string Project = "proj";
    private const string Suffix = ".endpoint.yml";

    private static readonly string[] Templates =
    [
        "grant-coins", "submit-score", "daily-reward", "profile", "inventory", "buy-item",
    ];

    private static readonly Dictionary<string, Func<Fixture, Task>> Scenarios = new(StringComparer.Ordinal)
    {
        ["grant-coins"] = async fixture =>
        {
            var granted = await fixture.Call("grant-coins", Obj(("steamId", "steam1"), ("amount", 25d)), secretKey: true);
            Assert.True(granted.Ok, Body(granted));
            Assert.Contains("\"coins\":125", Body(granted));
            Assert.Contains("\"coins\":125", Json(fixture.Records["proj:players:steam1"]));

            var withoutKey = await fixture.Call("grant-coins", Obj(("steamId", "steam1"), ("amount", 25d)));
            Assert.False(withoutKey.Ok);
            Assert.Contains("SECRET_KEY_REQUIRED", Body(withoutKey));

            var badAmount = await fixture.Call("grant-coins", Obj(("steamId", "steam1"), ("amount", 0d)), secretKey: true);
            Assert.False(badAmount.Ok);
            Assert.Contains("INVALID_AMOUNT", Body(badAmount));
        },

        ["submit-score"] = async fixture =>
        {
            var anonymous = await fixture.Call("submit-score", Obj(("score", 300d)), steamId: "anonymous");
            Assert.False(anonymous.Ok);
            Assert.Contains("AUTH_REQUIRED", Body(anonymous));

            var first = await fixture.Call("submit-score", Obj(("score", 300d)));
            Assert.True(first.Ok, Body(first));
            Assert.Contains("\"bestScore\":300", Body(first));
            Assert.Contains("\"improved\":true", Body(first));

            var lower = await fixture.Call("submit-score", Obj(("score", 200d)));
            Assert.True(lower.Ok, Body(lower));
            Assert.Contains("\"bestScore\":300", Body(lower));
            Assert.Contains("\"improved\":false", Body(lower));
            Assert.Contains("\"bestScore\":300", Json(fixture.Records["proj:players:steam1"]));

            var invalid = await fixture.Call("submit-score", Obj(("score", -1d)));
            Assert.False(invalid.Ok);
            Assert.Contains("INVALID_SCORE", Body(invalid));
        },

        ["daily-reward"] = async fixture =>
        {
            var anonymous = await fixture.Call("daily-reward", Obj(), steamId: "anonymous");
            Assert.False(anonymous.Ok);
            Assert.Contains("AUTH_REQUIRED", Body(anonymous));

            var first = await fixture.Call("daily-reward", Obj());
            Assert.True(first.Ok, Body(first));
            Assert.Contains("\"reward\":10", Body(first));
            Assert.Contains("\"coins\":110", Body(first));

            var again = await fixture.Call("daily-reward", Obj());
            Assert.False(again.Ok);
            Assert.Equal(429, again.Status);
            Assert.Contains("DAILY_REWARD_COOLDOWN", Body(again));

            var configured = fixture.Fresh();
            var gameValues = new Dictionary<string, object?> { ["rewards"] = new Dictionary<string, object?> { ["dailyReward"] = 25d } };
            var fromGameValue = await configured.Call("daily-reward", Obj(), gameValues: gameValues);
            Assert.True(fromGameValue.Ok, Body(fromGameValue));
            Assert.Contains("\"reward\":25", Body(fromGameValue));

            // A non-whole game value is owner misconfiguration: a 4xx with the script's own code, not a generic SCRIPT_ERROR.
            var misconfigured = await configured.Call("daily-reward", Obj(), gameValues: new Dictionary<string, object?>
            {
                ["rewards"] = new Dictionary<string, object?> { ["dailyReward"] = 2.5d },
            });
            Assert.False(misconfigured.Ok);
            Assert.InRange(misconfigured.Status, 400, 499);
            Assert.Contains("REWARD_NOT_CONFIGURED", Body(misconfigured));
        },

        ["profile"] = async fixture =>
        {
            var anonymous = await fixture.Call("profile", Obj(), steamId: "anonymous");
            Assert.False(anonymous.Ok);
            Assert.Contains("AUTH_REQUIRED", Body(anonymous));

            var before = Json(fixture.Records["proj:players:steam1"]);
            var profile = await fixture.Call("profile", Obj());
            Assert.True(profile.Ok, Body(profile));
            Assert.Contains("\"steamId\":\"steam1\"", Body(profile));
            Assert.Contains("\"coins\":100", Body(profile));
            Assert.Equal(before, Json(fixture.Records["proj:players:steam1"]));
        },

        ["inventory"] = async fixture =>
        {
            // A global module is never an HTTP route: a public call answers exactly as an unknown slug does.
            var result = await fixture.Call("inventory", Obj());
            Assert.False(result.Ok);
            Assert.Equal(404, result.Status);
            Assert.Contains("ENDPOINT_NOT_FOUND", Body(result));
        },

        ["buy-item"] = async fixture =>
        {
            var anonymous = await fixture.Call("buy-item", Obj(("itemId", "sword")), steamId: "anonymous");
            Assert.False(anonymous.Ok);
            Assert.Contains("AUTH_REQUIRED", Body(anonymous));

            var bought = await fixture.Call("buy-item", Obj(("itemId", "sword")));
            Assert.True(bought.Ok, Body(bought));
            Assert.Contains("\"coins\":50", Body(bought));
            Assert.Contains("\"items\":[\"sword\"]", Body(bought));
            Assert.Contains("\"items\":[\"sword\"]", Json(fixture.Records["proj:players:steam1"]));

            var owned = await fixture.Call("buy-item", Obj(("itemId", "sword")));
            Assert.False(owned.Ok);
            Assert.Equal(422, owned.Status);
            Assert.Contains("ITEM_OWNED", Body(owned));

            // 100 starting coins, 50 per item: the second item empties the purse, the third is refused.
            var second = await fixture.Call("buy-item", Obj(("itemId", "axe")));
            Assert.True(second.Ok, Body(second));
            Assert.Contains("\"coins\":0", Body(second));
            Assert.Contains("\"items\":[\"sword\",\"axe\"]", Json(fixture.Records["proj:players:steam1"]));

            var poor = await fixture.Call("buy-item", Obj(("itemId", "shield")));
            Assert.False(poor.Ok);
            Assert.Equal(402, poor.Status);
            Assert.Contains("INSUFFICIENT_COINS", Body(poor));
        },
    };

    [Fact]
    public void TemplateFiles_AreEachTemplateInBothLanguages()
    {
        var expected = Templates
            .SelectMany(id => new[] { $"{id}.javascript{Suffix}", $"{id}.typescript{Suffix}" })
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, EndpointTypings.TemplateFileNames().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public async Task Templates_ValidateAndRun_InEachRuntime(string language)
    {
        var wrappers = LoadWrappers(language);
        Assert.Equal(Templates.Order(StringComparer.Ordinal), wrappers.Keys.Order(StringComparer.Ordinal));

        var compiled = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (slug, wrapper) in wrappers)
        {
            Assert.True(NetworkStorageSourceResourceCompiler.TryCompile(wrapper, "endpoint", out var definition, out var error), $"{slug}: {error}");
            compiled[slug] = definition;
        }

        var runtime = EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true).Single(candidate => candidate.Language == language);
        var globals = compiled
            .Where(entry => IsGlobal(entry.Value))
            .ToDictionary(
                entry => entry.Key,
                entry => ScriptModuleAnalysis.Analyze(runtime.ToJavaScript(entry.Value.GetProperty("script").GetString()!)),
                StringComparer.Ordinal);

        foreach (var (slug, wrapper) in wrappers)
        {
            var context = new DefinitionValidationContext
            {
                HasProjectResources = true,
                Collections = new HashSet<string>(["players"], StringComparer.OrdinalIgnoreCase),
                ResourceId = slug,
                ScriptRuntimes = EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true),
                GlobalModules = globals.Where(global => global.Key != slug).ToDictionary(global => global.Key, global => global.Value, StringComparer.Ordinal),
            };
            var errors = NetworkStorageDefinitionValidator.ValidateResource(wrapper, "endpoint", context, out _)
                .Where(diagnostic => diagnostic.IsError)
                .Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
                .ToList();
            Assert.True(errors.Count == 0, $"{slug} ({language}) failed save-time validation: {string.Join("; ", errors)}");
        }

        foreach (var slug in Templates)
        {
            var fixture = new Fixture(compiled);
            await Scenarios[slug](fixture);
        }
    }

    private static Dictionary<string, JsonElement> LoadWrappers(string language)
    {
        var wrappers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var fileSuffix = $".{language}{Suffix}";
        foreach (var file in EndpointTypings.TemplateFileNames().Where(name => name.EndsWith(fileSuffix, StringComparison.Ordinal)))
        {
            using var stream = EndpointTypings.Template(file)!;
            using var reader = new StreamReader(stream);
            var slug = file[..^fileSuffix.Length];
            wrappers[slug] = OwnerResourceSource.Wrap(slug, reader.ReadToEnd(), "endpoint");
        }
        return wrappers;
    }

    private static bool IsGlobal(JsonElement definition) =>
        definition.TryGetProperty("scope", out var scope) && scope.GetString() == "global";

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

    private static string Body(EndpointExecutionResult result) => JsonSerializer.Serialize(result.Body);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>One project with every template of a language, a players record for steam1 and a fresh store per scenario.</summary>
    private sealed class Fixture
    {
        private readonly IReadOnlyDictionary<string, JsonElement> _compiled;
        private readonly EndpointExecutor _executor;

        public Fixture(IReadOnlyDictionary<string, JsonElement> compiled)
        {
            _compiled = compiled;
            Records = new Dictionary<string, object?>
            {
                ["proj:players:steam1"] = new Dictionary<string, object?> { ["coins"] = 100d },
            };
            var definitions = compiled.ToDictionary(
                entry => $"{Project}:ep:{entry.Key}",
                entry => EndpointExpression.FromJson(entry.Value),
                StringComparer.Ordinal);
            _executor = new EndpointExecutor(
                new ScriptedEndpointExecutorTests.FakeDataSource(definitions, Records),
                scriptRuntimes: EndpointExecutor.CreateScriptRuntimes(scriptsEnabled: true));
        }

        public Dictionary<string, object?> Records { get; }

        public Fixture Fresh() => new(_compiled);

        /// <summary>Calls an endpoint the way its HTTP route does: live serve (writes commit), public gates on, method from its definition.</summary>
        public async Task<EndpointExecutionResult> Call(
            string slug, Dictionary<string, object?> input, string steamId = "steam1", bool secretKey = false,
            Dictionary<string, object?>? gameValues = null)
        {
            var method = _compiled[slug].TryGetProperty("method", out var declared) ? declared.GetString()! : "POST";
            var result = await _executor.TryExecuteAsync(
                Project, slug, input, steamId, userId: "u1", gameValues: gameValues ?? new Dictionary<string, object?>(),
                hasSecretKey: secretKey, isDedicatedServer: false, CancellationToken.None,
                enforcePublicAccessGates: true, httpMethod: method, liveServe: true);
            return result!;
        }
    }
}
