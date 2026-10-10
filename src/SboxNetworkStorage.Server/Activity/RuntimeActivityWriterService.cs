using System.Globalization;
using SboxNetworkStorage.Server.Operations;

namespace SboxNetworkStorage.Server.Activity;

/// <summary>
/// The single writer behind <see cref="RuntimeActivityLog"/>: writes buffered request-log and error rows in
/// batches of up to <see cref="BatchSize"/>, one transaction per batch, and once a day deletes rows older than
/// <see cref="RuntimeActivityLog.Retention"/> with the store's purge methods. Writing is best-effort: a failed
/// batch is logged and dropped, never retried, so a database outage cannot grow memory.
/// </summary>
public sealed class RuntimeActivityWriterService(
    RuntimeActivityLog log,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<RuntimeActivityWriterService> logger) : BackgroundService
{
    public const int BatchSize = 200;
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private const string LocalOwnerUserId = "1";
    private static readonly TimeSpan FirstPurgeDelay = TimeSpan.FromMinutes(1);

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
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    // Wake at least hourly so the daily purge runs on an idle server too.
                    window.CancelAfter(TimeSpan.FromHours(1));
                    try
                    {
                        await log.Reader.WaitToReadAsync(window.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                    }
                }
                await FlushAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: write what is still buffered below.
        }

        log.Complete();
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Writes every entry buffered before the call.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var batch = new List<RuntimeActivityEntry>(BatchSize);
                while (batch.Count < BatchSize && log.Reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count == 0) break;
                await WriteBatchAsync(batch);
            }
        }
        finally
        {
            _writeGate.Release();
        }
        ReportDropped();
    }

    /// <summary>Deletes request-log and error rows older than the retention window for every project.</summary>
    internal async Task PurgeAsync(CancellationToken ct)
    {
        var cutoff = (time.GetUtcNow() - RuntimeActivityLog.Retention).ToUnixTimeMilliseconds();
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        foreach (var membership in await store.ListProjectsForUserAsync(LocalOwnerUserId, ct))
        {
            if (!membership.TryGetProperty("project_id", out var id) || id.GetString() is not { Length: > 0 } projectId) continue;
            await store.PurgeStorageRequestLogAsync(projectId, cutoff, ct);
            await store.PurgeStorageErrorsAsync(projectId, cutoff, ct);
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken ct)
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
            logger.LogWarning(ex, "Pruning runtime request logs and errors failed; it will run again in {Interval}", PurgeInterval);
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<RuntimeActivityEntry> batch)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
            // Rejected requests carry the project ID from the URL: keep rows only for projects that exist.
            var exists = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var projectId in batch.Select(entry => entry.ProjectId).Distinct(StringComparer.Ordinal))
                exists[projectId] = ExportFormat.IsValidProjectId(projectId)
                    && await store.ReadProjectAsync(projectId, CancellationToken.None) is not null;
            if (!exists.ContainsValue(true)) return;

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);
            foreach (var entry in batch.Where(entry => exists[entry.ProjectId]))
            {
                switch (entry)
                {
                    case RuntimeRequestEntry request:
                        await transaction.Store.InsertStorageRequestLogAsync(request.ProjectId, request.CreatedAtUnixMs, request.Method,
                            request.Path, request.StatusCode, request.DurationMs, null, CancellationToken.None);
                        break;
                    case RuntimeErrorEntry error:
                        await transaction.Store.InsertStorageErrorAsync(error.ProjectId, error.CreatedAtUnixMs, error.ErrorId, error.Message,
                            error.StackTrace, error.Source, error.RequestPath, error.Severity, CancellationToken.None);
                        break;
                }
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Writing {Count} runtime request log and error rows failed; they were dropped", batch.Count);
        }
    }

    private void ReportDropped()
    {
        var dropped = log.DroppedCount;
        var previous = Interlocked.Exchange(ref _reportedDropped, dropped);
        if (dropped > previous)
            logger.LogDebug("Runtime request log skipped {Count} entries over the per-project budget ({Total} since start)",
                (dropped - previous).ToString(CultureInfo.InvariantCulture), dropped);
    }
}
