using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

namespace SboxNetworkStorage.Server.Infrastructure;

/// <summary>
/// Fails fast at startup if the Network Storage auth-session HMAC secret is not
/// configured. Without this guard the <c>POST /v3/auth-sessions/{projectId}/create</c>
/// endpoint (and its siblings) throws <see cref="InvalidOperationException"/> on the
/// first real request, which is harder to diagnose than a failed process start.
/// </summary>
public sealed class AuthSessionSecretValidationHostedService(
    IAuthSessionSecretProvider secretProvider,
    ILogger<AuthSessionSecretValidationHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Force the provider to resolve the secret. It throws a descriptive
        // InvalidOperationException when none of the configured keys is set.
        var secret = secretProvider.GetSecret();

        logger.LogInformation(
            "Network Storage auth session secret resolved ({Length} chars).",
            secret.Length);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
