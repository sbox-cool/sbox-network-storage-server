using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.Common;

using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Application.NetworkStorage.Secrets;
using SboxNetworkStorage.Application.Workspace;

using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Media;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Secrets;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Metadata;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;
using SboxNetworkStorage.Infrastructure.Workspace;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Infrastructure;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Monitoring;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Server.Webhooks;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

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

        services.AddHttpClient("endpoint-webhook", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"sbox-ns-server/{BuildInfo.Version}");
        });
        services.AddHttpClient(WebhookOutboxDispatcher.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"sbox-ns-server/{BuildInfo.Version}");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient("alerts-discord", client => { client.Timeout = TimeSpan.FromSeconds(10); })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient(HttpWebhookClient.ClientName, client => { client.Timeout = TimeSpan.FromSeconds(10); })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddResponseCompression();
        services.AddMemoryCache();

        services.AddSingleton(TimeProvider.System);
        // Client-version analytics and opt-in library policy (docs/compatibility.md). The tracker buffers
        // per-request identity groups in memory; the writer flushes them into client_versions_daily.
        // Shared with the CLI (`sbox-ns project clients` reads ClientVersionsQuery, which needs the matrix).
        services.AddSingleton<LibraryCompatProvider>();
        services.AddSingleton<LibraryPolicyCache>();
        services.AddSingleton<ClientVersionTracker>();
        services.AddSingleton<IClientVersionStore>(sp =>
        {
            var admin = sp.GetRequiredService<INetworkStorageStoreAdmin>();
            return admin is IClientVersionStore versions ? versions
                : throw new InvalidOperationException($"The configured {nameof(INetworkStorageStoreAdmin)} ({admin.GetType().FullName}) does not implement {nameof(IClientVersionStore)}: client-version analytics need a relational store.");
        });
        // Media (docs/media.md): grant and object metadata live in the relational store; bytes live in the
        // configured blob backend. Both are inert until a project enables media.
        services.AddSingleton<IMediaStore>(sp =>
        {
            var admin = sp.GetRequiredService<INetworkStorageStoreAdmin>();
            return admin is IMediaStore media ? media
                : throw new InvalidOperationException($"The configured {nameof(INetworkStorageStoreAdmin)} ({admin.GetType().FullName}) does not implement {nameof(IMediaStore)}: media needs a relational store.");
        });
        services.AddSingleton<IProjectSecretStore>(sp =>
        {
            var admin = sp.GetRequiredService<INetworkStorageStoreAdmin>();
            return admin is IProjectSecretStore secrets ? secrets
                : throw new InvalidOperationException($"The configured {nameof(INetworkStorageStoreAdmin)} ({admin.GetType().FullName}) does not implement {nameof(IProjectSecretStore)}: project secrets need a relational store.");
        });
        services.AddSingleton<IProjectSecretCipher, ProjectSecretCipher>();
        services.AddSingleton<ProjectSecretsService>();
        services.AddSingleton<IMediaBlobStore>(_ => config.GetString("media.backend") == "s3"
            ? S3MediaBlobStore.Create(config.GetString("media.s3.endpoint"), config.GetString("media.s3.region"),
                config.GetString("media.s3.bucket"), config.GetBoolean("media.s3.force_path_style"),
                config.GetString("media.s3.access_key_id"), config.GetString("media.s3.secret_access_key"))
            : new LocalDiskMediaBlobStore(string.IsNullOrWhiteSpace(config.GetString("media.local_dir"))
                ? Path.Combine(config.DataDirectory, "media")
                : config.GetPath("media.local_dir", config.DataDirectory)));
        services.AddSingleton(new MediaServerOptions(Math.Max(1, config.GetInteger("media.max_object_bytes"))));
        services.AddSingleton<MediaGrantTokens>();
        services.AddSingleton<NetworkStorageMediaService>();
        services.AddSingleton<ClientOutcomeTracker>(sp => new ClientOutcomeTracker(sp.GetRequiredService<TimeProvider>(), BuildInfo.Version));
        services.AddSingleton<ClientVersionsQuery>();
        services.AddSingleton<ClientVersionWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<ClientVersionWriter>());
        services.AddSingleton<PostUpdateVerifier>();
        services.AddHostedService<UpdateWatchService>();

        // Errors are logged locally; the bounded in-memory archive backs correlation lookups.
        services.AddSingleton<IErrorArchive, InMemoryErrorArchive>();
        // Captured errors and alert rules share one throttle and one dispatcher over the alerts.toml
        // channels and the channels added in the owner console (docs/alerting.md, docs/monitoring.md).
        services.AddSingleton(AlertOptions.Bind(config));
        services.AddSingleton<LoggingExceptionAlertSink>();
        services.AddSingleton<LoggingNetworkStorageErrorAlertSink>();
        services.AddSingleton<IDiscordWebhookClient, HttpDiscordWebhookClient>();
        services.AddSingleton<ISmtpTransport, SmtpTransport>();
        services.AddSingleton<IWebhookClient, HttpWebhookClient>();
        services.AddSingleton<MonitoringStore>();
        services.AddSingleton<AlertThrottle>();
        // Optional one-click providers (issue #72, section 4): adapters only, behind the channel
        // abstraction. Nothing runs until the owner sets a provider up; plain SMTP/webhooks are untouched.
        services.AddHttpClient("alerts-cloudflare", client => { client.Timeout = TimeSpan.FromSeconds(30); });
        services.AddHttpClient("alerts-sboxns", client => { client.Timeout = TimeSpan.FromSeconds(30); });
        services.AddSingleton<IAlertProvider>(sp => new CloudflareAlertProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("alerts-cloudflare")));
        services.AddSingleton<IAlertProvider>(sp => new SboxNsAlertProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("alerts-sboxns"),
            sp.GetRequiredService<EffectiveConfig>()));
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton(sp => new AlertDispatcher(
            sp.GetRequiredService<AlertOptions>(),
            sp.GetRequiredService<MonitoringStore>(),
            sp.GetRequiredService<IDiscordWebhookClient>(),
            sp.GetRequiredService<ISmtpTransport>(),
            sp.GetRequiredService<IWebhookClient>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AlertDispatcher>>())
        {
            ProviderStore = sp.GetRequiredService<MonitoringStore>(),
            Providers = sp.GetRequiredService<ProviderRegistry>(),
        });
        // One instance behind both interfaces, so its alert limits cover every call site.
        services.AddSingleton<OperatorAlertSink>();
        services.AddSingleton<IExceptionAlertSink>(sp => sp.GetRequiredService<OperatorAlertSink>());
        services.AddSingleton<INetworkStorageErrorAlertSink>(sp => sp.GetRequiredService<OperatorAlertSink>());
        // Server page and alert rules: one on-demand sampler (docs/monitoring.md).
        services.AddSingleton<HostMetricsReader>();
        services.AddSingleton(sp => new RequestMetrics(sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AddSingleton<AlertRuleEngine>();
        services.AddSingleton<MetricsSampler>();
        services.AddHostedService(sp => sp.GetRequiredService<MetricsSampler>());
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
        // Game logs (epic #111): ingest only enqueues; one batch writer drains the bounded queue. Drops are counted.
        services.AddSingleton<IGameLogStore>(sp =>
        {
            var admin = sp.GetRequiredService<INetworkStorageStoreAdmin>();
            return admin is IGameLogStore logs ? logs
                : throw new InvalidOperationException($"The configured {nameof(INetworkStorageStoreAdmin)} ({admin.GetType().FullName}) does not implement {nameof(IGameLogStore)}: game logs need a relational store.");
        });
        services.AddSingleton<GameLogQueue>();
        services.AddSingleton<GameLogRateLimiter>();
        services.AddSingleton(new GameLogWriterOptions(LocalOwnerUserId));
        services.AddSingleton<GameLogWriterService>();
        services.AddHostedService(sp => sp.GetRequiredService<GameLogWriterService>());
        // Concrete registrations let staged ("next") endpoint calls derive revision-overlay copies.
        services.AddScoped<StoreEndpointDataSource>();
        services.AddScoped<IEndpointDataSource>(sp => sp.GetRequiredService<StoreEndpointDataSource>());
        services.AddSingleton<IReadOnlyList<IEndpointScriptRuntime>>(sp => EndpointExecutor.CreateScriptRuntimes(config.GetBoolean("endpoints.scripts"), sp.GetRequiredService<ILogger<JavaScriptEndpointRuntime>>()));
        // One gate for the process: the executor is scoped, so a per-request gate would bound nothing.
        services.AddSingleton<ScriptConcurrencyGate>(_ => new ScriptConcurrencyGate(
            globalLimit: Environment.ProcessorCount,
            perProjectLimit: ScriptConcurrencyGate.DefaultPerProjectLimit,
            waitTime: TimeSpan.FromMilliseconds(Math.Clamp(config.GetInteger("endpoints.script_gate.wait_ms"), 0L, 60_000L))));
        // One write queue for the process, for the same reason: the executor is scoped, so a per-request queue would serialize nothing.
        services.AddSingleton<EndpointWriteQueue>(_ => new EndpointWriteQueue(
            enabled: config.GetBoolean("endpoints.write_queue.enabled"),
            maxQueued: (int)Math.Clamp(config.GetInteger("endpoints.write_queue.max_queued"), 0L, 1_000_000L),
            timeout: TimeSpan.FromMilliseconds(Math.Clamp(config.GetInteger("endpoints.write_queue.timeout_ms"), 1L, 3_600_000L))));
        // ns.fetch network transport. endpoints.fetch_private_networks is read once at startup (restart to change).
        services.AddSingleton<IScriptFetchTransport>(_ => new HttpScriptFetchTransport(config.GetBoolean("endpoints.fetch_private_networks")));
        services.AddScoped<EndpointExecutor>();
        services.AddScoped<IEndpointWebhookSender, HttpEndpointWebhookSender>();
        // Webhook delivery outbox (docs/alerting.md, "Endpoint webhook outbox"). Only projects that set
        // webhookOutbox use it; the inline sender above stays the default path.
        services.AddSingleton<IWebhookOutboxStore>(sp => sp.GetRequiredService<INetworkStorageStoreAdmin>() is IWebhookOutboxStore outbox
            ? outbox : new InMemoryWebhookOutboxStore());
        services.AddSingleton<IEndpointWebhookOutbox, WebhookOutboxWriter>();
        services.AddSingleton<WebhookRateLimitGate>();
        services.AddSingleton<WebhookOutboxDispatcher>();
        services.AddHostedService<WebhookOutboxWorker>();
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

        services.AddSingleton(sp => new RequestEncryptionKeyStore(
            config.GetPath("auth.request_encryption_keys_file", config.RuntimeDirectory),
            sp.GetRequiredService<ILogger<RequestEncryptionKeyStore>>()));
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

        // Module system, integrity monitoring and player insights (docs/modules.md).
        // modules.json absent means nothing is installed; every behavior is opt-in per project.
        services.AddSingleton(new ModuleOwnerOptions(LocalOwnerUserId));
        services.AddSingleton<IModuleDataStore>(sp => sp.GetRequiredService<INetworkStorageStoreAdmin>() is IModuleDataStore modules
            ? modules : new InMemoryModuleDataStore());
        services.AddSingleton<ModuleHookDispatcher>();
        services.AddSingleton<IModuleHookEmitter>(sp => sp.GetRequiredService<ModuleHookDispatcher>());
        services.AddSingleton<IntegrityDetectors>();
        services.AddSingleton<PlayerBlockService>();
        services.AddSingleton<InsightsSettingsProvider>();
        services.AddSingleton<PlayerActionLogWriter>();
        services.AddSingleton<IntegrityModuleService>();
        services.AddSingleton<IIntegrityAlertSink, IntegrityAlertForwarder>();
        services.AddSingleton<InsightsActionLogSubscriber>();
        services.AddSingleton<IModuleHookSubscriber>(sp => sp.GetRequiredService<IntegrityModuleService>());
        services.AddSingleton<IModuleHookSubscriber>(sp => sp.GetRequiredService<InsightsActionLogSubscriber>());
        services.AddScoped<ModuleStateStore>();
        services.AddSingleton<ModuleStateCache>();
        services.AddScoped<ModuleInstaller>();
        services.AddScoped<InsightsService>();
        services.AddHostedService<ModuleHookService>();
        services.AddHostedService<PlayerActionLogService>();

        return services;
    }

    /// <summary>
    /// Wraps the registered <see cref="INetworkStorageStore"/> so collection, endpoint and game-value reads come from a
    /// per-project snapshot that every write, project delete and import invalidates, and so writes reach the
    /// <see cref="QueryResultCache"/>.
    /// </summary>
    private static IServiceCollection AddProjectMetadataCache(this IServiceCollection services)
    {
        var registration = services.Last(d => d.ServiceType == typeof(INetworkStorageStore));
        services.Remove(registration);
        services.AddSingleton<ProjectMetadataCache>();
        services.AddSingleton(sp => new QueryResultCache(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<INetworkStorageStore>(sp => new MetadataCachingNetworkStore(
            (INetworkStorageStore)registration.ImplementationFactory!(sp),
            sp.GetRequiredService<ProjectMetadataCache>(),
            sp.GetRequiredService<QueryResultCache>()));
        return services;
    }
    /// <summary>Entries held by each throttle/cache. Every entry costs one unit, so this caps tracked callers.</summary>
    private const int ThrottleCacheEntries = 10_000;

    private static MemoryCache BoundedCache() => new(new MemoryCacheOptions { SizeLimit = ThrottleCacheEntries });
}

