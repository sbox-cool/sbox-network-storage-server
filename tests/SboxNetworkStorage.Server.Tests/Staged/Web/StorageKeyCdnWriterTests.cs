using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Pins the CDN key-file byte shape the legacy server runtime data plane validates against
// (tools/sbox/cache.js lookupApiKey / lookupSecretKey). The data plane rejects a
// key unless its file carries userId + projectId, and treats `enabled` as a real
// boolean (it checks `enabled === false`). A .NET writer that omitted those fields
// or wrote `enabled` as the string "false" would silently break runtime auth.
public sealed class StorageKeyCdnWriterTests
{
    [Fact]
    public async Task WritePublicKeyFile_IncludesUserIdAndProjectIdSoDataPlaneAccepts()
    {
        var workspace = new InMemoryWorkspaceClient();
        var writer = new StorageKeyCdnWriter(workspace, NullLogger<StorageKeyCdnWriter>.Instance);

        await writer.WritePublicKeyFileAsync("sbox_ns_abc", 77L, "proj_1", true, "public", CancellationToken.None);

        var root = workspace.Read("network-storage/keys/projects/proj_1/sbox_ns_abc.json");
        Assert.Equal(77L, root.GetProperty("userId").GetInt64());
        Assert.Equal("proj_1", root.GetProperty("projectId").GetString());
        Assert.Equal(JsonValueKind.True, root.GetProperty("enabled").ValueKind);
        Assert.Equal("public", root.GetProperty("keyType").GetString());
    }

    [Fact]
    public async Task WriteSecretKeyFile_MatchesJsShapeWithUserIdProjectIdAndBooleanEnabled()
    {
        var workspace = new InMemoryWorkspaceClient();
        var writer = new StorageKeyCdnWriter(workspace, NullLogger<StorageKeyCdnWriter>.Instance);

        var perms = new Dictionary<string, string> { ["endpoints"] = "rwx" };
        await writer.WriteSecretKeyFileAsync("idnt", "deadbeefhash", 88L, "proj_2", perms, CancellationToken.None);

        var root = workspace.Read("network-storage/keys/projects/proj_2/sk_idnt.json");
        Assert.Equal("deadbeefhash", root.GetProperty("keyHash").GetString());
        Assert.Equal(88L, root.GetProperty("userId").GetInt64());
        Assert.Equal("proj_2", root.GetProperty("projectId").GetString());
        Assert.Equal(JsonValueKind.True, root.GetProperty("enabled").ValueKind);
        Assert.Equal("secret", root.GetProperty("keyType").GetString());
        Assert.Equal("rwx", root.GetProperty("permissions").GetProperty("endpoints").GetString());
    }

    [Fact]
    public async Task UpdateSecretKeyFile_DisablesWithBooleanFalseAndPreservesOwner()
    {
        var workspace = new InMemoryWorkspaceClient();
        var writer = new StorageKeyCdnWriter(workspace, NullLogger<StorageKeyCdnWriter>.Instance);

        await writer.WriteSecretKeyFileAsync("idt", "h", 5L, "proj_3", null, CancellationToken.None);

        await writer.UpdateSecretKeyFileAsync("idt", "proj_3",
            new Dictionary<string, object> { ["enabled"] = false }, CancellationToken.None);

        var root = workspace.Read("network-storage/keys/projects/proj_3/sk_idt.json");
        // Must be a JSON boolean false, never the string "false" — the data plane
        // checks `enabled === false`, so a string would leave the key authenticating.
        Assert.Equal(JsonValueKind.False, root.GetProperty("enabled").ValueKind);
        Assert.Equal(5L, root.GetProperty("userId").GetInt64());
        Assert.Equal("proj_3", root.GetProperty("projectId").GetString());
    }

    private sealed class InMemoryWorkspaceClient : IWorkspaceStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);

        public JsonElement Read(string path)
        {
            Assert.True(_files.TryGetValue(path, out var json), $"expected a CDN file at {path}");
            return JsonDocument.Parse(json!).RootElement.Clone();
        }

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
        {
            _files[absolutePath] = JsonSerializer.Serialize(data, Options);
            return Task.CompletedTask;
        }

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult(_files.TryGetValue(absolutePath, out var json)
                ? JsonSerializer.Deserialize<T>(json!, Options)
                : default);

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
        {
            _files.TryRemove(absolutePath, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
