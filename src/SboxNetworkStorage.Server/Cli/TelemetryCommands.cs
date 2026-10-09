using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary><c>sbox-ns telemetry status|enable|disable|preview</c>: opt-in anonymous usage statistics.</summary>
public static class TelemetryCommands
{
    public const string SetupPrompt = "Share anonymous usage statistics (version, platform, project and player counts)? [y/N] ";

    public static async Task<int> RunAsync(CliContext context)
    {
        switch (context.Args.Positional(1))
        {
            case "status":
                Status(context.LoadValidConfig());
                return CliApp.Ok;
            case "enable":
                Enable(context.LoadValidConfig());
                return CliApp.Ok;
            case "disable":
            {
                SetEnabled(context.LoadValidConfig(), false);
                Console.WriteLine("Anonymous usage statistics disabled. Restart the server to apply (sbox-ns service restart).");
                return CliApp.Ok;
            }
            case "preview":
                Console.WriteLine(await PreviewAsync(context.LoadValidConfig()));
                return CliApp.Ok;
            default:
                throw new CliException("Use telemetry status|enable|disable|preview.", CliApp.Usage);
        }
    }

    public static void ConfigureDuringSetup(CliContext context, bool interactive)
    {
        if (!interactive) return;
        var config = context.LoadValidConfig();
        if (config.GetBoolean("telemetry.enabled")) return;
        Console.Write(SetupPrompt);
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes")
        {
            try { Enable(config); }
            catch (CliException ex) { Console.Error.WriteLine($"Usage statistics were not enabled: {ex.Message}"); }
        }
    }

    private static void Status(EffectiveConfig config)
    {
        var enabled = config.GetBoolean("telemetry.enabled");
        var endpoint = config.GetString("telemetry.endpoint");
        Console.WriteLine($"Anonymous usage statistics: {(enabled ? "enabled" : "disabled")}");
        Console.WriteLine($"Endpoint: {endpoint}{(UsageTelemetry.ValidateEndpoint(endpoint) is null ? " (invalid: HTTPS required except loopback)" : string.Empty)}");
        Console.WriteLine($"Telemetry ID file: {config.TelemetryIdPath} ({(File.Exists(config.TelemetryIdPath) ? "present" : "not created")})");
        Console.WriteLine(enabled
            ? "Sent about 10 minutes after the server starts, then every 24 hours. Preview: sbox-ns telemetry preview"
            : "Nothing is sent. Opt in with: sbox-ns telemetry enable");
    }

    private static void Enable(EffectiveConfig config)
    {
        if (UsageTelemetry.ValidateEndpoint(config.GetString("telemetry.endpoint")) is null)
            throw new CliException("telemetry.endpoint must be an HTTPS endpoint (HTTP is allowed only on loopback for local tests).", CliApp.Usage);
        _ = LoadOrCreateId(config);
        SetEnabled(config, true);
        Console.WriteLine("Anonymous usage statistics enabled. Restart the server to apply (sbox-ns service restart).");
        Console.WriteLine("See exactly what is sent with: sbox-ns telemetry preview");
    }

    /// <summary>The exact JSON the server would send now, without sending it or creating the telemetry ID.</summary>
    private static async Task<string> PreviewAsync(EffectiveConfig config)
    {
        var id = PrivateIdFile.Read(config.TelemetryIdPath);
        if (id is null)
            Console.Error.WriteLine("No telemetry ID exists yet; showing a random placeholder. A new random ID is created on enable.");
        await using var services = CliServices.Build(config);
        var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
        await admin.MigrateAsync(CancellationToken.None);
        var payload = await UsageTelemetry.BuildAsync(config, admin, id ?? Guid.NewGuid(),
            UsageTelemetry.ProcessUptime(), DateTimeOffset.UtcNow, CancellationToken.None);
        return UsageTelemetry.Serialize(payload);
    }

    /// <summary>
    /// State layout: the managed telemetry overlay in the state folder, so the operator's server.toml is never written.
    /// Legacy layout: <c>server.toml</c> in the config folder, as before.
    /// </summary>
    private static void SetEnabled(EffectiveConfig config, bool enabled)
    {
        if (config.Layout == ConfigLayout.Legacy)
        {
            ConfigFiles.SetValue(config.ConfigDirectory, SettingDefinitions.Find("telemetry.enabled")!, enabled);
            return;
        }

        ManagedOverlay.Write(config, StateLayout.TelemetryOverlayFile,
            "Managed by sbox-ns telemetry enable|disable.", new Dictionary<string, object> { ["telemetry.enabled"] = enabled });
    }

    private static Guid LoadOrCreateId(EffectiveConfig config)
    {
        using var identity = RuntimeIdentity.Enter(config);
        config.EnsureRuntimeDirectory();
        return PrivateIdFile.LoadOrCreate(config.TelemetryIdPath)
            ?? throw new CliException($"The telemetry ID file {config.TelemetryIdPath} is invalid. Delete it to generate a new random ID.");
    }
}
