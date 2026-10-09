using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>A sample call shown next to an example and replayed by the example tests.</summary>
public sealed class OwnerExampleCall
{
    public string Method { get; set; } = "POST";
    /// <summary>JSON request body (POST) or query object (GET).</summary>
    public string Input { get; set; } = "{}";
    /// <summary>Call with the project's secret key (dedicated server) instead of the public key.</summary>
    public bool Secret { get; set; }
    /// <summary>Player to call as; defaults to <see cref="OwnerResourceExamples.SamplePlayer"/>.</summary>
    public string? SteamId { get; set; }
    public int Status { get; set; } = 200;
}

/// <summary>A ready-to-save definition in the dashboard examples gallery.</summary>
public sealed class OwnerResourceExample
{
    /// <summary>Catalog id: "{kind}.{resource id}".</summary>
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>Catalog ids of companion definitions this example reads or calls.</summary>
    public List<string> Requires { get; set; } = [];
    public List<OwnerExampleCall> Calls { get; set; } = [];
    /// <summary>The YAML source exactly as inserted into the editor.</summary>
    public string Source { get; set; } = "";
    public string ResourceId => Id[(Id.IndexOf('.') + 1)..];
}

/// <summary>
/// The examples gallery, new-resource skeletons and step palette defaults. All of
/// it is YAML embedded from Owner/Examples so the dashboard inserts exactly what
/// the example tests save and execute through the real executor.
/// </summary>
public static class OwnerResourceExamples
{
    public const string SamplePlayer = "76561198000000001";
    public const string SampleFriend = "76561198000000002";

    private static readonly Lazy<IReadOnlyList<OwnerResourceExample>> CatalogValue = new(LoadCatalog);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> SkeletonValue = new(LoadSkeletons);
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> StepDefaultValue = new(LoadStepDefaults);

    public static IReadOnlyList<OwnerResourceExample> Catalog => CatalogValue.Value;

    /// <summary>Default YAML for a new resource of each kind.</summary>
    public static IReadOnlyDictionary<string, string> Skeletons => SkeletonValue.Value;

    /// <summary>The step palette's default step per type, taken from the builder tour endpoint.</summary>
    public static IReadOnlyDictionary<string, JsonElement> StepDefaults => StepDefaultValue.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> StepYamlValue = new(LoadStepYaml);

    /// <summary>Palette defaults rendered as YAML step snippets, exactly as inserted into the editor.</summary>
    public static IReadOnlyDictionary<string, string> StepYaml => StepYamlValue.Value;

    public static OwnerResourceExample? Find(string id) => Catalog.FirstOrDefault(example => example.Id == id);

    /// <summary>The example and all companions it needs, companions first.</summary>
    public static IReadOnlyList<OwnerResourceExample> WithCompanions(OwnerResourceExample example)
    {
        var ordered = new List<OwnerResourceExample>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(OwnerResourceExample item)
        {
            if (!seen.Add(item.Id)) return;
            foreach (var required in item.Requires)
                Visit(Find(required) ?? throw new InvalidOperationException($"Example {item.Id} requires unknown {required}."));
            ordered.Add(item);
        }
        Visit(example);
        return ordered;
    }

    private static IReadOnlyList<OwnerResourceExample> LoadCatalog()
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var items = new List<OwnerResourceExample>();
        foreach (var file in new[] { "endpoints.yml", "integrations.yml", "data.yml", "builder.yml" })
            items.AddRange(deserializer.Deserialize<List<OwnerResourceExample>>(ReadResource(file)));
        var duplicate = items.GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate example id {duplicate.Key}.");
        return items;
    }

    private static IReadOnlyDictionary<string, string> LoadSkeletons()
    {
        var deserializer = new DeserializerBuilder().Build();
        return deserializer.Deserialize<Dictionary<string, string>>(ReadResource("skeletons.yml"));
    }

    private static IReadOnlyDictionary<string, JsonElement> LoadStepDefaults()
    {
        var tour = Find("endpoint.builder-tour") ?? throw new InvalidOperationException("Missing builder tour example.");
        var source = OwnerYamlDefinitions.ParseDefinition(tour.Source, out _);
        var defaults = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var step in source.GetProperty("steps").EnumerateArray())
            defaults.TryAdd(step.GetProperty("type").GetString()!, step.Clone());
        return defaults;
    }

    private static IReadOnlyDictionary<string, string> LoadStepYaml()
    {
        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, step) in StepDefaults)
            snippets[type] = "- " + OwnerYamlDefinitions.ToYaml(step).TrimEnd().Replace("\n", "\n  ");
        return snippets;
    }

    private static string ReadResource(string file)
    {
        using var stream = typeof(OwnerResourceExamples).Assembly.GetManifestResourceStream("SboxNetworkStorage.Server.Owner.Examples." + file)
            ?? throw new InvalidOperationException($"Missing embedded example file {file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
