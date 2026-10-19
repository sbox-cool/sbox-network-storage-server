namespace SboxNetworkStorage.Storage;

/// <summary>One normalized game-log row. <see cref="SearchText"/> is the space-joined token list used for free-text search.</summary>
public sealed record GameLogRow(
    string ProjectId,
    string EntryId,
    long TsUnixMs,
    string Stream,
    string Level,
    string Event,
    string? ActorSteamId,
    string? TargetSteamId,
    long? RevisionId,
    string? ClientType,
    string? LibraryVersion,
    string Message,
    string AttrsJson,
    string? TraceId,
    string SearchText);

/// <summary>Keyset cursor: entries strictly older than (<see cref="TsUnixMs"/>, <see cref="EntryId"/>) in (ts DESC, entry_id DESC) order.</summary>
public sealed record GameLogCursor(long TsUnixMs, string EntryId);

/// <summary>An attribute equality filter. <see cref="Key"/> is validated upstream to <c>[A-Za-z0-9_]{1,64}</c>; <see cref="Value"/> is the scalar text form.</summary>
public sealed record GameLogAttrFilter(string Key, string Value);

/// <summary>
/// Search filters. <see cref="Streams"/> is the set of enabled streams to search (never empty here).
/// <see cref="SearchTokens"/> are ANDed whole-token matches produced by the same tokenizer that wrote <see cref="GameLogRow.SearchText"/>.
/// </summary>
public sealed record GameLogQuery(
    string ProjectId,
    IReadOnlyList<string> Streams,
    long? FromUnixMs,
    long? ToUnixMs,
    string? Level,
    string? Event,
    string? PlayerSteamId,
    IReadOnlyList<GameLogAttrFilter> Attrs,
    IReadOnlyList<string> SearchTokens,
    GameLogCursor? Cursor,
    int Limit);

public sealed record GameLogStreamCount(string ProjectId, string Stream, long Entries);

/// <summary>Storage for structured game logs (epic #111, L2). Implemented by each relational driver.</summary>
public interface IGameLogStore
{
    /// <summary>Inserts a batch in one transaction. Returns the number of rows written.</summary>
    Task<int> InsertGameLogsAsync(IReadOnlyList<GameLogRow> rows, CancellationToken ct);

    /// <summary>Returns up to <see cref="GameLogQuery.Limit"/> rows in (ts DESC, entry_id DESC) order.</summary>
    Task<IReadOnlyList<GameLogRow>> SearchGameLogsAsync(GameLogQuery query, CancellationToken ct);

    /// <summary>Deletes one project's stream rows older than <paramref name="beforeUnixMs"/>. Returns rows deleted.</summary>
    Task<long> PurgeGameLogsAsync(string projectId, string stream, long beforeUnixMs, CancellationToken ct);

    /// <summary>Row counts per project and stream, used to drive retention purges.</summary>
    Task<IReadOnlyList<GameLogStreamCount>> CountGameLogStreamsAsync(CancellationToken ct);
}
