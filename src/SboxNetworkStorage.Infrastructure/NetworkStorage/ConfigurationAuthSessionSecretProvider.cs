using Microsoft.Extensions.Configuration;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Resolves the auth-session HMAC secret from configuration, mirroring the legacy server
/// <c>authSessionSecret()</c> env fallback chain:
/// <c>NETWORK_STORAGE_AUTH_SESSION_SECRET</c> → <c>SESSION_SECRET</c> →
/// <c>COOKIE_SECRET</c>. Throws when none is configured (fail-closed: tokens must
/// never be signed with an empty/guessable key).
/// </summary>
public sealed class ConfigurationAuthSessionSecretProvider : IAuthSessionSecretProvider
{
    private readonly IConfiguration _configuration;

    public ConfigurationAuthSessionSecretProvider(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public string GetSecret()
    {
        var secret = _configuration["NETWORK_STORAGE_AUTH_SESSION_SECRET"]
            ?? _configuration["SESSION_SECRET"]
            ?? _configuration["COOKIE_SECRET"]
            ?? _configuration["OPS_TOKEN"];
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException(
                "Network Storage auth session secret is not configured. " +
                "Set NETWORK_STORAGE_AUTH_SESSION_SECRET (or SESSION_SECRET / COOKIE_SECRET / OPS_TOKEN).");
        }
        return secret;
    }
}
