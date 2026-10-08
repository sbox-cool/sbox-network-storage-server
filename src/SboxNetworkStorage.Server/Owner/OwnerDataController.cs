using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerDataCollection(string Id, string Name, bool Global, int RecordCount = 0)
{
    public string Kind => Global ? "Global" : "Per-player";
}

public sealed record OwnerDataRecord(string Key, long? Version, long? ChangedAtUnixMs, int SizeBytes, string Preview, JsonElement Payload)
{
    public string ChangedAt => ChangedAtUnixMs is { } ms
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "-";
}

public sealed record OwnerDataModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerDataCollection> Collections);

public sealed record OwnerDataRecordsModel(string ProjectId, string ProjectName, OwnerDataCollection Collection,
    IReadOnlyList<OwnerDataRecord> Records, string? Query, int Page, int PageSize, int MatchCount)
{
    public int PageCount => Math.Max(1, (MatchCount + PageSize - 1) / PageSize);
}

public sealed record OwnerDataRecordModel(string ProjectId, string ProjectName, OwnerDataCollection Collection,
    OwnerDataRecord Record, string PrettyJson, string? Error = null);

/// <summary>Browser over a project's stored records, with confirmed, audited deletion.</summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerDataController(INetworkStorageProjectService projects, INetworkStorageStore store,
    INetworkStorageDataPlane dataPlane, IAuditLogger audit) : Controller
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxQueryLength = 256;
    private const int PreviewLength = 160;
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string Base = "/dashboard/projects/{projectId}/data";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Export = new() { WriteIndented = true };

    [HttpGet(Base)]
    public async Task<IActionResult> Collections(string projectId, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        var collections = new List<OwnerDataCollection>();
        foreach (var row in await store.ListCollectionsAsync(projectId, ct))
        {
            if (Describe(row) is not { } collection) continue;
            collections.Add(collection with { RecordCount = (await LoadRecordsAsync(projectId, collection, ct)).Count });
        }
        collections.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return View("~/Views/Owner/Data.cshtml", new OwnerDataModel(projectId, name, collections));
    }

    [HttpGet(Base + "/{collectionId}")]
    public async Task<IActionResult> Records(string projectId, string collectionId, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int size = DefaultPageSize, CancellationToken ct = default)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        if (await CollectionAsync(projectId, collectionId, ct) is not { } collection) return NotFound();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (q?.Length > MaxQueryLength) return BadRequest($"Search text may contain at most {MaxQueryLength} characters.");
        size = Math.Clamp(size, 1, MaxPageSize);
        var all = await LoadRecordsAsync(projectId, collection, ct);
        var matches = all.Where(record => q is null || record.Key.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var model = new OwnerDataRecordsModel(projectId, name, collection with { RecordCount = all.Count }, [], q, 1, size, matches.Count);
        page = Math.Clamp(page, 1, model.PageCount);
        return View("~/Views/Owner/DataRecords.cshtml", model with
        {
            Page = page,
            Records = matches.Skip((page - 1) * size).Take(size).ToList()
        });
    }

    [HttpGet(Base + "/{collectionId}/records/{recordKey}")]
    public async Task<IActionResult> Record(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        var model = await LoadRecordModelAsync(projectId, collectionId, recordKey, ct);
        return model is null ? NotFound() : View("~/Views/Owner/DataRecord.cshtml", model);
    }

    [HttpPost(Base + "/{collectionId}/records/{recordKey}/delete")]
    public async Task<IActionResult> DeleteRecord(string projectId, string collectionId, string recordKey,
        [FromForm] string? confirmation, CancellationToken ct)
    {
        var model = await LoadRecordModelAsync(projectId, collectionId, recordKey, ct);
        if (model is null) return NotFound();
        if (!string.Equals(confirmation, recordKey, StringComparison.Ordinal))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/DataRecord.cshtml", model with { Error = "Type the exact record key to confirm deletion." });
        }
        await dataPlane.DeleteRecordAsync(Owner, projectId, collectionId, recordKey, ct);
        await audit.LogActionAsync(new AuditLogRequest(ProjectId: projectId, UserId: Owner.ToString(CultureInfo.InvariantCulture),
            Action: "record.delete", Actor: new { id = Owner, type = "owner-dashboard" },
            Target: new { id = recordKey, type = "record", collectionId },
            Summary: new { collectionId, recordKey, collectionType = model.Collection.Global ? "global" : "player", model.Record.Version },
            Before: null, After: null), ct);
        return Redirect(CollectionUrl(projectId, collectionId));
    }

    [HttpGet(Base + "/{collectionId}/export")]
    public async Task<IActionResult> ExportCollection(string projectId, string collectionId, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is null) return NotFound();
        if (await CollectionAsync(projectId, collectionId, ct) is not { } collection) return NotFound();
        var records = await LoadRecordsAsync(projectId, collection, ct);
        var document = new
        {
            format = "sbox-ns.collection-export",
            formatVersion = 1,
            projectId,
            collectionId,
            collectionName = collection.Name,
            collectionType = collection.Global ? "global" : "player",
            exportedAt = DateTimeOffset.UtcNow,
            recordCount = records.Count,
            records = records.Select(record => new { key = record.Key, version = record.Version, changedAtUnixMs = record.ChangedAtUnixMs, payload = record.Payload })
        };
        return File(JsonSerializer.SerializeToUtf8Bytes(document, Export), "application/json", $"{projectId}-{collectionId}.json");
    }

    public static string CollectionUrl(string projectId, string collectionId)
        => $"/dashboard/projects/{Uri.EscapeDataString(projectId)}/data/{Uri.EscapeDataString(collectionId)}";

    public static string RecordUrl(string projectId, string collectionId, string recordKey)
        => $"{CollectionUrl(projectId, collectionId)}/records/{Uri.EscapeDataString(recordKey)}";

    private async Task<OwnerDataRecordModel?> LoadRecordModelAsync(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return null;
        if (await CollectionAsync(projectId, collectionId, ct) is not { } collection) return null;
        var validKey = collection.Global ? StorageIdValidation.IsValidCollectionId(recordKey) : StorageIdValidation.IsValidRecordKey(recordKey);
        if (!validKey) return null;
        var row = collection.Global
            ? await store.ReadGlobalRecordAsync(projectId, collectionId, recordKey, ct)
            : await store.ReadRecordAsync(projectId, collectionId, recordKey, ct);
        if (row is not { } value || ToRecord(value, collection.Global) is not { } record) return null;
        return new OwnerDataRecordModel(projectId, name, collection, record, JsonSerializer.Serialize(record.Payload, Pretty));
    }

    private async Task<string?> ProjectNameAsync(string projectId, CancellationToken ct)
    {
        if (!StorageIdValidation.IsValidCollectionId(projectId)) return null;
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        return access is not null && access.StorageOwnerUserId == Owner && access.CanManage
            && string.Equals(access.Project.Id, projectId, StringComparison.Ordinal) ? access.Project.Name : null;
    }

    private async Task<OwnerDataCollection?> CollectionAsync(string projectId, string collectionId, CancellationToken ct)
        => StorageIdValidation.IsValidCollectionId(collectionId) && await store.ReadCollectionAsync(projectId, collectionId, ct) is { } row
            ? Describe(row) : null;

    private async Task<List<OwnerDataRecord>> LoadRecordsAsync(string projectId, OwnerDataCollection collection, CancellationToken ct)
    {
        var rows = collection.Global
            ? await store.ListGlobalRecordsAsync(projectId, collection.Id, ct)
            : await store.ListRecordsAsync(projectId, collection.Id, ct);
        var records = new List<OwnerDataRecord>(rows.Count);
        foreach (var row in rows)
        {
            if (ToRecord(row, collection.Global) is { } record) records.Add(record);
        }
        records.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        return records;
    }

    /// <summary>Maps a stored row; tombstoned or payload-less rows are skipped exactly like the data plane reads them.</summary>
    private static OwnerDataRecord? ToRecord(JsonElement row, bool global)
    {
        if (RecordRow.ExtractPayload(row) is not { } payload) return null;
        if (Text(row, global ? "record_id" : "record_key") is not { } key) return null;
        var raw = payload.GetRawText();
        var preview = raw.Length <= PreviewLength ? raw : raw[..PreviewLength] + "…";
        return new OwnerDataRecord(key, Number(row, "version"), Number(row, global ? "created_at_unix_ms" : "updated_at_unix_ms"),
            Encoding.UTF8.GetByteCount(raw), preview, payload);
    }

    private static OwnerDataCollection? Describe(JsonElement row)
    {
        if (Text(row, "collection_id") is not { } id) return null;
        return new OwnerDataCollection(id, Text(row, "name") is { Length: > 0 } name ? name : id, IsGlobal(row));
    }

    // Same routing rule as the data plane: definition_json.collectionType == "global" (string or parsed column).
    private static bool IsGlobal(JsonElement row)
    {
        if (!row.TryGetProperty("definition_json", out var definition)) return false;
        if (definition.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(definition.GetString() ?? string.Empty);
                return IsGlobalDefinition(document.RootElement);
            }
            catch (JsonException) { return false; }
        }
        return IsGlobalDefinition(definition);
    }

    private static bool IsGlobalDefinition(JsonElement definition)
        => definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty("collectionType", out var type) && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "global", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
