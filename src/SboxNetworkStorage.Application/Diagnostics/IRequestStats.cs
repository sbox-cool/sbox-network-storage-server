namespace SboxNetworkStorage.Application.Diagnostics;

public interface IRequestStats
{
    DateTimeOffset StartedAt { get; }

    long TotalRequests { get; }

    int ActiveRequests { get; }

    IReadOnlyDictionary<int, long> StatusCodes { get; }

    IDisposable BeginRequest();

    void CompleteRequest(int statusCode);
}

public sealed class RequestStats : IRequestStats
{
    private readonly Dictionary<int, long> statusCodes = [];
    private long totalRequests;
    private int activeRequests;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public long TotalRequests => Interlocked.Read(ref totalRequests);

    public int ActiveRequests => Volatile.Read(ref activeRequests);

    public IReadOnlyDictionary<int, long> StatusCodes
    {
        get
        {
            lock (statusCodes)
            {
                return statusCodes.ToDictionary();
            }
        }
    }

    public IDisposable BeginRequest()
    {
        Interlocked.Increment(ref totalRequests);
        Interlocked.Increment(ref activeRequests);
        return new ActiveRequest(this);
    }

    public void CompleteRequest(int statusCode)
    {
        lock (statusCodes)
        {
            statusCodes[statusCode] = statusCodes.GetValueOrDefault(statusCode) + 1;
        }
    }

    private sealed class ActiveRequest(RequestStats owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner.activeRequests);
            }
        }
    }
}
