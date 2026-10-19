using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Pure port of <c>routeManageGetAgentManifest</c> (controllers/manage-api-controller.js:1118-1252)
/// and the helpers it uses. The caller owns auth, the <c>ok</c> key and usage logging.
/// </summary>
internal static class ManagementAgentManifest
{
    private const int AgentManifestVersion = 1;

    private static readonly string[] ExpressionOperators = ["==", "!=", ">", "<", ">=", "<=", "contains", "not_contains", "in", "not_in", "exists", "not_exists", "starts_with", "not_starts_with"];
    private static readonly string[] ExpressionOperatorAliases = ["eq", "neq", "ne", "gt", "lt", "gte", "ge", "lte", "le", "includes", "has", "not_contains", "not_includes", "not_has", "notcontains", "not_in", "notin", "not_exists", "notexist", "notexists", "startswith", "notstartswith"];
    private static readonly string[] ExpressionMathFunctions = ["abs", "ceil", "clamp", "dayOfWeek", "diffMs", "diffS", "floor", "hour", "log10", "max", "min", "now", "nowS", "pow", "random", "round"];
    private static readonly string[] StepTypeNames = ["assert", "block", "call", "condition", "delete", "forEach", "filter", "lookup", "lookup_many", "read", "random_select", "transform", "write"];

    private static readonly string[] CompilerNoiseKeys = ["canonicalDefinition", "executionPlan", "diagnostics", "compilerFingerprint", "compilerFingerprintHash", "sourceHash", "dependencyHash", "canonicalHash", "executionPlanHash", "dependencies", "sourceObject", "sourcePathSystemGenerated", "compileStatus", "compiledAt", "previousKnownGoodPlan", "lastRecompileAttemptAt", "legacyJson"];
    private static readonly string[] SourceOnlyOutputKeys = ["kind", "resourceKind"];
    private static readonly string[] EndpointDuplicateKeys = ["input", "steps", "response", "method", "enabled", "skipSboxAuth", "requireReauth", "authPolicy"];
    private static readonly string[] WorkflowDuplicateKeys = ["steps", "params", "returns", "condition", "requires", "onFail", "builtIn"];
    private static readonly string[] CollectionDuplicateKeys = ["schema", "collectionType", "accessMode", "visibility", "version", "rateLimits", "rateLimitAction", "webhookOnRateLimit", "maxRecords", "allowRecordDelete", "requireSaveVersion", "constants", "tables"];
    private static readonly string[] StepBranchKeys = ["onTrue", "onFalse", "else", "then", "cases"];

