using System.Text.Json;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// The store-backed <see cref="IQueryValuesContextProvider"/>. Reads game-values
/// and collections from the store, then flattens them into a nested dictionary
/// keyed by group/table id. Port of legacy server <c>getQueryValuesContext</c> +
/// <c>flattenGameValues</c> (<c>tools/sbox/game-values.js</c>).
///
/// <para>Collection-level constants and tables take precedence over legacy
/// game-values items (same priority as the legacy server implementation).</para>
/// </summary>
public sealed class StoreQueryValuesContextProvider : IQueryValuesContextProvider
{
    private readonly INetworkStorageStore _store;
    private readonly ILogger<StoreQueryValuesContextProvider> _logger;
    private readonly RevisionOverlay? _overlay;

    public StoreQueryValuesContextProvider(INetworkStorageStore store, ILogger<StoreQueryValuesContextProvider> logger)
        : this(store, logger, overlay: null)
    {
    }

    private StoreQueryValuesContextProvider(INetworkStorageStore store, ILogger<StoreQueryValuesContextProvider> logger, RevisionOverlay? overlay)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _overlay = overlay;
    }

    /// <summary>A provider for a request targeting the staged revision: staged collection constants and tables apply.</summary>
    public StoreQueryValuesContextProvider WithRevisionOverlay(RevisionOverlay overlay) => new(_store, _logger, overlay);

    public async Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(string projectId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            // Read game_values and collections from the store in parallel.
            var gvTask = _store.ReadGameValuesAsync(projectId, cancellationToken);
            var colTask = _store.ListCollectionsAsync(projectId, cancellationToken);
            await Task.WhenAll(gvTask, colTask);
            var collections = _overlay is null ? colTask.Result : _overlay.MergeCollectionRows(colTask.Result);

            // ── Collection-level constants and tables take precedence ──
            foreach (var col in collections)
            {
                if (col.ValueKind != JsonValueKind.Object) continue;

                // Parse definition_json to get constants/tables.
                if (col.TryGetProperty("definition_json", out var def))
                {
                    var defEl = def.ValueKind == JsonValueKind.String
                        ? SafeParse(def.GetString()!)
                        : def;

                    if (defEl.ValueKind == JsonValueKind.Object)
                    {
                        // Constants (groups)
                        if (defEl.TryGetProperty("constants", out var constantsProp) && constantsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var c in constantsProp.EnumerateArray())
                            {
                                if (c.ValueKind != JsonValueKind.Object) continue;
                                var cId = c.TryGetProperty("id", out var cIdProp) && cIdProp.ValueKind == JsonValueKind.String
                                    ? cIdProp.GetString() : null;
                                if (string.IsNullOrEmpty(cId)) continue;

                                if (!result.ContainsKey(cId!))
                                {
                                    var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
                                    if (c.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Object)
                                    {
                                        foreach (var prop in entriesEl.EnumerateObject())
                                            entries[prop.Name] = PropValue(prop.Value);
                                    }
                                    result[cId!] = entries;
                                }
                            }
                        }

                        // Tables
                        if (defEl.TryGetProperty("tables", out var tablesProp) && tablesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var t in tablesProp.EnumerateArray())
                            {
                                if (t.ValueKind != JsonValueKind.Object) continue;
                                var tId = t.TryGetProperty("id", out var tIdProp) && tIdProp.ValueKind == JsonValueKind.String
                                    ? tIdProp.GetString() : null;
                                if (string.IsNullOrEmpty(tId)) continue;

                                if (!result.ContainsKey(tId!))
                                {
                                    var rows = new List<object?>();
                                    if (t.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tableRow in rowsEl.EnumerateArray())
                                        {
                                            var rowDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                                            if (tableRow.ValueKind == JsonValueKind.Object)
                                            {
                                                foreach (var prop in tableRow.EnumerateObject())
                                                    rowDict[prop.Name] = PropValue(prop.Value);
                                            }
                                            rows.Add(rowDict);
                                        }
                                    }
                                    result[tId!] = rows;
                                }
                            }
                        }
                    }
                }
            }

            // ── Legacy game-values items — only fill in what collections don't define ──
            var gvRow = gvTask.Result;
            if (gvRow is { } row && row.TryGetProperty("payload_json", out var payload))
            {
                var gv = payload.ValueKind == JsonValueKind.String
                    ? SafeParse(payload.GetString()!)
                    : payload;

                if (gv.ValueKind == JsonValueKind.Object)
                {
                    // New format: { items: [...] }
                    if (gv.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in itemsEl.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            var itemId = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                                ? idProp.GetString() : null;
                            if (string.IsNullOrEmpty(itemId)) continue;
                            if (result.ContainsKey(itemId!)) continue; // collection-level takes precedence

                            var itemType = item.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                                ? typeProp.GetString() : null;

                            if (itemType == "table")
                            {
                                var rows = new List<object?>();
                                if (item.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var tableRow in rowsEl.EnumerateArray())
                                    {
                                        var rowDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                                        if (tableRow.ValueKind == JsonValueKind.Object)
                                        {
                                            foreach (var prop in tableRow.EnumerateObject())
                                                rowDict[prop.Name] = PropValue(prop.Value);
                                        }
                                        rows.Add(rowDict);
                                    }
                                }
                                result[itemId!] = rows;
                            }
                            else
                            {
                                // group: entries is a simple { key: value } map
                                var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
                                if (item.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var prop in entriesEl.EnumerateObject())
                                        entries[prop.Name] = PropValue(prop.Value);
                                }
                                result[itemId!] = entries;
                            }
                        }
                    }
                    // Legacy format: { groups: [...], tables: [...] }
                    else
                    {
                        if (gv.TryGetProperty("groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var g in groupsEl.EnumerateArray())
                            {
                                if (g.ValueKind != JsonValueKind.Object) continue;
                                var gId = g.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                                    ? idProp.GetString() : null;
                                if (string.IsNullOrEmpty(gId)) continue;
                                if (result.ContainsKey(gId!)) continue;

                                var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
                                if (g.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var prop in entriesEl.EnumerateObject())
                                        entries[prop.Name] = PropValue(prop.Value);
                                }
                                result[gId!] = entries;
                            }
                        }
                        if (gv.TryGetProperty("tables", out var tablesEl) && tablesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var t in tablesEl.EnumerateArray())
                            {
                                if (t.ValueKind != JsonValueKind.Object) continue;
                                var tId = t.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                                    ? idProp.GetString() : null;
                                if (string.IsNullOrEmpty(tId)) continue;
                                if (result.ContainsKey(tId!)) continue;

                                var rows = new List<object?>();
                                if (t.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var tableRow in rowsEl.EnumerateArray())
                                    {
                                        var rowDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                                        if (tableRow.ValueKind == JsonValueKind.Object)
                                        {
                                            foreach (var prop in tableRow.EnumerateObject())
                                                rowDict[prop.Name] = PropValue(prop.Value);
                                        }
                                        rows.Add(rowDict);
                                    }
                                }
                                result[tId!] = rows;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Values context is best-effort — a failure should not break query execution.
            _logger.LogWarning(ex, "Failed to build query values context for project {ProjectId}", projectId);
        }

        return result;
    }

    private static JsonElement SafeParse(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return default; }
    }

    private static object? PropValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetDouble(out var d) ? d : null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ValueKind == JsonValueKind.Object || el.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<object>(el.GetRawText())
            : null
    };
}
