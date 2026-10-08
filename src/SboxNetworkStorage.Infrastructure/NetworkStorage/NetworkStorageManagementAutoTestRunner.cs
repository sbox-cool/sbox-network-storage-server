using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

internal static class NetworkStorageManagementAutoTestRunner
{
    private const string SyntheticSteamId = "76561198000000000";

    public static async Task<NetworkStorageCandidateResult> RunAsync(
        NetworkStorageCandidateRequest request,
        string projectId,
        long ownerUserId,
        string authDecision,
        INetworkStorageStore store,
        NativeEndpointShadowExecutor? executor)
    {
        if (executor is null)
        {
            return NetworkStorageCandidateResult.Error(
                501,
                "NATIVE_AUTO_TEST_UNAVAILABLE",
                new
                {
                    ok = false,
                    error = new { code = "NATIVE_AUTO_TEST_UNAVAILABLE", message = "The native endpoint auto-test runner is unavailable." },
                },
                authDecision: authDecision);
        }

        var requestedSlug = ReadRequestedSlug(request.Body);
        var endpoints = await store.ListEndpointsAsync(projectId, request.CancellationToken);
        var candidates = endpoints
            .Select(TryReadEndpoint)
            .Where(endpoint => endpoint is not null)
            .Select(endpoint => endpoint!.Value)
            .Where(endpoint => string.IsNullOrWhiteSpace(requestedSlug)
                || string.Equals(endpoint.Slug, requestedSlug, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(requestedSlug) && candidates.Length == 0)
        {
            return NetworkStorageCandidateResult.Error(
                400,
                "ENDPOINT_NOT_FOUND",
                new
                {
                    ok = false,
                    error = new { code = "ENDPOINT_NOT_FOUND", message = $"Endpoint '{requestedSlug}' was not found." },
                },
                authDecision: authDecision);
        }

        var results = new List<object>(candidates.Length);
        foreach (var endpoint in candidates)
        {
            var input = BuildInput(endpoint.Definition);
            EndpointExecutionResult? execution;
            try
            {
                execution = await executor.TryExecuteAsync(
                    projectId,
                    endpoint.Slug,
                    input,
                    SyntheticSteamId,
                    ownerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new Dictionary<string, object?>(),
                    hasSecretKey: true,
                    isDedicatedServer: false,
                    request.CancellationToken,
                    liveServe: false);
            }
            catch (Exception ex) when (!request.CancellationToken.IsCancellationRequested)
            {
                results.Add(BuildResult(endpoint, passed: false, [ex.Message], execution: null));
                continue;
            }

            var errors = execution switch
            {
                null => new[] { "The native executor does not support this endpoint definition." },
                { Ok: false } => new[] { $"Endpoint returned HTTP {execution.Status}." },
                _ => Array.Empty<string>(),
            };
            results.Add(BuildResult(endpoint, execution?.Ok == true, errors, execution));
        }

        if (!string.IsNullOrWhiteSpace(requestedSlug))
        {
            var single = JsonSerializer.SerializeToElement(results[0]);
            var response = new Dictionary<string, object?> { ["ok"] = true };
            foreach (var property in single.EnumerateObject())
                response[property.Name] = JsonElementToObject(property.Value);
            return NetworkStorageCandidateResult.Ok(response, authDecision: authDecision);
        }

        var passed = results.Count(result => JsonSerializer.SerializeToElement(result).GetProperty("passed").GetBoolean());
        return NetworkStorageCandidateResult.Ok(
            new
            {
                ok = true,
                results,
                summary = new { total = results.Count, passed, failed = results.Count - passed },
            },
            authDecision: authDecision);
    }

    private static object BuildResult(
        EndpointCandidate endpoint,
        bool passed,
        IReadOnlyList<string> errors,
        EndpointExecutionResult? execution) => new
        {
            name = $"Auto-test {endpoint.Slug}",
            endpoint = endpoint.Slug,
            method = endpoint.Method,
            passed,
            deprecated = endpoint.Deprecated,
            errors,
            warnings = Array.Empty<string>(),
            result = execution is null ? null : new { status = execution.Status, body = execution.Body },
        };

    private static EndpointCandidate? TryReadEndpoint(JsonElement row)
    {
        var definition = ReadDefinition(row);
        if (definition is null)
            return null;

        var slug = ReadString(row, "slug") ?? ReadString(definition.Value, "slug") ?? ReadString(definition.Value, "id");
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        return new EndpointCandidate(
            slug,
            ReadString(row, "method") ?? ReadString(definition.Value, "method") ?? "POST",
            ReadBoolean(definition.Value, "deprecated"),
            definition.Value);
    }

    private static JsonElement? ReadDefinition(JsonElement row)
    {
        if (!row.TryGetProperty("definition_json", out var definition))
            return row.ValueKind == JsonValueKind.Object ? row.Clone() : null;
        if (definition.ValueKind == JsonValueKind.Object)
            return definition.Clone();
        if (definition.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(definition.GetString()))
            return null;

        try
        {
            using var document = JsonDocument.Parse(definition.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildInput(JsonElement definition)
    {
        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!definition.TryGetProperty("input", out var schema)
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return input;
        }

        foreach (var property in properties.EnumerateObject())
        {
            input[property.Name] = property.Value.TryGetProperty("default", out var defaultValue)
                ? JsonElementToObject(defaultValue)
                : DefaultValueForSchema(property.Value);
        }
        return input;
    }

    private static object? DefaultValueForSchema(JsonElement schema) => ReadString(schema, "type") switch
    {
        "string" => "test",
        "integer" => 1L,
        "number" => 1d,
        "boolean" => true,
        "array" => Array.Empty<object>(),
        "object" => new Dictionary<string, object?>(),
        _ => null,
    };

    private static string? ReadRequestedSlug(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "slug");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? JsonElementToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonElementToObject(property.Value),
            StringComparer.OrdinalIgnoreCase),
        JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadBoolean(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.True;

    private readonly record struct EndpointCandidate(string Slug, string Method, bool Deprecated, JsonElement Definition);
}
