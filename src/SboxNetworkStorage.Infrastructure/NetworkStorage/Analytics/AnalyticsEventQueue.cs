using System.Threading.Channels;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;

/// <summary>
/// The bounded buffer between request threads and the single analytics writer. When it is full the oldest
/// event is dropped and counted, so producers never wait and memory stays bounded.
/// </summary>
public sealed class AnalyticsEventQueue
{
    public const int Capacity = 10_000;

    private readonly Channel<AnalyticsEvent> _channel;
    private long _dropped;

    public AnalyticsEventQueue()
    {
        _channel = Channel.CreateBounded<AnalyticsEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public ChannelReader<AnalyticsEvent> Reader => _channel.Reader;

    /// <summary>Events dropped because the buffer was full, since the process started.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Events currently buffered.</summary>
    public int Buffered => _channel.Reader.Count;

    /// <summary>Buffers an event without waiting; a full buffer drops its oldest event instead.</summary>
    public void Enqueue(AnalyticsEvent analyticsEvent)
    {
        // A DropOldest channel accepts every write; false only means it was completed on shutdown.
        _channel.Writer.TryWrite(analyticsEvent);
    }

    /// <summary>Stops accepting events; the reader still delivers what is buffered.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
