using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;

/// <summary>Settings for <see cref="AnalyticsWriterService"/>.</summary>
/// <param name="RetentionDays">Analytics older than this many days are purged daily; 0 keeps analytics forever.</param>
public sealed record AnalyticsWriterOptions(int RetentionDays = 90);

/// <summary>
/// The single writer behind <see cref="AnalyticsEventQueue"/>: it writes buffered events in batches of up to
/// <see cref="BatchSize"/> rows, one transaction per batch, whenever a batch fills or <see cref="FlushInterval"/>
/// passes, and purges analytics older than the retention window once a day. One writer also serializes the
/// profile and session read-modify-write cycles that concurrent requests used to race on.
/// </summary>
public sealed class AnalyticsWriterService(
    AnalyticsEventQueue queue,
    IServiceScopeFactory scopes,
    TimeProvider time,
    AnalyticsIngestionFailureTracker failureTracker,
    AnalyticsWriterOptions options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    public const int BatchSize = 500;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan FirstPurgeDelay = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = loggerFactory.CreateLogger<AnalyticsWriterService>();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _collectLock = new();
    private List<AnalyticsEvent> _collecting = new(BatchSize);
    private DateTimeOffset? _nextPurge;
    private long _reportedDropped;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeIfDueAsync(stoppingToken);
                await CollectAsync(stoppingToken);
                ReportDropped();
                await FlushAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: write what is still buffered below.
        }

        queue.Complete();
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>
    /// Writes every event that was enqueued before the call, whether still queued, collected by the background
    /// loop or being written by it, in batches of at most <see cref="BatchSize"/>.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var batch = TakeBatch();
                if (batch.Count == 0) break;
                // Not cancellable: the batch has left the queue, so an abandoned write would lose it.
                await WriteBatchAsync(batch, CancellationToken.None);
            }
        }
        finally
        {
            _writeGate.Release();
        }
        ReportDropped();
    }

    /// <summary>Runs the retention purge when the daily interval has elapsed.</summary>
    internal async Task PurgeIfDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        _nextPurge ??= now + FirstPurgeDelay;
        if (now < _nextPurge) return;

        _nextPurge = now + PurgeInterval;
        try
        {
            await PurgeAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Analytics retention purge failed; it will run again in {Interval}", PurgeInterval);
        }
    }

    /// <summary>Deletes analytics older than the retention window. Returns the number of rows deleted.</summary>
    internal async Task<long> PurgeAsync(CancellationToken ct)
    {
        if (options.RetentionDays <= 0) return 0;

        // Day granularity: an event from exactly RetentionDays ago today is still inside the
        // window; it becomes eligible once the calendar day rolls over.
        var cutoffDay = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-options.RetentionDays);
        var cutoff = new DateTimeOffset(cutoffDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var deleted = await store.PurgeAnalyticsBeforeAsync(cutoff, ct);
        if (deleted > 0)
            _logger.LogInformation("Purged {Deleted} analytics rows older than {Days} days", deleted, options.RetentionDays);
        return deleted;
    }

    /// <summary>
    /// Moves events from the queue into the collecting batch until it holds <see cref="BatchSize"/> events or
    /// <see cref="FlushInterval"/> passes. Collected events stay visible to <see cref="FlushAsync"/> the whole time.
    /// </summary>
    private async Task CollectAsync(CancellationToken stoppingToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        window.CancelAfter(FlushInterval);
        try
        {
            while (Collect() < BatchSize && await queue.Reader.WaitToReadAsync(window.Token))
            {
            }
        }
        catch (OperationCanceledException)
        {
            // Window elapsed or shutting down; the caller flushes what was collected.
        }
    }

    private int Collect()
    {
        lock (_collectLock)
        {
            while (_collecting.Count < BatchSize && queue.Reader.TryRead(out var analyticsEvent)) _collecting.Add(analyticsEvent);
            return _collecting.Count;
        }
    }

    /// <summary>Next batch to write, oldest first: the collecting batch, else up to <see cref="BatchSize"/> queued events.</summary>
    private List<AnalyticsEvent> TakeBatch()
    {
        lock (_collectLock)
        {
            if (_collecting.Count > 0)
            {
                var collected = _collecting;
                _collecting = new List<AnalyticsEvent>(BatchSize);
                return collected;
            }

            var batch = new List<AnalyticsEvent>(BatchSize);
            while (batch.Count < BatchSize && queue.Reader.TryRead(out var analyticsEvent)) batch.Add(analyticsEvent);
            return batch;
        }
    }

    /// <summary>Writes one batch; the caller holds <c>_writeGate</c>. A failed batch is rolled back and reported, never thrown.</summary>
    private async Task WriteBatchAsync(IReadOnlyList<AnalyticsEvent> batch, CancellationToken ct)
    {
        try
        {
            await WriteTransactionAsync(batch, ct);
            RecordSuccess(batch[0].ProjectId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Writing a batch of {Count} analytics events failed; the transaction was rolled back", batch.Count);
            if (failureTracker.RecordFailure())
                _ = failureTracker.FireTransitionAlertAsync(batch[0].ProjectId, failureTracker.ConsecutiveFailures, CancellationToken.None);
        }
    }

    private async Task WriteTransactionAsync(IReadOnlyList<AnalyticsEvent> events, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await using var transaction = await store.BeginTransactionAsync(ct);
        var ingester = new PlayerAnalyticsIngester(transaction.Store, time, failureTracker, loggerFactory.CreateLogger<PlayerAnalyticsIngester>());
        foreach (var analyticsEvent in events)
            await ingester.IngestAsync(analyticsEvent, ct);
        await transaction.CommitAsync(ct);
    }

    private void RecordSuccess(string projectId)
    {
        if (failureTracker.RecordSuccess())
            _logger.LogInformation("Analytics ingestion recovered after sustained failure for project {ProjectId}", projectId);
    }

    private void ReportDropped()
    {
        var dropped = queue.DroppedCount;
        var previous = Interlocked.Exchange(ref _reportedDropped, dropped);
        if (dropped > previous)
            _logger.LogWarning("Analytics buffer was full: dropped {Count} oldest events ({Total} since start)", dropped - previous, dropped);
    }
}
