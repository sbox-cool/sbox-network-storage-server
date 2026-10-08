using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Telemetry;

/// <summary>
/// Sends the opt-in anonymous usage statistics report about 10 minutes after startup, then every 24 hours.
/// Does nothing unless <c>telemetry.enabled</c> is true. Never throws, never delays startup or shutdown,
/// and only logs at Debug.
/// </summary>
public sealed class UsageTelemetryService(
    EffectiveConfig config,
    INetworkStorageStoreAdmin store,
    ILogger<UsageTelemetryService> logger,
    HttpMessageHandler? handler = null,
    TimeSpan? firstSendDelay = null) : BackgroundService
{
    public static readonly TimeSpan FirstSendDelay = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetBoolean("telemetry.enabled")) return;
        if (UsageTelemetry.ValidateEndpoint(config.GetString("telemetry.endpoint")) is not { } endpoint)
        {
            logger.LogDebug("Usage statistics are enabled but telemetry.endpoint is not a valid HTTPS URL; nothing is sent");
            return;
        }

        using var http = UsageTelemetry.CreateHttpClient(handler);
        try
        {
            await Task.Delay(firstSendDelay ?? FirstSendDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SendOnceAsync(http, endpoint, stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendOnceAsync(HttpClient http, Uri endpoint, CancellationToken ct)
    {
        UsageTelemetryPayload payload;
        try
        {
            if (PrivateIdFile.LoadOrCreate(UsageTelemetry.IdPath(config)) is not { } id)
            {
                logger.LogDebug("The usage statistics ID file is invalid; delete {Path} to generate a new one", UsageTelemetry.IdPath(config));
                return;
            }
            payload = await UsageTelemetry.BuildAsync(config, store, id, UsageTelemetry.ProcessUptime(), DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Usage statistics could not be collected; skipping until the next interval");
            return;
        }
        await UsageTelemetry.SendAsync(http, endpoint, payload, logger, ct);
    }
}
