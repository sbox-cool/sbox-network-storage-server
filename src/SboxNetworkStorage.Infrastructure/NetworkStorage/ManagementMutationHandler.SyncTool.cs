using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Routes the editor Sync Tool relies on beyond batch push: single-resource PATCH upserts,
/// saved tests, dry-run test execution, plus the version snapshots every endpoint and
/// workflow save records.
/// </summary>
public sealed partial class ManagementMutationHandler
{
    private static readonly string[] SourceAuthoringFields =
        ["authoringMode", "sourceFormat", "sourcePath", "sourceText", "sourceVersion"];

    /// <summary>Single upsert path for endpoints, collections and workflows; endpoint and workflow saves also snapshot a version.</summary>
    private async Task UpsertDefinitionAsync(long ownerUserId, string projectId, string kind, string id, string name,
        JsonElement definition, long version, string versionSource, CancellationToken ct)
    {
        switch (kind)
        {
            case "endpoint":
                await _store.UpsertEndpointAsync(projectId, id, name,
                    GetOptionalString(definition, "method") ?? "GET",
                    GetOptionalBool(definition, "enabled") ?? true,
                    definition, GetOptionalString(definition, "versionHash"), version, ct);
                break;
            case "collection":
                await _store.UpsertCollectionAsync(projectId, id, name,
                    GetOptionalString(definition, "visibility") ?? "private", definition, version, ct);
                return;
            case "workflow":
                await _store.UpsertWorkflowAsync(projectId, id, name, definition, GetOptionalString(definition, "versionHash"), version, ct);
                break;
            default:
                throw new ArgumentException($"Unknown resource kind '{kind}'.");
        }
        await RecordVersionAsync(ownerUserId, projectId, kind, id, definition, versionSource, version, ct);
    }

    private Task RecordVersionAsync(long ownerUserId, string projectId, string kind, string id, JsonElement definition,
        string source, long version, CancellationToken ct)
        => ManagementProjectObjects.RecordVersionAsync(_store, ownerUserId, projectId, kind, id, definition, source, version, ct);

    // ── PATCH /endpoints | /collections | /workflows ──

    /// <summary>
    /// Upserts one resource without touching the others (legacy server <c>routeManagePatch*</c>):
    /// body <c>{ endpoint|collection|workflow: {...} }</c>. Source-backed payloads replace the
    /// stored definition; plain payloads are shallow-merged over it. A next-targeted endpoint or
    /// collection merges over its staged copy (else the live one) and is staged.
    /// </summary>
    private async Task<NetworkStorageResult> PatchResourceAsync(
        NetworkStorageRequest request, string projectId, long ownerUserId, string kind)
    {
        if (!TryParseBody(request.Body, out var document, out var parseError))
            return PatchFailure(kind, null, ManagementMutationConstants.ValidationFailedCode, parseError);
        using (document)
        {
            // The Sync Tool single-resource push sends the resource itself; bulk
            // shapes may nest it under the kind. Accept both.
            var incoming = document.RootElement;
            if (incoming.ValueKind == JsonValueKind.Object && incoming.TryGetProperty(kind, out var nested)
                && nested.ValueKind == JsonValueKind.Object && !incoming.TryGetProperty("id", out _))
                incoming = nested;
            if (incoming.ValueKind != JsonValueKind.Object)
                return PatchFailure(kind, null, ManagementMutationConstants.ValidationFailedCode, $"Body must contain a {kind} object.");

            if (!NetworkStorageSourceResourceCompiler.TryCompile(incoming, kind, out var compiled, out var compileError))
                return PatchFailure(kind, ResolveSyncId(kind, incoming) ?? GetOptionalString(incoming, "sourcePath"),
                    "SOURCE_COMPILE_FAILED", compileError ?? "Source compilation failed.");

            var requestedId = ResolveSyncId(kind, compiled);
            if (string.IsNullOrWhiteSpace(requestedId))
                return PatchFailure(kind, null, ManagementMutationConstants.ValidationFailedCode,
                    kind switch { "endpoint" => "Endpoint slug is required.", "collection" => "Collection name is required.", _ => "Workflow id is required." });

            var publish = kind == "workflow" ? PublishDecision.Live : await ResolvePublishAsync(request, ownerUserId, projectId);
            var existing = publish == PublishDecision.Staged
                ? await FindStagedDefinitionAsync(ownerUserId, projectId, kind, compiled, request.CancellationToken)
                    ?? await FindExistingDefinitionAsync(projectId, kind, compiled, request.CancellationToken)
                : await FindExistingDefinitionAsync(projectId, kind, compiled, request.CancellationToken);
            var id = existing?.Id ?? requestedId;
            if (!StorageIdValidation.IsValidCollectionId(id))
                return PatchFailure(kind, id, ManagementMutationConstants.ValidationFailedCode,
                    "Resource ids may contain only letters, numbers, underscores or hyphens (maximum 128 characters).");

            var merged = MergeDefinition(existing?.Definition, compiled, id);
            var name = kind == "endpoint" ? GetOptionalString(merged, "slug") ?? id : GetOptionalString(merged, "name") ?? id;
            var resourceId = kind == "workflow" ? id : name;
            if (existing is { } current && current.Definition.GetRawText() == merged.GetRawText())
                return ReportPublishTarget(
                    ProductionOkResult(new { ok = true, source = "candidate", resourceKind = kind, resourceId, id, action = "unchanged", skipped = true }),
                    publish);

            if (publish == PublishDecision.Staged)
            {
                var staged = new StagedRevisionWrites();
                AddStaged(staged, kind, id, name, merged);
                await StageAsync(ownerUserId, projectId, staged, request.CancellationToken);
            }
            else
            {
                await UpsertDefinitionAsync(ownerUserId, projectId, kind, id, name, merged, NextVersion(), "patch", request.CancellationToken);
            }
            return ReportPublishTarget(ProductionOkResult(new
            {
                ok = true, source = "candidate", resourceKind = kind, resourceId, id,
                action = existing is null ? "created" : "updated",
            }), publish);
        }
    }

