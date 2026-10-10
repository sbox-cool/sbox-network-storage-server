using System.Threading.Channels;

namespace SboxNetworkStorage.Server.Activity;

/// <summary>One row for the project's Logs (<c>storage_request_log</c>) or Errors (<c>storage_errors</c>) tab.</summary>
public abstract record RuntimeActivityEntry(string ProjectId, long CreatedAtUnixMs);

public sealed record RuntimeRequestEntry(string ProjectId, long CreatedAtUnixMs, string Method, string Path, int StatusCode, int DurationMs)
    : RuntimeActivityEntry(ProjectId, CreatedAtUnixMs);

public sealed record RuntimeErrorEntry(string ProjectId, long CreatedAtUnixMs, string ErrorId, string Message, string? StackTrace,
    string Source, string? RequestPath, string Severity) : RuntimeActivityEntry(ProjectId, CreatedAtUnixMs);

/// <summary>
/// Bounded buffer between request threads and <see cref="RuntimeActivityWriterService"/>. Producers never wait.
/// Volume is capped twice: each project may record at most <see cref="RowsPerMinute"/> successful requests,
/// <see cref="RowsPerMinute"/> failed requests and <see cref="RowsPerMinute"/> errors per minute, and the buffer
/// drops its oldest entry when full. The project ID of a rejected request comes from the URL, so the writer
/// stores it only for projects that exist, and at most <see cref="TrackedProjectsPerMinute"/> distinct IDs
/// get a budget each minute.
/// </summary>
public sealed class RuntimeActivityLog
{
    public const int Capacity = 5_000;
    public const int RowsPerMinute = 20;
    public const int TrackedProjectsPerMinute = 1_000;
    public const int MaxPathLength = 512;
    public const int MaxMessageLength = 2_000;
    public const int MaxStackTraceLength = 4_000;

    /// <summary>Rows older than this are pruned by the writer once a day.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly Channel<RuntimeActivityEntry> _channel;
    private readonly TimeProvider _time;
    private readonly Dictionary<(string ProjectId, Budget Budget), int> _used = new();
    private long _window = -1;
    private long _dropped;

    public RuntimeActivityLog(TimeProvider time)
    {
        _time = time;
        _channel = Channel.CreateBounded<RuntimeActivityEntry>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _dropped));
    }

    private enum Budget { Succeeded, Failed, Error }

    public ChannelReader<RuntimeActivityEntry> Reader => _channel.Reader;

    /// <summary>Entries not recorded because a budget was used up or the buffer was full, since start.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void RecordRequest(string projectId, string method, string path, int statusCode, double durationMs)
    {
        if (!TryReserve(projectId, statusCode >= StatusCodes.Status400BadRequest ? Budget.Failed : Budget.Succeeded, out var now)) return;
        _channel.Writer.TryWrite(new RuntimeRequestEntry(projectId, now, method, Truncate(path, MaxPathLength), statusCode,
            (int)Math.Clamp(Math.Round(durationMs), 0, int.MaxValue)));
    }

    public void RecordError(string projectId, string errorId, string message, string? stackTrace, string source, string? requestPath, string severity)
    {
        if (!TryReserve(projectId, Budget.Error, out var now)) return;
        _channel.Writer.TryWrite(new RuntimeErrorEntry(projectId, now, Truncate(errorId, 128), Truncate(message, MaxMessageLength),
            stackTrace is null ? null : Truncate(stackTrace, MaxStackTraceLength), Truncate(source, 128),
            requestPath is null ? null : Truncate(requestPath, MaxPathLength), severity));
    }

    /// <summary>Stops accepting entries; the reader still delivers what is buffered.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private bool TryReserve(string projectId, Budget budget, out long nowUnixMs)
    {
        nowUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (string.IsNullOrEmpty(projectId) || projectId.Length > 128) return false;
        var minute = nowUnixMs / 60_000;
        lock (_used)
        {
            if (minute != _window)
            {
                _used.Clear();
                _window = minute;
            }
            var key = (projectId, budget);
            _used.TryGetValue(key, out var used);
            if (used >= RowsPerMinute || (used == 0 && _used.Count >= TrackedProjectsPerMinute * 3))
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _used[key] = used + 1;
            return true;
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
