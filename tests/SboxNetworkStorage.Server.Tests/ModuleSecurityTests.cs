using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Security controls: public gates, player blocks, hook emission, CSV safety, webhook SSRF, fail-closed state.</summary>
public sealed class ModuleSecurityTests
{
    private static Dictionary<string, object?> Def(params (string Key, object? Value)[] fields)
        => new(fields.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);

    [Fact]
    public void Disabled_endpoints_and_modules_are_denied_while_healthy_ones_pass()
    {
        Assert.Equal("ENDPOINT_DISABLED",
            ErrorOf(EndpointExecutor.EvaluatePublicGates(Def(("enabled", false)), "slug", false)));
        Assert.Equal("ENDPOINT_DISABLED",
            ErrorOf(EndpointExecutor.EvaluatePublicGates(Def(("module", "economy")), "slug", false, _ => true)));
        Assert.Null(EndpointExecutor.EvaluatePublicGates(Def(("module", "economy")), "slug", false, _ => false));
        Assert.Null(EndpointExecutor.EvaluatePublicGates(Def(), "slug", false));
    }

    [Fact]
    public void Secret_key_internal_and_deprecated_gates_hold()
    {
        Assert.Equal("SECRET_KEY_REQUIRED",
            ErrorOf(EndpointExecutor.EvaluatePublicGates(Def(("requiresSecretKey", true)), "slug", hasSecretKey: false)));
        Assert.Null(EndpointExecutor.EvaluatePublicGates(Def(("requiresSecretKey", true)), "slug", hasSecretKey: true));
        Assert.Equal("ENDPOINT_DEPRECATED",
            ErrorOf(EndpointExecutor.EvaluatePublicGates(Def(("deprecated", true)), "slug", false)));
        Assert.Equal("ENDPOINT_NOT_FOUND",
            ErrorOf(EndpointExecutor.EvaluatePublicGates(Def(("exposure", "internal")), "slug", false)));
    }

    private static string? ErrorOf(EndpointExecutionResult? denial)
    {
        if (denial is null) return null;
        var body = JsonSerializer.SerializeToElement(denial.Body);
        return body.GetProperty("error").GetProperty("code").GetString();
    }

