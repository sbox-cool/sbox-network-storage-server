using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Sends claimed <see cref="WebhookOutboxRow"/>s and records the outcome. Retries use exponential backoff with
/// jitter, up to <see cref="MaxAttempts"/> attempts in total. Discord <c>429</c> waits for <c>retry_after</c>.
/// <c>401</c>/<c>404</c> disable the target until re-enabled on the owner Webhooks page. A row that was claimed
/// but never finished is reaped by the store as failed and is never resent.
/// </summary>
public sealed class WebhookOutboxDispatcher(
    IWebhookOutboxStore store,
    IHttpClientFactory httpClientFactory,
    WebhookRateLimitGate rateLimits,
    TimeProvider time,
    ILogger<WebhookOutboxDispatcher> logger)
{
    public const string ClientName = "webhook-outbox";
    public const int MaxAttempts = 8;
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(1);

    /// <summary>Claims up to <paramref name="batchSize"/> due rows and delivers them. Returns how many were claimed.</summary>
    public async Task<int> DrainOnceAsync(int batchSize, CancellationToken ct)
    {
        var now = NowMs();
        var rows = await store.ClaimDueAsync(now, now + (long)Lease.TotalMilliseconds, batchSize, ct);
        foreach (var row in rows)
            await DeliverAsync(row, ct);
        return rows.Count;
    }

    private async Task DeliverAsync(WebhookOutboxRow row, CancellationToken ct)
    {
        var now = NowMs();
        if (!DiscordWebhookUrl.IsAllowed(row.TargetUrl))
        {
            // The step validates the URL before enqueue; re-check here so a row written any other way is never sent.
            logger.LogWarning("Webhook outbox row {OutboxId} has a target that is not a Discord webhook URL; not sent", row.OutboxId);
            await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Failed, row.Attempts, now, null, "target is not a Discord webhook URL", now), ct);
            return;
        }
        var target = await store.ReadTargetAsync(row.ProjectId, row.TargetUrl, ct);
        if (target?.DisabledAtUnixMs is not null)
        {
            await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Suppressed, row.Attempts, now, null, "target disabled", now), ct);
            return;
        }

        if (rateLimits.BlockedUntilUnixMs(row.TargetUrl, now) is { } blockedUntil)
        {
            // The bucket is known to be exhausted: wait without spending an attempt.
            await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Pending, row.Attempts, blockedUntil, null, null, now), ct);
            return;
        }

        var attempt = await SendAsync(row, ct);
        now = NowMs();
        var attempts = row.Attempts + 1;
        if (attempt.Headers is not null)
            rateLimits.Observe(row.TargetUrl, attempt.Headers, now);

        switch (attempt.Kind)
        {
            case SendKind.Delivered:
                await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Delivered, attempts, now, attempt.Status, null, now), ct);
                await RecordTargetAsync(row, now, success: true, attempt.Status, null, disable: null, ct);
                return;

            case SendKind.RateLimited:
                rateLimits.BlockAfter(row.TargetUrl, (attempt.RetryAfter ?? DefaultRetryAfter).TotalSeconds, now);
                if (attempts >= MaxAttempts)
                {
                    await FailAsync(row, attempts, now, attempt.Status, "rate limited too many times", ct);
                    return;
                }
                await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Pending, attempts,
                    now + (long)(attempt.RetryAfter ?? DefaultRetryAfter).TotalMilliseconds, attempt.Status, attempt.Error, now), ct);
                return;

            case SendKind.Disable:
                await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Failed, attempts, now, attempt.Status, attempt.Error, now), ct);
                await RecordTargetAsync(row, now, success: false, attempt.Status, attempt.Error, disable: attempt.Error, ct);
                logger.LogWarning("Webhook target disabled after HTTP {Status} (project {ProjectId}, outbox {OutboxId})",
                    attempt.Status, row.ProjectId, row.OutboxId);
                return;

            case SendKind.Retry:
                if (attempts >= MaxAttempts)
                {
                    await FailAsync(row, attempts, now, attempt.Status, attempt.Error, ct);
                    return;
                }
                await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Pending, attempts,
                    now + (long)BackoffFor(attempts).TotalMilliseconds, attempt.Status, attempt.Error, now), ct);
                await RecordTargetAsync(row, now, success: false, attempt.Status, attempt.Error, disable: null, ct);
                return;

            default:
                await FailAsync(row, attempts, now, attempt.Status, attempt.Error, ct);
                return;
        }
    }

    private async Task FailAsync(WebhookOutboxRow row, int attempts, long now, int? status, string? error, CancellationToken ct)
    {
        await store.UpdateAsync(Finish(row, WebhookOutboxStatus.Failed, attempts, now, status, error, now), ct);
        await RecordTargetAsync(row, now, success: false, status, error, disable: null, ct);
        logger.LogWarning("Webhook delivery failed after {Attempts} attempt(s) (project {ProjectId}, outbox {OutboxId}): {Error}",
            attempts, row.ProjectId, row.OutboxId, error);
    }

    private async Task<Attempt> SendAsync(WebhookOutboxRow row, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, row.TargetUrl)
            {
                Content = new StringContent(row.PayloadJson, Encoding.UTF8, "application/json"),
            };
            using var response = await httpClientFactory.CreateClient(ClientName).SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return new Attempt(SendKind.Delivered, status, null, null, response.Headers);

            var body = await response.Content.ReadAsStringAsync(ct);
            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => new Attempt(SendKind.RateLimited, status, "HTTP 429", RetryAfterOf(response.Headers, body), response.Headers),
                HttpStatusCode.NotFound => new Attempt(SendKind.Disable, status, "HTTP 404: webhook not found", null, response.Headers),
                HttpStatusCode.Unauthorized => new Attempt(SendKind.Disable, status, "HTTP 401: webhook unauthorized", null, response.Headers),
                HttpStatusCode.RequestTimeout => new Attempt(SendKind.Retry, status, $"HTTP {status}", null, response.Headers),
                _ when status >= 500 => new Attempt(SendKind.Retry, status, $"HTTP {status}", null, response.Headers),
                _ => new Attempt(SendKind.Terminal, status, $"HTTP {status}", null, response.Headers),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never store the exception message: transport errors can echo the request URL, which carries the webhook token.
            return new Attempt(SendKind.Retry, null, ex.GetType().Name, null, null);
        }
    }

    private async Task RecordTargetAsync(WebhookOutboxRow row, long now, bool success, int? status, string? error, string? disable, CancellationToken ct)
    {
        var current = await store.ReadTargetAsync(row.ProjectId, row.TargetUrl, ct)
            ?? new WebhookTargetState(row.ProjectId, row.TargetUrl, 0, null, null, null, null, null, now);
        var next = success
            ? current with { ConsecutiveFailures = 0, FailingSinceUnixMs = null, LastStatus = status, LastError = null, UpdatedAtUnixMs = now }
            : current with
            {
                ConsecutiveFailures = current.ConsecutiveFailures + 1,
                FailingSinceUnixMs = current.FailingSinceUnixMs ?? now,
                LastStatus = status,
                LastError = error,
                DisabledAtUnixMs = disable is null ? current.DisabledAtUnixMs : now,
                DisabledReason = disable ?? current.DisabledReason,
                UpdatedAtUnixMs = now,
            };
        await store.WriteTargetAsync(next, ct);
    }

    private static WebhookOutboxRow Finish(WebhookOutboxRow row, WebhookOutboxStatus status, int attempts, long nextAttemptAtUnixMs,
        int? lastStatus, string? lastError, long now)
        => row with
        {
            Status = status,
            Attempts = attempts,
            NextAttemptAtUnixMs = nextAttemptAtUnixMs,
            LeaseUntilUnixMs = null,
            LastStatus = lastStatus,
            LastError = lastError,
            UpdatedAtUnixMs = now,
        };

    /// <summary>Discord sends <c>retry_after</c> (fractional seconds) in the JSON body; <c>Retry-After</c> is the fallback.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseHeaders headers, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("retry_after", out var value)
                && value.TryGetDouble(out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the header.
        }
        return headers.RetryAfter?.Delta;
    }

    /// <summary>Exponential backoff, capped, with jitter in [0.5, 1.0) of the computed delay.</summary>
    private static TimeSpan BackoffFor(int attempts)
    {
        var exponent = Math.Min(attempts - 1, 16);
        var capped = TimeSpan.FromTicks(Math.Min(BaseBackoff.Ticks << exponent, MaxBackoff.Ticks));
        return capped * (0.5 + Random.Shared.NextDouble() / 2);
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private enum SendKind { Delivered, RateLimited, Disable, Retry, Terminal }

    private sealed record Attempt(SendKind Kind, int? Status, string? Error, TimeSpan? RetryAfter, HttpResponseHeaders? Headers);
}
