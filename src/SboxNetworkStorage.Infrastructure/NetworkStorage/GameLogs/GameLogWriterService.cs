using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.NetworkStorage.GameLogs;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;

/// <summary>Owner whose projects the retention purge reads settings from (self-hosted: a single local owner).</summary>
public sealed record GameLogWriterOptions(long OwnerUserId);

/// <summary>
/// The single writer behind <see cref="GameLogQueue"/>. It writes buffered entries in batches of up to
/// <see cref="BatchSize"/> rows, one transaction per batch, whenever a batch fills or <see cref="FlushInterval"/>
/// passes, and purges each project's stream rows older than that stream's retention once a day.
/// A failed batch is counted per project and logged; it is never retried silently.
/// </summary>
public sealed class GameLogWriterService(
    GameLogQueue queue,
    IGameLogStore store,
    IServiceScopeFactory scopes,
    GameLogWriterOptions options,
    TimeProvider time,
    ILoggerFactory loggerFactory) : BackgroundService
{
    public const int BatchSize = 500;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan FirstPurgeDelay = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = loggerFactory.CreateLogger<GameLogWriterService>();
    private DateTimeOffset? _nextPurge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PurgeIfDueAsync(stoppingToken);
                var batch = await CollectAsync(stoppingToken);
                if (batch.Count > 0) await WriteBatchAsync(batch, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: write what is still buffered below.
        }

        queue.Complete();
        while (true)
        {
            var rest = new List<GameLogRow>(BatchSize);
            while (rest.Count < BatchSize && queue.Reader.TryRead(out var row)) rest.Add(row);
            if (rest.Count == 0) break;
            await WriteBatchAsync(rest, CancellationToken.None);
        }
    }

    /// <summary>
    /// Moves entries from the queue into one batch until it holds <see cref="BatchSize"/> entries or
    /// <see cref="FlushInterval"/> passes after the first entry arrived.
    /// </summary>
    private async Task<List<GameLogRow>> CollectAsync(CancellationToken stoppingToken)
    {
        var batch = new List<GameLogRow>(BatchSize);
        if (!await queue.Reader.WaitToReadAsync(stoppingToken)) return batch;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        window.CancelAfter(FlushInterval);
        while (batch.Count < BatchSize)
        {
            while (batch.Count < BatchSize && queue.Reader.TryRead(out var row)) batch.Add(row);
            if (batch.Count >= BatchSize) break;
            try
            {
                if (!await queue.Reader.WaitToReadAsync(window.Token)) break;
            }
            catch (OperationCanceledException)
            {
                // Window elapsed or shutting down: write what was collected.
                break;
            }
        }

        return batch;
    }

    private async Task WriteBatchAsync(List<GameLogRow> batch, CancellationToken ct)
    {
        try
        {
            await store.InsertGameLogsAsync(batch, ct);
            foreach (var group in batch.GroupBy(row => row.ProjectId, StringComparer.Ordinal))
                queue.RecordWritten(group.Key, group.Count());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var group in batch.GroupBy(row => row.ProjectId, StringComparer.Ordinal))
                queue.RecordWriteFailed(group.Key, group.Count());
            _logger.LogError(ex, "Game-log batch of {Count} entries failed to write", batch.Count);
        }
        finally
        {
            foreach (var group in batch.GroupBy(row => row.ProjectId, StringComparer.Ordinal))
                queue.ReleaseInFlight(group.Key, group.Count());
        }
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
            _logger.LogWarning(ex, "Game-log retention purge failed; it will run again in {Interval}", PurgeInterval);
        }
    }

    /// <summary>
    /// Deletes, for every project and stream that holds rows, the rows older than that stream's retention.
    /// Returns the number of rows deleted.
    /// </summary>
    internal async Task<long> PurgeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        long deleted = 0;
        // Project settings are scoped services and this writer is a singleton: resolve them per purge.
        await using var scope = scopes.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var counts = await store.CountGameLogStreamsAsync(ct);
        var policies = new Dictionary<string, GameLogPolicy>(StringComparer.Ordinal);
        foreach (var count in counts)
        {
            if (!policies.TryGetValue(count.ProjectId, out var policy))
            {
                var access = await projects.ResolveProjectAccessAsync(options.OwnerUserId, count.ProjectId, ct);
                policy = new GameLogPolicy(access?.Project.GameLogs ?? new Domain.Workspace.GameLogSettings());
                policies[count.ProjectId] = policy;
            }

            var cutoff = now - policy.RetentionDaysFor(count.Stream) * 86_400_000L;
            deleted += await store.PurgeGameLogsAsync(count.ProjectId, count.Stream, cutoff, ct);
        }

        if (deleted > 0)
            _logger.LogInformation("Purged {Deleted} game-log entries past their retention", deleted);
        return deleted;
    }
}
