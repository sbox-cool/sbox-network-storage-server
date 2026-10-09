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
    private DateTimeOffset? _nextPurge;
    private long _reportedDropped;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeIfDueAsync(stoppingToken);
                var batch = await CollectAsync(stoppingToken);
                ReportDropped();
                if (batch.Count > 0) await WriteBatchAsync(batch, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: write what is still buffered below.
        }

        queue.Complete();
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Writes everything currently buffered, in batches of at most <see cref="BatchSize"/>.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        while (true)
        {
            var batch = new List<AnalyticsEvent>(BatchSize);
            while (batch.Count < BatchSize && queue.Reader.TryRead(out var analyticsEvent)) batch.Add(analyticsEvent);
            if (batch.Count == 0) break;
            await WriteBatchAsync(batch, ct);
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

    /// <summary>Collects up to <see cref="BatchSize"/> events, waiting at most <see cref="FlushInterval"/> for them.</summary>
    private async Task<List<AnalyticsEvent>> CollectAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AnalyticsEvent>(BatchSize);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        window.CancelAfter(FlushInterval);
        try
        {
            while (batch.Count < BatchSize && await queue.Reader.WaitToReadAsync(window.Token))
            {
                while (batch.Count < BatchSize && queue.Reader.TryRead(out var analyticsEvent)) batch.Add(analyticsEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // Keep already-collected events on shutdown too; the caller flushes them before draining the queue.
        }
        return batch;
    }

    private async Task WriteBatchAsync(IReadOnlyList<AnalyticsEvent> batch, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
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
        finally
        {
            _writeGate.Release();
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
