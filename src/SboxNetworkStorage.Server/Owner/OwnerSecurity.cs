using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Owner;

public sealed class OwnerAccessPolicy(EffectiveConfig config)
{
    private readonly System.Net.IPNetwork[] networks = config.GetString("adminpanel.allowed_ips")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => System.Net.IPNetwork.Parse(value.Contains('/') ? value : value + (value.Contains(':') ? "/128" : "/32"))).ToArray();

    public bool Allows(IPAddress? address)
    {
        if (!config.GetBoolean("adminpanel.enabled")) return false;
        if (networks.Length == 0) return true;
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(network => network.Contains(address));
    }
}

public sealed class OwnerTurnstile(EffectiveConfig config, HttpClient client)
{
    public bool Enabled => config.GetBoolean("adminpanel.turnstile.enabled");
    public async Task<bool> VerifyAsync(string? token, string action, IPAddress? address, CancellationToken ct)
    {
        if (!Enabled) return true;
        if (token is not { Length: >= 1 and <= 2048 }) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = config.GetString("adminpanel.turnstile.secret"), ["response"] = token,
                ["remoteip"] = address?.ToString() ?? string.Empty
            });
            using var response = await client.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", body, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            var result = await response.Content.ReadFromJsonAsync<Verification>(cancellationToken: timeout.Token);
            return result?.Success == true && result.Action == action && string.Equals(result.Hostname,
                config.GetString("adminpanel.turnstile.hostname"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException)
        { return false; }
    }
    public sealed record Verification([property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("action")] string? Action, [property: JsonPropertyName("hostname")] string? Hostname);
}
