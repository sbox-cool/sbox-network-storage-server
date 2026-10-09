using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SboxNetworkStorage.Application.Diagnostics;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Endpoints;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Routing;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Server.Tunnels;
using SboxNetworkStorage.Server.SignedDns;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>Builds and runs the self-hosted Network Storage web server.</summary>
public static class ServerHost
{
    /// <param name="configureBuilder">Optional last-step customization (tests use it to swap in a TestServer).</param>
    public static WebApplication Build(EffectiveConfig config, string[]? args = null, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        Directory.CreateDirectory(config.DataDirectory);
        var secrets = ServerSecrets.EnsureAndLoad(config, path => Console.WriteLine($"Generated secret file {path}"));

        // The security-config signer reads its key and key id from the process environment.
        SecurityConfigEnvironment.Apply(config, secrets);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = config.DataDirectory,
            ApplicationName = typeof(ServerHost).Assembly.GetName().Name
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NETWORK_STORAGE_AUTH_SESSION_SECRET"] = secrets.AuthSessionSecret,
            ["STORAGE_ENCRYPTION_KEY"] = secrets.StorageEncryptionKeyHex,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(config.GetString("logging.level")));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        ConfigureKestrel(builder, config);
        builder.Host.UseWindowsService(o => o.ServiceName = "sbox-ns");

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Only loopback proxies (the default) are trusted, so clients cannot spoof their address.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownProxies.Add(System.Net.IPAddress.Loopback);
            o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
            o.ForwardLimit = 1;
        });
        builder.Services.AddNetworkStorageServer(config);
        builder.Services.AddOwnerManagement(config);
        builder.Services.AddSingleton<UpdateNoticeState>();
        builder.Services.AddHostedService<UpdateCheckService>();
        builder.Services.AddSingleton<TunnelConnectorState>();
        builder.Services.AddHostedService<TunnelConnectorService>();
        builder.Services.AddDnsProof();
        builder.Services.AddHostedService<DnsAddressService>();
        builder.Services.AddHostedService(sp => new UsageTelemetryService(config,
            sp.GetRequiredService<INetworkStorageStoreAdmin>(), sp.GetRequiredService<ILogger<UsageTelemetryService>>()));

        builder.Host.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });

        configureBuilder?.Invoke(builder);
        var app = builder.Build();
        RouteOwnershipBootstrap.Seed(app.Services.GetRequiredService<IRouteOwnershipRegistry>());

        app.UseForwardedHeaders();
        app.UseMiddleware<HstsMiddleware>();
        app.UseMiddleware<RequestBodyBufferingMiddleware>(256 * 1024, 1126L * 1024 * 1024);
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseMiddleware<RequestTimingMiddleware>();
        app.UseRouting();
        app.UseOwnerManagement();
        app.UseMiddleware<RouteOwnershipMiddleware>();
        app.UseMiddleware<NetworkStorageUsageMiddleware>();
        app.UseResponseCompression();

        app.MapServerInfo();
        app.MapDnsProof();
        app.MapAuthSessions();
        app.MapQueries();
        app.MapRecords();
        app.MapNetworkStorageGateway();
        app.MapStorageApi();

        return app;
    }

    /// <summary>Prepares the database, then serves until shutdown.</summary>
    public static async Task<int> RunAsync(EffectiveConfig config, string[] args)
    {
        var app = Build(config, args);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("sbox-ns");

        foreach (var file in config.LoadedFiles)
        {
            logger.LogInformation("Loaded config {File}", file);
        }

        logger.LogInformation("sbox-ns {Version} data directory {DataDirectory}", BuildInfo.Version, config.DataDirectory);

        try
        {
            await StoreRegistration.PrepareDatabaseAsync(
                app.Services.GetRequiredService<INetworkStorageStoreAdmin>(), config, logger, app.Lifetime.ApplicationStopping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical("Database is not usable: {Error}", ex.Message);
            return 2;
        }

        await app.RunAsync();
        return 0;
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, EffectiveConfig config)
    {
        ListenAddress.TryParse(config.GetString("server.listen"), out var http);
        var tlsMode = config.GetString("tls.mode");
        X509Certificate2? certificate = null;
        if (tlsMode == "certificate")
        {
            certificate = X509Certificate2.CreateFromPemFile(
                config.GetPath("tls.certificate_path", config.ConfigDirectory),
                config.GetPath("tls.key_path", config.ConfigDirectory));
        }

        if (tlsMode == "acme")
        {
            AcmeCertificates.Register(builder.Services, config);
        }

        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1126L * 1024 * 1024;
            Listen(kestrel, http, _ => { });

            if (tlsMode == "off")
            {
                return;
            }

            ListenAddress.TryParse(config.GetString("tls.https_listen"), out var https);
            Listen(kestrel, https, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
                else
                {
                    AcmeCertificates.UseHttps(listen, kestrel.ApplicationServices);
                }
            });
        });
    }

    private static void Listen(KestrelServerOptions kestrel, ListenAddress address, Action<ListenOptions> configure)
    {
        if (address.IsLocalhost)
        {
            kestrel.ListenLocalhost(address.Port, configure);
        }
        else
        {
            kestrel.Listen(address.Address!, address.Port, configure);
        }
    }
}
