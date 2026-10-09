using System.Text.Json;
using System.Linq;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Analytics;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Domain.Workspace;
namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native handler for <c>GET /v3/storage/:projectId/:collectionId/:steamId/ledger</c>.
/// Reads the authoritative <c>ledger_entries</c> projection written by
/// <c>PlayerAnalyticsIngester</c>, matching the production player-ledger reader.
/// An existing record without tracked-field changes has an empty ledger.
/// Flushes queued analytics first so the read reflects prior writes; never
/// writes ledger entries itself.
/// </summary>
public sealed class StorageLedgerReadHandler : INetworkStorageHandler
{
    private readonly IStorageApiKeyResolver _apiKeyResolver;
    private readonly INetworkStorageStore _store;
    private readonly AnalyticsWriterService _writer;

    public StorageLedgerReadHandler(
        IStorageApiKeyResolver apiKeyResolver,
        INetworkStorageStore store,
        AnalyticsWriterService writer)
    {
        _apiKeyResolver = apiKeyResolver;
        _store = store;
        _writer = writer;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.StorageLedger;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.StorageLedger
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.ProjectId ?? string.Empty;
        var collectionId = request.RouteParameter("collectionId");
        var steamId = request.RouteParameter("steamId");

        if (string.IsNullOrEmpty(collectionId) || string.IsNullOrEmpty(steamId))
        {
            return NetworkStorageResult.Error(
                400,
                "INVALID_KEY",
                new { error = new { code = "INVALID_KEY", message = "Key must be alphanumeric, hyphens, or underscores (max 128 chars)." } },
                authDecision: "denied");
        }

        // ── Auth: resolve API key ──
        var apiKey = request.Credentials.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            return UnauthorizedResult();
        }

        StorageApiKeyAuthResult? auth;
        try
        {
            auth = await _apiKeyResolver.ResolveApiKeyAsync(apiKey, projectId, request.CancellationToken);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return UnauthorizedResult();
        }

        if (auth is null)
        {
            return UnauthorizedResult();
        }

        var keyType = auth.KeyType;

        var storagePathsRead = new[] { $"ledger_entries/{projectId}/{collectionId}/{steamId}" };
        // Analytics (including player presence) drains in the background; flush first
        // so a just-written record, profile or entry is visible to this read.
        await _writer.FlushAsync(request.CancellationToken);
        IReadOnlyList<JsonElement> rows;
        bool recordExists;
        bool playerKnown;
        try
        {
            rows = await _store.ListLedgerEntriesAsync(projectId, collectionId, steamId, request.CancellationToken);
            var record = rows.Count == 0
                ? await _store.ReadRecordAsync(projectId, collectionId, steamId, request.CancellationToken)
                : null;
            recordExists = record is { } row && RecordRow.ExtractPayload(row) is not null;
            // legacy server never 404s a present collection's ledger on missing history: a
            // deleted record with past activity still reads 200-empty. A player
            // profile (written on endpoint/analytics activity) marks the player
            // known; a key with no record, no entries and no profile is missing.
            playerKnown = recordExists || (rows.Count == 0
                && await _store.ReadPlayerProfileAsync(projectId, steamId, request.CancellationToken) is not null);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                500,
                "ENDPOINT_CONFIG_ERROR",
                new { error = new { code = "ENDPOINT_CONFIG_ERROR", message = "Endpoint configuration error. Check step collection references and field names." } },
                storagePathsRead,
                authDecision: keyType);
        }

        // No record and no history remain a genuine missing resource.
        if (rows.Count == 0 && !playerKnown)
        {
            return NetworkStorageResult.Error(
                404,
                "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Resource not found." } },
                storagePathsRead,
                authDecision: keyType);
        }

        var entries = new List<JsonElement>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("entry_json", out var entry))
                continue;
            if (entry.ValueKind == JsonValueKind.String)
            {
                using var document = JsonDocument.Parse(entry.GetString()!);
                entry = document.RootElement.Clone();
            }
            if (entry.ValueKind == JsonValueKind.Object)
                entries.Add(entry);
        }
        var summary = ComputeSummary(entries);

        return NetworkStorageResult.Ok(
            new
            {
                steamId,
                field = "all",
                from = (string?)null,
                to = (string?)null,
                entries,
                summary,
                cursor = (string?)null,
            },
            storagePathsRead,
            authDecision: keyType);
    }

    private static NetworkStorageResult UnauthorizedResult()
    {
        return NetworkStorageResult.Error(
            401,
            "UNAUTHORIZED",
            new { error = new { code = "UNAUTHORIZED", message = "Invalid or missing API key." } },
            authDecision: "denied");
    }

    private static object ComputeSummary(IReadOnlyList<JsonElement> entries)
    {
        double totalIncrease = 0;
        double totalDecrease = 0;
        double netChange = 0;
        var sources = new Dictionary<string, SourceAccumulator>();

        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            if (entry.TryGetProperty("delta", out var deltaProp) && deltaProp.ValueKind == JsonValueKind.Number)
            {
                double delta = deltaProp.GetDouble();
                if (delta > 0) totalIncrease += delta;
                else totalDecrease += delta;
                netChange += delta;
            }

            var src = "unknown";
            if (entry.TryGetProperty("source", out var srcProp) && srcProp.ValueKind == JsonValueKind.String)
            {
                src = srcProp.GetString() ?? "unknown";
            }

            if (!sources.TryGetValue(src, out var acc))
            {
                acc = new SourceAccumulator();
                sources[src] = acc;
            }
            acc.Count++;
            if (entry.TryGetProperty("delta", out var delta2) && delta2.ValueKind == JsonValueKind.Number)
            {
                acc.Total += delta2.GetDouble();
            }
        }

        return new
        {
            totalIncrease,
            totalDecrease,
            netChange,
            entryCount = entries.Count,
            sources = sources.ToDictionary(kvp => kvp.Key, kvp => new { count = kvp.Value.Count, total = kvp.Value.Total }),
        };
    }

    private sealed class SourceAccumulator
    {
        public int Count;
        public double Total;
    }
}
