using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Server.Tunnels;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Endpoints;

public static class ServerInfoEndpoints
{
    /// <summary>API versions this server answers on.</summary>
    public static readonly string[] ApiVersions = ["v3"];

    /// <summary>Capabilities this server answers on.</summary>
    public static readonly string[] Capabilities =
        ["records", "global-records", "endpoints", "workflows", "queries", "game-values",
            "rate-limits", "auth-sessions", "analytics", "analytics-batch", "package-sync", "https-tunnel", "api-root", "media",
            "library-error-envelope"];

    /// <summary>The capabilities list, plus <c>endpoint-js</c> while the JavaScript runtime is enabled (<c>endpoints.scripts</c>).</summary>
    public static string[] CapabilitiesFor(IEnumerable<IEndpointScriptRuntime> scriptRuntimes) =>
        scriptRuntimes.Any(runtime => runtime.Language == "javascript" && runtime.Enabled) ? [.. Capabilities, "endpoint-js"] : Capabilities;

    public static IEndpointRouteBuilder MapServerInfo(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v3/server-info", (UpdateNoticeState updates, INetworkStorageStoreAdmin store, TunnelConnectorState tunnel, LibraryCompatProvider compat, IReadOnlyList<IEndpointScriptRuntime> scriptRuntimes) =>
            {
                var notice = updates.Current;
                return Results.Json(new
                {
                    kind = "self-hosted",
                    product = "sbox-network-storage-server",
                    version = BuildInfo.Version,
                    apiVersions = ApiVersions,
                    database = store.ProviderName,
                    capabilities = CapabilitiesFor(scriptRuntimes),
                    api = new { root = NetworkStorageRoots.Api, aliases = new[] { NetworkStorageRoots.ApiV3, NetworkStorageRoots.V3 }, since = NetworkStorageRoots.ApiSince },
                    tunnel = tunnel.Current,
                    compat = compat.Current.ToServerInfo(),
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
            });

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
            });

        return endpoints;
    }
}
