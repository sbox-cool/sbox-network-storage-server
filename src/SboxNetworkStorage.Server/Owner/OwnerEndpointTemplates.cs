using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// The starter endpoint templates. <c>endpoint-api/templates/index.json</c> is the single list: the New dialog,
/// the gallery's Templates group and the New action all read it. Each entry names one YAML file per runtime,
/// <c>&lt;id&gt;.&lt;runtime&gt;.endpoint.yml</c>.
/// </summary>
internal static class OwnerEndpointTemplates
{
    private const string IndexResource = "SboxNetworkStorage.Server.Owner.endpoint-templates-index.json";
    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly string[] KnownRuntimes = ["javascript", "typescript"];
    private static readonly JsonSerializerOptions IndexOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<LoadedIndex> Loaded = new(Load);

    /// <summary>Every shipped template, one per runtime, in index order.</summary>
    public static IReadOnlyList<OwnerEndpointTemplate> Templates => Loaded.Value.Templates;

    /// <summary>Why the index could not be loaded, or null. The New dialog and the gallery show this as their error state.</summary>
    public static string? LoadError => Loaded.Value.Error;

    /// <summary>The templates the New dialog offers.</summary>
    public static IReadOnlyList<OwnerEndpointTemplate> Offered => Templates.Where(template => template.OffersInNew).ToList();

    /// <summary>The offered template for a choice such as <c>grant-coins.typescript</c>, or null.</summary>
    public static OwnerEndpointTemplate? FindOffered(string choice) =>
        Offered.FirstOrDefault(template => template.Choice == choice);

    /// <summary>The gallery note for a template that imports another template's module, or null.</summary>
    public static string? NeedsNote(OwnerEndpointTemplate template)
    {
        if (template.Needs is null)
        {
            return null;
        }

        var module = Templates.First(candidate => candidate.Id == template.Needs);
        return $"Needs the {module.Title} template saved first, because it imports globals/{module.Id}.";
    }

    private static LoadedIndex Load()
    {
        try
        {
            return new LoadedIndex(ReadIndex(), null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new LoadedIndex(Array.Empty<OwnerEndpointTemplate>(), exception.Message);
        }
    }

    private static IReadOnlyList<OwnerEndpointTemplate> ReadIndex()
    {
        using var stream = typeof(OwnerEndpointTemplates).Assembly.GetManifestResourceStream(IndexResource)
            ?? throw new InvalidOperationException("The starter template index is missing from the server build.");
        var entries = JsonSerializer.Deserialize<List<IndexEntry>>(stream, IndexOptions)
            ?? throw new InvalidOperationException("The starter template index is empty.");
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The starter template index is empty.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var templates = new List<OwnerEndpointTemplate>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !IdPattern.IsMatch(entry.Id) || !ids.Add(entry.Id))
            {
                throw new InvalidOperationException($"Template id '{entry.Id}' is missing, invalid or repeated.");
            }

            if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Summary))
            {
                throw new InvalidOperationException($"Template {entry.Id} needs a title and a summary.");
            }

            if (entry.Scope is not ("route" or "global"))
            {
                throw new InvalidOperationException($"Template {entry.Id} has an unknown scope.");
            }

            if (entry.Runtimes is null || entry.Runtimes.Count == 0
                || entry.Runtimes.Except(KnownRuntimes).Any()
                || entry.Runtimes.Distinct().Count() != entry.Runtimes.Count)
            {
                throw new InvalidOperationException($"Template {entry.Id} lists runtimes javascript and typescript only.");
            }

            if (entry.Needs is not null && !entries.Any(other => other.Id == entry.Needs && other.Scope == "global"))
            {
                throw new InvalidOperationException($"Template {entry.Id} needs {entry.Needs}, which is not a global template.");
            }

            foreach (var runtime in entry.Runtimes)
            {
                templates.Add(new OwnerEndpointTemplate(
                    entry.Id, runtime, entry.Title, entry.Summary, entry.Scope, entry.Needs,
                    ReadTemplate($"{entry.Id}.{runtime}.endpoint.yml")));
            }
        }

        return templates;
    }

    private static string ReadTemplate(string fileName)
    {
        using var stream = EndpointTypings.Template(fileName)
            ?? throw new InvalidOperationException($"Template file {fileName} is missing from the server build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record IndexEntry(
        string? Id,
        string? Title,
        string? Summary,
        string? Scope,
        List<string>? Runtimes,
        string? Needs);

    private sealed record LoadedIndex(IReadOnlyList<OwnerEndpointTemplate> Templates, string? Error);
}

/// <summary>One starter template for one runtime: the index entry plus its YAML source.</summary>
internal sealed record OwnerEndpointTemplate(
    string Id,
    string Runtime,
    string Title,
    string Summary,
    string Scope,
    string? Needs,
    string Source)
{
    /// <summary>The New dialog option value and the New action's template parameter, for example <c>grant-coins.typescript</c>.</summary>
    public string Choice => Id + "." + Runtime;

    public string RuntimeLabel => Runtime == "typescript" ? "TypeScript" : "JavaScript";

    /// <summary>A standalone route. Global modules are not routes, and a template that imports a module fails to save without it.</summary>
    public bool OffersInNew => Scope == "route" && Needs is null;
}