    [Fact]
    public async Task Blocks_honor_scope_expiry_and_secret_exemption()
    {
        var blocks = new PlayerBlockService(new InMemoryModuleDataStore(),
            new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await blocks.BlockAsync("game", "p1", PlayerBlockService.WritesScope, "cheating", null, "owner", default);
        var write = await blocks.CheckAsync("game", "p1", isWrite: true, hasSecretKey: false, default);
        Assert.NotNull(write);
        Assert.Equal(PlayerBlockService.WritesScope, write.Scope);
        Assert.Equal("cheating", write.Reason);
        Assert.Null(await blocks.CheckAsync("game", "p1", isWrite: false, hasSecretKey: false, default));
        Assert.Null(await blocks.CheckAsync("game", "p1", isWrite: true, hasSecretKey: true, default));
        Assert.Null(await blocks.CheckAsync("game", "stranger", isWrite: true, hasSecretKey: false, default));

        await blocks.BlockAsync("game", "p2", PlayerBlockService.AllScope, null, null, "owner", default);
        Assert.NotNull(await blocks.CheckAsync("game", "p2", isWrite: false, hasSecretKey: false, default));

        await blocks.BlockAsync("game", "p3", PlayerBlockService.WritesScope, null, now - 1000, "owner", default);
        Assert.Null(await blocks.CheckAsync("game", "p3", isWrite: true, hasSecretKey: false, default));

        await blocks.UnblockAsync("game", "p1", default);
        Assert.Null(await blocks.CheckAsync("game", "p1", isWrite: true, hasSecretKey: false, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            blocks.BlockAsync("game", "p1", "everything", null, null, "owner", default));
    }

    [Fact]
    public async Task Emitted_endpoint_event_reaches_the_action_log_only_for_installed_insights()
    {
        var modules = new InMemoryModuleDataStore();
        var writer = new PlayerActionLogWriter(modules, ProviderWith(enabled: true), TimeProvider.System,
            NullLogger<PlayerActionLogWriter>.Instance);
        var subscriber = new InsightsActionLogSubscriber(writer, ProviderWith(enabled: true), TimeProvider.System);
        await subscriber.OnEventAsync(ModuleHookDispatcher.Describe("endpoint.executed", "game",
            DateTimeOffset.UtcNow, steamId: "p1", endpointSlug: "economy_earn", revisionId: 42,
            data: new Dictionary<string, object?>
            {
                ["outcome"] = "ok",
                ["changedFields"] = new List<string> { "gold" },
            }), default);
        Assert.Equal(1, writer.QueuedEntries);

        var offWriter = new PlayerActionLogWriter(modules, ProviderWith(enabled: false), TimeProvider.System,
            NullLogger<PlayerActionLogWriter>.Instance);
        var offSubscriber = new InsightsActionLogSubscriber(offWriter, ProviderWith(enabled: false), TimeProvider.System);
        await offSubscriber.OnEventAsync(ModuleHookDispatcher.Describe("endpoint.executed", "game",
            DateTimeOffset.UtcNow, steamId: "p1", endpointSlug: "economy_earn",
            data: new Dictionary<string, object?> { ["outcome"] = "ok" }), default);
        Assert.Equal(0, offWriter.QueuedEntries);
    }

    [Fact]
    public void Csv_prefixes_formula_cells_with_a_single_quote()
    {
        var csv = InsightsService.ToCsv([
            new ActionLogRow(1000, "p1", "=cmd|'/c calc'!A0", 1, "+evil", "@x"),
            new ActionLogRow(2000, "p2", "economy_earn", null, "ok", "gold"),
        ]);
        Assert.Contains("'=cmd", csv);
        Assert.Contains("'+evil", csv);
        Assert.Contains("'@x", csv);
        Assert.DoesNotContain("\"=cmd", csv);
        Assert.Contains(",economy_earn,", csv);
    }

    [Theory]
    [InlineData("http://127.0.0.1/hook", true)]
    [InlineData("http://127.1.2.3:8080/hook", true)]
    [InlineData("http://[::1]/hook", true)]
    [InlineData("http://localhost/hook", true)]
    [InlineData("http://LOCALHOST/hook", true)]
    [InlineData("http://169.254.169.254/latest", true)]
    [InlineData("http://169.254.10.20/hook", true)]
    [InlineData("http://[fe80::1]/hook", true)]
    [InlineData("http://0.0.0.0/hook", true)]
    [InlineData("http://[::ffff:169.254.169.254]/latest", true)]
    [InlineData("http://[::ffff:a9fe:a9fe]/latest", true)]
    [InlineData("http://[::ffff:0:0]/hook", true)]
    [InlineData("http://[::ffff:192.168.1.10]/hook", false)]
    [InlineData("https://discord.com/api/webhooks/abc", false)]
    [InlineData("https://hooks.example.com/hook", false)]
    [InlineData("http://192.168.1.10/hook", false)]
    [InlineData("http://10.0.0.5:9000/hook", false)]
    [InlineData("http://172.16.4.4/hook", false)]
    public void Webhook_url_policy_rejects_server_side_targets_but_keeps_private_nets(string url, bool rejected)
    {
        var reason = AlertWebhookUrlPolicy.UnsafeReason(new Uri(url));
        Assert.Equal(rejected, reason is not null);
    }

    [Fact]
    public async Task Module_state_failures_refuse_lifecycle_writes()
    {
        var throwing = new ThrowingWorkspaceStore();
        var installer = new ModuleInstaller(new InMemoryNetworkStorageStore(), throwing,
            new ModuleStateStore(throwing), new FakeAuditLogger(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(1, "game", "economy", null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ModuleStateStore(throwing).LoadAsync(1, "game", default));
    }

    private static InsightsSettingsProvider ProviderWith(bool enabled)
    {
        var workspace = new FakeWorkspaceStore();
        var states = new ModuleStateStore(workspace);
        if (enabled)
        {
            states.SaveAsync(1, "game", new ModuleProjectState(
                [new InstalledModule("player-insights", "1.0.0", true,
                    new Dictionary<string, string>(), ["read-analytics"],
                    [], [], [], [], [], "now")], []), default).GetAwaiter().GetResult();
        }
        var provider = new FakeProvider(states);
        return new InsightsSettingsProvider(new FakeScopes(provider), new ModuleOwnerOptions(1),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<InsightsSettingsProvider>.Instance);
    }

    private sealed class FakeScopes(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FakeScope(provider);
        private sealed class FakeScope(IServiceProvider inner) : IServiceScope
        {
            public IServiceProvider ServiceProvider => inner;
            public void Dispose() { }
        }
    }

    private sealed class FakeProvider(ModuleStateStore states) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ModuleStateStore) ? states : null;
    }

    private sealed class ThrowingWorkspaceStore : Application.Workspace.IWorkspaceStore
    {
        public Task<IReadOnlyList<Domain.Workspace.WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("workspace unavailable");
        public Task<Domain.Workspace.WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("workspace unavailable");
        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<Domain.Workspace.WorkspaceProject> projects, CancellationToken cancellationToken)
            => throw new InvalidOperationException("workspace unavailable");
        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("workspace unavailable");
        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("workspace unavailable");
        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
            => throw new InvalidOperationException("workspace unavailable");
        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken) => throw new InvalidOperationException("workspace unavailable");
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken) => throw new InvalidOperationException("workspace unavailable");
        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken) => throw new InvalidOperationException("workspace unavailable");
    }
}
