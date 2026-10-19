using System.Data;
using System.Data.Common;
using V = SboxNetworkStorage.Storage.Relational.StoreValidation;

namespace SboxNetworkStorage.Storage.Relational;

// Structured game logs (epic #111, L2): one table, an FTS mirror on the driver side, and a single keyset-paginated
// search that both drivers share. The SQL that differs per driver is reached through the three hooks below.
public abstract partial class RelationalNetworkStorageStore : IGameLogStore
{
    /// <summary>Upper bound on free-text tokens per query. The API layer enforces the same limit.</summary>
    protected const int GameLogMaxSearchTokens = 8;

    /// <summary>SQL expression for the <c>@attrs_json</c> parameter on insert (driver casts it to its JSON type).</summary>
    protected abstract string GameLogAttrsValueSql { get; }

    /// <summary>Predicate over <c>e</c> for ANDed free-text tokens. Binds <c>@fts</c> (FTS5 expression) and <c>@tok0..@tok{n-1}</c>.</summary>
    protected abstract string GameLogTextPredicateSql(int tokenCount);

    /// <summary>Predicate over <c>e</c> for attribute filter <paramref name="index"/>. Binds <c>@attr_path{i}</c>, <c>@attr_key{i}</c>, <c>@attr_value{i}</c>.</summary>
    protected abstract string GameLogAttrPredicateSql(int index);

    private string GameLogInsertSql => $"""
        INSERT INTO {TablePrefix}game_log_entries
            (project_id, entry_id, ts_unix_ms, stream, level, event, actor_steam_id, target_steam_id, revision_id,
             client_type, library_version, message, attrs_json, trace_id, search_text)
        VALUES (@project, @entry, @ts, @stream, @level, @event, @actor, @target, @revision,
                @client, @library, @message, {GameLogAttrsValueSql}, @trace, @search)
        """;

    private string GameLogSelectColumns => """
        e.project_id, e.entry_id, e.ts_unix_ms, e.stream, e.level, e.event, e.actor_steam_id, e.target_steam_id,
        e.revision_id, e.client_type, e.library_version, e.message, CAST(e.attrs_json AS TEXT), e.trace_id, e.search_text
        """;

