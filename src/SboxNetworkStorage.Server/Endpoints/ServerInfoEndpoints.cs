using SboxNetworkStorage.Contracts.Diagnostics;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Routing;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Endpoints;

public static class ServerInfoEndpoints
{
    /// <summary>API versions this server answers on.</summary>
    public static readonly string[] ApiVersions = ["v3"];

    public static IEndpointRouteBuilder MapServerInfo(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v3/server-info", (UpdateNoticeState updates, INetworkStorageStoreAdmin store) =>
            {
                var notice = updates.Current;
                return Results.Json(new
                {
                    kind = "self-hosted",
                    product = "sbox-network-storage-server",
                    version = BuildInfo.Version,
                    apiVersions = ApiVersions,
                    database = store.ProviderName,
                    capabilities = new[] { "records", "global-records", "endpoints", "workflows", "queries", "game-values", "rate-limits", "auth-sessions", "analytics", "package-sync" },
                    update = notice is null
                        ? null
                        : new
                        {
                            available = notice.UpdateAvailable,
                            latestVersion = notice.Latest.Version,
                            security = notice.Latest.Security,
                            changelogUrl = notice.Latest.ChangelogUrl,
                            checkedAt = notice.CheckedAt
                        }
                });
            })
            .WithRouteOwner(RouteOwner.DotNetNative, "Self-hosted server identity, version and capabilities");

        endpoints.MapGet("/health", async (INetworkStorageStoreAdmin store, CancellationToken ct) =>
            {
                try
                {
                    var ping = await store.PingAsync(ct);
                    return Results.Json(new { status = "ok", version = BuildInfo.Version, database = store.ProviderName, databaseLatencyMs = (int)ping.RoundTrip.TotalMilliseconds });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Results.Json(new { status = "database-unavailable", version = BuildInfo.Version, database = store.ProviderName }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .WithRouteOwner(RouteOwner.DotNetNative, "Self-hosted liveness and database health");

        return endpoints;
    }
}
