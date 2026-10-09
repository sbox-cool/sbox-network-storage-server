using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// <see cref="IEndpointWebhookSender"/> backed by <see cref="IHttpClientFactory"/>.
/// POSTs the endpoint <c>webhook</c> step's Discord payload as JSON (Discord returns
/// 204 on success). Network/HTTP failures map to <c>Ok=false</c> with the status/
/// message — the executor records that on the step result rather than failing the
/// whole endpoint, matching the legacy server runtime's behavior.
/// </summary>
public sealed class HttpEndpointWebhookSender(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpEndpointWebhookSender> logger) : IEndpointWebhookSender
{
    public async Task<(bool Ok, int Status, string? Error)> SendAsync(
        string webhookUrl, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("endpoint-webhook");
            // Dictionary keys (embeds/title/color/...) serialize verbatim; the
            // executor already produced the exact Discord embed shape.
            using var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return (true, status, null);

            var detail = await response.Content.ReadAsStringAsync(ct);
            return (false, status, Truncate(detail, 500));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Endpoint webhook delivery to Discord failed");
            return (false, 0, ex.Message);
        }
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
