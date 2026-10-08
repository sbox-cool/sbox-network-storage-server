using System.Text.Json;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.Workspace;

/// <summary>
/// Self-hosted replacement for the managed service's workspace object bucket.
/// Uses the same object layout (<c>network-storage/users/{userId}/...</c>) and
/// JSON conventions as the managed client, but persists objects in the
/// configured <see cref="INetworkStorageStore"/> so one database backup holds
/// all server state.
/// </summary>
public sealed class StoreWorkspaceObjectClient(INetworkStorageStore store) : IBunnyWorkspaceClient, IWorkspaceStorageEnumerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string UserRoot(long userId) => $"network-storage/users/{userId}";

    private static string ProjectPath(long userId, string projectId, string resourcePath)
        => $"{UserRoot(userId)}/{projectId}/{resourcePath.TrimStart('/')}";

    public async Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
    {
        var json = await store.ReadWorkspaceObjectAsync($"{UserRoot(userId)}/projects.json", cancellationToken);
        return json is null
            ? Array.Empty<BunnyProject>()
            : JsonSerializer.Deserialize<List<BunnyProject>>(json, JsonOptions) ?? [];
    }

    public async Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
    {
        var json = await store.ReadWorkspaceObjectAsync(ProjectPath(userId, projectId, $"usage/{monthKey}.json"), cancellationToken);
        if (json is null)
        {
            return null;
        }

        var usage = JsonSerializer.Deserialize<WorkspaceProjectUsage>(json, JsonOptions);
        if (usage is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        DateTimeOffset? flushedAt = document.RootElement.TryGetProperty("flushedAt", out var flushed)
            && flushed.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(flushed.GetString(), out var parsed)
                ? parsed
                : null;
        return usage with { Source = "retained-snapshot", LastUpdatedAt = flushedAt };
    }

    public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken cancellationToken)
        => store.PutWorkspaceObjectAsync($"{UserRoot(userId)}/projects.json", JsonSerializer.Serialize(projects, JsonOptions), cancellationToken);

    public async Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
    {
        var json = await store.ReadWorkspaceObjectAsync(ProjectPath(userId, projectId, resourcePath), cancellationToken);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        => store.ReadWorkspaceObjectAsync(ProjectPath(userId, projectId, resourcePath), cancellationToken);

    public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
        => store.PutWorkspaceObjectAsync(ProjectPath(userId, projectId, resourcePath), JsonSerializer.Serialize(data, JsonOptions), cancellationToken);

    public async Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
    {
        var json = await store.ReadWorkspaceObjectAsync(absolutePath, cancellationToken);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
        => store.PutWorkspaceObjectAsync(absolutePath, JsonSerializer.Serialize(data, JsonOptions), cancellationToken);

    public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
        => store.DeleteWorkspaceObjectAsync(absolutePath, cancellationToken);

    public async Task<IReadOnlyList<BunnyStorageEntry>> ListProjectResourceAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
    {
        var entries = await store.ListWorkspaceObjectsAsync(ProjectPath(userId, projectId, resourcePath.TrimEnd('/')), cancellationToken);
        return entries
            .Select(e => new BunnyStorageEntry(e.Name, e.IsDirectory, e.LastChanged, e.LengthBytes))
            .ToList();
    }
}
