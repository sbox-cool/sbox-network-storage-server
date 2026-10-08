using System.Globalization;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// Creates or updates the config folder. Every choice is validated (including a
/// live database connection test) before anything is written.
/// </summary>
public static class SetupCommand
{
    private const string PostgresPasswordFile = "secrets/postgres_password";

    public static async Task<int> RunAsync(CliContext context)
    {
        var interactive = !context.Args.Flag("non-interactive") && !Console.IsInputRedirected;
        var current = context.LoadConfig();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? postgresPassword = null;

        Console.WriteLine($"sbox-ns setup: config folder {current.ConfigDirectory}");
        if (current.LoadedFiles.Count > 0)
        {
            Console.WriteLine("Existing configuration found; current values are offered as defaults.");
        }

        while (true)
        {
            var provider = Ask(context, interactive, "database", "Database (sqlite or postgres)", current.GetString("database.provider"),
                v => v is "sqlite" or "postgres" ? null : "enter sqlite or postgres");
            values["database.provider"] = provider;

            if (provider == "postgres")
            {
                var connectionString = context.Args.Option("pg-connection-string");
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    values["database.postgres.connection_string"] = connectionString;
                }
                else
                {
                    values["database.postgres.host"] = Ask(context, interactive, "pg-host", "PostgreSQL host", current.GetString("database.postgres.host"));
                    values["database.postgres.port"] = Ask(context, interactive, "pg-port", "PostgreSQL port", current.GetInteger("database.postgres.port").ToString(CultureInfo.InvariantCulture),
                        v => int.TryParse(v, out var p) && p is > 0 and < 65536 ? null : "enter a port number");
                    values["database.postgres.database"] = Ask(context, interactive, "pg-database", "Database name", current.GetString("database.postgres.database"));
                    values["database.postgres.username"] = Ask(context, interactive, "pg-user", "Database user", current.GetString("database.postgres.username"));
                    values["database.postgres.ssl_mode"] = Ask(context, interactive, "pg-ssl-mode", "SSL mode (Disable, Prefer, Require, VerifyFull)", current.GetString("database.postgres.ssl_mode"));
                    var passwordFile = context.Args.Option("pg-password-file");
                    if (passwordFile is not null)
                    {
                        values["database.postgres.password_file"] = passwordFile;
                    }
                    else
                    {
                        postgresPassword = context.Args.Option("pg-password")
                            ?? (interactive ? ReadSecret("Database password (stored in config/secrets, input hidden): ") : null);
                        if (!string.IsNullOrEmpty(postgresPassword))
                        {
                            values["database.postgres.password_file"] = PostgresPasswordFile;
                        }
                    }
                }

                values["database.postgres.schema"] = Ask(context, interactive, "pg-schema", "Schema for sbox-ns tables", current.GetString("database.postgres.schema"));
            }

            var error = await TestDatabaseAsync(context, values, postgresPassword);
            if (error is null)
            {
                break;
            }

            Console.Error.WriteLine($"Database connection failed: {error}");
            if (!interactive)
            {
                return CliApp.Failure;
            }

            Console.WriteLine("Let's try that again (nothing has been written).");
        }

        values["server.listen"] = Ask(context, interactive, "listen", "Listen address", current.GetString("server.listen"),
            v => ListenAddress.TryParse(v, out _) ? null : "use host:port, e.g. 0.0.0.0:8080");
        values["server.public_url"] = Ask(context, interactive, "public-url", "Public URL players will use (optional, e.g. https://ns.example.com)", current.GetString("server.public_url"));

        var candidate = ConfigLoader.Load(context.Args.Option("config-dir"), context.Args.Option("data-dir"), values);
        if (!candidate.IsValid)
        {
            ConfigCommands.PrintIssues(candidate);
            return CliApp.Usage;
        }

        Persist(candidate.ConfigDirectory, current.LoadedFiles.Count > 0, values, postgresPassword);
        var written = ConfigLoader.Load(context.Args.Option("config-dir"), context.Args.Option("data-dir"));
        ServerSecrets.EnsureAndLoad(written, path => Console.WriteLine($"Generated {path}"));

