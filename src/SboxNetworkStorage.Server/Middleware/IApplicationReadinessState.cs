namespace SboxNetworkStorage.Server.Middleware;

public interface IApplicationReadinessState
{
    bool IsReady { get; }
    void MarkReady();
    void Reset();

    /// <summary>
    /// True once the startup grace window has elapsed. After this point the
    /// loading page must stop masking traffic even if the database is still
    /// unreachable, so a DB outage cannot take the whole site offline behind a
    /// spinner — DB-independent routes (static pages, health checks) keep
    /// serving and DB-dependent routes surface their own errors.
    /// </summary>
    bool StartupGraceElapsed { get; }
}

public sealed class ApplicationReadinessState : IApplicationReadinessState
{
    /// <summary>
    /// How long the loading page may mask traffic while waiting for the database
    /// pools to come up on a cold start. Kept short: a healthy cold start warms
    /// the pools in well under a second; anything longer is treated as an outage
    /// the loading page must not hide.
    /// </summary>
    private static readonly TimeSpan StartupGraceWindow = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time;
    private long _startedAtTicks;
    private volatile bool _ready;

    public ApplicationReadinessState() : this(TimeProvider.System)
    {
    }

    public ApplicationReadinessState(TimeProvider time)
    {
        _time = time;
        _startedAtTicks = time.GetUtcNow().UtcTicks;
    }

    public bool IsReady => _ready;

    public bool StartupGraceElapsed =>
        _time.GetUtcNow().UtcTicks - Interlocked.Read(ref _startedAtTicks) >= StartupGraceWindow.Ticks;

    public void MarkReady()
    {
        _ready = true;
    }

    public void Reset()
    {
        _ready = false;
        Interlocked.Exchange(ref _startedAtTicks, _time.GetUtcNow().UtcTicks);
    }
}
