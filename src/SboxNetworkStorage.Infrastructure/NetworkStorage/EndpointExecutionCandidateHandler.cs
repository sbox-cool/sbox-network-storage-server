using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Dry-run native candidate for <c>NetworkStorageRouteFamily.Endpoint</c> (methods GET and POST).
///
/// Mirrors Bun's <c>routeEndpointExecute</c> auth-and-initial-validation section up to the point
/// where user code, workflows, billing debits, storage writes, webhooks, and analytics/audit
/// state mutations would begin. This candidate NEVER executes user steps, debits credits, writes
/// storage, fires webhooks, or emits analytics/audit events — it returns a dependency-inventory
/// body that documents every side-effect category the real Bun handler would touch.
///
/// Production writes are suppressed via <c>reason="write_suppressed_dry_run"</c>; the
/// <c>IntendedWritePaths</c> list contains placeholder logical paths for each suppressed category.
/// </summary>
public sealed class EndpointExecutionCandidateHandler : INetworkStorageCandidateHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;

    public EndpointExecutionCandidateHandler(IStorageApiKeyResolver apiKeyResolver)
    {
        _apiKeyResolver = apiKeyResolver ?? throw new ArgumentNullException(nameof(apiKeyResolver));
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Endpoint;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Endpoint
        && (string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase)
         || string.Equals(route.Method, "POST", StringComparison.OrdinalIgnoreCase));

    public async Task<NetworkStorageCandidateResult> ExecuteAsync(NetworkStorageCandidateRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var method = request.Route.Method;
        var endpointSlug = request.RouteParameter("endpointSlug");

        // ── Auth: resolve API key ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return UnauthorizedResult();
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return UnauthorizedResult();
        }

        if (auth is null)
        {
            return UnauthorizedResult();
        }

        // ── Project enabled check ──
        if (!auth.Enabled)
        {
            return ProjectDisabledResult();
        }

        // ── Endpoint slug from body (POST /v3/endpoints/:projectId has no slug in path) ──
        // When the request body provides the slug inline (POST without path slug), note it
        // as body-resolved. We do NOT parse the full body — this is a dry-run inventory pass.
        var slugSource = !string.IsNullOrEmpty(endpointSlug) ? "route" : "body";

        // ── Build dependency inventory ──
        var intendedWritePaths = new List<string>
        {
            $"endpoint-execution:{projectId}:{endpointSlug ?? "<slug-from-body>"}:storage-write",
            $"endpoint-execution:{projectId}:{endpointSlug ?? "<slug-from-body>"}:billing-debit",
            $"endpoint-execution:{projectId}:{endpointSlug ?? "<slug-from-body>"}:analytics-event",
            $"endpoint-execution:{projectId}:{endpointSlug ?? "<slug-from-body>"}:audit-log",
            $"endpoint-execution:{projectId}:{endpointSlug ?? "<slug-from-body>"}:webhook-dispatch",
        };

        var body = new EndpointExecutionDryRunBody
        {
            Executed = true,
            Status = "dry_run",
            DryRun = true,
            ProjectId = projectId,
            EndpointSlug = endpointSlug,
            Method = method,
            SlugSource = slugSource,
            AuthDecision = auth.KeyType,
            ProjectEnabled = auth.Enabled,
            DependencyInventory = new DependencyInventory
            {
                Auth = new AuthCategory { Resolved = true, KeyType = auth.KeyType, ProjectId = projectId },
                Project = new ProjectCategory { Enabled = auth.Enabled, ProjectId = projectId },
                Endpoint = new EndpointCategory { Method = method, Slug = endpointSlug, SlugSource = slugSource },
                StorageRead = new StorageReadCategory { RequiredForEndpointSteps = true },
                ComputeBilling = new BillingCategory { WouldDebitCredits = true, WouldCheckQuota = true },
                StorageMutation = new StorageMutationCategory { WouldPerformWrites = true, WriteCategory = "collection_record_write" },
                RevisionResolution = new RevisionResolutionCategory { WouldResolveRevisionTarget = true, WouldOverlayRevisionOverrides = true },
                WorkflowExecution = new WorkflowCategory { RequiredForWorkflowSteps = true },
                AnalyticsEvent = new AnalyticsCategory { WouldEmitEvent = true, EventType = "endpoint.call" },
                AuditLog = new AuditCategory { WouldLogAction = true },
                WebhookDispatch = new WebhookCategory { WouldFireWebhook = true, WouldSendErrorToDiscord = true },
                RateLimiting = new RateLimitingCategory { WouldCheckRateLimits = true },
                SboxAuth = new SboxAuthCategory { Required = true, SkipWhenSecretKeyPresent = true },
                SecurityConfig = new SecurityConfigCategory { WouldCheckMode = true, WouldCheckConfigVersion = true },
                EncryptedRequests = new EncryptedRequestsCategory { Supported = true },
            },
            SideEffectsSuppressed = true,
            SuppressionReason = "write_suppressed_dry_run",
        };

        return new NetworkStorageCandidateResult(
            StatusCode: 200,
            PublicErrorCode: null,
            Body: body,
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: intendedWritePaths,
            AuthDecision: auth.KeyType);
    }

    private static NetworkStorageCandidateResult UnauthorizedResult() =>
        new(401, "UNAUTHORIZED",
            JsonSerializer.SerializeToElement(new
            {
                ok = false,
                status = 401,
                error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." }
            }),
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: Array.Empty<string>(),
            AuthDecision: "denied");

    private static NetworkStorageCandidateResult ProjectDisabledResult() =>
        new(403, "PROJECT_DISABLED",
            JsonSerializer.SerializeToElement(new
            {
                ok = false,
                status = 403,
                error = new { code = "PROJECT_DISABLED", message = "Project is disabled." }
            }),
            StoragePathsRead: Array.Empty<string>(),
            IntendedWritePaths: Array.Empty<string>(),
            AuthDecision: "denied");

    // ── Dry-run response model ──

    internal sealed record EndpointExecutionDryRunBody
    {
        public bool Executed { get; init; }
        public string Status { get; init; } = string.Empty;
        public bool DryRun { get; init; }
        public string ProjectId { get; init; } = string.Empty;
        public string? EndpointSlug { get; init; }
        public string Method { get; init; } = string.Empty;
        public string SlugSource { get; init; } = string.Empty;
        public string AuthDecision { get; init; } = string.Empty;
        public bool ProjectEnabled { get; init; }
        public DependencyInventory DependencyInventory { get; init; } = new();
        public bool SideEffectsSuppressed { get; init; }
        public string SuppressionReason { get; init; } = string.Empty;
    }

    internal sealed record DependencyInventory
    {
        public AuthCategory Auth { get; init; } = new();
        public ProjectCategory Project { get; init; } = new();
        public EndpointCategory Endpoint { get; init; } = new();
        public StorageReadCategory StorageRead { get; init; } = new();
        public BillingCategory ComputeBilling { get; init; } = new();
        public StorageMutationCategory StorageMutation { get; init; } = new();
        public RevisionResolutionCategory RevisionResolution { get; init; } = new();
        public WorkflowCategory WorkflowExecution { get; init; } = new();
        public AnalyticsCategory AnalyticsEvent { get; init; } = new();
        public AuditCategory AuditLog { get; init; } = new();
        public WebhookCategory WebhookDispatch { get; init; } = new();
        public RateLimitingCategory RateLimiting { get; init; } = new();
        public SboxAuthCategory SboxAuth { get; init; } = new();
        public SecurityConfigCategory SecurityConfig { get; init; } = new();
        public EncryptedRequestsCategory EncryptedRequests { get; init; } = new();
    }

    internal sealed record AuthCategory
    {
        public bool Resolved { get; init; }
        public string KeyType { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
    }

    internal sealed record ProjectCategory
    {
        public bool Enabled { get; init; }
        public string ProjectId { get; init; } = string.Empty;
    }

    internal sealed record EndpointCategory
    {
        public string Method { get; init; } = string.Empty;
        public string? Slug { get; init; }
        public string SlugSource { get; init; } = string.Empty;
    }

    internal sealed record StorageReadCategory
    {
        public bool RequiredForEndpointSteps { get; init; }
    }

    internal sealed record BillingCategory
    {
        public bool WouldDebitCredits { get; init; }
        public bool WouldCheckQuota { get; init; }
    }

    internal sealed record StorageMutationCategory
    {
        public bool WouldPerformWrites { get; init; }
        public string WriteCategory { get; init; } = string.Empty;
    }

    internal sealed record RevisionResolutionCategory
    {
        public bool WouldResolveRevisionTarget { get; init; }
        public bool WouldOverlayRevisionOverrides { get; init; }
    }

    internal sealed record WorkflowCategory
    {
        public bool RequiredForWorkflowSteps { get; init; }
    }

    internal sealed record AnalyticsCategory
    {
        public bool WouldEmitEvent { get; init; }
        public string EventType { get; init; } = string.Empty;
    }

    internal sealed record AuditCategory
    {
        public bool WouldLogAction { get; init; }
    }

    internal sealed record WebhookCategory
    {
        public bool WouldFireWebhook { get; init; }
        public bool WouldSendErrorToDiscord { get; init; }
    }

    internal sealed record RateLimitingCategory
    {
        public bool WouldCheckRateLimits { get; init; }
    }

    internal sealed record SboxAuthCategory
    {
        public bool Required { get; init; }
        public bool SkipWhenSecretKeyPresent { get; init; }
    }

    internal sealed record SecurityConfigCategory
    {
        public bool WouldCheckMode { get; init; }
        public bool WouldCheckConfigVersion { get; init; }
    }

    internal sealed record EncryptedRequestsCategory
    {
        public bool Supported { get; init; }
    }
}
