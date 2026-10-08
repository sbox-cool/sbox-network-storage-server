using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class PackageSyncHandlerTests
{
    private const long OwnerId = 7;
    private const string ProjectId = "ec13d753a7ea4d66";
    private const string SecretKey = "sbox_sk_test_secret_key";

    [Fact]
    public async Task HandleAsync_persists_game_package_and_promotes_revision_overrides()
    {
        var workspace = new StatefulWorkspaceClient();
        workspace.Seed("game-package.json", """
            {"packageIdent":"hooked_inc","currentRevisionId":1,"latestRevisionId":1,"publishStatus":"draft","revisionPublishedAt":"2026-06-01T00:00:00Z"}
            """);
        workspace.Seed("revision-overrides.json", """
            {"endpoints":{"save-all":{"slug":"save-all","name":"Save All Updated","steps":[{"type":"write"}]}},"collections":{"players":{"name":"players","schema":{"type":"object"}}}}
            """);
        workspace.Seed("endpoints.json", """
            [{"id":"ep1","slug":"save-all","name":"Save All","method":"POST","enabled":true}]
            """);
        workspace.Seed("collections.json", """
            []
            """);

        var handler = CreateHandler(workspace, authUserId: OwnerId);
        var context = BuildContext("""
            {"packageIdent":"hooked_inc","currentRevisionId":2,"latestRevisionId":2,"publishStatus":"draft"}
            """);

        await handler.HandleAsync(context, ProjectId, CancellationToken.None);

        Assert.Equal(200, context.Response.StatusCode);

        var savedPackage = workspace.Read("game-package.json");
        Assert.NotNull(savedPackage);
        using var package = JsonDocument.Parse(savedPackage);
        Assert.Equal(2, package.RootElement.GetProperty("currentRevisionId").GetInt32());
        Assert.Equal("published", package.RootElement.GetProperty("publishStatus").GetString());
        Assert.True(package.RootElement.TryGetProperty("lastSyncedAt", out _));

        var savedEndpoints = workspace.Read("endpoints.json");
        Assert.NotNull(savedEndpoints);
        using var endpoints = JsonDocument.Parse(savedEndpoints);
        Assert.Single(endpoints.RootElement.EnumerateArray());
        var endpoint = endpoints.RootElement[0];
        Assert.Equal("Save All Updated", endpoint.GetProperty("name").GetString());

        var clearedOverrides = workspace.Read("revision-overrides.json");
        Assert.NotNull(clearedOverrides);
        Assert.Contains("\"endpoints\":{}", clearedOverrides);
        Assert.Contains("\"collections\":{}", clearedOverrides);
    }

    [Fact]
    public async Task HandleAsync_no_revision_change_does_not_promote_overrides()
    {
        var workspace = new StatefulWorkspaceClient();
        workspace.Seed("game-package.json", """
            {"packageIdent":"hooked_inc","currentRevisionId":3,"latestRevisionId":3,"publishStatus":"published"}
            """);
        workspace.Seed("revision-overrides.json", """
            {"endpoints":{"save-all":{"slug":"save-all","name":"Changed"}}}
            """);
        workspace.Seed("endpoints.json", """
            [{"id":"ep1","slug":"save-all","name":"Save All","method":"POST","enabled":true}]
            """);

        var handler = CreateHandler(workspace);
        var context = BuildContext("""
            {"packageIdent":"hooked_inc","currentRevisionId":3,"latestRevisionId":3}
            """);

        await handler.HandleAsync(context, ProjectId, CancellationToken.None);

        Assert.Equal(200, context.Response.StatusCode);

        var savedEndpoints = workspace.Read("endpoints.json");
        Assert.NotNull(savedEndpoints);
        using var endpoints = JsonDocument.Parse(savedEndpoints);
        Assert.Equal("Save All", endpoints.RootElement[0].GetProperty("name").GetString());

        var savedOverrides = workspace.Read("revision-overrides.json");
        Assert.Contains("Changed", savedOverrides);
    }

    [Fact]
    public async Task HandleAsync_missing_key_returns_unauthorized()
    {
        var workspace = new StatefulWorkspaceClient();
        var handler = CreateHandler(workspace);
        var context = BuildContext("{}", withKey: false);

        await handler.HandleAsync(context, ProjectId, CancellationToken.None);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_public_key_returns_unauthorized()
    {
        var workspace = new StatefulWorkspaceClient();
        var handler = CreateHandler(workspace, keyType: "public");
        var context = BuildContext("{}");

        await handler.HandleAsync(context, ProjectId, CancellationToken.None);

        Assert.Equal(401, context.Response.StatusCode);
    }

    private static PackageSyncHandler CreateHandler(
        IBunnyWorkspaceClient workspace,
        long authUserId = OwnerId,
        string keyType = "secret",
        bool enabled = true)
    {
        var resolver = new FixedApiKeyResolver(new StorageApiKeyAuthResult(authUserId, ProjectId, enabled, keyType, null));
        return new PackageSyncHandler(workspace, resolver, NullLogger<PackageSyncHandler>.Instance);
    }

    private static DefaultHttpContext BuildContext(string body, bool withKey = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (withKey)
        {
            context.Request.Headers["x-api-key"] = SecretKey;
        }
        return context;
    }

    private sealed class FixedApiKeyResolver(StorageApiKeyAuthResult result) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string projectId, CancellationToken cancellationToken)
            => Task.FromResult<StorageApiKeyAuthResult?>(result);
    }

    private sealed class StatefulWorkspaceClient : IBunnyWorkspaceClient
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly Dictionary<string, string> _store = new();

        public void Seed(string resourcePath, string json) => _store[resourcePath] = json;
        public string? Read(string resourcePath) => _store.TryGetValue(resourcePath, out var v) ? v : null;

        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(_store.TryGetValue(resourcePath, out var json)
                ? JsonSerializer.Deserialize<T>(json, Options)
                : default);

        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
        {
            _store[resourcePath] = JsonSerializer.Serialize(data, Options);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BunnyProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BunnyProject>>(new List<BunnyProject>());

        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);

        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<BunnyProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(Read(resourcePath));

        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken)
            => Task.FromResult<T?>(default);

        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
