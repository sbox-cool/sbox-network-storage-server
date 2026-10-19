namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Queues an endpoint <c>webhook</c> step's already-built payload for durable background delivery (per-project
/// opt-in, see <see cref="IEndpointDataSource.IsWebhookOutboxEnabledAsync"/>). Returning means the row is persisted;
/// the HTTP send happens later and never on the request path.
/// </summary>
public interface IEndpointWebhookOutbox
{
    /// <summary>
    /// Persists the send unless the target is already at its pending cap. Returns <see cref="WebhookEnqueueOutcome.QueueFull"/>
    /// in that case, with nothing stored.
    /// </summary>
    Task<WebhookEnqueueOutcome> EnqueueAsync(string projectId, string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct);
}

/// <summary>Whether an outbox enqueue stored the send.</summary>
public enum WebhookEnqueueOutcome
{
    Queued,
    QueueFull,
}
