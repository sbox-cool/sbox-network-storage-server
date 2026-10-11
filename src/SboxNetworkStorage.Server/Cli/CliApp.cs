using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Entry point: one binary is both the server (<c>start</c>) and the operator CLI.</summary>
public static class CliApp
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;

    public const string HelpText = """
        sbox-ns - self-hosted s&box Network Storage server

        Usage: sbox-ns <command> [options]

        Get started
          quickstart <name> [--public-url URL] [--json]
                                        Configure (if needed), create the project and keys,
                                        and print the line to add to your game. Safe to re-run.
          mcp                           Serve the Model Context Protocol over stdio for coding agents
                                        (e.g. command: ssh my-vps sudo sbox-ns mcp)
          dev <tool>                    The agent backend behind mcp: reads JSON arguments on stdin,
                                        prints JSON (see docs/mcp.md)

        Server
          start                         Run the server in the foreground
          setup                         Create or update the config folder interactively
                                        (--non-interactive with --database, --listen, --public-url, --pg-* flags)
                                        (--admin-username with --admin-password-file FILE or NS_ADMIN_PASSWORD)
          doctor [--project ID] [--json]
                                        Check config, database, port, TLS, disk and updates;
                                        with --project, the authority check for that project instead
          version                       Print the version

        HTTPS tunnel (optional, no account required)
          tunnel enable [--json]        Register an HTTPS name and bind the origin to loopback; restart afterward
          tunnel status [--json]        Show the name, registry and supervised connector state (no secrets)
          tunnel disable [--json]       Delete the hosted name and restore previous listener/public URL; restart afterward

        Free hosted name with your own IP (optional, no account required)
          dns enable [--ipv4 IP] [--ipv6 IP] --accept-letsencrypt-terms --email ADDRESS
                                        Publish <name>.nN.sboxns.com pointing at this server (needs a public IP,
                                        ports 443 and the HTTP port open, and the server running); restart afterward
          dns status [--json]           Show the hosted name, published addresses and registry
          dns disable                   Delete the hosted name and restore the previous public URL/TLS; restart afterward

        Optional security notices
          register --email ADDRESS       Request security/update notices (email confirmation required)
          register --remove              Unsubscribe this install; no automatic reporting

        Anonymous usage statistics (opt-in, off by default)
          telemetry status              Show whether usage statistics are enabled and where they are sent
          telemetry enable|disable      Opt in or out (restart the server afterward)
          telemetry preview             Print the exact JSON that would be sent, without sending it

        Configuration
          config path                   Print the config folder
          config show [--show-secrets]  Print every effective setting and where it came from
          config get <key> [--show-secrets]  Print one setting (secrets redacted)
          config set <key> <value>      Change one setting in its file (comments are kept)
          config validate               Validate the config folder
          config edit [file]            Open a config file in $EDITOR and validate it

        Database
          db test                       Connect to the configured database
          db status                     Show the schema version
          db migrate                    Apply pending schema migrations
          db backup [--output FILE]     Back up the database
          db restore <FILE>             Restore a backup (stop the server first)

        Export and import (move servers, switch SQLite <-> PostgreSQL; docs/export.md)
          export [--out FILE] [--no-secrets]   Write the whole server (data + config) to one .tar.gz
          import <FILE> [--config] [--force]   Restore an export into the configured database
                                        (--config also restores config and secrets; --force merges into a non-empty database)
          import <FILE> --verify-only   Validate an archive (format, row counts, rows, owner/workspace contract)
                                        in a throwaway database; writes nothing to this server

        Local owner
          admin create [--username NAME] [--password-file FILE]
          admin reset-password [--password-file FILE]
          admin reset-2fa                Recover locally by removing the authenticator and invalidating sessions
          adminpanel disable|enable      Persist owner endpoint availability (restart the server); game APIs remain available
                                        Password input is hidden; NS_ADMIN_USERNAME / NS_ADMIN_PASSWORD also work
          admin login-link [--minutes N]  Print a single-use owner login link (default 15, max 60 minutes);
                                        creates the owner if none exists. Run on the server, e.g. over SSH

        Projects and API keys
          project create <name> [--hosting player-hosted|dedicated|hybrid|unset]
                                        Create a project (hosting profile defaults to unset)
          project list                  List projects
          project authority <projectId> [--json]
                                        Advisory check: game data a client could still change, and records
                                        damaged by the 0.4.0 update-ops bug
          project delete <projectId>    Delete a project
          key create <projectId> --type public|secret [--label LABEL]
          key list <projectId>
          key revoke <projectId> <key>

        Service
          service install|uninstall     Register sbox-ns as a system service (systemd, launchd, Windows)
          service install --instance NAME --port PORT [--auto-update]
                                        Add instance NAME (unit sbox-ns@NAME, /etc/sbox-ns/NAME, /var/lib/sbox-ns/NAME,
                                        listening on 127.0.0.1:PORT); Linux, as root
          service install --auto-update Also install the sbox-ns-update timer and set updates.auto_install = true
          service uninstall --instance NAME
          service start|stop|restart|status
          logs [-f]                     Show service logs

        Updates (unattended only when updates.auto_install = true)
          update [--check] [--version X.Y.Z]   Check for or install a release
          update --auto [--all-instances]      Unattended update for updates.channel inside updates.window; every
                                        instance is health-checked and all are rolled back on any failure
                                        (exit 0 done/nothing to do, 1 rolled back, 2 config, 3 rollback incomplete,
                                        4 feed unavailable)
          rollback [--all-instances]    Restore the binary and database(s) from before the last update

        Install layout (Linux, as root; docs/self-hosting.md "Config and state folders")
          layout migrate                Move runtime files (generated secrets, tunnel and DNS state) out of the config
                                        folder into <data>/state so the service cannot write the config; no-op when done
          layout migrate --revert       Move them back (older binaries expect them in the config folder)

        Global options
          --config-dir DIR   Config folder (default: NS_CONFIG_DIR, /etc/sbox-ns, or <install dir>/config)
          --data-dir DIR     Data folder (default: NS_DATA_DIR, server.data_dir, /var/lib/sbox-ns, or <install dir>/data)
          --listen ADDR      Override server.listen, e.g. 0.0.0.0:8080
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = CliArguments.Parse(args);
        var command = parsed.Positional(0);
        if (command is null || parsed.Flag("help", "h") || command is "help")
        {
            Console.WriteLine(HelpText);
            return command is null && !parsed.Flag("help", "h") ? Usage : Ok;
        }

        var context = new CliContext(parsed);
        try
        {
            if (command is "db" or "export" or "import" or "admin" or "project" or "key" or "dev")
            {
                if (await RuntimeCommand.TryRunAsync(context.LoadValidConfig(), args) is { } runtimeExit)
                    return runtimeExit;
            }
            return command switch
            {
                "start" => await StartAsync(context),
                "version" or "--version" => Version(),
                "setup" => await SetupCommand.RunAsync(context),
                "quickstart" => await QuickstartCommand.RunAsync(context),
                "mcp" => await McpServer.RunStdioAsync(context),
                "dev" => await DevCommands.RunAsync(context),
                "tunnel" => await TunnelCommands.RunAsync(context),
                "dns" => await DnsCommands.RunAsync(context),
                "register" => await NoticeCommands.RunAsync(context),
                "telemetry" => await TelemetryCommands.RunAsync(context),
                "config" => ConfigCommands.Run(context),
                "db" => await DatabaseCommands.RunAsync(context),
                "export" => await ExportCommands.ExportAsync(context),
                "import" => await ExportCommands.ImportAsync(context),
                "admin" => await AdminCommands.RunAsync(context),
                "adminpanel" => RunAdminPanel(context),
                "project" => await ProjectCommands.RunProjectAsync(context),
                "key" => await ProjectCommands.RunKeyAsync(context),
                "service" => await ServiceCommands.RunAsync(context),
                "logs" => await ServiceCommands.LogsAsync(context),
                "doctor" => await DoctorCommand.RunAsync(context),
                "update" => await UpdateCommands.UpdateAsync(context),
                "rollback" => await UpdateCommands.RollbackAsync(context),
                "layout" => await LayoutCommands.RunAsync(context),
                _ => UnknownCommand(command)
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static async Task<int> StartAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        return await ServerHost.RunAsync(config, []);
    }

    private static int RunAdminPanel(CliContext context)
    {
        var command = context.RequirePositional(1, "disable|enable");
        if (command is not ("disable" or "enable")) throw new CliException("Use adminpanel disable or enable.", Usage);
        var config = context.LoadValidConfig();
        ConfigFiles.SetValue(config.ConfigDirectory, SettingDefinitions.All.Single(value => value.Key == "adminpanel.enabled"), command == "enable");
        Console.WriteLine($"Owner administration {(command == "enable" ? "enabled" : "disabled")} in configuration. Restart the server to apply. Game APIs are unchanged.");
        return Ok;
    }

    private static int Version()
    {
        Console.WriteLine($"sbox-ns {BuildInfo.Version} ({BuildInfo.RuntimeIdentifier})");
        return Ok;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'. Run `sbox-ns help`.");
        return Usage;
    }
}

/// <summary>An expected, user-facing failure; printed without a stack trace.</summary>
public sealed class CliException(string message, int exitCode = CliApp.Failure) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Parsed arguments plus lazily loaded configuration shared by all commands.</summary>
public sealed class CliContext(CliArguments args)
{
    public CliArguments Args { get; } = args;

    public IReadOnlyDictionary<string, string> FlagOverrides
    {
        get
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Args.Option("listen") is { } listen)
            {
                overrides["server.listen"] = listen;
            }

            return overrides;
        }
    }

    public EffectiveConfig LoadConfig()
        => ConfigLoader.Load(Args.Option("config-dir"), Args.Option("data-dir"), FlagOverrides);

    /// <summary>Loads configuration and fails with every validation issue listed.</summary>
    public EffectiveConfig LoadValidConfig()
    {
        var config = LoadConfig();
        if (config.IsValid)
        {
            return config;
        }

        foreach (var issue in config.Issues)
        {
            Console.Error.WriteLine($"  {issue}");
        }

        throw new CliException($"configuration in {config.ConfigDirectory} is invalid ({config.Issues.Count} problem(s))", CliApp.Usage);
    }

    public string RequirePositional(int index, string name)
        => Args.Positional(index) ?? throw new CliException($"missing <{name}>. Run `sbox-ns help`.", CliApp.Usage);
}
