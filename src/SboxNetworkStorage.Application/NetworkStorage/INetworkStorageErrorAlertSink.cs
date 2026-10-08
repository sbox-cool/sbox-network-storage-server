using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Alert sink for Network Storage data-plane errors (ScyllaDB failures on record
/// read/write/delete). Implementations notify an external channel (Discord) but
/// MUST NOT write to the website error archive (Postgres) — Network Storage errors
/// are air-gapped from the website database per AGENTS.md.
/// </summary>
public interface INetworkStorageErrorAlertSink
{
    Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken);
}

public sealed record NetworkStorageError(
    string ProjectId,
    string CollectionId,
    string RecordKey,
    string Operation,
    string Code,
    string Message);

public sealed class NoopNetworkStorageErrorAlertSink : INetworkStorageErrorAlertSink
{
    public Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken) => Task.CompletedTask;
}
