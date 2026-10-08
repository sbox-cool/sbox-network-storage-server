using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Tests.Hosting;

public enum StoreDriver
{
    Sqlite,
    Postgres
}

/// <summary>
/// Self-hosted replacement for the managed service's <c>WebApplicationFactory&lt;Program&gt;</c>:
/// a temp config + data folder, <see cref="ConfigLoader.Load"/> with driver overrides,
/// <see cref="ServerHost.Build"/> on a <see cref="TestServer"/>, and a migrated database.
/// Exposes the same surface the ported tests use (<c>WithWebHostBuilder</c>, <c>CreateClient</c>, <c>Services</c>).
/// </summary>
public abstract class SelfHostFactory : IDisposable
{
    public const string PostgresEnvVar = "NS_TEST_POSTGRES";

    private static readonly string SharedSecretsDirectory = CreateSharedSecretsDirectory();

    private readonly SelfHostFactory? _parent;
    private readonly List<Action<IWebHostBuilder>> _configure;
    private readonly List<IDisposable> _children = new();
    private readonly List<string> _isolatedSchemas = new();
    private readonly List<IDisposable> _ownedStores = new();
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string? _defaultSchema;
    private WebApplication? _app;

    protected SelfHostFactory(StoreDriver driver)
    {
        Driver = driver;
        _configure = new List<Action<IWebHostBuilder>>();
        _root = Path.Combine(Path.GetTempPath(), "sbox-ns-server-tests", Guid.NewGuid().ToString("N"));
        if (!IsAvailable)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        if (driver == StoreDriver.Postgres)
        {
            _defaultSchema = NewSchemaName();
        }

        Config = LoadConfig(DatabaseOverrides(Path.Combine(_root, "data", "sbox-ns.db"), _defaultSchema));
        var admin = (INetworkStorageStoreAdmin)CreateDriverStore(Config);
        admin.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();
        (admin as IDisposable)?.Dispose();
    }

    private SelfHostFactory(SelfHostFactory parent, Action<IWebHostBuilder> configure)
    {
        _parent = parent;
        Driver = parent.Driver;
        _root = parent._root;
        Config = parent.Config;
        _configure = new List<Action<IWebHostBuilder>>(parent._configure) { configure };
    }

    public StoreDriver Driver { get; }

    public static string? PostgresConnectionString
        => Environment.GetEnvironmentVariable(PostgresEnvVar) is { Length: > 0 } cs ? cs : null;

    public bool IsAvailable => Driver == StoreDriver.Sqlite || PostgresConnectionString is not null;

    public string SkipReason => $"PostgreSQL suite skipped: set {PostgresEnvVar} to a connection string.";

    /// <summary>Effective configuration of the default database.</summary>
    public EffectiveConfig Config { get; } = null!;

    public IServiceProvider Services => App.Services;

    public TestServer Server => App.GetTestServer();

    private WebApplication App
    {
        get
        {
            lock (_gate)
            {
                if (_app is null)
                {
                    var app = ServerHost.Build(Config, configureBuilder: builder =>
                    {
                        builder.WebHost.UseTestServer();
                        builder.Logging.ClearProviders();
                        foreach (var configure in _configure)
                        {
                            configure(builder.WebHost);
                        }
                    });
                    app.StartAsync().GetAwaiter().GetResult();
                    _app = app;
                }

                return _app;
            }
        }
    }

    /// <summary>Same contract as <c>WebApplicationFactory.WithWebHostBuilder</c>: a derived host sharing this factory's database.</summary>
    public SelfHostFactory WithWebHostBuilder(Action<IWebHostBuilder> configure)
    {
        var child = new DerivedFactory(this, configure);
        lock (_gate)
        {
            _children.Add(child);
        }

        return child;
    }

    public HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        var handlers = new List<DelegatingHandler>();
        if (options.AllowAutoRedirect)
        {
            handlers.Add(new RedirectHandler(options.MaxAutomaticRedirections));
        }

        if (options.HandleCookies)
        {
            handlers.Add(new CookieContainerHandler());
        }

        HttpMessageHandler handler = Server.CreateHandler();
        for (var i = handlers.Count - 1; i >= 0; i--)
        {
            handlers[i].InnerHandler = handler;
            handler = handlers[i];
        }

