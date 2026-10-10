using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerDataCollection(string Id, string Name, bool Global, long RecordCount = 0)
{
    public string Kind => Global ? "Global" : "Per-player";
}

public sealed record OwnerDataRecord(string Key, long? Version, long? ChangedAtUnixMs, int SizeBytes, string Preview, JsonElement Payload)
{
    public string ChangedAt => ChangedAtUnixMs is { } ms
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "-";
}

/// <summary>One record found by the player search.</summary>
public sealed record OwnerPlayerRecord(OwnerDataCollection Collection, OwnerDataRecord Record);

/// <param name="Player">The searched Steam ID, or null when no search ran.</param>
/// <param name="PlayerRecords">Records whose key is the Steam ID or starts with <c>{steamId}_</c>, in every per-player collection.</param>
/// <param name="PlayerRecordsCut">True when a collection had more matches than the page shows.</param>
public sealed record OwnerDataModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerDataCollection> Collections,
    string? Player = null, IReadOnlyList<OwnerPlayerRecord>? PlayerRecords = null, bool PlayerRecordsCut = false, string? PlayerError = null);

public sealed record OwnerDataRecordsModel(string ProjectId, string ProjectName, OwnerDataCollection Collection,
    IReadOnlyList<OwnerDataRecord> Records, string? Query, int Page, int PageSize, long MatchCount)
{
    public int PageCount => (int)Math.Clamp((MatchCount + PageSize - 1) / PageSize, 1, int.MaxValue);
}

public sealed record OwnerDataRecordModel(string ProjectId, string ProjectName, OwnerDataCollection Collection,
    OwnerDataRecord Record, string PrettyJson, string? Error = null, bool Creating = false, string? Confirmation = null,
    int MaxPayloadBytes = 65536, string? SnapshotToken = null);

