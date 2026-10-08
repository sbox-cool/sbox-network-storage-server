using System.Collections.Concurrent;
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
/// Tokens are single-use and never cached; a per-steamId failure tracker blocks
/// repeated failures, mirroring the Bun rate limiter.
/// </summary>
public sealed partial class FacepunchSboxAuthVerifier : ISboxAuthVerifier
{
    private const string VerifyUrl = "https://public.facepunch.com/sbox/auth/token";
    private const int FailureThreshold = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly ILogger<FacepunchSboxAuthVerifier> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, FailureState> _failures = new(StringComparer.Ordinal);

    public FacepunchSboxAuthVerifier(HttpClient http, ILogger<FacepunchSboxAuthVerifier> logger, TimeProvider time)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    public Task<SboxAuthResult> CheckAsync(SboxAuthCheck check, CancellationToken cancellationToken)
    {
        var hasClient = !string.IsNullOrEmpty(check.ClientSteamId);
        if (hasClient && !string.IsNullOrEmpty(check.ClientToken) && !string.IsNullOrEmpty(check.ProxySignature))
            return VerifyProxyAsync(check, cancellationToken);
        if (hasClient)
            return Task.FromResult(new SboxAuthResult(false, null,
                "Proxy request missing client token (x-on-behalf-of-token) or signature (x-proxy-signature)"));
        return VerifyDirectAsync(check.HostToken, check.HostSteamId, cancellationToken);
    }

    private async Task<SboxAuthResult> VerifyDirectAsync(string token, string steamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(steamId))
            return new SboxAuthResult(false, null, $"Missing token or steamId. token={(string.IsNullOrEmpty(token) ? "NO" : "YES")} steamId={(string.IsNullOrEmpty(steamId) ? "NONE" : steamId)}");

        if (IsBlocked(steamId))
            return new SboxAuthResult(false, null, "Too many failed auth attempts. Try again later.");

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

                RecordFailure(steamId);
                if (status is 401 or 403)
                    return new SboxAuthResult(false, null, $"Facepunch rejected the auth token (HTTP {status}). Generate a fresh token and retry.");
                return new SboxAuthResult(false, null, $"Facepunch returned HTTP {status}.");
            }

            var statusValue = StatusRegex().Match(body) is { Success: true } sm ? sm.Groups[1].Value : "unknown";
            var returnedSteamId = SteamIdRegex().Match(body) is { Success: true } im ? im.Groups[1].Value : string.Empty;

            if (!string.Equals(statusValue, "ok", StringComparison.Ordinal))
            {
                RecordFailure(steamId);
                return new SboxAuthResult(false, null, $"Facepunch rejected the auth token. Status={statusValue}. Generate a fresh token and retry.");
            }
            if (!string.Equals(returnedSteamId, steamId, StringComparison.Ordinal))
            {
                RecordFailure(steamId);
                return new SboxAuthResult(false, null, $"Steam ID mismatch: token={(string.IsNullOrEmpty(returnedSteamId) ? "missing" : returnedSteamId)} request={steamId}");
            }

            _failures.TryRemove(steamId, out _);
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
            RecordFailure(check.HostSteamId);
            return new SboxAuthResult(false, null,
                "Proxy auth: signature mismatch - request may have been tampered with or replayed from another server");
        }

        var host = await VerifyDirectAsync(check.HostToken, check.HostSteamId, cancellationToken);
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

    private bool IsBlocked(string steamId)
    {
        if (!_failures.TryGetValue(steamId, out var state)) return false;
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var elapsed = now - state.WindowStartMs;
        if (elapsed > (long)(FailureWindow + BlockDuration).TotalMilliseconds)
        {
            _failures.TryRemove(steamId, out _);
            return false;
        }
        return state.Count >= FailureThreshold && elapsed < (long)(FailureWindow + BlockDuration).TotalMilliseconds;
    }

    private void RecordFailure(string steamId)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _failures.AddOrUpdate(
            steamId,
            _ => new FailureState(1, now),
            (_, existing) => now - existing.WindowStartMs > (long)FailureWindow.TotalMilliseconds
                ? new FailureState(1, now)
                : new FailureState(existing.Count + 1, existing.WindowStartMs));
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private readonly record struct FailureState(int Count, long WindowStartMs);

    [GeneratedRegex("\"Status\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex StatusRegex();

    [GeneratedRegex("\"SteamId\"\\s*:\\s*(\\d+)")]
    private static partial Regex SteamIdRegex();
}