        return new HttpClient(handler) { BaseAddress = options.BaseAddress };
    }

    /// <summary>
    /// A fresh, migrated database of this factory's driver (new SQLite file / new
    /// Postgres schema) for tests that need isolated state. Dropped on dispose.
    /// </summary>
    public async Task<INetworkStorageStore> NewStoreAsync()
    {
        var owner = _parent ?? this;
        string? schema = null;
        if (Driver == StoreDriver.Postgres)
        {
            schema = NewSchemaName();
            lock (owner._gate)
            {
                owner._isolatedSchemas.Add(schema);
            }
        }

        var config = LoadConfig(DatabaseOverrides(Path.Combine(_root, "data", $"{Guid.NewGuid():N}.db"), schema));
        var store = CreateDriverStore(config);
        await ((INetworkStorageStoreAdmin)store).MigrateAsync(CancellationToken.None);
        if (store is IDisposable disposable)
        {
            lock (owner._gate)
            {
                owner._ownedStores.Add(disposable);
            }
        }
        return store;
    }

    /// <summary>
    /// Creates a project plus a public and a secret key exactly like
    /// <c>sbox-ns project create</c> / <c>sbox-ns key create</c> (owner = local owner).
    /// </summary>
    public async Task<SelfHostProject> CreateProjectAsync(string name = "Test Game", bool requireSboxAuth = false, string keyMode = "player")
    {
        await using var scope = Services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var owner = NetworkStorageServices.LocalOwnerUserId;
        var created = await projects.CreateProjectAsync(owner, name, string.Empty, enabled: true, requireSboxAuth, keyMode, organizationId: string.Empty, CancellationToken.None);
        var projectId = created.ProjectId ?? throw new InvalidOperationException("project creation failed");
        var (_, publicKey) = await projects.CreateProjectKeyAsync(owner, projectId, "Game client", "public", permissions: null, CancellationToken.None);
        var (_, secretKey) = await projects.CreateProjectKeyAsync(owner, projectId, "Editor sync", "secret", permissions: null, CancellationToken.None);
        return new SelfHostProject(projectId, publicKey, secretKey);
    }

    public void Dispose()
    {
        List<IDisposable> children;
        lock (_gate)
        {
            children = new List<IDisposable>(_children);
            _children.Clear();
        }

        foreach (var child in children)
        {
            child.Dispose();
        }

        WebApplication? app;
        lock (_gate)
        {
            app = _app;
            _app = null;
        }

        if (app is not null)
        {
            app.StopAsync().GetAwaiter().GetResult();
            ((IAsyncDisposable)app).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_parent is not null)
        {
            return;
        }

        foreach (var store in _ownedStores)
        {
            store.Dispose();
        }
        _ownedStores.Clear();

        if (Driver == StoreDriver.Postgres && IsAvailable)
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var schema in _isolatedSchemas.Append(_defaultSchema!))
            {
                DropSchema(schema);
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private Dictionary<string, string> DatabaseOverrides(string sqlitePath, string? schema)
    {
        var overrides = new Dictionary<string, string>
        {
            ["database.provider"] = Driver == StoreDriver.Postgres ? "postgres" : "sqlite",
            ["database.sqlite.path"] = sqlitePath,
            ["updates.check"] = "false",
            ["logging.level"] = "Warning",
            ["auth.session_secret_file"] = Path.Combine(SharedSecretsDirectory, "auth_session_secret"),
            ["auth.storage_encryption_key_file"] = Path.Combine(SharedSecretsDirectory, "storage_encryption_key"),
            ["auth.security_signing_key_file"] = Path.Combine(SharedSecretsDirectory, "security_signing_key.pem"),
        };
        if (Driver == StoreDriver.Postgres)
        {
            overrides["database.postgres.connection_string"] = PostgresConnectionString!;
            overrides["database.postgres.schema"] = schema!;
        }

        return overrides;
    }

    private EffectiveConfig LoadConfig(IReadOnlyDictionary<string, string> overrides)
    {
        var config = ConfigLoader.Load(Path.Combine(_root, "config"), Path.Combine(_root, "data"), overrides, environment: _ => null);
        if (!config.IsValid)
        {
            throw new InvalidOperationException("Invalid test config: " + string.Join("; ", config.Issues));
        }

        return config;
    }

    private static INetworkStorageStore CreateDriverStore(EffectiveConfig config)
        => config.GetString("database.provider") == "postgres"
            ? new PostgresNetworkStorageStore(StoreRegistration.BuildPostgresOptions(config))
            : new SqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = StoreRegistration.SqlitePath(config) });

    private static string NewSchemaName() => $"nss_{Guid.NewGuid():N}";

    private static void DropSchema(string schema)
    {
        try
        {
            using var connection = new NpgsqlConnection(PostgresConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            command.ExecuteNonQuery();
        }
        catch (NpgsqlException)
        {
        }
    }

    /// <summary>
    /// The security-config signer reads its key from a process-wide environment variable,
    /// so every host in the test run shares one set of secrets, generated once up front.
    /// </summary>
    private static string CreateSharedSecretsDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sbox-ns-server-tests", "secrets-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        var config = ConfigLoader.Load(directory, directory, new Dictionary<string, string>
        {
            ["auth.session_secret_file"] = Path.Combine(directory, "auth_session_secret"),
            ["auth.storage_encryption_key_file"] = Path.Combine(directory, "storage_encryption_key"),
            ["auth.security_signing_key_file"] = Path.Combine(directory, "security_signing_key.pem"),
        }, environment: _ => null);
        ServerSecrets.EnsureAndLoad(config);
        return directory;
    }

    private sealed class DerivedFactory(SelfHostFactory parent, Action<IWebHostBuilder> configure)
        : SelfHostFactory(parent, configure);
}

public sealed record SelfHostProject(string ProjectId, string PublicKey, string SecretKey);

public sealed class SqliteHostFactory : SelfHostFactory
{
    public SqliteHostFactory() : base(StoreDriver.Sqlite)
    {
    }
}

public sealed class PostgresHostFactory : SelfHostFactory
{
    public PostgresHostFactory() : base(StoreDriver.Postgres)
    {
    }
}
