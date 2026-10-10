using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SboxNetworkStorage.Application.Common;

using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.Workspace;

using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Infrastructure.Workspace;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Storage;

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
        services.AddProjectMetadataCache();

        services.AddHttpClient("endpoint-webhook", client => { client.Timeout = TimeSpan.FromSeconds(5); });
        services.AddHttpClient("alerts-discord", client => { client.Timeout = TimeSpan.FromSeconds(10); });
        services.AddResponseCompression();
        services.AddMemoryCache();

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
        services.AddScoped<EndpointErrorReporter>();
        // Every error report that names a project is also kept for that project's Errors tab.
        services.AddRuntimeActivityLog();

        // Workspace objects (project lists, package manifests, key indexes) live in the configured database.
        services.AddScoped<StoreWorkspaceObjectClient>();
        services.AddScoped<IWorkspaceStorageEnumerator>(sp => sp.GetRequiredService<StoreWorkspaceObjectClient>());
        services.AddScoped<IWorkspaceStore>(sp => new StoreMetadataWorkspaceClient(
            sp.GetRequiredService<StoreWorkspaceObjectClient>(),
            sp.GetRequiredService<INetworkStorageStore>(),
            sp.GetRequiredService<ILogger<StoreMetadataWorkspaceClient>>()));

        services.AddScoped<IAuditLogger, StoreAuditLogger>();
        services.AddSingleton<AnalyticsIngestionFailureTracker>();
        // Request threads only queue analytics; one background writer batches them into the store.
        services.AddSingleton<AnalyticsEventQueue>();
        services.AddSingleton(new AnalyticsWriterOptions(RetentionDays: (int)Math.Clamp(config.GetInteger("analytics.retention_days"), 0, 36_500)));
        services.AddSingleton<IPlayerAnalyticsService, QueuedPlayerAnalyticsService>();
        services.AddSingleton<AnalyticsWriterService>();
        services.AddHostedService(sp => sp.GetRequiredService<AnalyticsWriterService>());
        // Concrete registrations let staged ("next") endpoint calls derive revision-overlay copies.
        services.AddScoped<StoreEndpointDataSource>();
        services.AddScoped<IEndpointDataSource>(sp => sp.GetRequiredService<StoreEndpointDataSource>());
        services.AddScoped<EndpointExecutor>();
        services.AddScoped<IEndpointWebhookSender, HttpEndpointWebhookSender>();
        services.AddScoped<NativeQueryExecutor>();
        services.AddSingleton<IQueryRunRecorder, StoreQueryRunRecorder>();
        services.AddSingleton<NetworkStorageUsageTracker>();
        services.AddHostedService<NetworkStorageUsageFlushService>();
        services.AddScoped<IPlayerAnalyticsReader>(sp => new StorePlayerAnalyticsReader(sp.GetRequiredService<INetworkStorageStore>()));
        services.AddScoped<INetworkStorageDataPlane>(sp => new StoreNetworkStorageDataPlane(sp.GetRequiredService<INetworkStorageStore>()));
        services.AddScoped<IStorageApiKeyResolver, StoreStorageApiKeyResolver>();
        services.AddScoped<IApiKeyCacheInvalidator>(sp => sp.GetService<IStorageApiKeyResolver>() is IApiKeyCacheInvalidator invalidator
            ? invalidator
            : NullApiKeyCacheInvalidator.Instance);
        services.AddScoped<IStorageKeyCdnWriter, StorageKeyCdnWriter>();
        services.AddScoped<INetworkStorageProjectService, NetworkStorageProjectService>();

        services.AddScoped<SecurityConfigHandler>();
        services.AddScoped<GameValuesHandler>();
        services.AddScoped<RateLimitsHandler>();
        services.AddScoped<PagesHandler>();
        services.AddScoped<StorageGlobalReadHandler>();
        services.AddScoped<StorageLedgerReadHandler>();
        services.AddScoped<StatsReadHandler>();
        services.AddScoped<ManagementReadHandler>();
        services.AddScoped<ManagementMutationHandler>();
        services.AddScoped<EndpointSlugReadHandler>();
        services.AddScoped<PackageSyncHandler>();
        services.AddScoped<RevisionInitHandler>();
        services.AddScoped<NativeStatsHeartbeatHandler>();

        services.AddSingleton<IAuthSessionSecretProvider, ConfigurationAuthSessionSecretProvider>();
        services.AddSingleton<INetworkStorageAuthSessionService, NetworkStorageAuthSessionService>();
        services.AddHostedService<AuthSessionSecretValidationHostedService>();
        // The verifier is a transient typed client; throttle and token-cache state lives in
        // singletons so it is shared by every request.
        services.AddSingleton(sp => new SboxAuthFailureThrottle(BoundedCache(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new SboxTokenCache(BoundedCache(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new HeartbeatFailureThrottle(BoundedCache(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new HeartbeatAnalyticsGate(BoundedCache()));
        services.AddHttpClient<ISboxAuthVerifier, FacepunchSboxAuthVerifier>();
        services.AddScoped<StoreQueryValuesContextProvider>();
        services.AddScoped<IQueryValuesContextProvider>(sp => sp.GetRequiredService<StoreQueryValuesContextProvider>());
        services.AddSingleton<IAppendRateLimiter, InMemoryAppendRateLimiter>();

        return services;
    }

    /// <summary>
    /// Wraps the registered <see cref="INetworkStorageStore"/> so collection, endpoint and game-value reads come from a
    /// per-project snapshot that every write, project delete and import invalidates.
    /// </summary>
    private static IServiceCollection AddProjectMetadataCache(this IServiceCollection services)
    {
        var registration = services.Last(d => d.ServiceType == typeof(INetworkStorageStore));
        services.Remove(registration);
        services.AddSingleton<ProjectMetadataCache>();
        services.AddSingleton<INetworkStorageStore>(sp => new MetadataCachingNetworkStore(
            (INetworkStorageStore)registration.ImplementationFactory!(sp),
            sp.GetRequiredService<ProjectMetadataCache>()));
        return services;
    }
    /// <summary>Entries held by each throttle/cache. Every entry costs one unit, so this caps tracked callers.</summary>
    private const int ThrottleCacheEntries = 10_000;

    private static MemoryCache BoundedCache() => new(new MemoryCacheOptions { SizeLimit = ThrottleCacheEntries });
}

