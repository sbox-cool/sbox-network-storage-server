using System.Collections.Concurrent;
using System.Threading.Channels;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;

/// <summary>Per-project game-log counters since process start. Every drop, truncation and sample is visible here.</summary>
public sealed class GameLogCounters
{
    public long Accepted;
    public long Written;
    public long DroppedQueueFull;
    public long WriteFailed;
    public long Rejected;
    public long Sampled;
    public long Truncated;
    public long RateLimited;
    /// <summary>Entries of this project buffered or being written; bounded by <see cref="GameLogQueue.PerProjectInFlightCap"/>.</summary>
    public long InFlight;
}

/// <summary>Immutable copy of <see cref="GameLogCounters"/> for a project. <see cref="Buffered"/> counts all projects.</summary>
public sealed record GameLogCounterSnapshot(
    long Accepted,
    long Written,
    long DroppedQueueFull,
    long WriteFailed,
    long Rejected,
    long Sampled,
    long Truncated,
    long RateLimited,
    int Buffered);

/// <summary>
/// Bounded buffer between request threads and the single game-log writer. Producers never wait: a full buffer
/// rejects the entry with <c>QUEUE_FULL</c> and counts it. Nothing is dropped silently, and the oldest entry is
/// never discarded in favour of a newer one.
/// </summary>
public sealed class GameLogQueue
{
    public const int Capacity = 10_000;

    /// <summary>Most entries one project may hold in the buffer or in a batch being written. Keeps one project from filling the shared buffer.</summary>
    public const int PerProjectInFlightCap = Capacity / 4;

    private readonly Channel<GameLogRow> _channel = Channel.CreateBounded<GameLogRow>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ConcurrentDictionary<string, GameLogCounters> _counters = new(StringComparer.Ordinal);

    public ChannelReader<GameLogRow> Reader => _channel.Reader;

    /// <summary>Entries currently buffered (all projects).</summary>
    public int Buffered => _channel.Reader.Count;

    /// <summary>
    /// Buffers an entry without waiting. Returns false, and counts a drop for the project, when the project already holds
    /// <see cref="PerProjectInFlightCap"/> entries, when the buffer is full, or when the queue has stopped accepting entries
    /// during shutdown.
    /// </summary>
    public bool TryEnqueue(GameLogRow row)
    {
        var counters = Counters(row.ProjectId);
        if (Interlocked.Increment(ref counters.InFlight) <= PerProjectInFlightCap && _channel.Writer.TryWrite(row))
        {
            Interlocked.Increment(ref counters.Accepted);
            return true;
        }

        Interlocked.Decrement(ref counters.InFlight);
        Interlocked.Increment(ref counters.DroppedQueueFull);
        return false;
    }

    /// <summary>Frees in-flight slots once the writer has finished a project's rows, whether the write succeeded or not.</summary>
    public void ReleaseInFlight(string projectId, int count) => Interlocked.Add(ref Counters(projectId).InFlight, -count);

    public void RecordRejected(string projectId, int count) => Interlocked.Add(ref Counters(projectId).Rejected, count);

    public void RecordSampled(string projectId, int count) => Interlocked.Add(ref Counters(projectId).Sampled, count);

    public void RecordTruncated(string projectId, int count) => Interlocked.Add(ref Counters(projectId).Truncated, count);

    public void RecordRateLimited(string projectId, int count) => Interlocked.Add(ref Counters(projectId).RateLimited, count);

    public void RecordWritten(string projectId, int count) => Interlocked.Add(ref Counters(projectId).Written, count);

    public void RecordWriteFailed(string projectId, int count) => Interlocked.Add(ref Counters(projectId).WriteFailed, count);

    /// <summary>Stops accepting entries; the reader still delivers what is buffered.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public GameLogCounterSnapshot Snapshot(string projectId)
    {
        var c = Counters(projectId);
        return new GameLogCounterSnapshot(
            Accepted: Interlocked.Read(ref c.Accepted),
            Written: Interlocked.Read(ref c.Written),
            DroppedQueueFull: Interlocked.Read(ref c.DroppedQueueFull),
            WriteFailed: Interlocked.Read(ref c.WriteFailed),
            Rejected: Interlocked.Read(ref c.Rejected),
            Sampled: Interlocked.Read(ref c.Sampled),
            Truncated: Interlocked.Read(ref c.Truncated),
            RateLimited: Interlocked.Read(ref c.RateLimited),
            Buffered: Buffered);
    }

    private GameLogCounters Counters(string projectId) => _counters.GetOrAdd(projectId, static _ => new GameLogCounters());
}
