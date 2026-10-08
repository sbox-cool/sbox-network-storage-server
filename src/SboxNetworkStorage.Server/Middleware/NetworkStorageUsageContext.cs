using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;

namespace SboxNetworkStorage.Server.Middleware;

/// <summary>
/// Per-request usage annotation set by data-plane handlers after auth succeeds
/// and consumed by <see cref="NetworkStorageUsageMiddleware"/> when the response
/// completes. Handlers annotate the semantic fields only they know (read vs
/// write vs endpoint call, endpoint slug, storage byte delta); the middleware
/// measures the transport facts (duration, bytes in/out, status).
/// </summary>
public sealed class NetworkStorageUsageAnnotation
{
    public required string ProjectId { get; init; }
    public required UsageKind Kind { get; init; }
    public string? EndpointSlug { get; set; }
    public long StorageDeltaBytes { get; set; }

    /// <summary>
    /// When true the middleware skips metering entirely. Used by routes whose
    /// wire contract hides auth failures behind HTTP 200 (auth sessions), where
    /// the 401-skip rule cannot apply.
    /// </summary>
    public bool Suppressed { get; init; }
}

/// <summary>Accessor for the request's usage annotation in <see cref="HttpContext.Items"/>.</summary>
public static class NetworkStorageUsageContext
{
    private const string ItemKey = "sboxcool.network-storage.usage";

    /// <summary>
    /// Annotate the request. Call after API-key auth succeeds — unannotated
    /// requests fall back to route-value projectId + method-based kind, and
    /// 401 responses are never metered.
    /// </summary>
    public static void Set(HttpContext context, string projectId, UsageKind kind, string? endpointSlug = null)
    {
        if (string.IsNullOrEmpty(projectId)) return;
        context.Items[ItemKey] = new NetworkStorageUsageAnnotation
        {
            ProjectId = projectId,
            Kind = kind,
            EndpointSlug = endpointSlug,
        };
    }

    /// <summary>
    /// Mark the request as not-to-be-metered. A later successful
    /// <see cref="Set"/> replaces the suppression. Use on routes that return
    /// HTTP 200 for auth failures (auth sessions), where the middleware's
    /// 401-skip cannot distinguish unauthenticated noise.
    /// </summary>
    public static void Suppress(HttpContext context)
    {
        context.Items[ItemKey] = new NetworkStorageUsageAnnotation
        {
            ProjectId = "-",
            Kind = UsageKind.Auth,
            Suppressed = true,
        };
    }

    /// <summary>
    /// Add a stored-bytes delta (positive on writes/appends, negative on
    /// deletes) discovered while handling the request. No-op when the request
    /// was never annotated.
    /// </summary>
    public static void AddStorageDelta(HttpContext context, long deltaBytes)
    {
        if (context.Items.TryGetValue(ItemKey, out var value) && value is NetworkStorageUsageAnnotation annotation)
        {
            annotation.StorageDeltaBytes += deltaBytes;
        }
    }

    internal static NetworkStorageUsageAnnotation? Get(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) ? value as NetworkStorageUsageAnnotation : null;
}
