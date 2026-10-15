using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Flushes the in-memory client-version and outcome batches into the store once a minute (and at shutdown),
/// purges rows past their retention (90 days of versions, 14 days of outcomes), and notifies through the existing
/// error-alert sink when a project's configured affected-traffic share is reached. Telemetry only; never refuses a request.
/// </summary>
public sealed class ClientVersionWriter(ClientVersionTracker tracker, ClientOutcomeTracker outcomes, IClientVersionStore store,
    LibraryCompatProvider compat, IServiceScopeFactory scopes, TimeProvider time, ILogger<ClientVersionWriter> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var outcomeBatch = outcomes.Drain();
            if (outcomeBatch.Count > 0) await store.WriteClientOutcomesAsync(outcomeBatch, ct);
            var batch = tracker.Drain();
            if (batch.Count > 0) await store.WriteClientVersionsAsync(batch, ct);
            // Aggregate alert threshold is per-project; not a per-request network call.
            foreach (var project in batch.Select(delta => delta.Key.ProjectId).Distinct(StringComparer.Ordinal))
            {
                await using var scope = scopes.CreateAsyncScope();
                var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
                var access = await projects.ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, project, ct);
                if (access?.Project.LibraryAlertPercent is not { } threshold || threshold <= 0) continue;
                var matrix = compat.Current;
                var total = batch.Where(delta => delta.Key.ProjectId == project).Sum(delta => delta.Requests);
                var affected = batch.Where(delta => delta.Key.ProjectId == project && IsAffected(delta.Key, access.Project.LibraryMinimum, matrix)).Sum(delta => delta.Requests);
                if (total >= 100 && affected * 100d / total >= threshold)
                    await scope.ServiceProvider.GetRequiredService<INetworkStorageErrorAlertSink>().NotifyAsync(
                        new NetworkStorageError(project, "", "", "library-compatibility", "LIBRARY_TRAFFIC",
                            $"{affected}/{total} requests ({affected * 100d / total:0.0}%) use an unsupported or blocked library. See project Clients."), ct);
            }
        }
        finally { _gate.Release(); }
    }

    private static bool IsAffected(ClientVersionKey key, string? minimum, LibraryMatrix matrix)
    {
        if (ClientVersionsQuery.BelowMinimum(key.LibraryVersion, minimum)) return true;
        var library = ClientIdentity.TryParseLibraryVersion(key.LibraryVersion, out var version) ? version : null;
        var status = matrix.Evaluate(new ClientIdentity(
            library?.ToString() ?? ClientIdentity.Legacy, library, key.LibraryRevision,
            ClientIdentity.ParseClientType(key.ClientType), key.GameRevision, null)).Status;
        return status is LibraryStatus.Unsupported or LibraryStatus.Blocked;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        var lastPurgeDay = -1;
        var reportedDrops = 0L;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                    var now = time.GetUtcNow();
                    var day = DateOnly.FromDateTime(now.UtcDateTime).DayNumber;
                    if (day != lastPurgeDay)
                    {
                        await store.PurgeClientVersionsAsync(day - 89, stoppingToken);
                        await store.PurgeClientOutcomesAsync(ClientOutcomeTracker.HourOf(now) - ClientOutcomeTracker.RetentionHours, stoppingToken);
                        lastPurgeDay = day;
                    }
                    var dropped = tracker.Dropped + outcomes.Dropped;
                    if (dropped > reportedDrops)
                    {
                        logger.LogWarning("Client analytics dropped {Count} additional requests at the bounded group limit", dropped - reportedDrops);
                        reportedDrops = dropped;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Client version analytics flush failed; unpersisted batch was dropped"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Final client version analytics flush failed"); }
    }
}
