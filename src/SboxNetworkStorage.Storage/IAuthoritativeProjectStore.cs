using System.Text.Json;

namespace SboxNetworkStorage.Storage;

public sealed record ProjectSnapshotRow(string Table, JsonElement Row);

/// <summary>Full table enumeration and atomic exact replacement, not upsert-only restoration.</summary>
public interface IAuthoritativeProjectStore
{
    IAsyncEnumerable<ProjectSnapshotRow> ExportProjectRowsAsync(string projectId, CancellationToken ct);
    Task ReplaceProjectRowsAsync(string projectId, IAsyncEnumerable<ProjectSnapshotRow> rows, CancellationToken ct);
}