/// <summary>Owner record browser and schema-validated, atomically version-checked editor.</summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerDataController(INetworkStorageProjectService projects, INetworkStorageStore store,
    IAuditLogger audit, IDataProtectionProvider protection) : Controller
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxQueryLength = 256;
    /// <summary>Matches shown per collection by the player search.</summary>
    public const int PlayerRecordsPerCollection = 50;
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string Base = "/dashboard/projects/{projectId}/data";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Export = new() { WriteIndented = true };

    [HttpGet(Base)]
    public async Task<IActionResult> Collections(string projectId, [FromQuery] string? player, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        var collections = new List<OwnerDataCollection>();
        foreach (var row in await store.ListCollectionsAsync(projectId, ct))
        {
            if (OwnerDataRecords.Describe(row) is not { } collection) continue;
            collections.Add(collection with { RecordCount = await OwnerDataRecords.CountAsync(store, projectId, collection, null, ct) });
        }
        collections.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        var model = new OwnerDataModel(projectId, name, collections);
        player = string.IsNullOrWhiteSpace(player) ? null : player.Trim();
        if (player is null) return View("~/Views/Owner/Data.cshtml", model);
        // The key is the Steam ID itself or "{steamId}_..." (save slots), so the ID leaves room for the underscore.
        if (player.Length >= MaxQueryLength || !OwnerDataRecords.ValidPrefix(player))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Data.cshtml", model with { Player = player, PlayerError = "Enter a Steam ID such as 76561198000000000. Record keys only contain letters, numbers, underscores, hyphens and colons." });
        }
        var (records, cut) = await OwnerDataRecords.FindPlayerAsync(store, projectId, collections, player, PlayerRecordsPerCollection, ct);
        return View("~/Views/Owner/Data.cshtml", model with { Player = player, PlayerRecords = records, PlayerRecordsCut = cut });
    }

    [HttpGet(Base + "/{collectionId}")]
    public async Task<IActionResult> Records(string projectId, string collectionId, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int size = DefaultPageSize, CancellationToken ct = default)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        if (await OwnerDataRecords.CollectionAsync(store, projectId, collectionId, ct) is not { } collection) return NotFound();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (q?.Length > MaxQueryLength) return BadRequest($"Search text may contain at most {MaxQueryLength} characters.");
        size = Math.Clamp(size, 1, MaxPageSize);
        var total = await OwnerDataRecords.CountAsync(store, projectId, collection, null, ct);
        // Keys only hold record key characters, so other search text matches nothing.
        var matches = q is null ? total : OwnerDataRecords.ValidPrefix(q) ? await OwnerDataRecords.CountAsync(store, projectId, collection, q, ct) : 0;
        var model = new OwnerDataRecordsModel(projectId, name, collection with { RecordCount = total }, [], q, 1, size, matches);
        page = Math.Clamp(page, 1, model.PageCount);
        var offset = (int)Math.Min((long)(page - 1) * size, int.MaxValue);
        return View("~/Views/Owner/DataRecords.cshtml", model with
        {
            Page = page,
            Records = matches == 0 ? [] : await OwnerDataRecords.PageAsync(store, projectId, collection, q, offset, size, ct)
        });
    }

    [HttpGet(Base + "/{collectionId}/records/{recordKey}")]
    public async Task<IActionResult> Record(string projectId, string collectionId, string recordKey, CancellationToken ct)
    {
        var model = await LoadRecordModelAsync(projectId, collectionId, recordKey, ct);
        return model is null ? NotFound() : View("~/Views/Owner/DataRecord.cshtml", model);
    }

    [HttpGet(Base + "/{collectionId}/new")]
    public async Task<IActionResult> NewRecord(string projectId, string collectionId, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        if (await OwnerDataRecords.CollectionAsync(store, projectId, collectionId, ct) is not { } collection) return NotFound();
        return View("~/Views/Owner/DataRecord.cshtml", Draft(projectId, name, collection, "", "{}", null, true));
    }

    [HttpPost(Base + "/{collectionId}/new")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CreateRecord(string projectId, string collectionId,
        [FromForm] string? recordKey, [FromForm] string? payload, CancellationToken ct)
        => SaveAsync(projectId, collectionId, recordKey ?? "", payload ?? "", null, true, null, ct);

    [HttpPost(Base + "/{collectionId}/records/{recordKey}/save")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SaveRecord(string projectId, string collectionId, string recordKey,
        [FromForm] string? payload, [FromForm] long? expectedVersion, [FromForm] string? snapshotToken, CancellationToken ct)
        => SaveAsync(projectId, collectionId, recordKey, payload ?? "", expectedVersion, false, snapshotToken, ct);

    private async Task<IActionResult> SaveAsync(string projectId, string collectionId, string recordKey,
        string payload, long? expectedVersion, bool creating, string? snapshotToken, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        if (await OwnerDataRecords.CollectionAsync(store, projectId, collectionId, ct) is not { } collection) return NotFound();
        var draft = Draft(projectId, name, collection, recordKey, payload, expectedVersion, creating) with { SnapshotToken = snapshotToken };
        if (!OwnerDataRecords.ValidKey(collection, recordKey))
            return FormError(draft, "Use letters, numbers, underscores and hyphens" +
                (collection.Global ? " (1-128 characters) for the global record ID." : ", or colons (1-256 characters) for the player record key."));
        if (!ModelState.IsValid || (!creating && expectedVersion is null or < 1 or long.MaxValue))
            return FormError(draft, "A valid expected version is required. Reload the record before editing.");
        RecordMutationSnapshot? snapshot = null;
        if (!creating && !TrySnapshot(snapshotToken, projectId, collection, recordKey, expectedVersion, out snapshot))
            return FormError(draft, "The record snapshot is missing or invalid. Reload the current record before saving.");
        if (Encoding.UTF8.GetByteCount(payload) > store.MaxPayloadBytes)
            return FormError(draft, $"Payload exceeds the {store.MaxPayloadBytes} byte limit.");
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException error) { return FormError(draft, $"Invalid JSON: {error.Message}"); }
        using (document)
        {
            var row = await store.ReadCollectionAsync(projectId, collectionId, ct);
            if (row is null) return NotFound();
            try
            {
                if (OwnerRecordValidation.Validate(row.Value, document.RootElement) is { } error)
                    return FormError(draft, error);
            }
            catch (JsonException) { return FormError(draft, "The collection schema is not valid JSON. Correct the collection before saving records."); }
            if (!await store.TryMutateRecordAsync(projectId, collectionId, recordKey, collection.Global,
                document.RootElement, false, expectedVersion, ct, snapshot))
                return FormError(draft, "Conflict: this record was created, changed or deleted since the form opened. Your entered JSON is preserved; reload the current record and reconcile your changes before saving.", StatusCodes.Status409Conflict);
        }
        await AuditMutationAsync(projectId, collection, recordKey, creating ? "record.create" : "record.update", expectedVersion, ct);
        OwnerFlash.Success(this, creating ? "Record created." : "Record saved.");
        return Redirect(RecordUrl(projectId, collectionId, recordKey));
    }

    [HttpPost(Base + "/{collectionId}/records/{recordKey}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRecord(string projectId, string collectionId, string recordKey,
        [FromForm] string? confirmation, [FromForm] long? expectedVersion, [FromForm] string? payload,
        [FromForm] string? snapshotToken, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is not { } name) return NotFound();
        if (await OwnerDataRecords.CollectionAsync(store, projectId, collectionId, ct) is not { } collection || !OwnerDataRecords.ValidKey(collection, recordKey)) return NotFound();
        var current = await LoadRecordModelAsync(projectId, collectionId, recordKey, ct);
        var draft = Draft(projectId, name, collection, recordKey, payload ?? current?.PrettyJson ?? "", expectedVersion, false)
            with { Confirmation = confirmation, SnapshotToken = snapshotToken };
        if (!string.Equals(confirmation, recordKey, StringComparison.Ordinal))
            return FormError(draft, "Type the exact record key to confirm deletion.");
        if (!ModelState.IsValid || expectedVersion is null or < 1 or long.MaxValue)
            return FormError(draft, "A valid expected version is required. Reload the record before deleting.");
        if (!TrySnapshot(snapshotToken, projectId, collection, recordKey, expectedVersion, out var snapshot))
            return FormError(draft, "The record snapshot is missing or invalid. Reload the current record before deleting.");
        if (!await store.TryMutateRecordAsync(projectId, collectionId, recordKey, collection.Global,
            JsonSerializer.SerializeToElement<object?>(null), true, expectedVersion, ct, snapshot))
            return FormError(draft, "Conflict: this record changed or was deleted since the form opened. Reload it before deleting.", StatusCodes.Status409Conflict);
        await AuditMutationAsync(projectId, collection, recordKey, "record.delete", expectedVersion, ct);
        OwnerFlash.Success(this, "Record deleted.");
        return Redirect(CollectionUrl(projectId, collectionId));
    }

    private OwnerDataRecordModel Draft(string projectId, string name, OwnerDataCollection collection,
        string key, string payload, long? version, bool creating)
        => new(projectId, name, collection, new OwnerDataRecord(key, version, null, Encoding.UTF8.GetByteCount(payload), "", default),
            payload, Creating: creating, MaxPayloadBytes: store.MaxPayloadBytes);

    private IActionResult FormError(OwnerDataRecordModel model, string error, int status = StatusCodes.Status400BadRequest)
    {
        Response.StatusCode = status;
        return View("~/Views/Owner/DataRecord.cshtml", model with { Error = error });
    }

    private Task AuditMutationAsync(string projectId, OwnerDataCollection collection, string key, string action, long? version, CancellationToken ct)
        => audit.LogActionAsync(new AuditLogRequest(ProjectId: projectId, UserId: Owner.ToString(CultureInfo.InvariantCulture),
            Action: action, Actor: new { id = Owner, type = "owner-dashboard" },
            Target: new { id = key, type = "record", collectionId = collection.Id },
            Summary: new { collectionId = collection.Id, recordKey = key, collectionType = collection.Global ? "global" : "player", expectedVersion = version },
            Before: null, After: null), ct);

    private sealed record FormSnapshot(string OwnerStamp, string ProjectId, string CollectionId,
        string Key, bool Global, long? Version, RecordMutationSnapshot State);

    private string ProtectSnapshot(string projectId, OwnerDataCollection collection, OwnerDataRecord record)
        => protection.CreateProtector("owner-record-snapshot-v1").Protect(JsonSerializer.Serialize(new FormSnapshot(
            User.FindFirst(OwnerHostingExtensions.StampClaim)?.Value ?? "",
            projectId, collection.Id, record.Key, collection.Global, record.Version,
            new RecordMutationSnapshot(record.Payload.GetRawText(), record.ChangedAtUnixMs))));

    private bool TrySnapshot(string? token, string projectId, OwnerDataCollection collection, string key,
        long? version, out RecordMutationSnapshot? state)
    {
        state = null;
        if (string.IsNullOrEmpty(token) || token.Length > store.MaxPayloadBytes * 4L + 4096) return false;
        try
        {
            var snapshot = JsonSerializer.Deserialize<FormSnapshot>(
                protection.CreateProtector("owner-record-snapshot-v1").Unprotect(token));
            if (snapshot is null || snapshot.OwnerStamp != User.FindFirst(OwnerHostingExtensions.StampClaim)?.Value
                || snapshot.ProjectId != projectId || snapshot.CollectionId != collection.Id
                || snapshot.Key != key || snapshot.Global != collection.Global || snapshot.Version != version)
                return false;
            state = snapshot.State;
            return state is not null;
        }
        catch (CryptographicException) { return false; }
        catch (JsonException) { return false; }
    }

    [HttpGet(Base + "/{collectionId}/export")]
    public async Task<IActionResult> ExportCollection(string projectId, string collectionId, CancellationToken ct)
    {
        if (await ProjectNameAsync(projectId, ct) is null) return NotFound();
        if (await OwnerDataRecords.CollectionAsync(store, projectId, collectionId, ct) is not { } collection) return NotFound();
        var records = await OwnerDataRecords.LoadAsync(store, projectId, collection, ct);
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
        if (await OwnerDataRecords.CollectionAsync(store, projectId, collectionId, ct) is not { } collection) return null;
        if (await OwnerDataRecords.ReadAsync(store, projectId, collection, recordKey, ct) is not { } record) return null;
        return new OwnerDataRecordModel(projectId, name, collection, record, JsonSerializer.Serialize(record.Payload, Pretty),
            MaxPayloadBytes: store.MaxPayloadBytes, SnapshotToken: ProtectSnapshot(projectId, collection, record));
    }

    private async Task<string?> ProjectNameAsync(string projectId, CancellationToken ct)
    {
        if (!StorageIdValidation.IsValidCollectionId(projectId)) return null;
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        return access is not null && access.StorageOwnerUserId == Owner && access.CanManage
            && string.Equals(access.Project.Id, projectId, StringComparison.Ordinal) ? access.Project.Name : null;
    }
}