        await using (var services = CliServices.Build(written))
        {
            var result = await services.GetRequiredService<INetworkStorageStoreAdmin>().MigrateAsync(CancellationToken.None);
            Console.WriteLine($"Database ready (schema version {result.ToVersion}).");
            await AdminCommands.ConfigureDuringSetupAsync(context, written, services, interactive);
        }
        await NoticeCommands.ConfigureDuringSetupAsync(context, interactive);

        Console.WriteLine();
        Console.WriteLine($"Configuration written to {written.ConfigDirectory}");
        Console.WriteLine("Next steps:");
        Console.WriteLine("  sbox-ns start                          run in the foreground (or: sbox-ns service install)");
        Console.WriteLine("  Open /login for owner management (or the logged /setup URL when no owner exists)");
        Console.WriteLine("  sbox-ns project create \"My Game\"       create a project");
        Console.WriteLine("  sbox-ns key create <projectId> --type public");
        var url = string.IsNullOrWhiteSpace(written.GetString("server.public_url"))
            ? $"http://<this-host>:{(ListenAddress.TryParse(written.GetString("server.listen"), out var l) ? l.Port : 8080)}"
            : written.GetString("server.public_url");
        Console.WriteLine($"  In your game: NetworkStorage.Configure(projectId, publicKey, \"{url}\")");
        return CliApp.Ok;
    }

    private static void Persist(string configDirectory, bool hasExistingFiles, Dictionary<string, string> values, string? postgresPassword)
    {
        if (postgresPassword is not null)
        {
            var path = Path.Combine(configDirectory, PostgresPasswordFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ConfigFiles.WriteAtomically(path, postgresPassword);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        var typed = values.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var definition = SettingDefinitions.Find(kv.Key)!;
                ConfigLoader.TryConvert(definition, kv.Value, out var value, out _);
                return value;
            },
            StringComparer.Ordinal);

        if (!hasExistingFiles)
        {
            ConfigFiles.WriteAll(configDirectory, typed);
            return;
        }

        foreach (var (key, value) in typed)
        {
            ConfigFiles.SetValue(configDirectory, SettingDefinitions.Find(key)!, value);
        }
    }

    private static async Task<string?> TestDatabaseAsync(CliContext context, Dictionary<string, string> values, string? postgresPassword)
    {
        var overrides = new Dictionary<string, string>(values, StringComparer.Ordinal);
        if (postgresPassword is not null)
        {
            overrides.Remove("database.postgres.password_file");
            overrides["database.postgres.password"] = postgresPassword;
        }

        var candidate = ConfigLoader.Load(context.Args.Option("config-dir"), context.Args.Option("data-dir"), overrides);
        if (!candidate.IsValid)
        {
            return string.Join("; ", candidate.Issues);
        }

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            Hosting.StoreRegistration.AddConfiguredStore(services, candidate);
            await using var provider = services.BuildServiceProvider();
            var admin = provider.GetRequiredService<INetworkStorageStoreAdmin>();
            var ping = await admin.PingAsync(CancellationToken.None);
            Console.WriteLine($"Connected to {admin.ProviderName} at {admin.RedactedTarget} ({ping.ServerVersion}).");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string Ask(CliContext context, bool interactive, string flag, string prompt, string defaultValue, Func<string, string?>? validate = null)
    {
        var fromFlag = context.Args.Option(flag);
        if (fromFlag is not null)
        {
            var flagError = validate?.Invoke(fromFlag);
            return flagError is null ? fromFlag : throw new CliException($"--{flag}: {flagError}", CliApp.Usage);
        }

        if (!interactive)
        {
            return defaultValue;
        }

        while (true)
        {
            Console.Write(string.IsNullOrEmpty(defaultValue) ? $"{prompt}: " : $"{prompt} [{defaultValue}]: ");
            var answer = Console.ReadLine()?.Trim();
            var value = string.IsNullOrEmpty(answer) ? defaultValue : answer;
            var error = validate?.Invoke(value);
            if (error is null)
            {
                return value;
            }

            Console.WriteLine($"  {error}");
        }
    }

    internal static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
