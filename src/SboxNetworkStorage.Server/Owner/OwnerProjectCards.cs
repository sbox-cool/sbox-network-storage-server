using System.Text.Json;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Project-hub card facts shared by the dashboard and the export/import error paths.</summary>
internal static class OwnerProjectCards
{
    internal static async Task<List<OwnerProjectCard>> LoadAsync(IWorkspaceStore workspace, INetworkStorageStore store,
        long owner, CancellationToken ct)
    {
        var cards = new List<OwnerProjectCard>();
        foreach (var project in await workspace.GetUserProjectsAsync(owner, ct))
            cards.Add(await LoadOneAsync(workspace, store, owner, project, ct));
        cards.Sort((left, right) => string.Compare(left.Project.Name, right.Project.Name, StringComparison.OrdinalIgnoreCase));
        return cards;
    }

    private static async Task<OwnerProjectCard> LoadOneAsync(IWorkspaceStore workspace, INetworkStorageStore store,
        long owner, Domain.Workspace.WorkspaceProject project, CancellationToken ct)
    {
        long? revision = null;
        try
        {
            var package = await workspace.GetProjectResourceAsync<Dictionary<string, JsonElement>>(owner, project.Id, "game-package.json", ct);
            if (package is not null && package.TryGetValue("currentRevisionId", out var current)
                && current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var id))
                revision = id;
        }
        catch (Exception)
        {
            // A missing or unreadable game package means the project never synced.
        }
        DateTimeOffset? lastRequest = null;
        try
        {
            var row = (await store.ListStorageRequestLogAsync(project.Id, 1, ct)).FirstOrDefault();
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("created_at_unix_ms", out var at)
                && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ms))
                lastRequest = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (Exception)
        {
            // Request history is best effort on the hub; the project page shows the full log.
        }
        return new OwnerProjectCard(project, revision, lastRequest);
    }
}
