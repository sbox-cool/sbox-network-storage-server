using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>Per-execution state used only when a referenced collection requires save versions.</summary>
public sealed class EndpointReadSet(IReadOnlySet<string> protectedCollections)
{
    public IReadOnlySet<string> ProtectedCollections { get; } = protectedCollections;
    public Dictionary<(string Collection, string Key), EndpointRecordSnapshot> Records { get; } = new();
    public Dictionary<string, IReadOnlyList<EndpointRecordRead>> Scans { get; } = new(StringComparer.Ordinal);
}

public sealed record EndpointRecordSnapshot(object? Value, JsonElement? Row);
public sealed record EndpointRecordRead(string Key, EndpointRecordSnapshot Snapshot);

/// <summary>Optional provider capability for validating a complete read set in the commit transaction.</summary>
public interface IEndpointSnapshotDataSource
{
    Task<EndpointRecordSnapshot> ReadRecordSnapshotAsync(string projectId, string collectionId, string key, CancellationToken ct);
    Task<IReadOnlyList<EndpointRecordRead>> ScanCollectionSnapshotsAsync(string projectId, string collectionId, CancellationToken ct);
    Task<IEndpointWriteTransaction> BeginCheckedWriteTransactionAsync(string projectId, EndpointReadSet readSet, CancellationToken ct);
}

public sealed class EndpointWriteConflictException : Exception
{
    public EndpointWriteConflictException() : base("Endpoint state changed during execution.") { }
    public EndpointWriteConflictException(Exception inner) : base("Endpoint state changed during execution.", inner) { }
}
