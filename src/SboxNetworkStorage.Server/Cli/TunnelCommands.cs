using System.Security.Cryptography;
using System.Text.Json;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Cli;

public static class TunnelCommands
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var action = context.RequirePositional(1, "enable|status|disable");
        if (action is not ("enable" or "status" or "disable"))
            throw new CliException("Usage: sbox-ns tunnel enable|status|disable [--json]", CliApp.Usage);
        var config = context.LoadValidConfig();
        try
        {
            if (action != "status")
            {
                using var registryHttp = TunnelRegistryClient.CreateHttpClient();
                using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                downloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("sbox-ns-tunnel/1");
                var manager = new TunnelManager(new TunnelRegistryClient(registryHttp), new CloudflaredInstaller(downloadHttp));
                if (action == "enable") await manager.EnableAsync(config, CancellationToken.None);
                else await manager.DisableAsync(config, CancellationToken.None);
                config = context.LoadValidConfig();
            }
            var status = TunnelConnectorState.Read(config);
            if (context.Args.Flag("json"))
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, tunnel = status, restartRequired = action != "status" },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            else
            {
                Console.WriteLine($"Tunnel: {(status.Enabled ? "enabled" : "disabled")}");
                Console.WriteLine($"Name: {status.Name}");
                Console.WriteLine($"Hostname: {status.Hostname}");
                Console.WriteLine($"Registry: {status.Registry}");
                Console.WriteLine($"Connector: {status.ConnectorState} (cloudflared {status.ConnectorVersion})");
                if (action != "status") Console.WriteLine("Restart the server to apply the listener and public URL: sbox-ns service restart");
                if (status.Enabled) Console.WriteLine($"Public URL: https://{status.Hostname}");
            }
            return CliApp.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                      or CryptographicException or PlatformNotSupportedException or JsonException or TaskCanceledException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            // Registry and child content are never included. Local filesystem/signature errors are safe.
            var message = ex is HttpRequestException { StatusCode: not null } ? ex.Message
                : ex is HttpRequestException or JsonException ? "Tunnel registry could not be reached or returned an invalid response."
                : ex is TaskCanceledException ? "Tunnel operation timed out. Previous configuration was retained."
                : ex.Message;
            throw new CliException(message);
        }
    }
}
