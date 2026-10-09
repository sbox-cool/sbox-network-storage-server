namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Sends an endpoint <c>webhook</c> step's already-built Discord payload over HTTP.
/// Wired into the live-serve native endpoint path so a webhook-bearing endpoint
/// can be served natively. Dry-run mode never sends (it dry-runs webhooks for parity),
/// so this is invoked only when serving live traffic.
/// </summary>
public interface IEndpointWebhookSender
{
    /// <summary>
    /// POST the executor-built payload to <paramref name="webhookUrl"/>. Returns the
    /// delivery outcome <c>(Ok, Status, Error)</c>; network/HTTP failures map to
    /// <c>Ok=false</c> rather than throwing (a failed notification must not fail the
    /// whole endpoint). Honors cancellation by rethrowing <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<(bool Ok, int Status, string? Error)> SendAsync(
        string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct);
}