    /// <summary>
    /// Builds the agent manifest body. <paramref name="apiBaseUrl"/> must be the server base URL.
    /// <paramref name="endpoints"/>, <paramref name="collections"/> and <paramref name="workflows"/> are
    /// JSON arrays; non-array values are treated as empty. <paramref name="gamePackage"/> is the stored
    /// game-package document (for revision fields). <paramref name="query"/> null means no query keys.
    /// </summary>
    internal static JsonObject Build(
        WorkspaceProject project,
        string projectId,
        string apiBaseUrl,
        JsonElement endpoints,
        JsonElement gamePackage,
        JsonElement collections,
        JsonElement workflows,
        IReadOnlyDictionary<string, string>? query)
    {
        var includeCollections = IsTruthyQueryFlag(QueryValue(query, "includeCollections"));
        var includeWorkflows = IsTruthyQueryFlag(QueryValue(query, "includeWorkflows"));
        var includeDeprecated = IsTruthyQueryFlag(QueryValue(query, "includeDeprecated"));
        var includeInternal = IsTruthyQueryFlag(QueryValue(query, "includeInternal"));
        var baseUrl = apiBaseUrl.TrimEnd('/');

        var stepTypesObserved = new SortedSet<string>(StringComparer.Ordinal);
        var endpointEntries = new JsonArray();
        var liveEndpoints = ToObjectItems(endpoints).Where(endpoint => includeDeprecated || !IsEndpointDeprecated(endpoint));
        foreach (var endpoint in EndpointsForManage(liveEndpoints))
        {
            var summary = EndpointSummaryFor(endpoint, projectId, baseUrl);
            if (summary is null || (!includeInternal && !summary.PubliclyCallable))
            {
                continue;
            }

            foreach (var stepType in summary.StepTypes)
            {
                stepTypesObserved.Add(stepType);
            }

            summary.Json["auth"] = EndpointAuthHints(summary.RequiresSecretKey);
            endpointEntries.Add(summary.Json);
        }

        var gamePackageObject = ToRootObject(gamePackage);
        var response = new JsonObject
        {
            ["manifestVersion"] = AgentManifestVersion,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["project"] = new JsonObject
            {
                ["id"] = projectId,
                ["title"] = string.IsNullOrEmpty(project.Name) ? projectId : project.Name,
                ["enabled"] = project.Enabled,
                ["visibility"] = new JsonObject
                {
                    ["requireSboxAuth"] = project.RequireSboxAuth == true,
                    ["enableAuthSessions"] = project.EnableAuthSessions == true,
                    ["requireAuthSessions"] = project.RequireAuthSessions == true,
                    ["enableEncryptedRequests"] = project.EnableEncryptedRequests == true,
                },
                ["revision"] = new JsonObject
                {
                    ["currentRevisionId"] = Clone(Get(gamePackageObject, "currentRevisionId")),
                    ["latestRevisionId"] = Clone(Get(gamePackageObject, "latestRevisionId")),
                    ["revisionPublishedAt"] = Clone(FirstTruthy(Get(gamePackageObject, "revisionPublishedAt"), null)),
                },
                ["urls"] = new JsonObject
                {
                    ["endpointManifest"] = $"{baseUrl}/v3/manage/{projectId}/agent-manifest?includeCollections=false&includeWorkflows=false",
                    ["endpointTemplate"] = $"{baseUrl}/v3/endpoints/{{projectId}}/{{endpointSlug}}",
                },
            },
            ["execution"] = new JsonObject
            {
                ["routeTemplate"] = $"{baseUrl}/v3/endpoints/{projectId}/{{endpointSlug}}",
                ["methods"] = StringArray(["GET", "POST"]),
                ["responsePolicy"] = new JsonObject
                {
                    ["httpStatus"] = 200,
                    ["bodyStatusCode"] = "status",
                    ["bodyErrorCode"] = "error",
                },
            },
            ["capabilities"] = new JsonObject
            {
                ["expressionOperators"] = StringArray(ExpressionOperators),
                ["expressionOperatorAliases"] = StringArray(ExpressionOperatorAliases),
                ["expressionMathFunctions"] = StringArray(ExpressionMathFunctions),
                ["stepTypes"] = StringArray(StepTypeNames),
                ["stepTypesObserved"] = StringArray(stepTypesObserved),
            },
            ["endpoints"] = endpointEntries,
            ["endpointCount"] = endpointEntries.Count,
            ["endpointAuthHeaders"] = new JsonObject
            {
                ["projectHeader"] = "x-api-key",
                ["projectQuery"] = "apiKey",
                ["secretHeader"] = "x-secret-key",
                ["secretAliases"] = StringArray(["secretKey", "networkStorageSecret", "network_storage_secret_key"]),
                ["steamHeader"] = "x-steam-id",
                ["steamQuery"] = "steamId",
                ["revisionHeader"] = "x-ns-revision-id",
                ["authSessionHeader"] = "x-auth-session-token",
                ["endpointFieldAliases"] = new JsonObject
                {
                    ["endpointSlug"] = "_endpointSlug",
                    ["endpoint"] = "endpoint",
                    ["bodyEndpointField"] = "input.endpoint",
                },
            },
            ["links"] = new JsonObject
            {
                ["docs"] = new JsonObject
                {
                    ["base"] = "/wiki/network-storage-v3",
                    ["endpointStandards"] = "/wiki/network-storage-v3/endpoint-standards",
                    ["endpointExpressions"] = "/wiki/network-storage-v3/math-expressions",
                    ["revisioning"] = "/wiki/network-storage-v3/revision-handling",
                    ["agentDiscovery"] = "/wiki/network-storage-v3/agent-discovery",
                },
            },
        };

        if (includeCollections)
        {
            var collectionEntries = ToJsonArray(ToObjectItems(collections).Select(CollectionSummary));
            response["collections"] = collectionEntries;
            response["collectionCount"] = collectionEntries.Count;
        }

        if (includeWorkflows)
        {
            var workflowEntries = ToJsonArray(ToObjectItems(workflows).Select(WorkflowSummary));
            response["workflows"] = workflowEntries;
            response["workflowCount"] = workflowEntries.Count;
        }

        if (!includeCollections)
        {
            response["collections"] = new JsonArray();
            response["collectionCount"] = 0;
        }

        if (!includeWorkflows)
        {
            response["workflows"] = new JsonArray();
            response["workflowCount"] = 0;
        }

        return response;
    }

