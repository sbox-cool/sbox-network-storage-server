using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Holds every endpoint record read until <paramref name="participants"/> reads have happened, so concurrent endpoint
/// calls all observe the same version before any of them writes. Forces the CAS conflict path deterministically.
/// Do not combine with the write queue: the queue serializes the very calls this barrier waits for.
/// </summary>
internal sealed class ReadBarrierDataSource(StoreEndpointDataSource inner, int participants) : IEndpointDataSource, IEndpointSnapshotDataSource
{
    private static readonly TimeSpan MeetTimeout = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readers;

    public async Task<EndpointRecordSnapshot> ReadRecordSnapshotAsync(string projectId, string collectionId, string key, CancellationToken ct)
    {
        var snapshot = await inner.ReadRecordSnapshotAsync(projectId, collectionId, key, ct);
        if (Interlocked.Increment(ref _readers) == participants) _allRead.TrySetResult();
        // A timeout fails the test loudly instead of hanging when fewer than `participants` calls arrive.
        await _allRead.Task.WaitAsync(MeetTimeout, ct);
        return snapshot;
    }

    public Task<IReadOnlyList<EndpointRecordRead>> ScanCollectionSnapshotsAsync(string projectId, string collectionId, CancellationToken ct)
        => inner.ScanCollectionSnapshotsAsync(projectId, collectionId, ct);
    public Task<IEndpointWriteTransaction> BeginCheckedWriteTransactionAsync(string projectId, EndpointReadSet readSet, CancellationToken ct)
        => inner.BeginCheckedWriteTransactionAsync(projectId, readSet, ct);
    public Task<Dictionary<string, object?>?> ReadEndpointDefinitionAsync(string projectId, string endpointSlug, CancellationToken ct)
        => inner.ReadEndpointDefinitionAsync(projectId, endpointSlug, ct);
    public Task<IReadOnlyList<Dictionary<string, object?>>> ListCollectionsAsync(string projectId, CancellationToken ct)
        => inner.ListCollectionsAsync(projectId, ct);
    public Task<object?> ReadRecordAsync(string projectId, string collectionId, string key, CancellationToken ct)
        => inner.ReadRecordAsync(projectId, collectionId, key, ct);
    public Task<IReadOnlyList<object?>> ScanCollectionAsync(string projectId, string collectionId, CancellationToken ct)
        => inner.ScanCollectionAsync(projectId, collectionId, ct);
    public Task<Dictionary<string, object?>?> ReadWorkflowDefinitionAsync(string projectId, string workflowId, CancellationToken ct)
        => inner.ReadWorkflowDefinitionAsync(projectId, workflowId, ct);
    public Task<IReadOnlyList<string>> ListWorkflowIdsAsync(string projectId, CancellationToken ct)
        => inner.ListWorkflowIdsAsync(projectId, ct);
    public Task<IReadOnlyList<string>> ListEndpointIdsAsync(string projectId, CancellationToken ct)
        => inner.ListEndpointIdsAsync(projectId, ct);
    public Task<IEndpointWriteTransaction> BeginWriteTransactionAsync(CancellationToken ct) => inner.BeginWriteTransactionAsync(ct);
    public Task<object?> ReadGlobalRecordAsync(string projectId, string collectionId, string recordId, CancellationToken ct)
        => inner.ReadGlobalRecordAsync(projectId, collectionId, recordId, ct);
    public Task WriteGlobalRecordAsync(string projectId, string collectionId, string recordId, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
        => inner.WriteGlobalRecordAsync(projectId, collectionId, recordId, payload, ct);
    public Task<bool> IsLegacyPlayerProjectionsEnabledAsync(string projectId, CancellationToken ct)
        => inner.IsLegacyPlayerProjectionsEnabledAsync(projectId, ct);
    public Task<bool> IsWebhookOutboxEnabledAsync(string projectId, CancellationToken ct)
        => inner.IsWebhookOutboxEnabledAsync(projectId, ct);
}