    private async Task<(string Id, JsonElement Definition)?> FindExistingDefinitionAsync(
        string projectId, string kind, JsonElement compiled, CancellationToken ct)
    {
        var rows = kind switch
        {
            "endpoint" => await _store.ListEndpointsAsync(projectId, ct),
            "collection" => await _store.ListCollectionsAsync(projectId, ct),
            _ => await _store.ListWorkflowsAsync(projectId, ct),
        };
        var idColumn = kind + "_id";
        var nameField = kind == "endpoint" ? "slug" : "name";
        var id = GetOptionalString(compiled, "id");
        var name = GetOptionalString(compiled, nameField);
        var match = rows.FirstOrDefault(row => id is not null && GetOptionalString(row, idColumn) == id);
        if (match.ValueKind != JsonValueKind.Object)
            match = rows.FirstOrDefault(row => name is not null && GetOptionalString(row, nameField) == name);
        if (match.ValueKind != JsonValueKind.Object || GetOptionalString(match, idColumn) is not { } existingId) return null;
        return (existingId, ReadDefinitionColumn(match));
    }

    private static JsonElement ReadDefinitionColumn(JsonElement row)
    {
        if (!row.TryGetProperty("definition_json", out var definition)) return JsonSerializer.SerializeToElement(new { });
        if (definition.ValueKind == JsonValueKind.String)
        {
            using var parsed = JsonDocument.Parse(definition.GetString() ?? "{}");
            return parsed.RootElement.Clone();
        }
        return definition.Clone();
    }