    private sealed record EndpointSummary(JsonObject Json, bool PubliclyCallable, bool RequiresSecretKey, IEnumerable<string> StepTypes);

    private static EndpointSummary? EndpointSummaryFor(JsonObject endpoint, string projectId, string apiBaseUrl)
    {
        var safe = EndpointForManage(endpoint);
        var slugNode = FirstTruthy(Get(safe, "slug"), Get(safe, "id"), null);
        if (!Truthy(slugNode))
        {
            return null;
        }

        var slug = JsString(slugNode);
        var requiresSecretKey = Truthy(Get(safe, "requiresSecretKey"));
        var skipSboxAuth = Truthy(Get(safe, "skipSboxAuth"));
        var method = JsString(FirstTruthy(Get(safe, "method"), JsonValue.Create("POST"))).ToUpperInvariant();
        var publiclyCallable = BoolValue(Get(safe, "publiclyCallable")) != false;

        var stepTypes = new SortedSet<string>(StringComparer.Ordinal);
        CollectStepTypes(Get(safe, "steps"), stepTypes);

        var summary = new JsonObject
        {
            ["slug"] = Clone(slugNode),
            ["id"] = Clone(FirstTruthy(Get(safe, "id"), slugNode)),
            ["name"] = Clone(FirstTruthy(Get(safe, "name"), slugNode)),
            ["method"] = method is "GET" or "POST" ? method : "POST",
            ["enabled"] = BoolValue(Get(safe, "enabled")) != false,
            ["description"] = Clone(FirstTruthy(Get(safe, "description"), JsonValue.Create(""))),
            ["publiclyCallable"] = publiclyCallable,
            ["exposure"] = Clone(FirstTruthy(Get(safe, "exposure"), JsonValue.Create("public"))),
            ["requiresSecretKey"] = requiresSecretKey,
            ["skipSboxAuth"] = skipSboxAuth,
            ["requireReauth"] = Truthy(Get(safe, "requireReauth")),
            ["authPolicy"] = Clone(FirstTruthy(Get(safe, "authPolicy"), null)),
            ["endpointKey"] = $"{projectId}:{slug}",
            ["url"] = $"{apiBaseUrl}/v3/endpoints/{projectId}/{EncodeUriComponent(slug)}",
            ["input"] = Get(safe, "input") is JsonObject inputObject ? Clone(inputObject) : null,
            ["response"] = Get(safe, "response") is JsonObject responseObject ? Clone(responseObject) : null,
            ["responseCodes"] = ResponseCodes(Get(safe, "response")),
            ["stepTypes"] = StringArray(stepTypes),
            ["authModes"] = StringArray(AuthModes(requiresSecretKey, skipSboxAuth)),
            ["notes"] = Clone(FirstTruthy(Get(safe, "notes"), JsonValue.Create(""))),
        };

        return new EndpointSummary(summary, publiclyCallable, requiresSecretKey, stepTypes);
    }

    private static JsonObject EndpointAuthHints(bool requiresSecretKey) => new()
    {
        ["projectAuth"] = new JsonObject
        {
            ["header"] = "x-api-key",
            ["query"] = "apiKey",
            ["headerAliases"] = StringArray(["x-network-storage-key"]),
        },
        ["steamHint"] = requiresSecretKey ? "optional" : "required unless endpoint skips auth",
        ["secretHint"] = requiresSecretKey ? "x-secret-key (recommended)" : "optional",
    };

    private static IEnumerable<string> AuthModes(bool requiresSecretKey, bool skipSboxAuth)
    {
        if (requiresSecretKey)
        {
            yield return "secret-key";
        }

        if (!skipSboxAuth && !requiresSecretKey)
        {
            yield return "steam-id";
        }
    }

    private static JsonArray ResponseCodes(JsonNode? response)
    {
        var codes = new JsonArray();
        if (response is JsonObject responseObject && responseObject.ContainsKey("status"))
        {
            codes.Add(Clone(responseObject["status"]));
        }
        else
        {
            codes.Add(JsonValue.Create(200));
        }

        return codes;
    }

