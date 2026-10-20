using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Source upgrade for the management API. Ports the reference <c>upgradeSourceResource</c>
/// (controllers/manage-api-controller.js L909-956), <c>sourceUpgradeAmbiguityDiagnostics</c> (L883-907)
/// and <c>formatResourceAsYamlSource</c> (tools/sbox/source-compiler.js L395-417).
/// </summary>
/// <remarks>
/// Deviations: no <c>compilerFingerprint</c> (this server has no compiler build id); route validation against
/// stored flows is not run; YAML is written by YamlDotNet in the reference key order, so the byte layout of
/// <c>upgradedSourceText</c> can differ from the reference emitter.
/// </remarks>
internal static class ManagementSourceUpgrade
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    private static readonly string[] SupportedKinds = ["endpoint", "workflow", "collection", "query", "test"];
    private static readonly string[] AcceptedLayouts = ["flat", "definition-wrapper", "mixed-definition-wrapper"];
    private static readonly string[] FailureLayouts = ["flat", "definition-wrapper"];
    private static readonly string[] SupportedFeatures =
        ["exposure", "routes.true", "routes.false", "onTrue", "onFalse", "sleep", "retry", "goto", "run", "return", "legacy-onFail"];

    // Reference LEGACY_SOURCE_ONLY_KEYS (source-compiler.js L42-63): source bookkeeping, never part of the canonical definition.
    private static readonly HashSet<string> LegacySourceOnlyKeys = new(StringComparer.Ordinal)
    {
        "sourceText", "sourceObject", "sourceFormat", "sourceVersion", "sourcePath", "sourcePathSystemGenerated",
        "sourceHash", "dependencyHash", "canonicalDefinition", "canonicalHash", "executionPlan", "executionPlanHash",
        "diagnostics", "compilerFingerprint", "compileStatus", "compiledAt", "previousKnownGoodPlan",
        "lastRecompileAttemptAt", "authoringMode", "legacyJson",
    };

    // Reference GENERATED_SOURCE_SKIP_KEYS (source-compiler.js L65-74).
    private static readonly HashSet<string> GeneratedSkipKeys = new(StringComparer.Ordinal)
    {
        "createdAt", "updatedAt", "versionHash", "versions", "_deprecated", "deprecated", "depreciated", "depricated",
    };

    // Reference sourceFieldOrder (source-compiler.js L494-505).
    private static readonly Dictionary<string, string[]> FieldOrder = new(StringComparer.Ordinal)
    {
        ["endpoint"] = ["id", "name", "slug", "method", "description", "enabled", "exposure", "internal", "internalOnly", "skipSboxAuth", "requiresSecretKey", "requireReauth", "authPolicy", "input", "let", "steps", "response", "notes"],
        ["workflow"] = ["id", "name", "description", "exposure", "internal", "internalOnly", "params", "steps", "returns", "condition", "requires", "onFail", "notes"],
        ["collection"] = ["id", "name", "description", "collectionType", "accessMode", "maxRecords", "schema", "constants", "tables", "notes"],
    };

    // Reference sourceUpgradeAmbiguityDiagnostics allow-lists (manage-api-controller.js L885-896).
    private static readonly HashSet<string> TopLevelAllowed = new(StringComparer.Ordinal)
    {
        "sourceVersion", "kind", "resourceKind", "id", "name", "description", "notes", "imports", "metadata",
        "definition", "resource", "endpoint", "workflow", "collection", "test", "library",
    };

    private static readonly Dictionary<string, string[]> KindAllowed = new(StringComparer.Ordinal)
    {
        ["endpoint"] = ["slug", "method", "enabled", "exposure", "internal", "internalOnly", "skipSboxAuth", "requiresSecretKey", "requireReauth", "authPolicy", "input", "let", "steps", "response"],
        ["workflow"] = ["params", "input", "steps", "returns", "condition", "requires", "onFail", "exposure", "internal", "internalOnly", "builtIn"],
        ["collection"] = ["schema", "collectionType", "accessMode", "visibility", "version", "rateLimits", "rateLimitAction", "webhookOnRateLimit", "maxRecords", "allowRecordDelete", "requireSaveVersion", "constants", "tables"],
        ["test"] = ["endpoint", "method", "input", "inputMode", "mockData", "expect", "skipWebhooks", "tags"],
    };

    /// <summary>
    /// Compiles and validates one resource source. Returns false when the source cannot be compiled; <paramref name="body"/>
    /// is then the reference failure envelope (HTTP 400 at the route).
    /// </summary>
    public static bool TryUpgrade(JsonElement payload, out JsonObject body)
    {
        var kind = (ReadString(payload, "kind") ?? ReadString(payload, "resourceKind") ?? string.Empty).Trim().ToLowerInvariant();
        if (Array.IndexOf(SupportedKinds, kind) < 0)
        {
            body = FailureBody(Diagnostic("error", "SOURCE_COMPILE_FAILED", $"Source kind '{kind}' cannot be upgraded.", "/kind"));
            return false;
        }

        if (!NetworkStorageSourceResourceCompiler.TryCompile(payload, kind, out var compiled, out var error))
        {
            body = FailureBody(Diagnostic("error", "SOURCE_COMPILE_FAILED", error ?? "Source could not be compiled.", "/sourceText"));
            return false;
        }

        var compiledObject = (JsonObject)JsonNode.Parse(compiled.GetRawText())!;
        var canonical = Canonical(compiledObject);
        var canonicalElement = JsonSerializer.SerializeToElement(canonical);

        var diagnostics = new JsonArray();
        foreach (var finding in NetworkStorageDefinitionValidator.Validate(kind, canonicalElement, DefinitionValidationContext.None))
            diagnostics.Add(Diagnostic(finding.Severity, finding.Code, finding.Message, finding.Path));
        foreach (var finding in AmbiguityDiagnostics(kind, NetworkStorageSourceResourceCompiler.ParseSourceObject(payload)))
            diagnostics.Add(finding);

        var safeAutoWrite = !diagnostics.Any(d => d?["severity"]?.GetValue<string>() is "error" or "warning");

        var seed = ReadString(payload, "slug") ?? ReadString(payload, "id") ?? ReadString(payload, "name")
            ?? ReadString(payload, "sourcePath") ?? kind;
        var sourcePath = ReadString(payload, "sourcePath") ?? $"{seed}.{kind}.yml";

        body = new JsonObject
        {
            ["ok"] = true,
            ["resourceKind"] = kind,
            ["resourceId"] = ReadString(payload, "id") ?? ReadString(payload, "slug") ?? ReadString(payload, "name") ?? string.Empty,
            ["sourcePath"] = sourcePath,
            ["canonicalDefinition"] = canonical,
            ["diagnostics"] = diagnostics,
            ["upgradedSourceText"] = FormatYamlSource(kind, compiledObject, canonical),
            ["safeAutoWrite"] = safeAutoWrite,
            ["capabilities"] = new JsonObject
            {
                ["acceptedLayouts"] = JsonSerializer.SerializeToNode(AcceptedLayouts),
                ["supportedFeatures"] = JsonSerializer.SerializeToNode(SupportedFeatures),
            },
        };
        return true;
    }

    private static JsonObject FailureBody(JsonObject diagnostic)
    {
        var diagnostics = new JsonArray { diagnostic };
        return new JsonObject
        {
            ["ok"] = false,
            ["error"] = "SOURCE_UPGRADE_FAILED",
            ["diagnostics"] = diagnostics,
            ["safeAutoWrite"] = false,
            ["capabilities"] = new JsonObject
            {
                ["acceptedLayouts"] = JsonSerializer.SerializeToNode(FailureLayouts),
            },
        };
    }

    /// <summary>Compiled definition without source bookkeeping or kind markers (reference canonicalDefinition).</summary>
    private static JsonObject Canonical(JsonObject compiled)
    {
        var canonical = new JsonObject();
        foreach (var (key, value) in compiled)
        {
            if (LegacySourceOnlyKeys.Contains(key) || key is "kind" or "resourceKind")
                continue;
            canonical[key] = value?.DeepClone();
        }
        return canonical;
    }

    /// <summary>Reference formatResourceAsYamlSource: sourceText is returned verbatim, otherwise YAML in reference key order.</summary>
    private static string FormatYamlSource(string kind, JsonObject compiled, JsonObject canonical)
    {
        if (compiled["sourceText"] is JsonValue sourceValue
            && sourceValue.TryGetValue<string>(out var sourceText)
            && !string.IsNullOrWhiteSpace(sourceText))
        {
            var text = sourceText.StartsWith('\uFEFF') ? sourceText[1..] : sourceText;
            return text.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceVersion"] = "1",
            ["kind"] = kind,
        };

        if (FieldOrder.TryGetValue(kind, out var ordered))
        {
            foreach (var key in ordered)
            {
                if (canonical.ContainsKey(key))
                    map[key] = ToYamlValue(canonical[key]);
            }
        }

        foreach (var (key, value) in canonical)
        {
            if (GeneratedSkipKeys.Contains(key) || map.ContainsKey(key) || key is "kind" or "sourceVersion" or "resourceKind")
                continue;
            map[key] = ToYamlValue(value);
        }

        var yaml = YamlSerializer.Serialize(map);
        return yaml.EndsWith('\n') ? yaml : yaml + "\n";
    }

    private static object? ToYamlValue(JsonNode? node) => ToYamlValue(JsonSerializer.SerializeToElement(node));

    private static object? ToYamlValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToYamlValue(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(item => ToYamlValue(item)).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static IEnumerable<JsonObject> AmbiguityDiagnostics(string kind, JsonObject? source)
    {
        if (source is null)
            yield break;

        var allowed = new HashSet<string>(TopLevelAllowed, StringComparer.Ordinal);
        if (KindAllowed.TryGetValue(kind, out var kindKeys))
            allowed.UnionWith(kindKeys);

        foreach (var key in source.Select(p => p.Key).Where(k => !allowed.Contains(k)))
        {
            yield return Diagnostic(
                "warning",
                "SOURCE_UPGRADE_AMBIGUOUS_FIELD",
                $"Source field \"{key}\" is not recognized by backend upgrade and was not auto-written as a safe upgrade.",
                key,
                "Remove or rename the field, then retry the upgrade.");
        }
    }

    private static JsonObject Diagnostic(string severity, string code, string message, string sourcePath, string? suggestedFix = null)
    {
        var diagnostic = new JsonObject
        {
            ["severity"] = severity,
            ["code"] = code,
            ["message"] = message,
            ["sourcePath"] = sourcePath,
        };
        if (suggestedFix is not null)
            diagnostic["suggestedFix"] = suggestedFix;
        return diagnostic;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
