using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Enqueues an endpoint webhook step into <c>webhook_outbox</c>. Never performs HTTP: the background
/// <c>WebhookOutboxWorker</c> delivers. The payload is serialized with the same web defaults that
/// <c>HttpEndpointWebhookSender</c> uses (<c>PostAsJsonAsync</c>), so the queued body matches the inline body.
/// </summary>
public sealed class WebhookOutboxWriter(IWebhookOutboxStore store, TimeProvider time) : IEndpointWebhookOutbox
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task EnqueueAsync(string projectId, string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        await store.EnqueueAsync(new WebhookOutboxRow(
            ProjectId: projectId,
            OutboxId: Guid.NewGuid().ToString("N"),
            TargetUrl: webhookUrl,
            PayloadJson: JsonSerializer.Serialize(payload, PayloadOptions),
            Status: WebhookOutboxStatus.Pending,
            Attempts: 0,
            NextAttemptAtUnixMs: now,
            LeaseUntilUnixMs: null,
            LastStatus: null,
            LastError: null,
            CreatedAtUnixMs: now,
            UpdatedAtUnixMs: now), ct);
    }
}
