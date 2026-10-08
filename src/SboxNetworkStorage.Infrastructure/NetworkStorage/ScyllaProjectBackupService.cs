using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// ScyllaDB-backed project backup service. Stores backup settings, packages,
/// and runs inside the project payload JSON (in the ScyllaDB <c>projects</c>
/// table), keyed under <c>backupSettings</c>, <c>backupPackages</c>, and
/// <c>backupRuns</c> respectively. Replaces <see cref="PostgresProjectBackupService"/>
/// to complete the Postgres air-gap for Network Storage.
/// </summary>
public sealed class ScyllaProjectBackupService(
    INetworkStorageStore store,
    TimeProvider time,
    ILogger<ScyllaProjectBackupService> logger) : IProjectBackupService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan StaleRunTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// Cap on how many runs/packages are retained in the project payload. The
    /// payload is bounded by <c>ScyllaDbOptions.MaxPayloadBytes</c> (64 KB), so
    /// history is trimmed to the most recent entries — the authoritative export
    /// record lives in the <c>scylla_backups</c> control-plane table.
    /// </summary>
    private const int MaxStoredHistory = 25;

    public async Task<BackupSettings> GetBackupSettingsAsync(long userId, string projectId, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadAsync(projectId, cancellationToken);
        if (payload is not { } p) return DefaultSettings;

        return p.TryGetProperty("backupSettings", out var s) && s.ValueKind == JsonValueKind.Object
            ? Deserialize<BackupSettings>(s) ?? DefaultSettings
            : DefaultSettings;
    }

    public async Task SaveBackupSettingsAsync(long userId, string projectId, BackupSettings settings, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadMutableAsync(projectId, cancellationToken);
        payload["backupSettings"] = JsonSerializer.SerializeToNode(settings, Json);
        await SavePayloadAsync(projectId, payload, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupPackage>> ListBackupPackagesAsync(long userId, string projectId, int limit, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadAsync(projectId, cancellationToken);
        if (payload is not { } p) return [];

        if (!p.TryGetProperty("backupPackages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Take(Math.Clamp(limit, 1, 100))
            .Select(e => Deserialize<BackupPackage>(e))
            .Where(x => x is not null)
            .Cast<BackupPackage>()
            .ToList();
    }

    public async Task<IReadOnlyList<BackupRun>> ListBackupRunsAsync(long userId, string projectId, int limit, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadMutableAsync(projectId, cancellationToken);
        ExpireStaleRuns(payload);

        var runs = ReadRunsArray(payload);
        var result = runs
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();

        if (payload.ContainsKey("backupRuns"))
            await SavePayloadAsync(projectId, payload, cancellationToken);

        return result;
    }

    public async Task<string> StartBackupJobAsync(long userId, string projectId, string source, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadMutableAsync(projectId, cancellationToken);
        var jobId = Guid.NewGuid().ToString();
        var now = time.GetUtcNow().UtcDateTime;

        var run = new BackupRun(
            jobId, source, "running", now.ToString("o"), null, 0, null, null, 0, "queued", "Queued", "");

        var runs = ReadRunsList(payload);
        runs.Add(run);
        payload["backupRuns"] = JsonSerializer.SerializeToNode(runs, Json);

        await SavePayloadAsync(projectId, payload, cancellationToken);
        return jobId;
    }

    public async Task<BackupRun?> GetBackupJobStatusAsync(long userId, string projectId, string jobId, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadMutableAsync(projectId, cancellationToken);
        ExpireStaleRuns(payload);

        var result = ReadRunsArray(payload).FirstOrDefault(r => string.Equals(r.Id, jobId, StringComparison.Ordinal));

        if (payload.ContainsKey("backupRuns"))
            await SavePayloadAsync(projectId, payload, cancellationToken);

        return result;
    }

    public async Task RecordBackupResultAsync(long userId, string projectId, BackupRun run, BackupPackage? package, CancellationToken cancellationToken)
    {
        var payload = await LoadPayloadMutableAsync(projectId, cancellationToken);

        var runs = ReadRunsList(payload);
        runs.Add(run);
        runs = runs.OrderByDescending(r => r.StartedAt).Take(MaxStoredHistory).ToList();
        payload["backupRuns"] = JsonSerializer.SerializeToNode(runs, Json);

        if (package is not null)
        {
            var packages = ReadPackagesList(payload);
            packages.Add(package);
            packages = packages.OrderByDescending(p => p.CreatedAt).Take(MaxStoredHistory).ToList();
            payload["backupPackages"] = JsonSerializer.SerializeToNode(packages, Json);
        }

        await SavePayloadAsync(projectId, payload, cancellationToken);
    }

    // ── Helpers ──

    private static BackupSettings DefaultSettings => new(true, 1440, 7, true, null);

    private async Task<JsonElement?> LoadPayloadAsync(string projectId, CancellationToken ct)
    {
        try
        {
            return await store.ReadProjectAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ScyllaDB project read failed for backup lookup (project={ProjectId}); returning defaults", projectId);
            return null;
        }
    }

    private async Task<JsonObject> LoadPayloadMutableAsync(string projectId, CancellationToken ct)
    {
        var element = await store.ReadProjectAsync(projectId, ct);
        if (element is not { } el || el.ValueKind != JsonValueKind.Object)
            return new JsonObject();
        return JsonNode.Parse(el.GetRawText()) as JsonObject ?? new JsonObject();
    }

    private async Task SavePayloadAsync(string projectId, JsonObject payload, CancellationToken ct)
    {
        var element = JsonSerializer.SerializeToElement(payload, Json);
        await store.UpsertProjectAsync(projectId, element, 1, ct);
    }

    private List<BackupRun> ReadRunsArray(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("backupRuns", out var node) || node is not JsonArray arr)
            return [];
        return arr.Select(e => Deserialize<BackupRun>(e?.Deserialize<JsonElement>() ?? default)).Where(r => r is not null).Cast<BackupRun>().ToList();
    }

    private List<BackupRun> ReadRunsList(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("backupRuns", out var node) || node is not JsonArray arr)
            return [];
        return arr.Select(e => Deserialize<BackupRun>(e?.Deserialize<JsonElement>() ?? default)).Where(r => r is not null).Cast<BackupRun>().ToList();
    }

    private List<BackupPackage> ReadPackagesList(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("backupPackages", out var node) || node is not JsonArray arr)
            return [];
        return arr.Select(e => Deserialize<BackupPackage>(e?.Deserialize<JsonElement>() ?? default)).Where(p => p is not null).Cast<BackupPackage>().ToList();
    }

    private void ExpireStaleRuns(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("backupRuns", out var node) || node is not JsonArray arr)
            return;

        var staleBefore = time.GetUtcNow().UtcDateTime.Subtract(StaleRunTimeout);
        var changed = false;
        foreach (var item in arr.ToList())
        {
            if (item is not JsonObject run) continue;
            if (!run.TryGetPropertyValue("status", out var status) || status?.GetValue<string>() != "running")
                continue;
            if (!run.TryGetPropertyValue("startedAt", out var started) || started is null)
                continue;
            if (DateTime.TryParse(started.GetValue<string>(), out var startedAt) && startedAt < staleBefore)
            {
                run["status"] = "failed";
                run["completedAt"] = time.GetUtcNow().UtcDateTime.ToString("o");
                run["error"] = "Backup run timed out because no worker reported completion before the stale-run timeout.";
                run["progressPct"] = 100;
                run["currentStepId"] = "timed-out";
                run["currentStepLabel"] = "Timed out";
                run["currentStepDetail"] = "No backup worker reported completion before the stale-run timeout.";
                changed = true;
            }
        }

        if (changed) payload["backupRuns"] = arr;
    }

    private static T? Deserialize<T>(JsonElement element) where T : class
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try { return element.Deserialize<T>(Json); } catch { return null; }
    }
}
