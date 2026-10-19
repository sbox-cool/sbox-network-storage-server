using System.Globalization;
using System.Net.Http.Headers;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Process-local Discord rate-limit state for outbox delivery. A webhook URL maps to its <c>X-RateLimit-Bucket</c>
/// once one is seen; until then the webhook id (URL path segment, never the token) is the key. A 429 or an
/// exhausted bucket (<c>X-RateLimit-Remaining: 0</c>) blocks sends to that key until its reset time.
/// </summary>
public sealed class WebhookRateLimitGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _bucketByWebhook = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _blockedUntilUnixMs = new(StringComparer.Ordinal);

    /// <summary>Unix ms before which no send to <paramref name="webhookUrl"/> may go out, or null when it may send now.</summary>
    public long? BlockedUntilUnixMs(string webhookUrl, long nowUnixMs)
    {
        var key = KeyOf(webhookUrl);
        lock (_gate)
        {
            if (!_blockedUntilUnixMs.TryGetValue(key, out var until)) return null;
            if (until <= nowUnixMs)
            {
                _blockedUntilUnixMs.Remove(key);
                return null;
            }
            return until;
        }
    }

    /// <summary>Records the rate-limit headers of a response to a send against <paramref name="webhookUrl"/>.</summary>
    public void Observe(string webhookUrl, HttpResponseHeaders headers, long nowUnixMs)
    {
        var id = WebhookId(webhookUrl);
        lock (_gate)
        {
            if (headers.TryGetValues("X-RateLimit-Bucket", out var bucketValues) && bucketValues.FirstOrDefault() is { Length: > 0 } bucket)
                _bucketByWebhook[id] = bucket;
            var key = KeyOf(webhookUrl);
            if (headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
                && int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)
                && remaining <= 0
                && headers.TryGetValues("X-RateLimit-Reset-After", out var resetValues)
                && double.TryParse(resetValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var resetAfter))
                Block(key, nowUnixMs, resetAfter);
        }
    }

    /// <summary>Blocks sends to <paramref name="webhookUrl"/> for <paramref name="retryAfterSeconds"/> after a 429.</summary>
    public void BlockAfter(string webhookUrl, double retryAfterSeconds, long nowUnixMs)
    {
        lock (_gate) Block(KeyOf(webhookUrl), nowUnixMs, retryAfterSeconds);
    }

    private void Block(string key, long nowUnixMs, double seconds)
    {
        var until = nowUnixMs + (long)Math.Ceiling(Math.Max(0, seconds) * 1000);
        _blockedUntilUnixMs[key] = Math.Max(until, _blockedUntilUnixMs.GetValueOrDefault(key));
    }

    private string KeyOf(string webhookUrl)
    {
        var id = WebhookId(webhookUrl);
        return _bucketByWebhook.TryGetValue(id, out var bucket) ? "bucket:" + bucket : "webhook:" + id;
    }

    /// <summary>The webhook id from <c>https://discord.com/api/webhooks/{id}/{token}</c>; the token is never used as a key. Unexpected shapes fall back to the host.</summary>
    public static string WebhookId(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)) return "invalid";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 && segments[1] == "webhooks" ? segments[2] : uri.Host;
    }
}
