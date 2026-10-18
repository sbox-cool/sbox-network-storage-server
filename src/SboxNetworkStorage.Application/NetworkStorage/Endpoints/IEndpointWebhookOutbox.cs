namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Queues an endpoint <c>webhook</c> step's already-built payload for durable background delivery (per-project
/// opt-in, see <see cref="IEndpointDataSource.IsWebhookOutboxEnabledAsync"/>). Returning means the row is persisted;
/// the HTTP send happens later and never on the request path.
/// </summary>
public interface IEndpointWebhookOutbox
{
    Task EnqueueAsync(string projectId, string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct);
}
