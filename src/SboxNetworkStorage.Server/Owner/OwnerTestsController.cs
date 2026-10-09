using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerTestEndpoint(string Slug, string Method, string InputTemplate);
public sealed record OwnerSavedTest(string Id, string Name, string Endpoint, string InputJson, string SteamId, bool AsServer, string ExpectOutcome, int? ExpectStatus);
public sealed record OwnerTestForm(string? TestId, string Name, string Endpoint, string InputJson, string SteamId, bool AsServer, string ExpectOutcome, string ExpectStatus);
public sealed record OwnerTestsModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerTestEndpoint> Endpoints, IReadOnlyList<OwnerSavedTest> Tests,
    IReadOnlyList<string> KnownPlayers, OwnerTestForm Form, JsonElement? Result = null, JsonElement? Report = null, string? Error = null, string? Notice = null);

/// <summary>
/// Endpoint test runner ("Try it") and saved tests. Runs go through <see cref="ManagementEndpointTestRunner"/>, the same
/// dry-run path as <c>POST /v3/manage/{project}/test-endpoint</c>: the real executor, no durable writes, no webhooks.
/// Saved tests share <c>tests.json</c> with the editor Sync Tool.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerTestsController(INetworkStorageProjectService projects, INetworkStorageStore store,
    NativeEndpointShadowExecutor executor, IQueryValuesContextProvider valuesProvider, IAuditLogger audit) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/tests";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? endpoint, [FromQuery] string? test, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var saved = model.Tests.FirstOrDefault(candidate => candidate.Id == test);
        var form = saved is not null
            ? new OwnerTestForm(saved.Id, saved.Name, saved.Endpoint, saved.InputJson, saved.SteamId, saved.AsServer, saved.ExpectOutcome,
                saved.ExpectStatus?.ToString(CultureInfo.InvariantCulture) ?? "")
            : model.Endpoints.FirstOrDefault(candidate => candidate.Slug == endpoint) is { } selected
                ? model.Form with { Endpoint = selected.Slug, InputJson = selected.InputTemplate }
                : model.Form;
        return View("~/Views/Owner/Tests.cshtml", model with { Form = form });
    }

    [HttpPost(Route + "/run")]
    public async Task<IActionResult> Run(string projectId, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var request = await Request.ReadFormAsync(ct);
        string Field(string name) => request[name].ToString().Trim();
        var form = new OwnerTestForm(Field("testId") is { Length: > 0 } id ? id : null, Field("name"), Field("endpoint"),
            request["input"].ToString(), Field("steamId") is { Length: > 0 } steam ? steam : ManagementEndpointTestRunner.DefaultSteamId,
            request["asServer"] == "true", Field("expectOutcome") is "fail" or "any" ? Field("expectOutcome") : "pass", Field("expectStatus"));
        model = model with { Form = form };

        if (!model.Endpoints.Any(candidate => candidate.Slug == form.Endpoint)) return Invalid(model, "Choose an endpoint to run.");
        JsonElement input;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(form.InputJson) ? "{}" : form.InputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid(model, "Input must be a JSON object, for example {\"amount\": 5}.");
            input = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            return Invalid(model, $"Input is not valid JSON: {error.Message}");
        }
        int? expectStatus = null;
        if (form.ExpectStatus.Length > 0)
        {
            if (!int.TryParse(form.ExpectStatus, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) || status is < 100 or > 599)
                return Invalid(model, "Expected status must be an HTTP status between 100 and 599, or empty.");
            expectStatus = status;
        }
        if (!SteamIdLooksValid(form.SteamId)) return Invalid(model, "Player Steam ID must be numeric.");

        var spec = JsonSerializer.SerializeToElement(TestObject(form.TestId, form.Name, form.Endpoint, input, form.SteamId, form.AsServer, form.ExpectOutcome, expectStatus));
        string? notice = null;
        if (request["intent"] == "save")
        {
            if (form.Name.Length is < 1 or > 120) return Invalid(model, "Give the test a name of 1 to 120 characters before saving it.");
            var existing = await ManagementProjectObjects.ReadTestsAsync(store, OwnerProjectScope.Owner, projectId, ct);
            var replaced = form.TestId is not null && existing.Any(test => OwnerProjectScope.Text(test, "id") == form.TestId);
            var tests = replaced
                ? existing.Select(test => OwnerProjectScope.Text(test, "id") == form.TestId ? spec : test).ToList()
                : existing.Append(spec).ToList();
            try
            {
                var saved = await ManagementProjectObjects.WriteTestsAsync(store, OwnerProjectScope.Owner, projectId, tests, ct);
                var savedId = OwnerProjectScope.Text(replaced ? spec : saved[^1], "id");
                spec = saved.First(test => OwnerProjectScope.Text(test, "id") == savedId);
                form = form with { TestId = savedId };
            }
            catch (ArgumentException error)
            {
                return Invalid(model, error.Message);
            }
            await OwnerProjectScope.AuditAsync(audit, projectId, "test.save", new { id = form.TestId, form.Endpoint }, ct);
            notice = replaced ? "Test updated." : "Test saved. The Sync Tool sees it on its next pull.";
            model = (await LoadAsync(projectId, ct))! with { Form = form };
        }

        var outcome = await Runner().RunAsync(projectId, OwnerProjectScope.Owner, await PlayerKeyModeAsync(projectId, ct),
            ManagementEndpointTestRunner.ReadSpec(spec)!, ct);
        return View("~/Views/Owner/Tests.cshtml", model with { Result = JsonSerializer.SerializeToElement(outcome.Body), Notice = notice });
    }

    [HttpPost(Route + "/run-all")]
    public async Task<IActionResult> RunAll(string projectId, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var report = await Runner().RunSavedAsync(projectId, OwnerProjectScope.Owner, await PlayerKeyModeAsync(projectId, ct), ct);
        return View("~/Views/Owner/Tests.cshtml", model with { Report = JsonSerializer.SerializeToElement(report) });
    }

    [HttpPost(Route + "/delete")]
    public async Task<IActionResult> Delete(string projectId, [FromForm] string? id, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        if (model is null) return NotFound();
        var existing = await ManagementProjectObjects.ReadTestsAsync(store, OwnerProjectScope.Owner, projectId, ct);
        if (!existing.Any(test => OwnerProjectScope.Text(test, "id") == id)) return NotFound();
        await ManagementProjectObjects.WriteTestsAsync(store, OwnerProjectScope.Owner, projectId,
            existing.Where(test => OwnerProjectScope.Text(test, "id") != id).ToList(), ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "test.delete", new { id }, ct);
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/tests");
    }

    /// <summary>Pretty JSON for result bodies and step values in the view.</summary>
    public static string Pretty(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "null" : JsonSerializer.Serialize(value, Indented);

    private ManagementEndpointTestRunner Runner() => new(store, executor, valuesProvider);

    private static bool SteamIdLooksValid(string steamId) => steamId.Length is > 0 and <= 32 && steamId.All(char.IsAsciiDigit);

    private static Dictionary<string, object?> TestObject(string? id, string name, string endpoint, JsonElement input, string steamId,
        bool asServer, string outcome, int? status)
    {
        var expect = new Dictionary<string, object?> { ["outcome"] = outcome };
        if (status is not null) expect["status"] = status;
        var test = new Dictionary<string, object?>
        {
            ["name"] = name.Length > 0 ? name : $"{endpoint} test", ["endpoint"] = endpoint, ["input"] = input,
            ["steamId"] = steamId, ["asServer"] = asServer, ["expect"] = expect,
        };
        if (id is not null) test["id"] = id;
        return test;
    }

    private IActionResult Invalid(OwnerTestsModel model, string error)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("~/Views/Owner/Tests.cshtml", model with { Error = error });
    }

    private async Task<string?> PlayerKeyModeAsync(string projectId, CancellationToken ct)
        => (await OwnerProjectScope.ResolveAsync(projects, projectId, ct))?.PlayerKeyMode;

    private async Task<OwnerTestsModel?> LoadAsync(string projectId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var endpoints = new List<OwnerTestEndpoint>();
        foreach (var row in await store.ListEndpointsAsync(projectId, ct))
        {
            var definition = OwnerProjectScope.JsonColumn(row, "definition_json");
            var slug = OwnerProjectScope.Text(row, "slug") ?? OwnerProjectScope.Text(row, "endpoint_id");
            if (string.IsNullOrEmpty(slug)) continue;
            endpoints.Add(new OwnerTestEndpoint(slug, OwnerProjectScope.Text(row, "method") ?? "POST", InputTemplate(definition)));
        }
        endpoints.Sort((left, right) => string.CompareOrdinal(left.Slug, right.Slug));
        var tests = (await ManagementProjectObjects.ReadTestsAsync(store, OwnerProjectScope.Owner, projectId, ct)).Select(ReadTest).ToList();
        var players = (await store.ReadProjectProfilesAsync(projectId, ct))
            .Select(profile => OwnerProjectScope.Text(profile, "steam_id")).OfType<string>().Take(50).ToList();
        var first = endpoints.FirstOrDefault();
        return new OwnerTestsModel(projectId, project.Name, endpoints, tests, players,
            new OwnerTestForm(null, "", first?.Slug ?? "", first?.InputTemplate ?? "{}", ManagementEndpointTestRunner.DefaultSteamId, false, "pass", ""));
    }

    private static OwnerSavedTest ReadTest(JsonElement test)
    {
        var spec = ManagementEndpointTestRunner.ReadSpec(test);
        return new OwnerSavedTest(OwnerProjectScope.Text(test, "id") ?? "", OwnerProjectScope.Text(test, "name") ?? "Unnamed test",
            spec?.Slug ?? "", spec is null ? "{}" : JsonSerializer.Serialize(spec.Input, Indented),
            spec?.SteamId ?? ManagementEndpointTestRunner.DefaultSteamId, spec?.AsServer ?? false, spec?.ExpectOutcome ?? "pass", spec?.ExpectStatus);
    }

    /// <summary>Input skeleton from the endpoint's <c>input.properties</c>: declared defaults, else a value of the declared type.</summary>
    private static string InputTemplate(JsonElement? definition)
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (definition is { ValueKind: JsonValueKind.Object } def && def.TryGetProperty("input", out var schema)
            && schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                input[property.Name] = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("default", out var fallback)
                    ? fallback.Clone()
                    : OwnerProjectScope.Text(property.Value, "type") switch
                    {
                        "number" or "integer" => 0,
                        "boolean" => false,
                        "array" => Array.Empty<object>(),
                        "object" => new Dictionary<string, object?>(),
                        _ => "",
                    };
            }
        }
        return JsonSerializer.Serialize(input, Indented);
    }
}
