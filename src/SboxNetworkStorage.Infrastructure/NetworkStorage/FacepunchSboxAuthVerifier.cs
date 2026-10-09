using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Native .NET port of <c>tools/sbox/auth.js</c>. Verifies s&amp;box player auth
/// tokens against Facepunch's token service (<c>verifySboxToken</c>) and proxied
/// "on-behalf-of" requests (<c>verifyProxyAuth</c> + <c>computeProxySignature</c>).
/// Successful verifications are cached briefly by <see cref="SboxTokenCache"/>; a
/// shared <see cref="SboxAuthFailureThrottle"/> keyed by (client IP, project) blocks
/// repeated failures.
/// </summary>
public sealed partial class FacepunchSboxAuthVerifier : ISboxAuthVerifier
{
    private const string VerifyUrl = "https://public.facepunch.com/sbox/auth/token";
    private readonly HttpClient _http;
    private readonly ILogger<FacepunchSboxAuthVerifier> _logger;
    private readonly SboxAuthFailureThrottle _throttle;
    private readonly SboxTokenCache _tokens;

    public FacepunchSboxAuthVerifier(
        HttpClient http,
        ILogger<FacepunchSboxAuthVerifier> logger,
        SboxAuthFailureThrottle throttle,
        SboxTokenCache tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    public Task<SboxAuthResult> CheckAsync(SboxAuthCheck check, CancellationToken cancellationToken)
    {
        if (_throttle.IsBlocked(check.ClientIp, check.ProjectId))
            return Task.FromResult(new SboxAuthResult(false, null, "Too many failed auth attempts. Try again later."));

        var hasClient = !string.IsNullOrEmpty(check.ClientSteamId);
        if (hasClient && !string.IsNullOrEmpty(check.ClientToken) && !string.IsNullOrEmpty(check.ProxySignature))
            return VerifyProxyAsync(check, cancellationToken);
        if (hasClient)
            return Task.FromResult(new SboxAuthResult(false, null,
                "Proxy request missing client token (x-on-behalf-of-token) or signature (x-proxy-signature)"));
        return VerifyDirectAsync(check, cancellationToken);
    }

    private async Task<SboxAuthResult> VerifyDirectAsync(SboxAuthCheck check, CancellationToken cancellationToken)
    {
        var token = check.HostToken;
        var steamId = check.HostSteamId;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(steamId))
            return new SboxAuthResult(false, null, $"Missing token or steamId. token={(string.IsNullOrEmpty(token) ? "NO" : "YES")} steamId={(string.IsNullOrEmpty(steamId) ? "NONE" : steamId)}");

        if (_tokens.IsVerified(token, steamId))
            return new SboxAuthResult(true, steamId, null);

        try
        {
            using var content = new StringContent(
                $"{{\"steamid\":{JsonString(steamId)},\"token\":{JsonString(token)}}}",
                Encoding.UTF8,
                "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.PostAsync(VerifyUrl, content, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status >= 500)
                    return new SboxAuthResult(false, null, $"Facepunch auth service unavailable (HTTP {status}). Retry shortly.");
                if (status == 429)
                    return new SboxAuthResult(false, null, $"Facepunch auth service rate limited this request (HTTP {status}). Retry shortly.");

                _throttle.RecordFailure(check.ClientIp, check.ProjectId);
                if (status is 401 or 403)
                    return new SboxAuthResult(false, null, $"Facepunch rejected the auth token (HTTP {status}). Generate a fresh token and retry.");
                return new SboxAuthResult(false, null, $"Facepunch returned HTTP {status}.");
            }

            var statusValue = StatusRegex().Match(body) is { Success: true } sm ? sm.Groups[1].Value : "unknown";
            var returnedSteamId = SteamIdRegex().Match(body) is { Success: true } im ? im.Groups[1].Value : string.Empty;

            if (!string.Equals(statusValue, "ok", StringComparison.Ordinal))
            {
                _throttle.RecordFailure(check.ClientIp, check.ProjectId);
                return new SboxAuthResult(false, null, $"Facepunch rejected the auth token. Status={statusValue}. Generate a fresh token and retry.");
            }
            if (!string.Equals(returnedSteamId, steamId, StringComparison.Ordinal))
            {
                _throttle.RecordFailure(check.ClientIp, check.ProjectId);
                return new SboxAuthResult(false, null, $"Steam ID mismatch: token={(string.IsNullOrEmpty(returnedSteamId) ? "missing" : returnedSteamId)} request={steamId}");
            }

            _tokens.MarkVerified(token, steamId);
            return new SboxAuthResult(true, returnedSteamId, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SboxAuthResult(false, null, "Facepunch auth request timed out. Retry shortly.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Facepunch s&box auth request failed for steamId={SteamId}", steamId);
            return new SboxAuthResult(false, null, $"Could not reach Facepunch auth service: {ex.Message}. Retry shortly.");
        }
    }

    private async Task<SboxAuthResult> VerifyProxyAsync(SboxAuthCheck check, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(check.HostToken) || string.IsNullOrEmpty(check.HostSteamId))
            return new SboxAuthResult(false, null, "Proxy auth: missing host token or steamId");
        if (string.IsNullOrEmpty(check.ClientToken) || string.IsNullOrEmpty(check.ClientSteamId))
            return new SboxAuthResult(false, null, "Proxy auth: missing client token or steamId - client must authorize the request");
        if (string.IsNullOrEmpty(check.ProxySignature))
            return new SboxAuthResult(false, null, "Proxy auth: missing proxy signature");

        var expected = ComputeProxySignature(check.ApiKey, check.ProjectId, check.EndpointSlug, check.ClientSteamId!, check.ClientToken!);
        if (!string.Equals(check.ProxySignature, expected, StringComparison.Ordinal))
        {
            _throttle.RecordFailure(check.ClientIp, check.ProjectId);
            return new SboxAuthResult(false, null,
                "Proxy auth: signature mismatch - request may have been tampered with or replayed from another server");
        }

        var host = await VerifyDirectAsync(check, cancellationToken);
        if (!host.Ok)
            return new SboxAuthResult(false, null, $"Proxy auth: host verification failed - {host.Error}");

        return new SboxAuthResult(true, check.ClientSteamId, null);
    }

    /// <summary>HMAC-SHA256(apiKey, "projectId:endpointSlug:clientSteamId:clientToken") as lowercase hex.</summary>
    internal static string ComputeProxySignature(string apiKey, string projectId, string endpointSlug, string clientSteamId, string clientToken)
    {
        var data = $"{projectId}:{endpointSlug}:{clientSteamId}:{clientToken}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey ?? string.Empty));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [GeneratedRegex("\"Status\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.None, 100)]
    private static partial Regex StatusRegex();

    [GeneratedRegex("\"SteamId\"\\s*:\\s*(\\d+)", RegexOptions.None, 100)]
    private static partial Regex SteamIdRegex();
}

