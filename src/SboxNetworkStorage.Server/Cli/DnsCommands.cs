using System.Security.Cryptography;
using System.Text.Json;
using SboxNetworkStorage.Server.SignedDns;

namespace SboxNetworkStorage.Server.Cli;

public static class DnsCommands
{
    public const string Usage = "Usage: sbox-ns dns enable [--ipv4 IP] [--ipv6 IP] [--accept-letsencrypt-terms] [--email ADDRESS] | dns status [--json] | dns disable";

    public static async Task<int> RunAsync(CliContext context)
    {
        var action = context.RequirePositional(1, "enable|status|disable");
        if (action is not ("enable" or "status" or "disable"))
            throw new CliException(Usage, CliApp.Usage);
        var config = context.LoadValidConfig();
        try
        {
            if (action != "status")
            {
                using var http = DnsRegistryClient.CreateHttpClient();
                var manager = new DnsManager(new DnsRegistryClient(http));
                if (action == "enable")
                    await manager.EnableAsync(config, new DnsEnableOptions(context.Args.Option("ipv4"), context.Args.Option("ipv6"),
                        context.Args.Flag("accept-letsencrypt-terms"), context.Args.Option("email")), CancellationToken.None);
                else await manager.DisableAsync(config, CancellationToken.None);
                config = context.LoadValidConfig();
            }
            var status = DnsStatus.Read(config);
            if (context.Args.Flag("json"))
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, dns = status, restartRequired = action != "status" },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            else
            {
                Console.WriteLine($"DNS name: {(status.Enabled ? "enabled" : "disabled")}");
                if (status.Enabled)
                {
                    Console.WriteLine($"Hostname: {status.Hostname}");
                    Console.WriteLine($"IPv4: {(status.Ipv4.Length > 0 ? status.Ipv4 : "-")}");
                    Console.WriteLine($"IPv6: {(status.Ipv6.Length > 0 ? status.Ipv6 : "-")}");
                    Console.WriteLine($"Automatic address updates: {(status.AutoAddress ? "on" : "off")}");
                    Console.WriteLine($"Public URL: https://{status.Hostname}");
                }
                Console.WriteLine($"Registry: {status.Registry}");
                if (action != "status")
                {
                    Console.WriteLine("Restart the server to apply: sbox-ns service restart");
                    if (status.Enabled)
                        Console.WriteLine($"Ports 443 (HTTPS) and {status.ProofPort} (HTTP) must be reachable from the internet.");
                }
            }
            return CliApp.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                      or CryptographicException or JsonException or TaskCanceledException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            // Registry content is never included. Local filesystem/signature errors are safe.
            var message = ex is HttpRequestException { StatusCode: not null } ? ex.Message
                : ex is HttpRequestException or JsonException ? "DNS registry could not be reached or returned an invalid response."
                : ex is TaskCanceledException ? "DNS operation timed out. Previous configuration was retained."
                : ex.Message;
            throw new CliException(message);
        }
    }
}
