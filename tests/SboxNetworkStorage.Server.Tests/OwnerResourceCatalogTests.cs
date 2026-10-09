using System.Net;
using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The resources editor gallery and step palette are served from the example
/// catalog. Every catalog example must validate cleanly for its kind, and every
/// palette snippet must parse as a step of the claimed type.
/// </summary>
public sealed class OwnerResourceCatalogTests : IDisposable
{
    private readonly SqliteHostFactory factory = new();
    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task CatalogServesValidExamplesAndPalette()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Catalog");
        using var client = await LoggedInClientAsync(factory);

        using var response = await client.GetAsync($"/dashboard/projects/{project.ProjectId}/resources/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var examples = catalog.GetProperty("examples").EnumerateArray().ToList();
        Assert.NotEmpty(examples);
        foreach (var example in examples)
        {
            var kind = example.GetProperty("kind").GetString()!;
            var source = example.GetProperty("source").GetString()!;
            var parsed = SboxNetworkStorage.Server.Owner.OwnerYamlDefinitions.ParseDefinition(source, out _);
            var diagnostics = NetworkStorageDefinitionValidator.ValidateResource(
                parsed, kind, DefinitionValidationContext.None, out _);
            Assert.True(!diagnostics.Any(item => item.IsError),
                $"{example.GetProperty("id")}: {string.Join("; ", diagnostics.Select(item => item.Message))}");
        }

        var palette = catalog.GetProperty("stepYaml").EnumerateObject().ToList();
        Assert.NotEmpty(palette);
        foreach (var entry in palette)
        {
            var snippet = entry.Value.GetString()!;
            var wrapped = SboxNetworkStorage.Server.Owner.OwnerYamlDefinitions.ParseDefinition(
                "id: palette\nsteps:\n" + string.Join("\n", snippet.Split('\n').Select(line => "  " + line)), out _);
            Assert.Equal(entry.Name, wrapped.GetProperty("steps")[0].GetProperty("type").GetString());
            var stepDiagnostics = NetworkStorageDefinitionValidator.ValidateResource(
                wrapped, "endpoint", DefinitionValidationContext.None, out _);
            Assert.True(!stepDiagnostics.Any(item => item.IsError),
                $"palette {entry.Name}: {string.Join("; ", stepDiagnostics.Select(item => item.Message))}");
        }
    }

    [SkippableFact]
    public async Task CheckReportsDiagnosticsAndCompanionsCreateNeeds()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Catalog actions");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}/resources";

        var page = await client.GetStringAsync(root + "/endpoint");
        using (var invalid = await client.PostAsync(root + "/endpoint/check",
            Form(("definition", "id: bad\nmethod: PUT\nsteps:\n  - id: x\n    type: nope\n"), ("__RequestVerificationToken", Csrf(page)))))
        {
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            var result = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()).RootElement;
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Contains(result.GetProperty("diagnostics").EnumerateArray(),
                item => item.GetProperty("isError").GetBoolean());
        }

        var example = JsonDocument.Parse(await (await client.GetAsync(root + "/catalog")).Content.ReadAsStringAsync())
            .RootElement.GetProperty("examples").EnumerateArray()
            .First(item => item.GetProperty("requires").GetArrayLength() > 0 && !item.GetProperty("exists").GetBoolean());
        using var companions = await client.PostAsync(root + $"/catalog/{example.GetProperty("id").GetString()}/companions",
            Form(("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.OK, companions.StatusCode);
        var created = JsonDocument.Parse(await companions.Content.ReadAsStringAsync()).RootElement;
        Assert.NotEmpty(created.GetProperty("created").EnumerateArray());
        Assert.Empty(created.GetProperty("failed").EnumerateArray());
    }
}
