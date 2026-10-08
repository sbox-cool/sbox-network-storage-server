using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.Common;

public interface IAuditLogger
{
    Task LogActionAsync(AuditLogRequest request, CancellationToken cancellationToken);
}

public sealed record AuditLogRequest(
    string ProjectId,
    string UserId,
    string Action,
    object Actor,
    object Target,
    object Summary,
    object? Before,
    object? After,
    string? Diff = null
);
