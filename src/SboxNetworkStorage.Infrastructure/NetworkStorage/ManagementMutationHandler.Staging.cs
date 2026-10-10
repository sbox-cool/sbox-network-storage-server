using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Staged ("Push Staged") writes: endpoint and collection pushes that target the next revision
/// (<see cref="NetworkStorageRequest.TargetsNext"/>) are validated like live writes, then written to
/// <c>revision-overrides.json</c> instead of the live store. Workflows, tests and every other
/// resource always write live.
/// </summary>
public sealed partial class ManagementMutationHandler
{
    private const string StagedFallbackReason = "no synced game package";

    private async Task<(SboxNetworkStorage.Application.NetworkStorage.Endpoints.EndpointExecutor? Executor,
        IQueryValuesContextProvider? Values,
        RevisionOverlay? Overlay)> TestContextAsync(bool targetsNext, long ownerUserId, string projectId, CancellationToken ct)
    {
        var overlay = targetsNext
            ? await RevisionOverlay.LoadAsync(_workspaceClient, ownerUserId, projectId, ct)
            : null;
        if (overlay is null) return (_endpointExecutor, _valuesProvider, null);
        var source = new Storage.StoreEndpointDataSource(_store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Storage.StoreEndpointDataSource>.Instance)
            .WithRevisionOverlay(overlay);
        var values = _valuesProvider is StoreQueryValuesContextProvider provider
            ? provider.WithRevisionOverlay(overlay) : _valuesProvider;
        return (_endpointExecutor?.WithDataSource(source), values, overlay);
    }

    /// <summary>
    /// Dry-run test runner over the staged revision when <paramref name="targetsNext"/> and the project has
    /// staged definitions, otherwise over live. Null when no endpoint executor is wired.
    /// </summary>
    public async Task<ManagementEndpointTestRunner?> CreateTestRunnerAsync(long ownerUserId, string projectId, bool targetsNext, CancellationToken ct)
    {
        if (_endpointExecutor is null) return null;
        var context = await TestContextAsync(targetsNext, ownerUserId, projectId, ct);
        return new ManagementEndpointTestRunner(_store, context.Executor!, context.Values, context.Overlay);
    }

    private enum PublishDecision
    {
        /// <summary>The request targets live: write live, response unchanged.</summary>
        Live,
        /// <summary>The request targets next and the project has revision data: stage.</summary>
        Staged,
        /// <summary>The request targets next but there is no synced game package: write live and say so.</summary>
        StagedFallback,
    }

    /// <summary>Same rule as the owner dashboard: staging needs a synced game package revision.</summary>
    private async Task<PublishDecision> ResolvePublishAsync(NetworkStorageRequest request, long ownerUserId, string projectId)
    {
        if (!request.TargetsNext) return PublishDecision.Live;
        return await RevisionOverrides.HasRevisionDataAsync(_workspaceClient, ownerUserId, projectId, request.CancellationToken)
            ? PublishDecision.Staged
            : PublishDecision.StagedFallback;
    }

    /// <summary>
    /// Reports where a next-targeted write went (<c>publishTarget</c>, plus <c>stagedFallback</c> when it
    /// went live). Live-targeted responses keep their exact shape.
    /// </summary>
    private static NetworkStorageResult ReportPublishTarget(NetworkStorageResult result, PublishDecision publish)
    {
        if (publish == PublishDecision.Live || JsonSerializer.SerializeToNode(result.Body) is not JsonObject body)
            return result;
        body["publishTarget"] = publish == PublishDecision.Staged ? NetworkStoragePublishTarget.Next : NetworkStoragePublishTarget.Live;
        if (publish == PublishDecision.StagedFallback) body["stagedFallback"] = StagedFallbackReason;
        return result with { Body = body };
    }

    /// <summary>
    /// Queues a validated definition for the staged revision, keyed by endpoint slug or collection name.
    /// The identifier fields are always present so package-sync promotion can match or insert the item.
    /// </summary>
    private static void AddStaged(StagedRevisionWrites writes, string kind, string id, string key, JsonElement definition)
    {
        var item = JsonNode.Parse(definition.GetRawText())!.AsObject();
        item["id"] ??= id;
        item[kind == "endpoint" ? "slug" : "name"] ??= key;
        (kind == "endpoint" ? writes.Endpoints : writes.Collections)[key] = item;
    }

    private Task StageAsync(long ownerUserId, string projectId, StagedRevisionWrites writes, CancellationToken ct)
        => RevisionOverrides.StageAsync(_workspaceClient, ownerUserId, projectId, writes, ct);

    /// <summary>The staged copy of an endpoint/collection matching the payload's id, then its slug/name.</summary>
    private async Task<(string Id, JsonElement Definition)?> FindStagedDefinitionAsync(
        long ownerUserId, string projectId, string kind, JsonElement compiled, CancellationToken ct)
    {
        var section = kind == "endpoint" ? RevisionOverrides.EndpointsSection : RevisionOverrides.CollectionsSection;
        var nameField = kind == "endpoint" ? "slug" : "name";
        var items = RevisionOverrides.Items(await RevisionOverrides.ReadAsync(_workspaceClient, ownerUserId, projectId, ct), section)
            .Select(pair => (Key: pair.Key, Definition: JsonSerializer.SerializeToElement(pair.Value)))
            .ToList();
        var id = GetOptionalString(compiled, "id");
        var name = GetOptionalString(compiled, nameField);
        var match = items.FirstOrDefault(item => id is not null && GetOptionalString(item.Definition, "id") == id);
        if (match.Key is null)
            match = items.FirstOrDefault(item => name is not null
                && (item.Key == name || GetOptionalString(item.Definition, nameField) == name));
        if (match.Key is null) return null;
        return (GetOptionalString(match.Definition, "id") ?? match.Key, match.Definition);
    }
}
