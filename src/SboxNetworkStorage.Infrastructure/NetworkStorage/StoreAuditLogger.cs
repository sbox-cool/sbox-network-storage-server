using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Persists project audit-log entries to ScyllaDB (the <c>project_audit_logs</c>
/// table). Replaces the retired SpacetimeDB audit logger. Audit logging is
/// best-effort: a write failure is logged and swallowed so it never breaks the
/// request whose action it records.
/// </summary>
public sealed class StoreAuditLogger(
    INetworkStorageStore store,
    ILogger<StoreAuditLogger> logger) : IAuditLogger
{
    public async Task LogActionAsync(AuditLogRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var logId = Guid.NewGuid().ToString("N")[..8];
            await store.InsertAuditLogAsync(
                request.ProjectId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                logId,
                request.UserId,
                request.Action,
                JsonSerializer.Serialize(request.Actor),
                JsonSerializer.Serialize(request.Target),
                JsonSerializer.Serialize(request.Summary),
                request.Diff ?? string.Empty,
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to write audit log to Store for project {ProjectId}", request.ProjectId);
        }
    }
}
