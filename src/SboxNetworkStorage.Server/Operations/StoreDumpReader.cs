using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Replays the JSON-lines files written by <see cref="StoreDumpWriter"/> into any
/// <see cref="INetworkStorageStore"/> using the contract's upserts, so importing
/// the same archive twice leaves the same rows. Write timestamps the store stamps
/// itself (<c>updated_at_unix_ms</c> on versioned rows) take the import time.
/// </summary>
public sealed class StoreDumpReader(INetworkStorageStore store)
{
    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>Rows applied so far.</summary>
    public long Rows { get; private set; }

    /// <summary>Applies one <c>data/…</c> archive entry; throws <see cref="ExportArchiveException"/> for unknown entries.</summary>
    public async Task ApplyEntryAsync(string entryName, Stream content, CancellationToken ct)
    {
        Func<JsonElement, Task> apply = entryName switch
        {
            ExportFormat.WorkspaceObjectsEntry => row => store.PutWorkspaceObjectAsync(Str(row, "path"), Str(row, "content"), ct),
            ExportFormat.MembershipsEntry => row => store.UpsertProjectMembershipAsync(
                Str(row, "user_id"), Str(row, "project_id"), Str(row, "role"), Long(row, "created_at_unix_ms"), ct),
            _ when TryParseProjectEntry(entryName, out var projectId, out var resource) => ProjectApplier(projectId, resource, ct),
            _ => throw new ExportArchiveException($"Unexpected archive entry '{entryName}'.")
        };

        using var reader = new StreamReader(content, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                await apply(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
            {
                throw new ExportArchiveException($"{entryName}:{lineNumber}: invalid row ({ex.Message}).");
            }

            Rows++;
        }
    }

    private static bool TryParseProjectEntry(string entryName, out string projectId, out string resource)
    {
        projectId = resource = string.Empty;
        if (!entryName.StartsWith(ExportFormat.ProjectsPrefix, StringComparison.Ordinal) || !entryName.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = entryName[ExportFormat.ProjectsPrefix.Length..^".jsonl".Length].Split('/');
        if (parts.Length != 2 || !ExportFormat.IsValidProjectId(parts[0]) || !ExportFormat.ProjectResources.Contains(parts[1]))
        {
            return false;
        }

        (projectId, resource) = (parts[0], parts[1]);
        return true;
    }

    private Func<JsonElement, Task> ProjectApplier(string p, string resource, CancellationToken ct) => resource switch
    {
        "project" => row => store.UpsertProjectAsync(p, Json(row, "payload"), 1, ct),
        "collections" => row => store.UpsertCollectionAsync(p, Str(row, "collection_id"), Str(row, "name"), Str(row, "visibility"),
            Json(row, "definition_json"), Long(row, "version"), ct),
        "records" => row => store.UpsertRecordAsync(p, Str(row, "collection_id"), Str(row, "record_key"), Json(row, "payload_json"),
            Bool(row, "deleted"), Long(row, "version"), ct),
        "ledger-entries" => row => store.InsertLedgerEntryAsync(p, Str(row, "collection_id"), Str(row, "record_key"),
            Long(row, "sequence"), Json(row, "entry_json"), ct),
        "global-records" => row => store.UpsertGlobalRecordAsync(p, Str(row, "collection_id"), Str(row, "record_id"),
            Json(row, "payload_json"), Long(row, "version"), ct),
        "endpoints" => row => store.UpsertEndpointAsync(p, Str(row, "endpoint_id"), Str(row, "slug"), Str(row, "method"),
            Bool(row, "enabled"), Json(row, "definition_json"), OptStr(row, "version_hash"), Long(row, "version"), ct),
        "workflows" => row => store.UpsertWorkflowAsync(p, Str(row, "workflow_id"), Str(row, "name"), Json(row, "definition_json"),
            OptStr(row, "version_hash"), Long(row, "version"), ct),
        "queries" => row => store.UpsertQueryAsync(p, Str(row, "query_id"), Str(row, "name"), Bool(row, "requires_secret_key"),
            Json(row, "definition_json"), Long(row, "version"), ct),
        "api-keys" => row => store.UpsertApiKeyAsync(p, Str(row, "api_key"), Str(row, "user_id"), Str(row, "key_type"),
            Str(row, "key_hash"), Str(row, "key_identifier"), Str(row, "label"), Bool(row, "enabled"),
            Json(row, "permissions_json"), Long(row, "version"), ct),
        "pages" => row => store.UpsertPageAsync(p, Str(row, "page_slug"), Str(row, "title"), Str(row, "content_json"),
            Long(row, "created_at_unix_ms"), Long(row, "updated_at_unix_ms"), ct),
        "game-values" => row => store.UpsertGameValuesAsync(p, Json(row, "payload_json"), OptStr(row, "version_hash"), Long(row, "version"), ct),
        "rate-limits" => row => store.UpsertRateLimitRulesAsync(p, Json(row, "rules_json"), Long(row, "version"), ct),
        "checkpoint-cursor" => row => store.UpsertCheckpointCursorAsync(p, Long(row, "latest_sequence"), OptStr(row, "manifest_path"),
            Long(row, "version"), ct),
        "player-profiles" => row => store.UpsertPlayerProfileAsync(p, Str(row, "steam_id"), Str(row, "player_name"), Bool(row, "is_online"),
            OptLong(row, "online_since_unix_ms"), Long(row, "last_seen_unix_ms"), OptLong(row, "last_heartbeat_unix_ms"),
            OptStr(row, "current_session_id"), OptLong(row, "current_session_last_seconds"), Long(row, "total_seconds"),
            Long(row, "session_count"), OptStr(row, "last_event_type"), OptStr(row, "last_endpoint_slug"),
            RawJson(row, "managed_counters_json") ?? "{}", Long(row, "updated_at_unix_ms"), ct),
        "player-sessions" => row => store.InsertPlayerSessionAsync(p, Str(row, "steam_id"), Str(row, "session_id"),
            OptLong(row, "started_at_unix_ms"), OptLong(row, "last_heartbeat_at_unix_ms"), OptLong(row, "ended_at_unix_ms"),
            RawJson(row, "last_metrics_json"), RawJson(row, "summary_json"), ct),
        "player-events" => row => store.InsertPlayerAnalyticsEventV2Async(p, Str(row, "steam_id"), Long(row, "created_at_unix_ms"),
            Str(row, "event_id"), Str(row, "event_type"), Str(row, "category"), Str(row, "label"), Str(row, "endpoint_slug"),
            Str(row, "collection_id"), Json(row, "payload_json"), ct),
        "audit-logs" => row => store.InsertAuditLogAsync(p, Long(row, "created_at_unix_ms"), Str(row, "log_id"), Str(row, "user_id"),
            Str(row, "action"), Str(row, "actor_json"), Str(row, "target_json"), Str(row, "summary_json"), Str(row, "diff_json"), ct),
        "usage" => row => RestoreStorageBytesAsync(p, Long(row, "storage_bytes"), ct),
        _ => throw new ExportArchiveException($"Unknown project resource '{resource}'.")
    };

    /// <summary>
    /// The cumulative storage footprint is a sum of additive counters, so the import
    /// adds only the difference to the target's current total (idempotent on re-import).
    /// </summary>
    private async Task RestoreStorageBytesAsync(string projectId, long exportedBytes, CancellationToken ct)
    {
        var delta = exportedBytes - await store.ReadProjectStorageBytesAsync(projectId, ct);
        if (delta == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await store.IncrementProjectUsageAsync(projectId, now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), endpointSlug: null,
            new UsageDelta(0, 0, 0, 0, 0, 0, 0, 0, 0, StorageDeltaBytes: delta), ct);
    }

    /// <summary>A required string column; JSON null (a NULL text column) becomes empty, as the store renders it.</summary>
    private static string Str(JsonElement row, string name) => OptStr(row, name) ?? string.Empty;

    private static JsonElement Column(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) ? value : throw new KeyNotFoundException($"missing column '{name}'");

    private static string? OptStr(JsonElement row, string name)
    {
        var value = Column(row, name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static long Long(JsonElement row, string name) => OptLong(row, name) ?? 0;

    private static long? OptLong(JsonElement row, string name)
    {
        var value = Column(row, name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    }

    private static bool Bool(JsonElement row, string name)
    {
        var value = Column(row, name);
        return value.ValueKind != JsonValueKind.Null && value.GetBoolean();
    }

    private static JsonElement Json(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) ? value : JsonNull;

    private static string? RawJson(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetRawText() : null;
}
