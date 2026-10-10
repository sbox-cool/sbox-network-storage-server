using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dashboard form/JSON mutations for endpoints.json (ported from legacy server
/// endpoint-modules/management-routes.js routeCreateEndpoint / routeEditEndpoint /
/// routeDeleteEndpoint). Persists the full endpoint object — including
/// <c>steps</c>, <c>input</c>, <c>response</c>, and <c>let</c> — so the native
/// The store executor (which reads the stored definition verbatim, never a
/// recompiled source) serves the saved version.
///
/// The .NET runtime has no YAML source compiler, so the client-supplied compiled
/// <c>definition</c> (steps/input/response built by the visual/JSON editor) is the
/// authoritative execution shape; source-authoring metadata
/// (<c>sourceText</c>/<c>authoringMode</c>/<c>sourceFormat</c>/<c>sourcePath</c>)
/// is preserved verbatim so the editor round-trips the YAML on reload.
///
/// Without these handlers the editor's "Push to Live" POST was silently dropped —
/// the controller funnelled it through <c>UpdateProjectSettingsAsync</c>, whose
/// switch had no <c>endpoint-*</c> case, so it bumped the project timestamp and
/// returned <c>ok:true</c> while persisting nothing.
/// </summary>
internal static partial class EndpointDashboardMutations
{
    private const int MaxEndpoints = 50;
    private const int MaxSteps = 500; // ENDPOINT_LIMITS.maxSteps
    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.None, 100)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("-+", RegexOptions.None, 100)]
    private static partial Regex DashRun();

    public static async Task CreateEndpointAsync(
        IWorkspaceStore client,
        long storageOwnerUserId,
        string projectId,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var name = (form.GetValueOrDefault("name") ?? "").Trim();
        var definition = ParseDefinition(form.GetValueOrDefault("definition"));
        var isSourceMode = IsSourceMode(form);

        if (string.IsNullOrEmpty(name) && !isSourceMode)
            throw new InvalidOperationException("Endpoint name is required.");

        var slug = ResolveSlug(form.GetValueOrDefault("slug"), name);
        if (string.IsNullOrEmpty(slug))
            throw new InvalidOperationException("URL slug is required (lowercase alphanumeric and hyphens only).");
        if (!SlugPattern().IsMatch(slug))
            throw new InvalidOperationException("URL slug must use lowercase letters, numbers, and hyphens only.");

        var endpoints = await LoadEndpointsAsync(client, storageOwnerUserId, projectId, cancellationToken);
        if (endpoints.Count >= MaxEndpoints)
            throw new InvalidOperationException("Maximum 50 endpoints per project.");
        if (endpoints.Any(e => string.Equals(ReadString(e, "slug"), slug, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Endpoint slug \"{slug}\" already exists.");

        ValidateStepCount(definition);

        var endpoint = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = GenerateId(),
            ["name"] = string.IsNullOrEmpty(name) ? "Source Endpoint" : Truncate(name, 64),
            ["slug"] = slug,
            ["method"] = NormalizeMethod(form.GetValueOrDefault("method")),
            ["description"] = Truncate((form.GetValueOrDefault("description") ?? "").Trim(), 256),
            // legacy server create always provisions an enabled endpoint; the disable toggle lives in edit.
            ["enabled"] = true,
            ["_deprecated"] = IsChecked(form.GetValueOrDefault("deprecated")),
            ["skipSboxAuth"] = IsChecked(form.GetValueOrDefault("skipSboxAuth")),
            ["requiresSecretKey"] = IsChecked(form.GetValueOrDefault("requiresSecretKey")),
            ["exposure"] = ResolveExposure(form, definition, existing: null),
            ["input"] = DefinitionProperty(definition, "input") ?? DefaultInput(),
            ["steps"] = DefinitionProperty(definition, "steps") ?? EmptyArray(),
            ["response"] = DefinitionProperty(definition, "response") ?? DefaultResponse(),
            ["createdAt"] = NowIso(),
        };

        ApplyDefinitionExtras(endpoint, definition, form);
        ApplySourceMetadata(endpoint, isSourceMode, form, slug);

        endpoints.Add(endpoint);
        await SaveEndpointsAsync(client, storageOwnerUserId, projectId, endpoints, cancellationToken);
    }

    public static async Task UpdateEndpointAsync(
        IWorkspaceStore client,
        long storageOwnerUserId,
        string projectId,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var endpointId = form.GetValueOrDefault("endpointId");
        if (string.IsNullOrEmpty(endpointId))
            throw new InvalidOperationException("Endpoint ID is required for update.");

        var endpoints = await LoadEndpointsAsync(client, storageOwnerUserId, projectId, cancellationToken);
        var idx = endpoints.FindIndex(e => string.Equals(ReadString(e, "id"), endpointId, StringComparison.Ordinal));
        if (idx < 0)
            throw new InvalidOperationException("Endpoint not found.");

        var endpoint = endpoints[idx];
        var definition = ParseDefinition(form.GetValueOrDefault("definition"));
        var isSourceMode = IsSourceMode(form);

        // Apply top-level fields only when the payload provides them (mirrors legacy server's
        // `body.x !== undefined` guards), so a partial save never wipes settings.
        if (form.TryGetValue("name", out var name))
        {
            var trimmed = name.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new InvalidOperationException("Endpoint name is required.");
            endpoint["name"] = Truncate(trimmed, 64);
        }
        if (form.TryGetValue("description", out var description))
            endpoint["description"] = Truncate(description.Trim(), 256);
        if (form.TryGetValue("enabled", out var enabled))
            endpoint["enabled"] = IsChecked(enabled);
        if (form.TryGetValue("deprecated", out var deprecated))
            endpoint["_deprecated"] = IsChecked(deprecated);
        if (form.TryGetValue("skipSboxAuth", out var skipSboxAuth))
            endpoint["skipSboxAuth"] = IsChecked(skipSboxAuth);
        if (form.TryGetValue("requiresSecretKey", out var requiresSecretKey))
            endpoint["requiresSecretKey"] = IsChecked(requiresSecretKey);
        if (form.ContainsKey("exposure") || form.ContainsKey("internalOnly"))
            endpoint["exposure"] = ResolveExposure(form, definition, endpoint);

        ValidateStepCount(definition);

        // Merge the compiled definition. Steps/input/response are the execution shape
        // the store executor reads; only overwrite when present so a definition-less
        // metadata save preserves them.
        var steps = DefinitionProperty(definition, "steps");
        if (steps is not null) endpoint["steps"] = steps;
        var input = DefinitionProperty(definition, "input");
        if (input is not null) endpoint["input"] = input;
        var response = DefinitionProperty(definition, "response");
        if (response is not null) endpoint["response"] = response;

        ApplyDefinitionExtras(endpoint, definition, form);
        ApplySourceMetadata(endpoint, isSourceMode, form, ReadString(endpoint, "slug") ?? endpointId);

        endpoint["updatedAt"] = NowIso();
        endpoints[idx] = endpoint;

        var publishTarget = (form.GetValueOrDefault("publishTarget") ?? "live").Trim().ToLowerInvariant();
        if (publishTarget == "staged" && await RevisionOverrides.HasRevisionDataAsync(client, storageOwnerUserId, projectId, cancellationToken))
        {
            // Staged saves never touch the live store — they overlay onto the next
            // revision via revision-overrides.json, keyed by slug (matches legacy server).
            var staged = new StagedRevisionWrites();
            staged.Endpoints[ReadString(endpoint, "slug") ?? endpointId] = JsonSerializer.SerializeToNode(endpoint)!.AsObject();
            await RevisionOverrides.StageAsync(client, storageOwnerUserId, projectId, staged, cancellationToken);
        }
        else
        {
            await SaveEndpointsAsync(client, storageOwnerUserId, projectId, endpoints, cancellationToken);
        }
    }

    public static async Task DeleteEndpointAsync(
        IWorkspaceStore client,
        long storageOwnerUserId,
        string projectId,
        string endpointId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(endpointId)) return;
        var endpoints = await LoadEndpointsAsync(client, storageOwnerUserId, projectId, cancellationToken);
        var removed = endpoints.RemoveAll(e => string.Equals(ReadString(e, "id"), endpointId, StringComparison.Ordinal));
        if (removed > 0)
            await SaveEndpointsAsync(client, storageOwnerUserId, projectId, endpoints, cancellationToken);
    }

    public static async Task DeleteAllEndpointsAsync(
        IWorkspaceStore client,
        long storageOwnerUserId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var endpoints = await LoadEndpointsAsync(client, storageOwnerUserId, projectId, cancellationToken);
        if (endpoints.Count == 0) return;
        await SaveEndpointsAsync(client, storageOwnerUserId, projectId, new List<Dictionary<string, object?>>(), cancellationToken);
    }

    // ── Persistence ──

    private static async Task<List<Dictionary<string, object?>>> LoadEndpointsAsync(
        IWorkspaceStore client, long storageOwnerUserId, string projectId, CancellationToken cancellationToken)
    {
        var existing = await client.GetProjectResourceAsync<List<Dictionary<string, object?>>>(
            storageOwnerUserId, projectId, "endpoints.json", cancellationToken);
        return existing?.ToList() ?? new List<Dictionary<string, object?>>();
    }

    private static async Task SaveEndpointsAsync(
        IWorkspaceStore client, long storageOwnerUserId, string projectId,
        List<Dictionary<string, object?>> endpoints, CancellationToken cancellationToken)
    {
        // Dual-write the canonical resource (authoritative; routed to the store by the
        // metadata client) and the legacy v3 mirror, matching CollectionDashboardMutations.
        await client.PutProjectResourceAsync(
            storageOwnerUserId, projectId, "endpoints.json", endpoints, cancellationToken);
        await client.PutProjectResourceAsync(
            storageOwnerUserId, projectId, "_config/endpoints.v3.json", endpoints, cancellationToken);

        try
        {
            var verified = await LoadEndpointsAsync(client, storageOwnerUserId, projectId, cancellationToken);
            if (verified.Count != endpoints.Count)
                Console.Error.WriteLine($"[SaveEndpointsAsync] Verification failed: count mismatch (expected {endpoints.Count}, got {verified.Count})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SaveEndpointsAsync] Verification read failed: {ex.Message}");
        }
    }

    // ── Field shaping ──

    private static void ApplyDefinitionExtras(
        Dictionary<string, object?> endpoint, JsonElement? definition, IReadOnlyDictionary<string, string> form)
    {
        // `let` aliases are part of the execution shape but live outside the
        // input/steps/response triple; carry them through when present.
        var letNode = DefinitionProperty(definition, "let");
        if (letNode is not null) endpoint["let"] = letNode;

        // Notes precedence mirrors legacy server: explicit form value first, then the definition.
        if (form.TryGetValue("notes", out var formNotes))
        {
            var trimmed = formNotes.Trim();
            if (string.IsNullOrEmpty(trimmed)) endpoint.Remove("notes");
            else endpoint["notes"] = Truncate(trimmed, 16000);
        }
        else if (definition is { } def && def.ValueKind == JsonValueKind.Object
            && def.TryGetProperty("notes", out var defNotes) && defNotes.ValueKind == JsonValueKind.String)
        {
            var trimmed = (defNotes.GetString() ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed)) endpoint.Remove("notes");
            else endpoint["notes"] = Truncate(trimmed, 16000);
        }
    }

    private static void ApplySourceMetadata(
        Dictionary<string, object?> endpoint, bool isSourceMode, IReadOnlyDictionary<string, string> form, string slug)
    {
        if (!isSourceMode) return;
        endpoint["authoringMode"] = "source";
        var format = (form.GetValueOrDefault("sourceFormat") ?? "").Trim();
        endpoint["sourceFormat"] = string.IsNullOrEmpty(format) ? "yaml" : format;
        var sourceText = form.GetValueOrDefault("sourceText");
        if (!string.IsNullOrEmpty(sourceText)) endpoint["sourceText"] = sourceText;
        var sourcePath = (form.GetValueOrDefault("sourcePath") ?? "").Trim();
        endpoint["sourcePath"] = string.IsNullOrEmpty(sourcePath) ? $"{slug}.endpoint.yaml" : sourcePath;
    }

    // ── Helpers ──

    private static bool IsSourceMode(IReadOnlyDictionary<string, string> form)
    {
        var mode = (form.GetValueOrDefault("authoringMode") ?? "").Trim();
        if (string.Equals(mode, "source", StringComparison.OrdinalIgnoreCase)) return true;
        var format = (form.GetValueOrDefault("sourceFormat") ?? "").Trim();
        if (string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "yml", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(form.GetValueOrDefault("sourceText"));
    }

    private static JsonElement? ParseDefinition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            // Deserialize<JsonElement> returns a self-contained (cloned) element, so
            // extracted sub-properties stay valid after this method returns.
            var element = JsonSerializer.Deserialize<JsonElement>(raw);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? DefinitionProperty(JsonElement? definition, string name)
    {
        if (definition is { } def && def.ValueKind == JsonValueKind.Object
            && def.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return value;
        return null;
    }

    private static void ValidateStepCount(JsonElement? definition)
    {
        var steps = DefinitionProperty(definition, "steps");
        if (steps is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > MaxSteps)
            throw new InvalidOperationException($"Maximum {MaxSteps} steps per endpoint.");
    }

    private static string ResolveExposure(
        IReadOnlyDictionary<string, string> form, JsonElement? definition, Dictionary<string, object?>? existing)
    {
        if (form.TryGetValue("exposure", out var exposure))
            return string.Equals(exposure, "internal", StringComparison.OrdinalIgnoreCase) ? "internal" : "public";
        if (IsChecked(form.GetValueOrDefault("internalOnly"))) return "internal";
        if (DefinitionProperty(definition, "exposure") is { ValueKind: JsonValueKind.String } e
            && string.Equals(e.GetString(), "internal", StringComparison.OrdinalIgnoreCase)) return "internal";
        if (existing is not null && string.Equals(ReadString(existing, "exposure"), "internal", StringComparison.OrdinalIgnoreCase))
            return "internal";
        return "public";
    }

    private static string ResolveSlug(string? rawSlug, string name)
    {
        var slug = (rawSlug ?? "").Trim();
        if (!string.IsNullOrEmpty(slug)) return slug.ToLowerInvariant();
        var fromName = new string((name ?? "").ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        fromName = DashRun().Replace(fromName, "-").Trim('-');
        return string.IsNullOrEmpty(fromName) ? "endpoint" : fromName;
    }

    private static string NormalizeMethod(string? method)
    {
        var m = (method ?? "").Trim().ToUpperInvariant();
        return m switch
        {
            "GET" or "POST" or "PUT" or "PATCH" or "DELETE" => m,
            _ => "POST",
        };
    }

    private static object DefaultInput()
        => new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "object", ["properties"] = new Dictionary<string, object?>() };

    private static object EmptyArray() => new List<object?>();

    private static object DefaultResponse()
        => new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = 200, ["body"] = new Dictionary<string, object?> { ["ok"] = true } };

    private static string NowIso() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static string GenerateId() => Guid.NewGuid().ToString("N")[..16];

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    private static bool IsChecked(string? value)
        => string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(Dictionary<string, object?> obj, string key)
    {
        if (!obj.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => value.ToString(),
        };
    }
}