    public async Task<int> InsertGameLogsAsync(IReadOnlyList<GameLogRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // One prepared command, parameter values rebound per row: the writer thread is on the hot path.
        await using var command = Command(connection, GameLogInsertSql, [
            Text("project", null), Text("entry", null), Int64("ts", null), Text("stream", null), Text("level", null),
            Text("event", null), Text("actor", null), Text("target", null), Int64("revision", null), Text("client", null),
            Text("library", null), Text("message", null), Text("attrs_json", null), Text("trace", null), Text("search", null),
        ], transaction);
        foreach (var row in rows)
        {
            V.Id(row.ProjectId);
            command.Parameters["@project"].Value = row.ProjectId;
            command.Parameters["@entry"].Value = row.EntryId;
            command.Parameters["@ts"].Value = row.TsUnixMs;
            command.Parameters["@stream"].Value = row.Stream;
            command.Parameters["@level"].Value = row.Level;
            command.Parameters["@event"].Value = row.Event;
            command.Parameters["@actor"].Value = (object?)row.ActorSteamId ?? DBNull.Value;
            command.Parameters["@target"].Value = (object?)row.TargetSteamId ?? DBNull.Value;
            command.Parameters["@revision"].Value = (object?)row.RevisionId ?? DBNull.Value;
            command.Parameters["@client"].Value = (object?)row.ClientType ?? DBNull.Value;
            command.Parameters["@library"].Value = (object?)row.LibraryVersion ?? DBNull.Value;
            command.Parameters["@message"].Value = row.Message;
            command.Parameters["@attrs_json"].Value = row.AttrsJson;
            command.Parameters["@trace"].Value = (object?)row.TraceId ?? DBNull.Value;
            command.Parameters["@search"].Value = row.SearchText;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return rows.Count;
    }

    public async Task<IReadOnlyList<GameLogRow>> SearchGameLogsAsync(GameLogQuery query, CancellationToken ct)
    {
        V.Id(query.ProjectId);
        if (query.Streams.Count == 0) return [];
        if (query.SearchTokens.Count > GameLogMaxSearchTokens) throw new ArgumentOutOfRangeException(nameof(query));

        var args = new List<Arg> { Text("project", query.ProjectId), Int32("limit", query.Limit) };
        var where = new List<string> { "e.project_id = @project" };

        var streamParams = new List<string>();
        for (var i = 0; i < query.Streams.Count; i++)
        {
            args.Add(Text($"stream{i}", query.Streams[i]));
            streamParams.Add($"@stream{i}");
        }
        where.Add($"e.stream IN ({string.Join(", ", streamParams)})");

        if (query.FromUnixMs is { } from) { args.Add(Int64("from", from)); where.Add("e.ts_unix_ms >= @from"); }
        if (query.ToUnixMs is { } to) { args.Add(Int64("to", to)); where.Add("e.ts_unix_ms < @to"); }
        if (query.Level is not null) { args.Add(Text("level", query.Level)); where.Add("e.level = @level"); }
        if (query.Event is not null) { args.Add(Text("event", query.Event)); where.Add("e.event = @event"); }
        if (query.PlayerSteamId is not null)
        {
            args.Add(Text("player", query.PlayerSteamId));
            where.Add("(e.actor_steam_id = @player OR e.target_steam_id = @player)");
        }

        for (var i = 0; i < query.Attrs.Count; i++)
        {
            var attr = query.Attrs[i];
            args.Add(Text($"attr_path{i}", "$." + attr.Key));
            args.Add(Text($"attr_key{i}", attr.Key));
            args.Add(Text($"attr_value{i}", attr.Value));
            where.Add(GameLogAttrPredicateSql(i));
        }

        if (query.SearchTokens.Count > 0)
        {
            // The FTS5 expression is only read by the SQLite predicate; PostgreSQL uses @tokN.
            args.Add(Text("fts", string.Join(" AND ", query.SearchTokens.Select(static t => "\"" + t + "\""))));
            for (var i = 0; i < query.SearchTokens.Count; i++) args.Add(Text($"tok{i}", query.SearchTokens[i]));
            where.Add(GameLogTextPredicateSql(query.SearchTokens.Count));
        }

        if (query.Cursor is { } cursor)
        {
            args.Add(Int64("cursor_ts", cursor.TsUnixMs));
            args.Add(Text("cursor_id", cursor.EntryId));
            where.Add("(e.ts_unix_ms < @cursor_ts OR (e.ts_unix_ms = @cursor_ts AND e.entry_id < @cursor_id))");
        }

        var sql = $"""
            SELECT {GameLogSelectColumns}
            FROM {TablePrefix}game_log_entries e
            WHERE {string.Join(" AND ", where)}
            ORDER BY e.ts_unix_ms DESC, e.entry_id DESC
            LIMIT @limit
            """;

        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, args.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<GameLogRow>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadGameLogRow(reader));
        return rows;
    }

    public async Task<long> PurgeGameLogsAsync(string projectId, string stream, long beforeUnixMs, CancellationToken ct)
    {
        V.Id(projectId);
        return await ExecuteCountAsync($"DELETE FROM {TablePrefix}game_log_entries WHERE project_id = @project AND stream = @stream AND ts_unix_ms < @before",
            ct, Text("project", projectId), Text("stream", stream), Int64("before", beforeUnixMs));
    }

    public async Task<IReadOnlyList<GameLogStreamCount>> CountGameLogStreamsAsync(CancellationToken ct)
    {
        var sql = $"SELECT project_id, stream, COUNT(*) FROM {TablePrefix}game_log_entries GROUP BY project_id, stream";
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, sql, []);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var counts = new List<GameLogStreamCount>();
        while (await reader.ReadAsync(ct))
            counts.Add(new GameLogStreamCount(reader.GetString(0), reader.GetString(1), Convert.ToInt64(reader.GetValue(2))));
        return counts;
    }

    private static GameLogRow ReadGameLogRow(DbDataReader reader) => new(
        ProjectId: reader.GetString(0),
        EntryId: reader.GetString(1),
        TsUnixMs: reader.GetInt64(2),
        Stream: reader.GetString(3),
        Level: reader.GetString(4),
        Event: reader.GetString(5),
        ActorSteamId: reader.IsDBNull(6) ? null : reader.GetString(6),
        TargetSteamId: reader.IsDBNull(7) ? null : reader.GetString(7),
        RevisionId: reader.IsDBNull(8) ? null : reader.GetInt64(8),
        ClientType: reader.IsDBNull(9) ? null : reader.GetString(9),
        LibraryVersion: reader.IsDBNull(10) ? null : reader.GetString(10),
        Message: reader.GetString(11),
        AttrsJson: reader.GetString(12),
        TraceId: reader.IsDBNull(13) ? null : reader.GetString(13),
        SearchText: reader.GetString(14));
}
