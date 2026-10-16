using System.Text;
using System.Text.Json;
using SboxNetworkStorage.EndpointSpike.Plan;
using SboxNetworkStorage.EndpointSpike.Runtime;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.EndpointSpike.Broker;

/// <summary>Raised when a data budget or declaration rule is breached. <see cref="Code"/> is the stable limit code.</summary>
public sealed class BudgetExceededException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record PendingWrite(string Collection, string Key, Dictionary<string, object?> Data);

/// <summary>
/// Sole data path for a sandboxed run. Serves reads only from collections the plan declares, enforces the
/// materialized-bytes budget, and holds writes until the worker reports a complete successful result.
/// Commit is one store transaction, so a run either persists every write or none.
/// </summary>
public sealed class RecordBroker
{
    private readonly INetworkStorageStore _store;
    private readonly string _projectId;
    private readonly HashSet<string> _declared;
    private readonly List<PendingWrite> _writes = new();
    private long _materializedBytes;

    public RecordBroker(INetworkStorageStore store, string projectId, IEnumerable<string> declaredCollections)
    {
        _store = store;
        _projectId = projectId;
        _declared = new HashSet<string>(declaredCollections, StringComparer.Ordinal);
    }

    public long MaterializedBytes => _materializedBytes;

    public IReadOnlyList<PendingWrite> PendingWrites => _writes;

    public async Task<object?> ReadAsync(string collection, string key, CancellationToken ct)
    {
        RequireDeclared(collection);
        var row = await _store.ReadRecordAsync(_projectId, collection, key, ct);
        var payload = Unwrap(row);
        if (payload is null)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetByteCount(payload.Value.GetRawText());
        _materializedBytes += bytes;
        if (_materializedBytes > RuntimeLimits.MaxMaterializedBytes)
        {
            throw new BudgetExceededException(
                RuntimeLimits.CodeMaterialized,
                $"Materialized {_materializedBytes} bytes exceeds the {RuntimeLimits.MaxMaterializedBytes} byte budget.");
        }

        return PlanReader.Convert(payload.Value);
    }

    /// <summary>Validates writes reported by a completed worker run. Nothing is persisted here.</summary>
    public void Accept(IReadOnlyList<PendingWrite> writes)
    {
        foreach (var write in writes)
        {
            RequireDeclared(write.Collection);
            if (string.IsNullOrEmpty(write.Key))
            {
                throw new BudgetExceededException("WRITE_KEY_EMPTY", "A write must name a non-empty record key.");
            }
            var size = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(write.Data));
            if (size > _store.MaxPayloadBytes)
            {
                throw new BudgetExceededException(
                    "PAYLOAD_TOO_LARGE",
                    $"Record payload of {size} bytes exceeds the store limit of {_store.MaxPayloadBytes} bytes.");
            }
            _writes.Add(write);
        }
    }

    /// <summary>Persists every accepted write in one store transaction. No-op when there are no writes.</summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        if (_writes.Count == 0)
        {
            return;
        }

        await using var transaction = await _store.BeginTransactionAsync(ct);
        foreach (var write in _writes)
        {
            await transaction.Store.UpsertRecordAsync(
                _projectId,
                write.Collection,
                write.Key,
                PlanReader.ToJsonElement(write.Data),
                deleted: false,
                version: 1,
                ct);
        }
        await transaction.CommitAsync(ct);
    }

    private void RequireDeclared(string collection)
    {
        if (!_declared.Contains(collection))
        {
            throw new BudgetExceededException(
                "COLLECTION_NOT_DECLARED",
                $"Collection '{collection}' is not declared by this endpoint.");
        }
    }

    /// <summary>Returns the stored payload of a record row, or null when missing or soft-deleted.</summary>
    internal static JsonElement? Unwrap(JsonElement? row)
    {
        if (row is not { ValueKind: JsonValueKind.Object } wrapper)
        {
            return null;
        }
        if (wrapper.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
        {
            return null;
        }
        if (!wrapper.TryGetProperty("payload_json", out var payload))
        {
            return null;
        }
        if (payload.ValueKind == JsonValueKind.String)
        {
            using var parsed = JsonDocument.Parse(payload.GetString() ?? "null");
            return parsed.RootElement.ValueKind == JsonValueKind.Null ? null : parsed.RootElement.Clone();
        }
        return payload.ValueKind == JsonValueKind.Null ? null : payload.Clone();
    }
}