    private static JsonObject WorkflowSummary(JsonObject workflow)
    {
        var safe = ForManage(workflow, "workflow", WorkflowDuplicateKeys);
        var returns = Get(safe, "returns");
        return new JsonObject
        {
            ["id"] = Clone(FirstTruthy(Get(safe, "id"), Get(safe, "slug"), null)),
            ["slug"] = Clone(FirstTruthy(Get(safe, "slug"), Get(safe, "id"), null)),
            ["name"] = Clone(FirstTruthy(Get(safe, "name"), Get(safe, "slug"), Get(safe, "id"), JsonValue.Create(""))),
            ["exposure"] = Clone(FirstTruthy(Get(safe, "exposure"), JsonValue.Create("public"))),
            ["params"] = WorkflowParams(Get(safe, "params")),
            ["returns"] = returns is JsonObject or JsonArray ? Clone(returns) : null,
            ["endpointCalls"] = Clone(FirstTruthy(Get(safe, "endpointCalls"), null)),
            ["callsWorkflow"] = Clone(FirstTruthy(Get(safe, "callsWorkflow"), null)),
        };
    }

    private static JsonArray WorkflowParams(JsonNode? parameters)
    {
        if (parameters is JsonArray)
        {
            return (JsonArray)Clone(parameters)!;
        }

        if (parameters is JsonObject parameterObject)
        {
            return StringArray(OrderedKeys(parameterObject));
        }

        var text = StringValue(parameters);
        var keys = text is null
            ? Enumerable.Empty<string>()
            : Enumerable.Range(0, text.Length).Select(index => index.ToString(CultureInfo.InvariantCulture));
        return StringArray(keys);
    }

    private static JsonObject CollectionSummary(JsonObject collection)
    {
        var safe = ForManage(collection, "collection", CollectionDuplicateKeys);
        var schema = Get(safe, "schema");
        var fieldCount = schema is JsonObject schemaObject && Get(schemaObject, "properties") is { } properties
            ? CountKeys(properties)
            : 0;
        return new JsonObject
        {
            ["id"] = Clone(FirstTruthy(Get(safe, "id"), Get(safe, "sourcePath"), null)),
            ["name"] = Clone(FirstTruthy(Get(safe, "name"), Get(safe, "id"), Get(safe, "sourcePath"), null)),
            ["exposure"] = Clone(FirstTruthy(Get(safe, "exposure"), Get(safe, "visibility"), JsonValue.Create("public"))),
            ["fieldCount"] = fieldCount,
            ["schema"] = schema is JsonObject or JsonArray ? Clone(schema) : null,
        };
    }

    private static int CountKeys(JsonNode properties) => properties switch
    {
        JsonObject propertyObject => propertyObject.Count,
        JsonArray propertyArray => propertyArray.Count,
        _ => 0,
    };

    private static List<JsonObject> EndpointsForManage(IEnumerable<JsonObject> endpoints)
    {
        var byIdentity = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var identities = new List<string>();
        var anonymous = new List<JsonObject>();
        foreach (var endpoint in endpoints)
        {
            var identity = EndpointManageIdentity(endpoint);
            if (identity.Length == 0)
            {
                anonymous.Add(endpoint);
            }
            else if (!byIdentity.TryGetValue(identity, out var current))
            {
                byIdentity[identity] = endpoint;
                identities.Add(identity);
            }
            else if (ShouldReplaceEndpointForManage(current, endpoint))
            {
                byIdentity[identity] = endpoint;
            }
        }

        var result = identities.Select(identity => byIdentity[identity]).ToList();
        result.AddRange(anonymous);
        return result;
    }

    private static string EndpointManageIdentity(JsonObject endpoint)
    {
        var slug = ExtractSlug(endpoint);
        if (Truthy(slug))
        {
            return "slug:" + JsString(slug);
        }

        var id = (StringValue(Get(endpoint, "id")) ?? "").Trim();
        return id.Length > 0 ? "id:" + id : "";
    }

    private static JsonNode? ExtractSlug(JsonObject endpoint) => FirstTruthy(
        Get(Get(endpoint, "sourceObject"), "slug"),
        Get(Get(endpoint, "canonicalDefinition"), "slug"),
        Get(endpoint, "slug"),
        null);

    private static bool ShouldReplaceEndpointForManage(JsonObject current, JsonObject candidate)
    {
        var currentRank = EndpointRank(current);
        var candidateRank = EndpointRank(candidate);
        if (candidateRank.AuthoredPath != currentRank.AuthoredPath)
        {
            return candidateRank.AuthoredPath > currentRank.AuthoredPath;
        }

        if (candidateRank.Timestamp != currentRank.Timestamp)
        {
            return candidateRank.Timestamp > currentRank.Timestamp;
        }

        return true;
    }

