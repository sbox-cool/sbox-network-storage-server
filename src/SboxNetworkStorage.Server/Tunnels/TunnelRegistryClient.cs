using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Tunnels;

public sealed record TunnelRegistration(string Hostname, string TunnelToken);

public sealed class TunnelRegistryClient(HttpClient http)
{
    public const string DefaultRegistry = "https://sboxcool.com/api/network-storage/tunnels";
    public static Uri ValidateRegistry(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && IsLoopback(uri.Host))))
            throw new InvalidOperationException("The tunnel registry must be HTTPS (HTTP is allowed only on loopback for local testing).");
        return uri;
    }

    private static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    public static string PublicUrl(string hostname, string name)
    {
        var expected = $"{name}.sboxns.com";
        if (hostname == expected) return $"https://{expected}";
        if (!Uri.TryCreate(hostname, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != expected || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("The registry returned an unexpected tunnel hostname or HTTPS URL.");
        return $"https://{expected}";
    }

    public async Task<TunnelRegistration> RegisterAsync(string registry, TunnelRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(registry, request, ct);
        var result = await response.Content.ReadFromJsonAsync<TunnelRegistration>(cancellationToken: ct)
            ?? throw new InvalidDataException("Empty tunnel registry response.");
        _ = PublicUrl(result.Hostname, request.Name);
        if (string.IsNullOrWhiteSpace(result.TunnelToken) || result.TunnelToken.Length > 16384
            || result.TunnelToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=' or '-' or '_')))
            throw new InvalidDataException("Invalid tunnel token in registry response.");
        return result;
    }

    public async Task DeleteAsync(string registry, TunnelRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(registry, request, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The registry did not acknowledge tunnel deletion.");
    }

    private async Task<HttpResponseMessage> SendAsync(string registry, TunnelRequest request, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(ValidateRegistry(registry), request, ct);
        if (response.IsSuccessStatusCode) return response;
        // Do not echo remote response content, which can include credentials.
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.ToString();
        response.Dispose();
        throw new HttpRequestException(status switch
        {
            403 => "Tunnel registration is disabled by the operator.",
            409 => "The registry rejected a replayed request. Run the command again.",
            429 => $"Tunnel registry rate limit reached. Retry-After: {retryAfter ?? "unspecified"}.",
            503 => "Tunnel provisioning is temporarily unavailable.",
            _ => $"Tunnel registry request failed (HTTP {status})."
        }, null, (HttpStatusCode)status);
    }

    public static HttpClient CreateHttpClient()
    {
        // A redirect must not move signed identity requests to another endpoint or downgrade HTTPS.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("sbox-ns-tunnel/1");
        return http;
    }
}
