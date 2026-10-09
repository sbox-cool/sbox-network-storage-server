using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// Fans every captured error out to the log (existing behaviour) plus the
/// configured operator channels (Discord, SMTP). Registered as both
/// <see cref="IExceptionAlertSink"/> and <see cref="INetworkStorageErrorAlertSink"/>,
/// so every existing call site (endpoint error reporter, proxy reporter,
/// handler-caught reporter, storage-API error reports) alerts without changes.
///
/// The senders are best-effort and never throw; the two remote sends run
/// concurrently so a slow channel does not delay the other. The log sink
/// runs first and stays the always-on record.
/// </summary>
public sealed class OperatorAlertSink(
    LoggingExceptionAlertSink logSink,
    LoggingNetworkStorageErrorAlertSink storageLogSink,
    DiscordAlertSender discord,
    SmtpAlertSender smtp) : IExceptionAlertSink, INetworkStorageErrorAlertSink
{
    public async Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
    {
        await logSink.NotifyAsync(capturedError, cancellationToken);
        await Task.WhenAll(
            discord.SendAsync(capturedError, cancellationToken),
            smtp.SendAsync(capturedError, cancellationToken));
    }

    public async Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken)
    {
        await storageLogSink.NotifyAsync(error, cancellationToken);
        await Task.WhenAll(
            discord.SendAsync(error, cancellationToken),
            smtp.SendAsync(error, cancellationToken));
    }
}
