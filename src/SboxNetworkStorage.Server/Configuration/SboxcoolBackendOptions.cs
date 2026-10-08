namespace SboxNetworkStorage.Server.Configuration;

public sealed class SboxcoolBackendOptions
{
    public string ServiceName { get; set; } = "sboxcool-dotnet-backend";
    public string PublicBaseUrl { get; set; } = "https://sboxcool.com";
    public string GoogleClientId { get; set; } = string.Empty;
    public string GoogleClientSecret { get; set; } = string.Empty;
    public string TurnstileSiteKey { get; set; } = string.Empty;
    public string? FrontendAssetsRoot { get; set; }
    public string LegacyWebsiteCompatibilityBaseUrl { get; set; } = "http://127.0.0.1:4549";
    public string StorageApiGatewayBaseUrl { get; set; } = string.Empty;
    public string LegacyManagementApiBaseUrl { get; set; } = string.Empty;
    public string LegacyWebsiteCompatibilitySharedSecret { get; set; } = string.Empty;
    public string OpsToken { get; set; } = string.Empty;
    public bool LegacyWebsiteCompatibilityReadinessRequired { get; set; }
    public int LegacyWebsiteCompatibilityHealthTimeoutSeconds { get; set; } = 3;
    public bool LegacyWebsiteCompatibilityAutoStart { get; set; }
    public int LegacyWebsiteCompatibilityAutoStartRetrySeconds { get; set; } = 10;
    public string LegacyWebsiteCompatibilityAutoStartCommand { get; set; } = "bun";
    public int RequestTimeoutSeconds { get; set; } = 30;
    // Fail-fast bound for the Network Storage gateway proxy to Bun. Kept well
    // below RequestTimeoutSeconds and the nginx upstream timeout so a stalled
    // Bun/SpacetimeDB request returns a fast 502 and frees the worker instead of
    // holding it for the full request timeout and cascading to worker-pool-unhealthy.
    public int StorageApiGatewayTimeoutSeconds { get; set; } = 12;
    public int ShutdownTimeoutSeconds { get; set; } = 20;
    public int ToolWorkerRequestTimeoutSeconds { get; set; } = 30;
    public long ToolWorkerPayloadLimitBytes { get; set; } = 1024L * 1024 * 1024;
    public bool EnableFaultProbeEndpoints { get; set; }
    public bool StaticAssetDevelopmentNoCache { get; set; } = true;
}
