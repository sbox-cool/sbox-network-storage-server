using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

public interface IStorageKeyCdnWriter
{
    Task WritePublicKeyFileAsync(string apiKey, long userId, string projectId, bool enabled, string keyType, CancellationToken cancellationToken);
    Task WriteSecretKeyFileAsync(string identifier, string keyHash, long userId, string projectId, Dictionary<string, string>? permissions, CancellationToken cancellationToken);
    Task UpdateSecretKeyFileAsync(string identifier, string projectId, Dictionary<string, object> updates, CancellationToken cancellationToken);
    Task DeletePublicKeyFileAsync(string apiKey, string projectId, CancellationToken cancellationToken);
    Task DeleteSecretKeyFileAsync(string identifier, string projectId, CancellationToken cancellationToken);
    Task<CdnKeyIndex?> ReadKeyIndexAsync(string projectId, CancellationToken cancellationToken);
    Task WriteKeyIndexAsync(string projectId, CdnKeyIndex index, CancellationToken cancellationToken);
}

public sealed class StorageKeyCdnWriter(IBunnyWorkspaceClient bunnyWorkspaceClient, ILogger<StorageKeyCdnWriter> logger)
    : IStorageKeyCdnWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task WritePublicKeyFileAsync(string apiKey, long userId, string projectId, bool enabled, string keyType, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/{apiKey}.json";
        var newPath = $"network-storage-api/keys/projects/{projectId}/{apiKey}.json";
        // The runtime data plane (cache.js lookupApiKey) requires userId + projectId
        // in the key file to accept the key; omitting them silently breaks validation.
        var data = new { userId, projectId, enabled, keyType };

        await PutJsonWithVerifyAsync(legacyPath, data, cancellationToken);
        _ = WriteNewPathAsync(newPath, data).ConfigureAwait(false);
    }

    public async Task WriteSecretKeyFileAsync(string identifier, string keyHash, long userId, string projectId, Dictionary<string, string>? permissions, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/sk_{identifier}.json";
        var newPath = $"network-storage-api/keys/projects/{projectId}/sk_{identifier}.json";
        // Mirror the JS writeSecretKeyFile shape exactly: the data plane
        // (cache.js lookupSecretKey) requires keyHash + userId + projectId.
        var data = new Dictionary<string, object>
        {
            ["keyHash"] = keyHash,
            ["userId"] = userId,
            ["projectId"] = projectId,
            ["enabled"] = true,
            ["keyType"] = "secret",
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("o")
        };
        if (permissions is not null) data["permissions"] = permissions;

        await PutJsonWithVerifyAsync(legacyPath, data, cancellationToken);
        _ = WriteNewPathAsync(newPath, data).ConfigureAwait(false);
    }

    public async Task UpdateSecretKeyFileAsync(string identifier, string projectId, Dictionary<string, object> updates, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/sk_{identifier}.json";

        var existing = await bunnyWorkspaceClient.GetRawAsync<Dictionary<string, object>>(legacyPath, cancellationToken);
        if (existing is null) return;

        foreach (var kv in updates)
        {
            existing[kv.Key] = kv.Value;
        }

        await bunnyWorkspaceClient.PutRawAsync(legacyPath, existing, cancellationToken);
        _ = WriteNewPathRawAsync($"network-storage-api/keys/projects/{projectId}/sk_{identifier}.json", existing).ConfigureAwait(false);
    }

    public async Task DeletePublicKeyFileAsync(string apiKey, string projectId, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/{apiKey}.json";
        var newPath = $"network-storage-api/keys/projects/{projectId}/{apiKey}.json";
        var legacyGlobal = $"network-storage/keys/{apiKey}.json";

        var deletes = new[]
        {
            DeleteAsync(legacyPath, cancellationToken),
            DeleteAsync(newPath, cancellationToken),
            DeleteAsync(legacyGlobal, cancellationToken)
        };
        await Task.WhenAll(deletes);
    }

    public async Task DeleteSecretKeyFileAsync(string identifier, string projectId, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/sk_{identifier}.json";
        var newPath = $"network-storage-api/keys/projects/{projectId}/sk_{identifier}.json";

        var deletes = new[]
        {
            DeleteAsync(legacyPath, cancellationToken),
            DeleteAsync(newPath, cancellationToken)
        };
        await Task.WhenAll(deletes);
    }

    public async Task<CdnKeyIndex?> ReadKeyIndexAsync(string projectId, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/_index.json";
        var newPath = $"network-storage-api/keys/projects/{projectId}/_index.json";

        var data = await bunnyWorkspaceClient.GetRawAsync<Dictionary<string, JsonElement>>(legacyPath, cancellationToken);
        if (data is not null && data.TryGetValue("keys", out var keysElement))
        {
            var keys = JsonSerializer.Deserialize<List<CdnKeyIndexEntry>>(keysElement.GetRawText(), JsonOptions);
            if (keys is not null)
            {
                return new CdnKeyIndex(keys);
            }
        }

        // Fallback to new path (dual-write target).
        data = await bunnyWorkspaceClient.GetRawAsync<Dictionary<string, JsonElement>>(newPath, cancellationToken);
        if (data is not null && data.TryGetValue("keys", out keysElement))
        {
            var keys = JsonSerializer.Deserialize<List<CdnKeyIndexEntry>>(keysElement.GetRawText(), JsonOptions);
            if (keys is not null)
            {
                return new CdnKeyIndex(keys);
            }
        }

        return null;
    }

    public async Task WriteKeyIndexAsync(string projectId, CdnKeyIndex index, CancellationToken cancellationToken)
    {
        var legacyPath = $"network-storage/keys/projects/{projectId}/_index.json";
        var newPath = $"network-storage-api/keys/projects/{projectId}/_index.json";

        var payload = new Dictionary<string, object>
        {
            ["keys"] = index.Keys.ToList()
        };

        await PutJsonAsync(legacyPath, payload, cancellationToken);
        _ = WriteNewPathAsync(newPath, payload).ConfigureAwait(false);
    }

    private async Task PutJsonWithVerifyAsync(string path, object data, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await bunnyWorkspaceClient.PutRawAsync(path, data, cancellationToken);

            var readBack = await bunnyWorkspaceClient.GetRawAsync<Dictionary<string, object>>(path, cancellationToken);
            if (readBack is not null)
            {
                return;
            }

            if (attempt < 2) await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException($"CDN key file write failed verification after 2 attempts: {path}");
    }

    private async Task PutJsonAsync(string path, object data, CancellationToken cancellationToken)
    {
        await bunnyWorkspaceClient.PutRawAsync(path, data, cancellationToken);
    }

    private async Task WriteNewPathAsync(string path, object data)
    {
        try
        {
            await bunnyWorkspaceClient.PutRawAsync(path, data, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write new-path CDN file {Path}", path);
        }
    }

    private async Task WriteNewPathRawAsync(string path, object data)
    {
        try
        {
            await bunnyWorkspaceClient.PutRawAsync(path, data, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write new-path CDN file {Path}", path);
        }
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await bunnyWorkspaceClient.DeleteRawAsync(path, cancellationToken);
        }
        catch
        {
            // Best-effort delete; ignore not-found and other failures.
        }
    }
}
