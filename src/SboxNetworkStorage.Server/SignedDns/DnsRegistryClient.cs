using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.SignedDns;

public sealed record DnsRegistration(string Hostname, string? Ipv4, string? Ipv6);

/// <summary>Signed client for the sboxns.com DNS registry (A/AAAA names pointing at this server's own IP).</summary>
public sealed partial class DnsRegistryClient(HttpClient http)
{
    public const string DefaultRegistry = "https://sboxcool.com/api/network-storage/dns";

    public static Uri ValidateRegistry(string value) => TunnelRegistryClient.ValidateRegistry(value, "DNS registry");

    [GeneratedRegex(@"^[a-z2-7]{12}\.n[0-9]{1,3}\.sboxns\.com$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HostnamePattern();

    /// <summary>True for any well-formed signed DNS hostname (<c>name.nN.sboxns.com</c>).</summary>
    public static bool IsHostname(string hostname) => HostnamePattern().IsMatch(hostname);

    /// <summary>Accepts only <c>&lt;name&gt;.n&lt;digits&gt;.sboxns.com</c> for this identity's own name.</summary>
    public static string ValidateHostname(string? hostname, string name)
    {
        if (hostname is null || !IsHostname(hostname) || !hostname.StartsWith(name + ".", StringComparison.Ordinal))
            throw new InvalidDataException("The registry returned an unexpected DNS hostname.");
        return hostname;
    }

    /// <summary>Normalizes an address of the given family; empty input stays empty.</summary>
    public static string NormalizeAddress(string? value, AddressFamily family)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!IPAddress.TryParse(value.Trim(), out var address))
            throw new ArgumentException($"'{value}' is not an IP address.");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != family)
            throw new ArgumentException($"'{value}' is not an {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address.");
        return address.ToString();
    }

    public async Task<DnsRegistration> RegisterAsync(string registry, DnsRequest request, CancellationToken ct)
    {
        if (request.Action is not ("register" or "update"))
            throw new ArgumentException("Register requests must use the register or update action.");
        using var response = await SendAsync(registry, request, ct);
        var result = await response.Content.ReadFromJsonAsync<DnsRegistration>(cancellationToken: ct)
            ?? throw new InvalidDataException("Empty DNS registry response.");
        return result with { Hostname = ValidateHostname(result.Hostname, request.Name) };
    }

    public async Task DeleteAsync(string registry, DnsRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(registry, request, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The registry did not acknowledge DNS name deletion.");
    }

    /// <summary>Asks the registry which public address this machine's outbound connection comes from.</summary>
    public async Task<IPAddress> WhoAmIAsync(string registry, CancellationToken ct)
    {
        var uri = new Uri(ValidateRegistry(registry).AbsoluteUri.TrimEnd('/') + "/whoami");
        using var response = await http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Public address lookup failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!body.RootElement.TryGetProperty("ip", out var ip) || ip.ValueKind != JsonValueKind.String
            || !IPAddress.TryParse(ip.GetString(), out var address))
            throw new InvalidDataException("The registry returned an invalid public address.");
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private async Task<HttpResponseMessage> SendAsync(string registry, DnsRequest request, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(ValidateRegistry(registry), request, ct);
        if (response.IsSuccessStatusCode) return response;
        // Do not echo remote response content.
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.ToString();
        response.Dispose();
        var targets = string.Join(" and ", new[] { request.Ipv4, request.Ipv6 }.Where(a => a.Length > 0)
            .Select(a => a.Contains(':') ? $"http://[{a}]:{request.Port}" : $"http://{a}:{request.Port}"));
        throw new HttpRequestException(status switch
        {
            400 => "The DNS registry rejected the request (invalid or non-public address, or a different key owns this name).",
            403 => "This DNS name is disabled by the operator.",
            409 => "The registry refused the request: this name has an active tunnel, or the request was replayed. Disable the tunnel or run the command again.",
            422 => $"Ownership proof failed: the registry could not reach {targets}/.well-known/sbox-ns/dns-proof/. Make sure the server is running with this identity and the HTTP port is open to the internet.",
            429 => $"DNS registry rate limit reached. Retry-After: {retryAfter ?? "unspecified"}.",
            503 => "No hosted DNS capacity is available right now. Try again later.",
            _ => $"DNS registry request failed (HTTP {status})."
        }, null, (HttpStatusCode)status);
    }

    public static HttpClient CreateHttpClient()
    {
        // A redirect must not move signed identity requests to another endpoint or downgrade HTTPS.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("sbox-ns-dns/1");
        return http;
    }
}
