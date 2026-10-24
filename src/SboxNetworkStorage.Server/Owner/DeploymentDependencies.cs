using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Static references only. Never executes script, resolves templates, or makes outbound calls.</summary>
internal static partial class DeploymentDependencies
{
    internal sealed record Reference(string Kind, string Id, string Path, bool Dynamic = false);

    internal static IReadOnlyList<Reference> Read(string kind, JsonElement definition, IReadOnlyList<IEndpointScriptRuntime> runtimes)
    {
        var result = new List<Reference>();
        if (kind is "endpoint" or "workflow")
        {
            if (definition.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.String
                && runtime.GetString() is "javascript" or "typescript")
            {
                if (NetworkStorageDefinitionValidator.TryAnalyzeScript(definition, runtimes, out var module))
                    result.AddRange(module.GlobalImports.Select(import => new Reference("endpoint", import.EndpointId, "/script/imports")));
                // Even syntactically valid scripts may construct record/value/workflow names at runtime.
                result.Add(new Reference("unknown", "Script runtime dependencies are not statically proven", "/script", true));
            }
            else if (definition.TryGetProperty("steps", out var steps)) ReadSteps(steps, "/steps", result);
        }
        if (kind == "query" && definition.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind == JsonValueKind.Object) Add(result, "collection", Text(source, "collectionId"), $"/sources/{index}/collectionId");
                index++;
            }
        }
        if (kind is "endpoint" or "workflow" or "query") ReadValues(definition, "", result);
        return result.Distinct().ToArray();
    }

    private static void ReadSteps(JsonElement steps, string path, List<Reference> result)
    {
        if (steps.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var at = $"{path}/{index++}";
            if (step.ValueKind != JsonValueKind.Object) continue;
            switch (Text(step, "type"))
            {
                case "read" or "write" or "delete":
                    Add(result, "collection", Text(step, "collection"), at + "/collection");
                    break;
                case "lookup" or "lookup_many" or "filter" or "random_select":
                    var values = Text(step, "source") == "values";
                    Add(result, values ? "value" : "collection", values ? Text(step, "table") ?? Text(step, "collection") : Text(step, "collection"), at + (values ? "/table" : "/collection"));
                    break;
                case "workflow":
                    Add(result, "workflow", Text(step, "workflow"), at + "/workflow");
                    break;
                case "block":
                    if (step.TryGetProperty("steps", out var children)) ReadSteps(children, at + "/steps", result);
                    break;
            }
        }
    }

    private static void Add(List<Reference> result, string kind, string? id, string path)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        result.Add(new Reference(kind, id, path, id.Contains("{{", StringComparison.Ordinal)));
    }

    private static void ReadValues(JsonElement node, string path, List<Reference> result)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "script") continue;
                ReadValues(property.Value, path + "/" + property.Name, result);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in node.EnumerateArray()) ReadValues(item, path + "/" + i++, result);
        }
        else if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString()!;
            foreach (Match match in ValueReference().Matches(text))
            {
                var directToken = text.AsSpan(0, match.Index).TrimEnd().EndsWith("{{", StringComparison.Ordinal);
                var fieldPath = path.EndsWith("/field", StringComparison.Ordinal) && match.Index == 0;
                result.Add(new Reference("value", match.Groups[1].Value, path, !directToken && !fieldPath));
            }
            if (text.Contains("values[", StringComparison.Ordinal))
                result.Add(new Reference("unknown", "Computed game-value reference", path, true));
        }
    }

    private static string? Text(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"\bvalues\.([A-Za-z_][A-Za-z0-9_-]*)\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ValueReference();
}