    private static (int AuthoredPath, long Timestamp) EndpointRank(JsonObject endpoint)
    {
        var hasAuthoredPath = Truthy(Get(endpoint, "sourcePath")) && !HasSystemGeneratedSourcePath(endpoint, "endpoint");
        var timestampText = JsString(FirstTruthy(
            Get(endpoint, "updatedAt"),
            Get(endpoint, "compiledAt"),
            Get(endpoint, "createdAt"),
            JsonValue.Create("")));
        return (hasAuthoredPath ? 1 : 0, ParseTimestampMs(timestampText));
    }

    private static long ParseTimestampMs(string text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : 0;

    private static JsonObject EndpointForManage(JsonObject endpoint)
    {
        var normalized = ForManage(endpoint, "endpoint", EndpointDuplicateKeys);

        var notes = NormalizeNotesValue(Get(endpoint, "notes"));
        if (notes.Length > 0)
        {
            normalized["notes"] = notes;
        }
        else
        {
            normalized.Remove("notes");
        }

        var isInternal = StringValue(Get(endpoint, "exposure")) == "internal"
            || BoolValue(Get(endpoint, "internal")) == true
            || BoolValue(Get(endpoint, "internalOnly")) == true;
        normalized["exposure"] = isInternal ? "internal" : "public";
        normalized["publiclyCallable"] = !isInternal;
        return normalized;
    }

    private static JsonObject ForManage(JsonObject resource, string kind, string[] duplicateKeys)
    {
        var normalized = (JsonObject)Clone(resource)!;
        StripSystemGeneratedSourcePath(normalized, resource, kind);
        foreach (var key in CompilerNoiseKeys)
        {
            normalized.Remove(key);
        }

        foreach (var key in SourceOnlyOutputKeys)
        {
            normalized.Remove(key);
        }

        if (IsSourceAuthored(resource))
        {
            StripDefinitionDuplicates(normalized, duplicateKeys);
        }

        return normalized;
    }

    private static void StripSystemGeneratedSourcePath(JsonObject normalized, JsonObject resource, string kind)
    {
        if (HasSystemGeneratedSourcePath(resource, kind))
        {
            normalized.Remove("sourcePath");
        }

        normalized.Remove("sourcePathSystemGenerated");
    }

    private static void StripDefinitionDuplicates(JsonObject normalized, string[] duplicateKeys)
    {
        if (Get(normalized, "definition") is not JsonObject definition)
        {
            return;
        }

        foreach (var key in duplicateKeys)
        {
            if (definition.ContainsKey(key))
            {
                normalized.Remove(key);
            }
        }
    }

    private static bool HasSystemGeneratedSourcePath(JsonObject resource, string kind) =>
        BoolValue(Get(resource, "sourcePathSystemGenerated")) == true
        || (kind == "endpoint" && IsLegacyEndpointGeneratedSourcePath(resource));

    private static bool IsLegacyEndpointGeneratedSourcePath(JsonObject resource)
    {
        var sourcePath = StringValue(Get(resource, "sourcePath")) ?? "";
        var id = (StringValue(Get(resource, "id")) ?? "").Trim();
        var slug = (StringValue(Get(resource, "slug")) ?? "").Trim();
        if (sourcePath.Length == 0 || id.Length == 0 || slug.Length == 0 || id == slug)
        {
            return false;
        }

        return sourcePath == $"{id}.endpoint.yml"
            || sourcePath == $"{id}.endpoint.yaml"
            || sourcePath == $"{id}.endpoint.json";
    }

    private static bool IsSourceAuthored(JsonObject resource)
    {
        var sourceFormat = JsString(FirstTruthy(Get(resource, "sourceFormat"), JsonValue.Create("json"))).ToLowerInvariant();
        var authoringMode = Get(resource, "authoringMode");
        var mode = Truthy(authoringMode)
            ? StringValue(authoringMode)
            : sourceFormat == "json" ? "legacy-json" : "source";
        return mode == "source" || sourceFormat is "yaml" or "yml";
    }

    private static string NormalizeNotesValue(JsonNode? value)
    {
        if (value is null)
        {
            return "";
        }

        var text = JsString(value).Replace("\r\n", "\n").Replace('\r', '\n');
        var normalized = string.Join("\n", text.Split('\n').Select(line => line.TrimEnd())).Trim();
        return normalized.Length > 16000 ? normalized[..16000] : normalized;
    }

    private static bool IsEndpointDeprecated(JsonObject endpoint) =>
        IsTruthyJsonFlag(Get(endpoint, "_deprecated"))
        || IsTruthyJsonFlag(Get(endpoint, "deprecated"))
        || IsTruthyJsonFlag(Get(endpoint, "depreciated"))
        || IsTruthyJsonFlag(Get(endpoint, "depricated"));

    private static void CollectStepTypes(JsonNode? steps, ISet<string> output)
    {
        if (steps is not JsonArray stepList)
        {
            return;
        }

        foreach (var step in stepList)
        {
            if (step is not JsonObject stepObject)
            {
                continue;
            }

            if (StringValue(Get(stepObject, "type")) is { Length: > 0 } type)
            {
                output.Add(type);
            }

            CollectStepTypes(Get(stepObject, "steps"), output);
            foreach (var key in StepBranchKeys)
            {
                var branch = Get(stepObject, key);
                if (branch is JsonArray)
                {
                    CollectStepTypes(branch, output);
                }
                else if (branch is JsonObject branchObject)
                {
                    CollectStepTypes(Get(branchObject, "steps"), output);
                }
            }
        }
    }

    private static List<JsonObject> ToObjectItems(JsonElement array)
    {
        var items = new List<JsonObject>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var element in array.EnumerateArray())
        {
            items.Add(ToRootObject(element) ?? new JsonObject());
        }

        return items;
    }

