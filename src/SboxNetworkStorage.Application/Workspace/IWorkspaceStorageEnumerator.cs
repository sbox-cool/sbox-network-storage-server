using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.Workspace;

/// <summary>A single object returned by a workspace edge-storage directory listing.</summary>
public sealed record WorkspaceStorageEntry(
    string ObjectName,
    bool IsDirectory,
    DateTimeOffset? LastChanged = null,
    long? LengthBytes = null);

/// <summary>
/// Directory enumeration over the fast-edge workspace storage. Kept separate from
/// <see cref="IWorkspaceStore"/> so adding listing does not force every existing
/// test double to implement it. Used by the native player-analytics log/transaction/ledger
/// scans, which mirror the legacy <c>listFiles</c> calls in the legacy server data plane.
/// </summary>
public interface IWorkspaceStorageEnumerator
{
    /// <summary>
    /// Lists the immediate children of <paramref name="resourcePath"/> under
    /// <c>network-storage/users/{userId}/{projectId}/</c>. Returns an empty list when the
    /// directory does not exist.
    /// </summary>
    Task<IReadOnlyList<WorkspaceStorageEntry>> ListProjectResourceAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken);
}
