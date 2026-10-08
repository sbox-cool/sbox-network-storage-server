using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.SignedDns;

/// <summary>Keeps the hosted DNS name pointing at this server's public address (dns.enabled and dns.auto_address).</summary>
public sealed class DnsAddressService(EffectiveConfig config, ILogger<DnsAddressService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetBoolean("dns.enabled") || !config.GetBoolean("dns.auto_address")) return;
        using var http = DnsRegistryClient.CreateHttpClient();
        var manager = new DnsManager(new DnsRegistryClient(http));
        var delay = FirstCheck;
        var backoff = MinBackoff;
        try
        {
            while (true)
            {
                // The first check waits until the HTTP listener can answer the ownership proof.
                await Task.Delay(delay, stoppingToken);
                try
                {
                    if (await manager.RefreshAddressAsync(config, stoppingToken))
                        logger.LogInformation("Updated hosted DNS name {Hostname} to the new public address", config.GetString("dns.hostname"));
                    delay = Interval;
                    backoff = MinBackoff;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Messages are local or status-only; registry bodies are never surfaced.
                    logger.LogWarning("Hosted DNS address check failed ({Error}); retrying in {Minutes} minute(s)",
                        ex is HttpRequestException { StatusCode: null } ? "registry unreachable" : ex.Message, (int)backoff.TotalMinutes);
                    delay = backoff;
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
