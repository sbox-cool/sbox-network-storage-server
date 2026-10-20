using System.Net;
using System.Text.Json;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The starter templates behind the New dialog and the examples gallery. The index is the single list, the picker
/// offers only standalone routes, and the New action refuses any choice it does not offer.
/// </summary>
public sealed class OwnerEndpointTemplatesTests : IDisposable
{
    private readonly SqliteHostFactory factory = new();
    public void Dispose() => factory.Dispose();

    [Fact]
    public void IndexLoadsEveryTemplateFileInBothRuntimes()
    {
        Assert.Null(OwnerEndpointTemplates.LoadError);
        var files = OwnerEndpointTemplates.Templates
            .Select(template => $"{template.Id}.{template.Runtime}.endpoint.yml")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(EndpointTypings.TemplateFileNames().OrderBy(name => name, StringComparer.Ordinal), files);
        Assert.Equal(files.Count, files.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NewDialogOffersStandaloneRoutesInBothRuntimes()
    {
        var offered = OwnerEndpointTemplates.Offered.Select(template => template.Choice)
            .OrderBy(choice => choice, StringComparer.Ordinal)
            .ToList();
        var expected = new[] { "daily-reward", "grant-coins", "profile", "submit-score" }
            .SelectMany(id => new[] { id + ".javascript", id + ".typescript" })
            .OrderBy(choice => choice, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, offered);
        Assert.NotNull(OwnerEndpointTemplates.FindOffered("grant-coins.typescript"));
        Assert.Null(OwnerEndpointTemplates.FindOffered("buy-item.typescript"));
        Assert.Null(OwnerEndpointTemplates.FindOffered("inventory.javascript"));
        Assert.Null(OwnerEndpointTemplates.FindOffered("grant-coins"));
    }

    [Fact]
    public void BuyItemNamesItsInventoryModuleAndGrantCoinsNeedsNothing()
    {
        var buyItem = OwnerEndpointTemplates.Templates.Single(template => template.Choice == "buy-item.javascript");
        Assert.Equal("inventory", buyItem.Needs);
        Assert.Contains("globals/inventory", OwnerEndpointTemplates.NeedsNote(buyItem)!);
        var grant = OwnerEndpointTemplates.Templates.Single(template => template.Choice == "grant-coins.javascript");
        Assert.Null(OwnerEndpointTemplates.NeedsNote(grant));
    }

    [SkippableFact]
    public async Task NewStartsTheDraftFromTheChosenTemplate()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Starter draft");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}/resources";

        var page = await client.GetStringAsync(root + "/endpoint/new?draft=my-grant&template=grant-coins.typescript");
        Assert.Contains("Server-side grant", page);
        Assert.Contains("my-grant", page);
    }

    [SkippableFact]
    public async Task NewRefusesTemplatesTheDialogDoesNotOfferAndOtherKinds()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Starter refusals");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}/resources";

        using (var response = await client.GetAsync(root + "/endpoint/new?draft=x1&template=buy-item.typescript"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var response = await client.GetAsync(root + "/endpoint/new?draft=x1&template=nope.javascript"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var response = await client.GetAsync(root + "/collection/new?draft=x1&template=grant-coins.typescript"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task NewWithoutTemplateKeepsTheBlankSkeleton()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Blank draft");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}/resources";

        var page = await client.GetStringAsync(root + "/endpoint/new?draft=plain-one");
        Assert.Contains("plain-one", page);
        Assert.DoesNotContain("Server-side grant", page);
    }

    [SkippableFact]
    public async Task NewDialogAndNoJsFormOfferTheStarterPickerOnlyForEndpoints()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Starter picker");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}/resources";

        var endpointPage = await client.GetStringAsync(root + "/endpoint/new?draft=pick-one");
        Assert.Contains("id=\"new-resource-template\"", endpointPage);
        Assert.Contains("value=\"grant-coins.typescript\"", endpointPage);
        Assert.Contains("name=\"template\"", endpointPage);
        Assert.Contains("action=\"" + root + "/endpoint/new\"", endpointPage);
        Assert.DoesNotContain("value=\"buy-item.typescript\"", endpointPage);

        var listPage = await client.GetStringAsync(root + "/endpoint");
        Assert.Contains("id=\"new-resource-template\"", listPage);
        Assert.Contains("name=\"template\"", listPage);

        var collectionPage = await client.GetStringAsync(root + "/collection/new?draft=pick-one");
        Assert.DoesNotContain("name=\"template\"", collectionPage);
        Assert.DoesNotContain("id=\"new-resource-template\"", collectionPage);
    }

    [SkippableFact]
    public async Task CatalogListsTemplatesSeparatelyFromExamples()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        await CreateOwnerAsync(factory);
        var project = await factory.CreateProjectAsync("Starter catalog");
        using var client = await LoggedInClientAsync(factory);
        var root = $"/dashboard/projects/{project.ProjectId}/resources";

        var catalog = JsonDocument.Parse(await client.GetStringAsync(root + "/catalog")).RootElement;
        Assert.Equal(JsonValueKind.Null, catalog.GetProperty("templateError").ValueKind);

        var templates = catalog.GetProperty("templates").EnumerateArray().ToList();
        Assert.Equal(12, templates.Count);
        Assert.All(templates, template =>
        {
            Assert.True(template.GetProperty("template").GetBoolean());
            Assert.Equal("endpoint", template.GetProperty("kind").GetString());
            Assert.Equal("Templates", template.GetProperty("category").GetString());
        });
        var buyItem = templates.Single(template => template.GetProperty("title").GetString() == "Buy item (TypeScript)");
        Assert.Contains("globals/inventory", buyItem.GetProperty("note").GetString()!);

        Assert.All(catalog.GetProperty("examples").EnumerateArray(), example =>
            Assert.False(example.TryGetProperty("template", out _)));
    }

    [Fact]
    public void MultiLineScriptsAreWrittenAsLiteralBlocks()
    {
        using var document = JsonDocument.Parse("""{"id":"x","script":"export default 1;\nconst b = 2;\n"}""");
        var yaml = OwnerYamlDefinitions.ToYaml(document.RootElement);
        Assert.Contains("script: |", yaml);
        Assert.DoesNotContain("script: >", yaml);
        Assert.Equal("export default 1;\nconst b = 2;\n",
            OwnerYamlDefinitions.ParseDefinition(yaml, out _).GetProperty("script").GetString());
    }

    [Fact]
    public void ScriptsWithLeadingOrTrailingSpacesRoundTripExactly()
    {
        const string script = " const a = 1;  \nconst b = 2;  \n";
        using var document = JsonDocument.Parse("""{"id":"x","script":" const a = 1;  \nconst b = 2;  \n"}""");
        var yaml = OwnerYamlDefinitions.ToYaml(document.RootElement);
        Assert.Contains("script: |", yaml);
        Assert.Equal(script, OwnerYamlDefinitions.ParseDefinition(yaml, out _).GetProperty("script").GetString());
    }
}
