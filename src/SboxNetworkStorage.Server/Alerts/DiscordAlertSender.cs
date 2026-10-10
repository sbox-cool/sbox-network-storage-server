using System.Net.Http.Json;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Posts a JSON payload to a Discord webhook URL. Test fakes capture the call.</summary>
public interface IDiscordWebhookClient
{
    Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken);
}

/// <summary>Production <see cref="IDiscordWebhookClient"/> over the shared HTTP stack.</summary>
public sealed class HttpDiscordWebhookClient(IHttpClientFactory httpClientFactory) : IDiscordWebhookClient
{
    public async Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("alerts-discord");
        using var response = await client.PostAsync(webhookUrl, JsonContent.Create(payload.RootElement), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Sends captured errors to Discord. Best-effort: misconfiguration and
/// delivery failures are logged and never thrown, so alerting can never
/// break the request that triggered it.
/// </summary>
public sealed class DiscordAlertSender(
    AlertOptions options,
    IDiscordWebhookClient webhookClient,
    ILogger<DiscordAlertSender> logger)
{
    public async Task SendAsync(CapturedErrorDto error, CancellationToken cancellationToken)
    {
        if (!options.DiscordEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.DiscordWebhookUrl))
        {
            logger.LogWarning("Discord alerts are enabled but no webhook URL is configured; skipping alert for correlationId={CorrelationId}", error.CorrelationId);
            return;
        }

        try
        {
            using var payload = BuildPayload(error, options.DiscordUsername);
            await webhookClient.SendAsync(options.DiscordWebhookUrl, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send Discord alert correlationId={CorrelationId}", error.CorrelationId);
        }
    }

    public Task SendAsync(NetworkStorageError error, CancellationToken cancellationToken)
        => SendAsync(ToCaptured(error), cancellationToken);

    /// <summary>
    /// Builds the minimal Discord webhook embed. The webhook URL itself is never part of the payload.
    /// Route, project and message can carry caller-chosen text (paths, ids, a client's failure reason),
    /// so they are shown as code, where Discord renders no links or formatting, and no mention pings.
    /// </summary>
    public static JsonDocument BuildPayload(CapturedErrorDto error, string username)
    {
        var route = $"{error.Method} {error.Path}";
        var message = error.Message.Length <= 1500 ? error.Message : error.Message[..1500] + "…";
        var description = "```\n" + Literal(message) + "\n```";
        var fields = new List<object>
        {
            new { name = "Route", value = InlineCode(Truncate(route, 256)), inline = true },
            new { name = "Status", value = error.StatusCode.ToString(), inline = true },
            new { name = "Classification", value = Truncate(error.Classification, 256), inline = true },
            new { name = "Correlation ID", value = Truncate(error.CorrelationId, 256), inline = false },
        };
        if (!string.IsNullOrWhiteSpace(error.ProjectId))
        {
            fields.Add(new { name = "Project", value = InlineCode(Truncate(error.ProjectId, 256)), inline = true });
        }

        var payload = new
        {
            username = string.IsNullOrWhiteSpace(username) ? "sbox-ns" : username,
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = Truncate($"sbox-ns error: {error.Classification}", 256),
                    description,
                    color = 15548997,
                    timestamp = error.Timestamp.ToString("o"),
                    fields,
                },
            },
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }

    internal static CapturedErrorDto ToCaptured(NetworkStorageError error) => new(
        Id: Guid.NewGuid().ToString("D"),
        Timestamp: DateTimeOffset.UtcNow,
        Source: "Network Storage data plane",
        Method: error.Operation,
        Path: $"{error.CollectionId}/{error.RecordKey}",
        StatusCode: 500,
        Classification: error.Code,
        CorrelationId: Guid.NewGuid().ToString("D"),
        Message: error.Message,
        StackTrace: null,
        ProjectId: string.IsNullOrWhiteSpace(error.ProjectId) ? null : error.ProjectId);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    // A backtick would close the code span or block early; the look-alike U+02CB keeps the text readable.
    private static string Literal(string value) => value.Replace('`', 'ˋ');

    private static string InlineCode(string value) => "`" + Literal(value) + "`";
}
