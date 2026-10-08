using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

public sealed class NetworkStorageManagementService(
    IBunnyWorkspaceClient bunnyWorkspaceClient,
    IAuditLogger auditLogger)
    : IQueryManagementService, IWorkflowManagementService, IPageManagementService
{
    /// <summary>
    /// Clears the in-process query result cache for the target query and audit-logs
    /// the action. The in-process scope means other worker processes may still
    /// serve a cached result until their own TTL expires (≤ cache.ttlSeconds,
    /// default 300 s). Cross-process invalidation is not implemented.
    /// </summary>
    public async Task ClearQueryCacheAsync(long userId, string projectId, string queryId, CancellationToken cancellationToken)
    {
        SboxNetworkStorage.Infrastructure.NetworkStorage.Storage.NativeQueryExecutor.ClearQueryCache(queryId, projectId);
        await auditLogger.LogActionAsync(new AuditLogRequest(
            projectId, userId.ToString(), "query-rerun", "user", "query",
            new { queryId }, null, null), cancellationToken);
    }

    /// <summary>
    /// Republishes a query by clearing its cached result (so the next read
    /// executes freshly) and audit-logs the action. Same in-process scope
    /// caveat as <see cref="ClearQueryCacheAsync"/>.
    /// </summary>
    public async Task RepublishQueryAsync(long userId, string projectId, string queryId, CancellationToken cancellationToken)
    {
        SboxNetworkStorage.Infrastructure.NetworkStorage.Storage.NativeQueryExecutor.ClearQueryCache(queryId, projectId);
        await auditLogger.LogActionAsync(new AuditLogRequest(
            projectId, userId.ToString(), "query-republish", "user", "query",
            new { queryId }, null, null), cancellationToken);
    }

    public async Task<string> CreateWorkflowAsync(long userId, string projectId, IReadOnlyDictionary<string, string> workflowData, CancellationToken cancellationToken)
    {
        var workflows = await bunnyWorkspaceClient.GetProjectResourceAsync<List<Dictionary<string, object>>>(
            userId, projectId, "workflows.json", cancellationToken) ?? [];

        var newWorkflow = BuildWorkflowFromPayload(workflowData);
        if (!newWorkflow.TryGetValue("id", out var idObj) || string.IsNullOrWhiteSpace(idObj?.ToString()))
        {
            newWorkflow["id"] = $"wf_{Guid.NewGuid():N}"[..16];
        }

        newWorkflow["createdAt"] = DateTimeOffset.UtcNow.ToString("o");
        newWorkflow["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");
        workflows.Add(newWorkflow);
        await SaveWorkflowsAsync(userId, projectId, workflows, cancellationToken);
        return newWorkflow["id"].ToString() ?? string.Empty;
    }

    public async Task UpdateWorkflowAsync(long userId, string projectId, string workflowId, IReadOnlyDictionary<string, string> workflowData, CancellationToken cancellationToken)
    {
        var workflows = await bunnyWorkspaceClient.GetProjectResourceAsync<List<Dictionary<string, object>>>(
            userId, projectId, "workflows.json", cancellationToken) ?? [];
        
        var index = workflows.FindIndex(w => w.TryGetValue("id", out var id) && id?.ToString() == workflowId);
        if (index >= 0)
        {
            var workflow = workflows[index];
            foreach (var kv in workflowData)
            {
                if (kv.Key == "id" || kv.Key == "workflowId") continue;

                // Try parsing complex fields as JSON if they look like JSON
                var value = kv.Value;
                if (!string.IsNullOrWhiteSpace(value) && (value.Trim().StartsWith("{") || value.Trim().StartsWith("[")))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<object>(value);
                        if (parsed is not null)
                        {
                            workflow[kv.Key] = parsed;
                            continue;
                        }
                    }
                    catch { /* Fallback to string */ }
                }

                workflow[kv.Key] = value;
            }
            workflow["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");
            await SaveWorkflowsAsync(userId, projectId, workflows, cancellationToken);
        }
    }

    public async Task DeleteWorkflowAsync(long userId, string projectId, string workflowId, CancellationToken cancellationToken)
    {
        var workflows = await bunnyWorkspaceClient.GetProjectResourceAsync<List<Dictionary<string, object>>>(
            userId, projectId, "workflows.json", cancellationToken) ?? [];
        
        var removed = workflows.RemoveAll(w => w.TryGetValue("id", out var id) && id?.ToString() == workflowId);
        if (removed > 0)
        {
            await SaveWorkflowsAsync(userId, projectId, workflows, cancellationToken);
        }
    }


    private static Dictionary<string, object> BuildWorkflowFromPayload(IReadOnlyDictionary<string, string> workflowData)
    {
        var workflow = new Dictionary<string, object>();
        foreach (var kv in workflowData)
        {
            workflow[kv.Key] = ParseWorkflowValue(kv.Value);
        }
        return workflow;
    }

    private static object ParseWorkflowValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<object>(trimmed);
                if (parsed is not null) return parsed;
            }
            catch { /* keep string */ }
        }
        return value;
    }

    private async Task SaveWorkflowsAsync(long userId, string projectId, List<Dictionary<string, object>> workflows, CancellationToken cancellationToken)
    {
        // Dual-write to legacy and v3 paths to ensure immediate visibility in Bun
        await bunnyWorkspaceClient.PutProjectResourceAsync(userId, projectId, "workflows.json", workflows, cancellationToken);
        await bunnyWorkspaceClient.PutProjectResourceAsync(userId, projectId, "_config/workflows.v3.json", workflows, cancellationToken);

        // Verification read
        try
        {
            var verified = await bunnyWorkspaceClient.GetProjectResourceAsync<List<Dictionary<string, object>>>(
                userId, projectId, "workflows.json", cancellationToken);
            if (verified?.Count != workflows.Count)
            {
                Console.Error.WriteLine($"[SaveWorkflowsAsync] Verification failed: count mismatch (expected {workflows.Count}, got {verified?.Count ?? 0})");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SaveWorkflowsAsync] Verification read failed: {ex.Message}");
        }
    }

    public async Task RepublishPageAsync(long userId, string projectId, string pageSlug, CancellationToken cancellationToken)
    {
        // TODO: Implement page publication
        await auditLogger.LogActionAsync(new AuditLogRequest(
            projectId, userId.ToString(), "page-republish", "user", "page", 
            new { pageSlug }, null, null), cancellationToken);
    }
}