    private static JsonObject? ToRootObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? JsonNode.Parse(element.GetRawText()) as JsonObject : null;

    private static JsonArray ToJsonArray(IEnumerable<JsonNode> items) => new(items.ToArray());

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    private static string? QueryValue(IReadOnlyDictionary<string, string>? query, string key) =>
        query is not null && query.TryGetValue(key, out var value) ? value : null;

    /// <summary>controllers/manage-api-controller.js isTruthyFlag: value must be true/on/yes/1 (no trim).</summary>
    private static bool IsTruthyQueryFlag(string? value) =>
        value is not null && value.ToLowerInvariant() is "true" or "on" or "yes" or "1";

    /// <summary>tools/sbox/manage-resource-serializer.js isTruthyFlag.</summary>
    private static bool IsTruthyJsonFlag(JsonNode? value)
    {
        if (value is null)
        {
            return false;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetValue<double>() != 0,
            JsonValueKind.String => value.GetValue<string>().Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on",
            _ => false,
        };
    }

    /// <summary>JS <c>a || b || ... </c>: first truthy operand, else the last operand.</summary>
    private static JsonNode? FirstTruthy(params JsonNode?[] values)
    {
        foreach (var value in values)
        {
            if (Truthy(value))
            {
                return value;
            }
        }

        return values[^1];
    }

    private static bool Truthy(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.Number => node.GetValue<double>() != 0,
            JsonValueKind.String => node.GetValue<string>().Length > 0,
            _ => true,
        };
    }

    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;

    private static string? StringValue(JsonNode? node) =>
        node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static bool? BoolValue(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>JS <c>String(value)</c>.</summary>
    private static string JsString(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => FormatNumber(node.GetValue<double>()),
            JsonValueKind.Array => string.Join(",", ((JsonArray)node).Select(item => item is null ? "" : JsString(item))),
            _ => "[object Object]",
        };
    }

    private static string FormatNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Deep copy in JS key order (array-index keys first, ascending).</summary>
    private static JsonNode? Clone(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var key in OrderedKeys(obj))
                {
                    copy[key] = Clone(obj[key]);
                }

                return copy;
            }
            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(Clone(item));
                }

                return copy;
            }
            default:
                return node.DeepClone();
        }
    }

    private static List<string> OrderedKeys(JsonObject obj)
    {
        var indexKeys = new List<(uint Index, string Key)>();
        var otherKeys = new List<string>();
        foreach (var pair in obj)
        {
            if (TryArrayIndex(pair.Key, out var index))
            {
                indexKeys.Add((index, pair.Key));
            }
            else
            {
                otherKeys.Add(pair.Key);
            }
        }

        indexKeys.Sort((left, right) => left.Index.CompareTo(right.Index));
        var keys = indexKeys.Select(entry => entry.Key).ToList();
        keys.AddRange(otherKeys);
        return keys;
    }

    private static bool TryArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10 || (key.Length > 1 && key[0] == '0'))
        {
            return false;
        }

        return uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index != uint.MaxValue;
    }

    /// <summary>JS <c>encodeURIComponent</c>.</summary>
    private static string EncodeUriComponent(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var ch = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(ch) || "-_.!~*'()".Contains(ch)))
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
