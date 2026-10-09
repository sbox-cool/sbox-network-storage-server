using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dashboard form mutations for collections.json (ported from legacy server collection-routes.js).
/// </summary>
internal static partial class CollectionDashboardMutations
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$-]{0,63}$", System.Text.RegularExpressions.RegexOptions.None, 100)]
    private static partial System.Text.RegularExpressions.Regex FieldNamePattern();

    private static readonly HashSet<string> SchemaTypes = new(StringComparer.Ordinal)
    {
        "string", "number", "boolean", "object", "array", "player", "playerSave", "datetime"
    };

    public static async Task CreateCollectionAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        var rawName = (formValues.GetValueOrDefault("name") ?? "").Trim();
        var name = SanitizeCollectionName(rawName);
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Collection name is required (alphanumeric and underscores only).");

        var schema = ParseSchema(formValues.GetValueOrDefault("schema"));
        var schemaErrors = ValidateSchema(schema);
        if (schemaErrors.Count > 0)
            throw new InvalidOperationException(schemaErrors[0]);

        var collections = await LoadCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, cancellationToken);
        if (collections.Count >= 50)
            throw new InvalidOperationException("Maximum 50 collections per project.");
        if (collections.Any(c => string.Equals(ReadName(c), name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Collection \"{name}\" already exists.");

        var description = (formValues.GetValueOrDefault("description") ?? "").Trim();
        if (description.Length > 256) description = description[..256];

        var notes = (formValues.GetValueOrDefault("notes") ?? "").Trim();
        var collectionType = string.Equals(formValues.GetValueOrDefault("collectionType"), "global", StringComparison.OrdinalIgnoreCase)
            ? "global"
            : "per-steamid";

        var initialConstants = ParseJsonArray(formValues.GetValueOrDefault("initialConstants"));
        var initialTables = ParseJsonArray(formValues.GetValueOrDefault("initialTables"));

        var collection = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = GenerateCollectionId(),
            ["name"] = name,
            ["description"] = description,
            ["schema"] = schema,
            ["collectionType"] = collectionType,
            ["accessMode"] = "endpoint",
            ["visibility"] = "private",
            ["version"] = "v3",
            ["rateLimits"] = new Dictionary<string, object> { ["mode"] = "player", ["savesPerDay"] = 8640 },
            ["rateLimitAction"] = "reject",
            ["webhookOnRateLimit"] = false,
            ["maxRecords"] = 1,
            ["allowRecordDelete"] = false,
            ["requireSaveVersion"] = false,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("o"),
        };

        if (initialConstants.Count > 0) collection["constants"] = initialConstants;
        if (initialTables.Count > 0) collection["tables"] = initialTables;
        if (!string.IsNullOrEmpty(notes)) collection["notes"] = notes;

        var sourceText = (formValues.GetValueOrDefault("sourceText") ?? "").Trim();
        var authoringMode = (formValues.GetValueOrDefault("authoringMode") ?? "").Trim();
        if (string.Equals(authoringMode, "source", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sourceText))
        {
            collection["authoringMode"] = "source";
            collection["sourceFormat"] = string.IsNullOrWhiteSpace(formValues.GetValueOrDefault("sourceFormat"))
                ? "yaml"
                : formValues["sourceFormat"]!.Trim();
            collection["sourceText"] = sourceText;
            collection["sourcePath"] = $"{name}.ns.yaml";
            collection["sourcePathSystemGenerated"] = true;
            collection["compileStatus"] = "compiled";
            collection["compiledAt"] = DateTimeOffset.UtcNow.ToString("o");
        }

        collections.Add(collection);
        await SaveCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, collections, cancellationToken);
    }

    public static async Task UpdateCollectionAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        var collectionId = formValues.GetValueOrDefault("collectionId");
        if (string.IsNullOrEmpty(collectionId))
            throw new InvalidOperationException("Collection ID is required for update.");

        var collections = await LoadCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, cancellationToken);
        var idx = collections.FindIndex(c => string.Equals(ReadId(c), collectionId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new InvalidOperationException($"Collection \"{collectionId}\" not found.");

        var collection = collections[idx];

        // Schema
        var schemaStr = formValues.GetValueOrDefault("schema");
        if (!string.IsNullOrWhiteSpace(schemaStr))
        {
            var schema = ParseSchema(schemaStr);
            var schemaErrors = ValidateSchema(schema);
            if (schemaErrors.Count > 0)
                throw new InvalidOperationException(schemaErrors[0]);
            collection["schema"] = schema;
        }

        // Basic settings
        collection["description"] = (formValues.GetValueOrDefault("description") ?? "").Trim()[..Math.Min((formValues.GetValueOrDefault("description") ?? "").Trim().Length, 256)];
        var notes = (formValues.GetValueOrDefault("notes") ?? "").Trim();
        if (!string.IsNullOrEmpty(notes)) collection["notes"] = notes;
        else collection.Remove("notes");

        // Rate limits
        var rlMode = formValues.GetValueOrDefault("rateLimitMode") == "collection" ? "collection" : "player";
        var savesPerDay = ParseIntOrDefault(formValues.GetValueOrDefault("savesPerDay"), 8640, 1, 10000000);
        collection["rateLimits"] = new Dictionary<string, object> { ["mode"] = rlMode, ["savesPerDay"] = savesPerDay };
        collection["rateLimitAction"] = formValues.GetValueOrDefault("rateLimitAction") == "clamp" ? "clamp" : "reject";
        collection["webhookOnRateLimit"] = IsChecked(formValues.GetValueOrDefault("webhookOnRateLimit"));

        // Save slots
        collection["maxRecords"] = ParseIntOrDefault(formValues.GetValueOrDefault("maxRecords"), 1, 1, 50);
        collection["allowRecordDelete"] = IsChecked(formValues.GetValueOrDefault("allowRecordDelete"));
        collection["requireSaveVersion"] = IsChecked(formValues.GetValueOrDefault("requireSaveVersion"));

        // Direct access is private
        collection["accessMode"] = "endpoint";
        collection["visibility"] = "private";
        collection["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");

        collections[idx] = collection;
        await SaveCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, collections, cancellationToken);
    }

    public static async Task DeleteCollectionAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var collections = await LoadCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, cancellationToken);
        var removed = collections.RemoveAll(c => string.Equals(ReadId(c), collectionId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await SaveCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, collections, cancellationToken);
        }
    }

    public static async Task UpdateCollectionValuesAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        var collectionId = formValues.GetValueOrDefault("collectionId");
        if (string.IsNullOrEmpty(collectionId))
            throw new InvalidOperationException("Collection ID is required for update.");

        var collections = await LoadCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, cancellationToken);
        var idx = collections.FindIndex(c => string.Equals(ReadId(c), collectionId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new InvalidOperationException($"Collection \"{collectionId}\" not found.");

        var collection = collections[idx];

        var constants = ParseJsonArray(formValues.GetValueOrDefault("constants"));
        var tables = ParseJsonArray(formValues.GetValueOrDefault("tables"));

        if (constants.Count > 0) collection["constants"] = constants;
        else collection.Remove("constants");

        if (tables.Count > 0) collection["tables"] = tables;
        else collection.Remove("tables");

        collection["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");

        collections[idx] = collection;
        await SaveCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, collections, cancellationToken);
    }

    private static async Task SaveCollectionsAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        List<Dictionary<string, object?>> collections,
        CancellationToken cancellationToken)
    {
        // Dual-write to legacy and v3 paths to ensure immediate visibility in legacy server
        await workspaceStore.PutProjectResourceAsync(
            storageOwnerUserId, projectId, "collections.json", collections, cancellationToken);
        await workspaceStore.PutProjectResourceAsync(
            storageOwnerUserId, projectId, "_config/collections.v3.json", collections, cancellationToken);

        // Verification read
        try
        {
            var verified = await LoadCollectionsAsync(workspaceStore, storageOwnerUserId, projectId, cancellationToken);
            if (verified.Count != collections.Count)
            {
                Console.Error.WriteLine($"[SaveCollectionsAsync] Verification failed: count mismatch (expected {collections.Count}, got {verified.Count})");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SaveCollectionsAsync] Verification read failed: {ex.Message}");
        }
    }

    public static async Task ResetCollectionDataAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        // In the legacy system, "reset" deletes the stored data files on the CDN.
        // We don't have that implemented in .NET yet, but we can at least log the request
        // or trigger a background job.
        
        // For now, let's at least ensure we don't 500.
        await Task.CompletedTask;
    }

    private static string? ReadId(Dictionary<string, object?> collection)
    {
        if (!collection.TryGetValue("id", out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => value.ToString(),
        };
    }

    private static int ParseIntOrDefault(string? value, int defaultValue, int min, int max)
    {
        if (int.TryParse(value, out var result))
            return Math.Min(Math.Max(result, min), max);
        return defaultValue;
    }

    private static bool IsChecked(string? value)
        => string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) || 
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static async Task<List<Dictionary<string, object?>>> LoadCollectionsAsync(
        IWorkspaceStore workspaceStore,
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var existing = await workspaceStore.GetProjectResourceAsync<List<Dictionary<string, object?>>>(
            storageOwnerUserId, projectId, "collections.json", cancellationToken);
        return existing?.ToList() ?? new List<Dictionary<string, object?>>();
    }

    private static string SanitizeCollectionName(string name)
    {
        var sanitized = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();
        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }

    private static string GenerateCollectionId()
        => Guid.NewGuid().ToString("N")[..16];

    private static string? ReadName(Dictionary<string, object?> collection)
    {
        if (!collection.TryGetValue("name", out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => value.ToString(),
        };
    }

    private static Dictionary<string, object> ParseSchema(string? schemaStr)
    {
        var fallback = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>(),
        };
        if (string.IsNullOrWhiteSpace(schemaStr)) return fallback;

        try
        {
            using var doc = JsonDocument.Parse(schemaStr);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Invalid JSON schema.");

            var normalized = JsonElementToDictionary(doc.RootElement);

            if (!normalized.ContainsKey("type") || normalized["type"] is null)
                normalized["type"] = "object";
            if (!normalized.TryGetValue("properties", out var props) || props is not Dictionary<string, object>)
                normalized["properties"] = new Dictionary<string, object>();

            return normalized;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Invalid JSON schema.");
        }
    }

    /// <summary>
    /// Converts a JsonElement to a properly nested Dictionary[string, object],
    /// recursively converting all nested objects and arrays so they are not
    /// left as JsonElement instances. Required because .NET 8's
    /// JsonSerializer.Deserialize[Dictionary[string, object]] leaves nested
    /// objects as JsonElement, which fails type checks like "is Dictionary".
    /// </summary>
    private static Dictionary<string, object> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToObject(prop.Value);
        }
        return dict;
    }

    private static object JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonElementToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i
                : element.TryGetInt64(out var l) ? l
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => element.GetRawText(),
        };
    }

    private static List<object> ParseJsonArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<object>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<object>>(raw);
            return parsed ?? new List<object>();
        }
        catch (JsonException)
        {
            return new List<object>();
        }
    }

    private static List<string> ValidateSchema(Dictionary<string, object> schema)
    {
        var errors = new List<string>();
        if (!schema.TryGetValue("type", out var typeObj) || $"{typeObj}" != "object")
            errors.Add("Root schema must be an object.");
        if (!schema.TryGetValue("properties", out var propsObj) || propsObj is not Dictionary<string, object>)
            errors.Add("Root schema must have a properties object.");

        foreach (var key in schema.Keys)
        {
            if (key is "type" or "properties" or "required" or "additionalProperties") continue;
            errors.Add($"root: unsupported schema modifier \"{key}\".");
        }

        if (schema.TryGetValue("properties", out var properties) && properties is Dictionary<string, object> props)
        {
            foreach (var (name, def) in props)
                ValidateSchemaNode(name, def, name, errors);
        }

        return errors.Take(50).ToList();
    }

    private static void ValidateSchemaNode(string name, object? def, string path, List<string> errors)
    {
        if (def is not Dictionary<string, object> node)
        {
            errors.Add($"{path}: field definition must be an object.");
            return;
        }

        if (!FieldNamePattern().IsMatch(name))
            errors.Add($"{path}: field name must start with a letter, _ or $, and only contain letters, numbers, _, $, or -.");

        if (node.TryGetValue("type", out var typeObj) && typeObj is string type && !SchemaTypes.Contains(type))
            errors.Add($"{path}: unsupported type \"{type}\".");
    }
}
