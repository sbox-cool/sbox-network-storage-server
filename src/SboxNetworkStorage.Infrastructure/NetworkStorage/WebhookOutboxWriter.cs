using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Enqueues an endpoint webhook step into <c>webhook_outbox</c>. Never performs HTTP: the background
/// <c>WebhookOutboxWorker</c> delivers. The payload is serialized with the same web defaults that
/// <c>HttpEndpointWebhookSender</c> uses (<c>PostAsJsonAsync</c>), so the queued body matches the inline body,
/// except that embed text is cut to Discord's documented limits (<see cref="DiscordEmbedLimits"/>).
/// </summary>
public sealed class WebhookOutboxWriter(IWebhookOutboxStore store, TimeProvider time) : IEndpointWebhookOutbox
{
    /// <summary>
    /// Pending rows allowed per (project, webhook URL). Past this the enqueue is refused with
    /// <see cref="WebhookEnqueueOutcome.QueueFull"/>; the endpoint keeps running and the step reports a fixed error.
    /// Documented in docs/admin-panel.md ("Delivery").
    /// </summary>
    public const int MaxPendingPerTarget = 500;

    /// <summary>Pending rows older than this are marked failed ("expired") by the worker instead of being sent late.</summary>
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task<WebhookEnqueueOutcome> EnqueueAsync(string projectId, string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var stored = await store.EnqueueAsync(new WebhookOutboxRow(
            ProjectId: projectId,
            OutboxId: Guid.NewGuid().ToString("N"),
            TargetUrl: webhookUrl,
            PayloadJson: JsonSerializer.Serialize(DiscordEmbedLimits.Apply(payload), PayloadOptions),
            Status: WebhookOutboxStatus.Pending,
            Attempts: 0,
            NextAttemptAtUnixMs: now,
            LeaseUntilUnixMs: null,
            LastStatus: null,
            LastError: null,
            CreatedAtUnixMs: now,
            UpdatedAtUnixMs: now), MaxPendingPerTarget, ct);
        return stored ? WebhookEnqueueOutcome.Queued : WebhookEnqueueOutcome.QueueFull;
    }
}
