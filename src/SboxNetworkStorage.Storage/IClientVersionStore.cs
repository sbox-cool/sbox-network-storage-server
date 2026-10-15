namespace SboxNetworkStorage.Storage;

/// <summary>Daily compatibility telemetry, separate from game data and portable project snapshots.</summary>
public interface IClientVersionStore
{
    Task WriteClientVersionsAsync(IReadOnlyList<ClientVersionDelta> batch, CancellationToken ct);
    Task<IReadOnlyList<ClientVersionRow>> ReadClientVersionsAsync(string projectId, int sinceDay, CancellationToken ct);
    Task PurgeClientVersionsAsync(int beforeDay, CancellationToken ct);

    /// <summary>Requests per library build across every project since <paramref name="sinceDay"/> (for update preflight).</summary>
    Task<IReadOnlyList<ObservedLibrary>> ReadObservedLibrariesAsync(int sinceDay, CancellationToken ct);

    /// <summary>Hourly request outcomes per library and server version (schema v7); buffered flushes merge additively.</summary>
    Task WriteClientOutcomesAsync(IReadOnlyList<ClientOutcomeDelta> batch, CancellationToken ct);
    /// <summary>Totals per (server version, library version) across every project, for hours at or after <paramref name="sinceHour"/>.</summary>
    Task<IReadOnlyList<ClientOutcomeTotals>> ReadClientOutcomeTotalsAsync(long sinceHour, CancellationToken ct);
    Task PurgeClientOutcomesAsync(long beforeHour, CancellationToken ct);
}

public sealed record ClientVersionKey(string ProjectId, int Day, string LibraryVersion, long? LibraryRevision, string ClientType, long? GameRevision)
{
    public string GroupKey => $"{LibraryVersion}|{LibraryRevision}|{ClientType}|{GameRevision}";
}
public sealed record ClientVersionDelta(ClientVersionKey Key, long Requests, long LastSeenUnixMs, byte[] PlayerSketch);
public sealed record ClientVersionRow(ClientVersionKey Key, long Requests, long LastSeenUnixMs, byte[] PlayerSketch)
{
    public long EstimatedPlayers => PlayerCountSketch.Estimate(PlayerSketch);
}

public sealed record ObservedLibrary(string LibraryVersion, long? LibraryRevision, long Requests);

/// <summary>
/// Hour is <c>unix ms / 3,600,000</c>. Rejected = 4xx a correct client should not receive (401, 403, 409 and 429 are
/// excluded); server errors = 5xx. Both are counted only for requests that were authenticated to a project.
/// </summary>
public sealed record ClientOutcomeKey(string ProjectId, long Hour, string ServerVersion, string LibraryVersion, long? LibraryRevision, string ClientType)
{
    public string GroupKey => $"{LibraryVersion}|{LibraryRevision}|{ClientType}";
}
public sealed record ClientOutcomeDelta(ClientOutcomeKey Key, long Requests, long Rejected, long ServerErrors);
public sealed record ClientOutcomeTotals(string ServerVersion, string LibraryVersion, long Requests, long Rejected, long ServerErrors, long LastHour);
