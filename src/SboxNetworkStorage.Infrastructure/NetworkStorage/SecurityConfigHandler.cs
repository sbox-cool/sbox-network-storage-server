using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Serves <c>GET /v3/security-config/:projectId</c> from the authoritative
/// The store project metadata. The response retains the signed legacy-compatible
/// wire shape so clients do not need a republish or library update.
/// </summary>
public sealed class SecurityConfigHandler : INetworkStorageHandler
{
    private readonly INetworkStorageStore _store;
    private readonly TimeProvider _timeProvider;

    public SecurityConfigHandler(INetworkStorageStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.SecurityConfig;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.SecurityConfig
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var storagePathsRead = new[] { $"store/projects/{projectId}" };

        JsonElement? project;
        try
        {
            project = await _store.ReadProjectAsync(projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                404,
                "SECURITY_CONFIG_READ_FAILED",
                new
                {
                    ok = false,
                    error = new { code = "SECURITY_CONFIG_READ_FAILED", message = "Security config could not be read. It may be corrupt or inaccessible." },
                    projectId,
                    source = "error"
                },
                storagePathsRead,
                authDecision: "anonymous");
        }

        if (project is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return NetworkStorageResult.Error(
                404,
                "SECURITY_CONFIG_NOT_FOUND",
                new
                {
                    ok = false,
                    error = new { code = "SECURITY_CONFIG_NOT_FOUND", message = "Security config is unavailable because the project metadata was not found." },
                    projectId,
                    source = "missing"
                },
                storagePathsRead,
                authDecision: "anonymous");
        }

        var config = NetworkStorageSecurityConfigBuilder.Build(projectId, project.Value, _timeProvider.GetUtcNow());
        return NetworkStorageResult.Ok(
            new
            {
                ok = true,
                source = "store",
                config
            },
            storagePathsRead,
            authDecision: "anonymous");
    }
}
