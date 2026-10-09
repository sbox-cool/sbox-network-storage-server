using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Telemetry;

/// <summary>
/// The complete opt-in usage statistics report (schema 1). Every field is listed in
/// docs/self-hosting.md; nothing else is ever sent: no IPs, emails, project IDs, keys or game data.
/// </summary>
public sealed record UsageTelemetryPayload(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("installId")] Guid InstallId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("container")] bool Container,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("tunnel")] bool Tunnel,
    [property: JsonPropertyName("uptimeHours")] long UptimeHours,
    [property: JsonPropertyName("projects")] long Projects,
    [property: JsonPropertyName("players")] long? Players,
    [property: JsonPropertyName("activePlayers30d")] long? ActivePlayers30d);

/// <summary>Builds and sends the anonymous usage statistics report.</summary>
public static class UsageTelemetry
{
    public const int Schema = 1;
    public const string IdFileName = "telemetry-id";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Returns the endpoint when it is HTTPS (HTTP only on loopback) without credentials, query or fragment; otherwise null.</summary>
    public static Uri? ValidateEndpoint(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            && (endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback)
            && endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0
                ? endpoint
                : null;

    public static async Task<UsageTelemetryPayload> BuildAsync(EffectiveConfig config, INetworkStorageStoreAdmin store, Guid installId,
        TimeSpan uptime, DateTimeOffset now, CancellationToken ct)
    {
        var counts = await store.CountUsageAsync(now.AddDays(-30).ToUnixTimeMilliseconds(), ct);
        return new UsageTelemetryPayload(
            Schema,
            installId,
            BuildInfo.Version,
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other",
            RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => "other" },
            IsContainer(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")),
            config.GetString("database.provider"),
            config.GetBoolean("tunnel.enabled"),
            Math.Max(0, (long)uptime.TotalHours),
            counts.Projects,
            counts.Players,
            counts.ActivePlayers);
    }

    public static string Serialize(UsageTelemetryPayload payload) => JsonSerializer.Serialize(payload);

    /// <summary>Time since this process started; zero when the platform does not report it.</summary>
    public static TimeSpan ProcessUptime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            return uptime > TimeSpan.Zero ? uptime : TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>HTTP client that never follows redirects and gives up after 15 seconds.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"sbox-ns/{BuildInfo.Version}");
        return client;
    }

    /// <summary>
    /// Posts the report once. Returns true on a 2xx response; any other status, network failure or timeout
    /// is logged at Debug and returns false. Only cancellation of <paramref name="ct"/> propagates.
    /// </summary>
    public static async Task<bool> SendAsync(HttpClient http, Uri endpoint, UsageTelemetryPayload payload, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, ct);
            if (response.IsSuccessStatusCode) return true;
            logger.LogDebug("Usage statistics were not accepted (HTTP {Status}); skipping until the next interval", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Usage statistics could not be sent; skipping until the next interval");
            return false;
        }
    }

    private static bool IsContainer(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
