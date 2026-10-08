using System.Net;
using System.Net.Mail;
using System.Text;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Sends one plain-text mail. Test fakes capture the call instead of touching the network.</summary>
public interface ISmtpTransport
{
    Task SendAsync(
        string host, int port, bool useTls,
        string username, string password,
        string from, IReadOnlyList<string> to,
        string subject, string body,
        CancellationToken cancellationToken);
}

/// <summary>Production <see cref="ISmtpTransport"/> over <see cref="SmtpClient"/>.</summary>
public sealed class SmtpTransport : ISmtpTransport
{
    public async Task SendAsync(
        string host, int port, bool useTls,
        string username, string password,
        string from, IReadOnlyList<string> to,
        string subject, string body,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = useTls,
            Timeout = 10_000,
        };
        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body,
        };
        foreach (var recipient in to)
        {
            message.To.Add(recipient);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}

/// <summary>
/// Sends captured errors as plain-text mail. Best-effort: misconfiguration
/// and delivery failures are logged and never thrown, so alerting can never
/// break the request that triggered it.
/// </summary>
public sealed class SmtpAlertSender(
    AlertOptions options,
    ISmtpTransport transport,
    ILogger<SmtpAlertSender> logger)
{
    public Task SendAsync(CapturedErrorDto error, CancellationToken cancellationToken)
    {
        var (subject, body) = BuildMessage(error);
        return SendBuiltAsync(subject, body, error.CorrelationId, cancellationToken);
    }

    public Task SendAsync(NetworkStorageError error, CancellationToken cancellationToken)
    {
        var (subject, body) = BuildMessage(error);
        return SendBuiltAsync(subject, body, error.ProjectId, cancellationToken);
    }

    private async Task SendBuiltAsync(string subject, string body, string logContext, CancellationToken cancellationToken)
    {
        if (!options.SmtpEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.SmtpHost) || options.SmtpTo.Count == 0 || string.IsNullOrWhiteSpace(options.SmtpFrom))
        {
            logger.LogWarning("SMTP alerts are enabled but host/from/to is incomplete; skipping alert for {Context}", logContext);
            return;
        }

        try
        {
            await transport.SendAsync(
                options.SmtpHost, options.SmtpPort, options.SmtpUseTls,
                options.SmtpUsername, options.SmtpPassword,
                options.SmtpFrom, options.SmtpTo,
                subject, body, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send SMTP alert for {Context}", logContext);
        }
    }

    /// <summary>Builds the plain-text digest. Secrets (passwords, webhook URLs) never appear in it.</summary>
    public static (string Subject, string Body) BuildMessage(CapturedErrorDto error)
    {
        var subject = $"[sbox-ns] {error.Classification} {error.Method} {error.Path} ({error.CorrelationId})";
        var body = new StringBuilder()
            .AppendLine($"Classification: {error.Classification}")
            .AppendLine($"Route: {error.Method} {error.Path}")
            .AppendLine($"Status: {error.StatusCode}")
            .AppendLine($"Correlation ID: {error.CorrelationId}")
            .AppendLine($"Time: {error.Timestamp:o}")
            .AppendLine($"Source: {error.Source}")
            .AppendLine($"Project: {error.ProjectId ?? "-"}")
            .AppendLine($"Steam ID: {error.SteamId ?? "-"}")
            .AppendLine()
            .AppendLine(error.Message)
            .ToString();
        return (subject, body);
    }

    public static (string Subject, string Body) BuildMessage(NetworkStorageError error)
    {
        var subject = $"[sbox-ns] {error.Code} {error.Operation} {error.CollectionId}/{error.RecordKey}";
        var body = new StringBuilder()
            .AppendLine($"Code: {error.Code}")
            .AppendLine($"Operation: {error.Operation}")
            .AppendLine($"Collection: {error.CollectionId}")
            .AppendLine($"Record: {error.RecordKey}")
            .AppendLine($"Project: {error.ProjectId}")
            .AppendLine()
            .AppendLine(error.Message)
            .ToString();
        return (subject, body);
    }
}
