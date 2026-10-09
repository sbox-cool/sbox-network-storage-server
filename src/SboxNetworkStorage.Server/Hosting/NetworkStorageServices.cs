using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Diagnostics;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Common;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Infrastructure.Workspace;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>
/// Registers the Network Storage request pipeline services with the same
/// composition the managed service uses, minus the managed-only concerns
/// (organizations, billing, external alerting, object-bucket mirroring).
/// </summary>
public static class NetworkStorageServices
{
    /// <summary>All self-hosted projects and API keys belong to this single local owner.</summary>
    public const long LocalOwnerUserId = 1;

    public static IServiceCollection AddNetworkStorageServer(this IServiceCollection services, EffectiveConfig config)
    {
        services.AddSingleton(config);
        services.AddConfiguredStore(config);

        // Runtime switches: the store is always authoritative and every route family is served natively.
        services.Configure<ScyllaDbOptions>(o => o.Primary = true);
        services.Configure<NetworkStorageShadowOptions>(o => o.Mode = NetworkStorageRuntimeMode.Serve);
        services.Configure<SboxNetworkStorage.Server.Configuration.SboxcoolBackendOptions>(_ => { });

        services.AddHttpClient("endpoint-webhook", client => { client.Timeout = TimeSpan.FromSeconds(5); });
        services.AddHttpClient("storage-api-gateway", client => { client.Timeout = Timeout.InfiniteTimeSpan; });
        services.AddHttpClient("alerts-discord", client => { client.Timeout = TimeSpan.FromSeconds(10); });
        services.AddResponseCompression();
        services.AddMemoryCache();
        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<IRequestStats, RequestStats>();
        services.AddSingleton<IRouteOwnershipRegistry, RouteOwnershipRegistry>();
        services.AddSingleton(TimeProvider.System);

        // Errors are logged locally; the bounded in-memory archive backs correlation lookups.
        services.AddSingleton<IErrorArchive, InMemoryErrorArchive>();
        // OperatorAlertSink fans every capture out to Discord/SMTP when configured (docs/alerting.md).
        services.AddSingleton(AlertOptions.Bind(config));
        services.AddSingleton<LoggingExceptionAlertSink>();
        services.AddSingleton<LoggingNetworkStorageErrorAlertSink>();
        services.AddSingleton<IDiscordWebhookClient, HttpDiscordWebhookClient>();
        services.AddSingleton<ISmtpTransport, SmtpTransport>();
        services.AddSingleton<DiscordAlertSender>();
        services.AddSingleton<SmtpAlertSender>();
        services.AddSingleton<IExceptionAlertSink, OperatorAlertSink>();
        services.AddSingleton<INetworkStorageErrorAlertSink, OperatorAlertSink>();
        services.AddSingleton<EndpointConflictReportThrottle>();
        services.AddScoped<EndpointShadowReporter>();
        services.AddSingleton<IProxyErrorReporter, ProxyErrorReporter>();

        // Workspace objects (project lists, package manifests, key indexes) live in the configured database.
        services.AddScoped<StoreWorkspaceObjectClient>();
        services.AddScoped<IWorkspaceStorageEnumerator>(sp => sp.GetRequiredService<StoreWorkspaceObjectClient>());
        services.AddScoped<IBunnyWorkspaceClient>(sp => new ScyllaMetadataWorkspaceClient(
            sp.GetRequiredService<StoreWorkspaceObjectClient>(),
            sp.GetRequiredService<INetworkStorageStore>(),
            sp.GetRequiredService<IOptions<ScyllaDbOptions>>(),
            sp.GetRequiredService<ILogger<ScyllaMetadataWorkspaceClient>>()));

        services.AddScoped<IAuditLogger, ScyllaAuditLogger>();
        services.AddSingleton<AnalyticsIngestionFailureTracker>();
        services.AddScoped<IPlayerAnalyticsService, PlayerAnalyticsIngester>();
        services.AddScoped<IEndpointShadowDataSource, ScyllaEndpointShadowDataSource>();
        services.AddScoped<NativeEndpointShadowExecutor>();
        services.AddScoped<IEndpointWebhookSender, HttpEndpointWebhookSender>();
        services.AddScoped<NativeQueryExecutor>();
        services.AddSingleton<IQueryRunRecorder, ScyllaQueryRunRecorder>();
        services.AddSingleton<NetworkStorageUsageTracker>();
        services.AddHostedService<NetworkStorageUsageFlushService>();
        services.AddScoped<IPlayerAnalyticsReader>(sp => new ScyllaPlayerAnalyticsReader(sp.GetRequiredService<INetworkStorageStore>()));
        services.AddScoped<INetworkStorageDataPlane>(sp => new ScyllaNetworkStorageDataPlane(sp.GetRequiredService<INetworkStorageStore>()));
        services.AddScoped<IProjectBackupService, ScyllaProjectBackupService>();
        services.AddScoped<IStorageApiKeyResolver, ScyllaStorageApiKeyResolver>();
        services.AddScoped<IApiKeyCacheInvalidator>(sp => sp.GetService<IStorageApiKeyResolver>() is IApiKeyCacheInvalidator invalidator
            ? invalidator
            : NullApiKeyCacheInvalidator.Instance);
        services.AddScoped<IStorageKeyCdnWriter, StorageKeyCdnWriter>();
        services.AddScoped<NetworkStorageManagementService>();
        services.AddScoped<IQueryManagementService>(sp => sp.GetRequiredService<NetworkStorageManagementService>());
        services.AddScoped<IWorkflowManagementService>(sp => sp.GetRequiredService<NetworkStorageManagementService>());
        services.AddScoped<IPageManagementService>(sp => sp.GetRequiredService<NetworkStorageManagementService>());
        services.AddScoped<INetworkStorageProjectService, NetworkStorageProjectService>();
        services.AddSingleton<INetworkStorageModeResolver>(sp => new NetworkStorageModeResolver(sp.GetRequiredService<IOptions<NetworkStorageShadowOptions>>().Value));

        services.AddScoped<INetworkStorageCandidateHandler, SecurityConfigCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, GameValuesCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, RateLimitsCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, PagesCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StorageRecordReadCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StorageGlobalReadCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StorageLedgerReadCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StatsReadCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, AuthSessionCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, ManagementReadCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StorageRecordMutationCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StorageGlobalMutationCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler, StatsAnalyticsMutationCandidateHandler>();
        services.AddScoped<ManagementMutationCandidateHandler>();
        services.AddScoped<INetworkStorageCandidateHandler>(sp => sp.GetRequiredService<ManagementMutationCandidateHandler>());
        services.AddScoped<INetworkStorageCandidateHandler, EndpointExecutionCandidateHandler>();
        services.AddScoped<EndpointSlugReadCandidateHandler>();
        services.AddScoped<PackageSyncHandler>();
        services.AddScoped<RevisionInitHandler>();
        services.AddScoped<NativeStatsHeartbeatHandler>();

        services.AddSingleton<IAuthSessionSecretProvider, ConfigurationAuthSessionSecretProvider>();
        services.AddSingleton<INetworkStorageAuthSessionService, NetworkStorageAuthSessionService>();
        services.AddHostedService<AuthSessionSecretValidationHostedService>();
        services.AddHttpClient<ISboxAuthVerifier, FacepunchSboxAuthVerifier>();
        services.AddSingleton<INetworkStorageWriteSafetyService>(sp => new NetworkStorageWriteSafetyService(sp.GetRequiredService<IOptions<NetworkStorageShadowOptions>>().Value));
        services.AddScoped<IQueryValuesContextProvider, ScyllaQueryValuesContextProvider>();
        services.AddScoped<INetworkStorageReadCandidateExecutor, NetworkStorageReadCandidateExecutor>();
        services.AddSingleton<IAppendRateLimiter, InMemoryAppendRateLimiter>();

        return services;
    }
}