    private static JsonElement MergeDefinition(JsonElement? existing, JsonElement compiled, string id)
    {
        var sourceMode = compiled.TryGetProperty("sourceText", out var sourceText) && sourceText.ValueKind == JsonValueKind.String;
        var merged = new JsonObject();
        if (!sourceMode && existing is { ValueKind: JsonValueKind.Object } previous)
        {
            // A compiled payload without source replaces the source metadata too; keeping
            // stale sourceText would make the dashboard show source that no longer matches.
            foreach (var property in previous.EnumerateObject())
                if (!SourceAuthoringFields.Contains(property.Name, StringComparer.Ordinal))
                    merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        foreach (var property in compiled.EnumerateObject())
            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        merged["id"] = id;
        return JsonSerializer.SerializeToElement(merged);
    }

    private static NetworkStorageResult PatchFailure(string kind, string? resourceId, string code, string message) =>
        NetworkStorageResult.Error(400, code,
            new { ok = false, resourceKind = kind, resourceId, error = code, message },
            authDecision: "allowed");

    // ── PUT /tests ──

    /// <summary>Replaces the saved test list (the Sync Tool merges locally, then PUTs the full list).</summary>
    private async Task<NetworkStorageResult> PutTestsAsync(NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        if (!TryParseBody(request.Body, out var document, out var parseError))
            return ValidationFailedResult("tests", parseError);
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tests", out var nested)) root = nested;
            if (root.ValueKind != JsonValueKind.Array)
                return ValidationFailedResult("tests", "Body must be an array of tests.");
            try
            {
                var saved = await ManagementProjectObjects.WriteTestsAsync(_store, ownerUserId, projectId,
                    root.EnumerateArray().Select(test => test.Clone()).ToList(), request.CancellationToken);
                return ProductionOkResult(new { ok = true, source = "candidate", resourceKind = "tests", action = "replace", total = saved.Count });
            }
            catch (ArgumentException error)
            {
                return ValidationFailedResult("tests", error.Message);
            }
        }
    }

    // ── POST /test-endpoint, POST /run-tests ──

    private async Task<NetworkStorageResult> TestEndpointAsync(NetworkStorageRequest request, string projectId,
        long ownerUserId, string? playerKeyMode, string authDecision)
    {
        if (_endpointExecutor is null) return TestRunnerUnavailable(authDecision);
        JsonElement body;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
            body = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            return ValidationFailedResult("test-endpoint", $"Invalid JSON: {error.Message}");
        }

        ManagementEndpointTestRunner.TestSpec? spec;
        if (GetOptionalString(body, "testId") is { } testId)
        {
            var saved = (await ManagementProjectObjects.ReadTestsAsync(_store, ownerUserId, projectId, request.CancellationToken))
                .FirstOrDefault(test => GetOptionalString(test, "id") == testId);
            if (saved.ValueKind != JsonValueKind.Object)
                return NetworkStorageResult.Error(404, "TEST_NOT_FOUND",
                    new { ok = false, error = new { code = "TEST_NOT_FOUND", message = $"Saved test '{testId}' was not found." } },
                    authDecision: authDecision);
            spec = ManagementEndpointTestRunner.ReadSpec(saved);
        }
        else
        {
            spec = ManagementEndpointTestRunner.ReadSpec(body);
        }
        if (spec is null)
            return ValidationFailedResult("test-endpoint", "Provide an endpoint slug, or the testId of a saved test that names an endpoint.");

        var testContext = await TestContextAsync(request, ownerUserId, projectId);
        var outcome = await new ManagementEndpointTestRunner(_store, testContext.Executor!, testContext.Values, testContext.Overlay)
            .RunAsync(projectId, ownerUserId, playerKeyMode, spec, request.CancellationToken);
        return outcome.Found
            ? NetworkStorageResult.Ok(outcome.Body, authDecision: authDecision)
            : NetworkStorageResult.Error(404, "ENDPOINT_NOT_FOUND",
                new { ok = false, error = new { code = "ENDPOINT_NOT_FOUND", message = $"Endpoint '{spec.Slug}' was not found." } },
                authDecision: authDecision);
    }

    private async Task<NetworkStorageResult> RunSavedTestsAsync(NetworkStorageRequest request, string projectId, long ownerUserId, string? playerKeyMode,
        string authDecision, CancellationToken ct)
    {
        if (_endpointExecutor is null) return TestRunnerUnavailable(authDecision);
        var testContext = await TestContextAsync(request, ownerUserId, projectId);
        var report = await new ManagementEndpointTestRunner(_store, testContext.Executor!, testContext.Values, testContext.Overlay)
            .RunSavedAsync(projectId, ownerUserId, playerKeyMode, ct);
        return NetworkStorageResult.Ok(report, authDecision: authDecision);
    }

    private static NetworkStorageResult TestRunnerUnavailable(string authDecision) =>
        NetworkStorageResult.Error(501, "NATIVE_TEST_RUNNER_UNAVAILABLE",
            new { ok = false, error = new { code = "NATIVE_TEST_RUNNER_UNAVAILABLE", message = "The native endpoint test runner is unavailable." } },
            authDecision: authDecision);
}
