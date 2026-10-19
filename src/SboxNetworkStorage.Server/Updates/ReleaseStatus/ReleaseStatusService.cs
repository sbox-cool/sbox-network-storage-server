using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Updates.ReleaseStatus;

/// <summary>What the owner dashboard may show: the release-status fetch is on, and where it reads from.</summary>
/// <param name="Enabled">False when update checks, the release-status switch or demo read-only mode turn the fetch off. Nothing is requested then.</param>
/// <param name="Url">The document URL, already validated as HTTPS (or loopback HTTP).</param>
public sealed record ReleaseStatusSettings(bool Enabled, Uri Url)
{
    public static ReleaseStatusSettings From(EffectiveConfig config)
    {
        var enabled = config.GetBoolean("updates.check")
            && config.GetBoolean("adminpanel.release_status")
            && !config.GetBoolean("adminpanel.demo_read_only");
        var url = TunnelRegistryClient.ValidateRegistry(config.GetString("adminpanel.release_status_url"), "release status URL");
        return new ReleaseStatusSettings(enabled, url);
    }
}

/// <summary>The last document read successfully, and when it was read.</summary>
public sealed record ReleaseStatusSnapshot(ReleaseStatusDocument Document, DateTimeOffset FetchedAt);

/// <summary>
/// Keeps one copy of the library's <c>release-status.json</c> for the owner dashboard. The server
/// fetches that single URL itself; dashboard views never call GitHub. A successful read is reused for
/// an hour. A failed read keeps the last good copy and is retried after five minutes. Every failure
/// is logged, but only the first of a run of failures is a warning, so an offline server does not
/// flood the log.
/// </summary>
public sealed class ReleaseStatusService : BackgroundService
{
    /// <summary>Largest response accepted. A schema 1 document with 50 pending changes is a few kilobytes.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ReleaseStatusSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<ReleaseStatusService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _state = new();
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _failureStreak;
    private int _refreshQueued;
    private ReleaseStatusSnapshot? _current;
    private DateTimeOffset? _lastAttemptAt;
    private bool _lastAttemptFailed;

    public ReleaseStatusService(ReleaseStatusSettings settings, TimeProvider time, ILogger<ReleaseStatusService> logger,
        HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
        _http = handler is null ? new HttpClient { Timeout = RequestTimeout } : new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sbox-ns", BuildInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool Enabled => _settings.Enabled;

    public ReleaseStatusSnapshot? Current
    {
        get { lock (_state) return _current; }
    }

    /// <summary>When the most recent attempt finished, successful or not. Null until the first attempt.</summary>
    public DateTimeOffset? LastAttemptAt
    {
        get { lock (_state) return _lastAttemptAt; }
    }

    public bool LastAttemptFailed
    {
        get { lock (_state) return _lastAttemptFailed; }
    }

    /// <summary>
    /// Starts a background refresh when the cached copy is due. Returns at once, so a dashboard view
    /// never waits on the network, and at most one refresh is queued at a time.
    /// </summary>
    public void RequestRefresh()
    {
        if (!_settings.Enabled || NextAttemptAt > _time.GetUtcNow() || Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshIfDueAsync(CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref _refreshQueued, 0);
            }
        });
    }

    /// <summary>Fetches and parses the document now, whatever the cache says. Failures are recorded, never thrown.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        await _fetchGate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            try
            {
                var document = await FetchAsync(_settings.Url, ct);
                int recoveredAfter;
                lock (_state)
                {
                    _current = new ReleaseStatusSnapshot(document, now);
                    _lastAttemptAt = now;
                    _lastAttemptFailed = false;
                    _nextAttemptAt = now + SuccessTtl;
                    recoveredAfter = _failureStreak;
                    _failureStreak = 0;
                }

                if (recoveredAfter > 0)
                {
                    _logger.LogInformation("Release status is available again after {Count} failed attempt(s)", recoveredAfter);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                int streak;
                lock (_state)
                {
                    _lastAttemptAt = now;
                    _lastAttemptFailed = true;
                    _nextAttemptAt = now + RetryAfterFailure;
                    streak = ++_failureStreak;
                }

                if (streak == 1)
                {
                    _logger.LogWarning("Release status could not be loaded from {Url}: {Reason}. The owner dashboard shows the last good copy and retries in {Minutes} minutes.",
                        _settings.Url, ex.Message, (int)RetryAfterFailure.TotalMinutes);
                }
                else
                {
                    _logger.LogDebug(ex, "Release status attempt {Streak} failed", streak);
                }
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Release status fetch is off (updates.check, adminpanel.release_status or demo mode)");
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshIfDueAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _fetchGate.Dispose();
        base.Dispose();
    }

    private DateTimeOffset NextAttemptAt
    {
        get { lock (_state) return _nextAttemptAt; }
    }

    private Task RefreshIfDueAsync(CancellationToken ct)
        => NextAttemptAt > _time.GetUtcNow() ? Task.CompletedTask : RefreshAsync(ct);

    private async Task<ReleaseStatusDocument> FetchAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException($"The release status response exceeds the {MaxResponseBytes}-byte cap.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var bounded = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(rented.AsMemory(), ct)) > 0)
            {
                if (bounded.Length + read > MaxResponseBytes)
                {
                    throw new InvalidDataException($"The release status response exceeds the {MaxResponseBytes}-byte cap.");
                }

                bounded.Write(rented, 0, read);
            }

            return ReleaseStatusParser.Parse(Encoding.UTF8.GetString(bounded.GetBuffer(), 0, (int)bounded.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
