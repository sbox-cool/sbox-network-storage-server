using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// Fans every captured error out to the log (existing behaviour) plus the
/// configured operator channels (Discord, SMTP). Registered as both
/// <see cref="IExceptionAlertSink"/> and <see cref="INetworkStorageErrorAlertSink"/>
/// (one shared instance), so every existing call site (endpoint error reporter,
/// proxy reporter, handler-caught reporter, storage-API error reports) alerts
/// without changes.
///
/// <para>Some alerts can be triggered by anyone holding a game's public key, so the
/// remote channels are limited here, for every call site at once: an alert like one
/// already sent in the last <see cref="RepeatWindow"/> is not sent again, and at most
/// <see cref="MaxPerMinute"/> alerts go out per minute. The next alert that is sent
/// says how many were held back. The log sink still records every error.</para>
///
/// The senders are best-effort and never throw; the two remote sends run
/// concurrently so a slow channel does not delay the other. The log sink
/// runs first and stays the always-on record.
/// </summary>
public sealed class OperatorAlertSink(
    LoggingExceptionAlertSink logSink,
    LoggingNetworkStorageErrorAlertSink storageLogSink,
    DiscordAlertSender discord,
    SmtpAlertSender smtp,
    TimeProvider time) : IExceptionAlertSink, INetworkStorageErrorAlertSink
{
    public const int MaxPerMinute = 10;
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    private const int MaxTrackedKinds = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Kind> _recent = new(StringComparer.Ordinal);
    private DateTimeOffset _minuteStart = DateTimeOffset.MinValue;
    private int _sentThisMinute;
    private int _overLimit;

    public async Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
    {
        await logSink.NotifyAsync(capturedError, cancellationToken);
        var fingerprint = $"exception\n{capturedError.ProjectId}\n{capturedError.Classification}\n{capturedError.Method}\n{capturedError.Path}";
        if (!TryAdmit(fingerprint, out var note)) return;
        var alert = note is null ? capturedError : capturedError with { Message = capturedError.Message + "\n\n" + note };
        await Task.WhenAll(
            discord.SendAsync(alert, cancellationToken),
            smtp.SendAsync(alert, cancellationToken));
    }

    public async Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken)
    {
        await storageLogSink.NotifyAsync(error, cancellationToken);
        // The record key is left out: one broken collection fails for every player.
        var fingerprint = $"storage\n{error.ProjectId}\n{error.Operation}\n{error.Code}\n{error.CollectionId}";
        if (!TryAdmit(fingerprint, out var note)) return;
        var alert = note is null ? error : error with { Message = error.Message + "\n\n" + note };
        await Task.WhenAll(
            discord.SendAsync(alert, cancellationToken),
            smtp.SendAsync(alert, cancellationToken));
    }

    /// <summary>Decides whether an alert goes out; <paramref name="note"/> reports alerts held back since the last one.</summary>
    private bool TryAdmit(string fingerprint, out string? note)
    {
        note = null;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_recent.TryGetValue(fingerprint, out var kind) && now - kind.SentAt < RepeatWindow)
            {
                kind.Repeats++;
                return false;
            }

            if (now - _minuteStart >= TimeSpan.FromMinutes(1))
            {
                _minuteStart = now;
                _sentThisMinute = 0;
            }

            if (_sentThisMinute >= MaxPerMinute)
            {
                _overLimit++;
                return false;
            }

            _sentThisMinute++;
            var notes = new List<string>(2);
            if (kind is { Repeats: > 0 })
                notes.Add($"{kind.Repeats} more like this were not sent in the {RepeatWindow.TotalMinutes:0} minutes before this one.");
            if (_overLimit > 0)
                notes.Add($"{_overLimit} other alerts were not sent because of the limit of {MaxPerMinute} per minute.");
            _overLimit = 0;
            note = notes.Count == 0 ? null : string.Join(" ", notes);

            if (kind is null && _recent.Count >= MaxTrackedKinds)
            {
                foreach (var stale in _recent.Where(entry => now - entry.Value.SentAt >= RepeatWindow).Select(entry => entry.Key).ToList())
                    _recent.Remove(stale);
            }

            if (kind is not null)
            {
                kind.SentAt = now;
                kind.Repeats = 0;
            }
            else if (_recent.Count < MaxTrackedKinds)
            {
                _recent[fingerprint] = new Kind { SentAt = now };
            }

            return true;
        }
    }

    private sealed class Kind
    {
        public DateTimeOffset SentAt;
        public int Repeats;
    }
}
