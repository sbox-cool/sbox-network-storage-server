using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class UsageTelemetryTests : IDisposable
{
    private static readonly string[] ContractFields =
        ["schema", "installId", "version", "os", "arch", "container", "database", "tunnel", "uptimeHours", "projects", "players", "activePlayers30d"];

    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-telemetry-").FullName;
    private string ConfigDir => Path.Combine(_root, "config");
    private string DataDir => Path.Combine(_root, "data");

    public UsageTelemetryTests()
        => ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["updates.check"] = false });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // pooled SQLite handles on Windows
    }

    private EffectiveConfig Load() => ConfigLoader.Load(ConfigDir, DataDir, null, _ => null);

    private CliContext Context(params string[] args)
        => new(CliArguments.Parse([.. args, "--config-dir", ConfigDir, "--data-dir", DataDir]));

    [Fact]
    public async Task Telemetry_is_disabled_by_default_and_the_service_sends_nothing_or_creates_an_id()
    {
        var config = Load();
        Assert.False(config.GetBoolean("telemetry.enabled"));
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        await using var services = CliServices.Build(config);
        using var service = new UsageTelemetryService(config, services.GetRequiredService<INetworkStorageStoreAdmin>(),
            NullLogger<UsageTelemetryService>.Instance, handler, TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(handler.Bodies);
        Assert.False(File.Exists(config.TelemetryIdPath));
    }

    [Fact]
    public async Task Telemetry_payload_has_exactly_the_contract_fields_and_no_project_ids_keys_or_player_ids()
    {
        Assert.Equal(CliApp.Ok, await ProjectCommands.RunProjectAsync(Context("project", "create", "Secret Game Name")));
        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(Context("telemetry", "enable")));
        var config = Load();
        string projectId;
        string[] keys;
        const string steamId = "76561198000000042";
        await using (var services = CliServices.Build(config))
        {
            var store = services.GetRequiredService<INetworkStorageStore>();
            projectId = Assert.Single(await store.ListProjectsForUserAsync("1", CancellationToken.None)).GetProperty("project_id").GetString()!;
            Assert.Equal(CliApp.Ok, await ProjectCommands.RunKeyAsync(Context("key", "create", projectId, "--type", "secret")));
            keys = [.. (await store.ListApiKeysAsync(projectId, CancellationToken.None))
                .SelectMany(k => k.EnumerateObject())
                .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length >= 16)
                .Select(p => p.Value.GetString()!)];
            Assert.NotEmpty(keys);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await store.UpsertPlayerProfileAsync(projectId, steamId, "Player", true, now, now, now, null, null, 0, 1, null, null, "{}", now, CancellationToken.None);
        }

        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        await using (var services = CliServices.Build(config))
        {
            using var service = new UsageTelemetryService(config, services.GetRequiredService<INetworkStorageStoreAdmin>(),
                NullLogger<UsageTelemetryService>.Instance, handler, TimeSpan.Zero);
            await service.StartAsync(CancellationToken.None);
            await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await service.StopAsync(CancellationToken.None);
        }

        var body = Assert.Single(handler.Bodies);
        Assert.Equal("application/json", handler.ContentType);
        Assert.True(body.Length <= 4096);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(ContractFields, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(PrivateIdFile.Read(config.TelemetryIdPath), root.GetProperty("installId").GetGuid());
        Assert.Contains(root.GetProperty("os").GetString(), new[] { "linux", "windows", "macos", "other" });
        Assert.Contains(root.GetProperty("arch").GetString(), new[] { "x64", "arm64", "other" });
        Assert.Equal("sqlite", root.GetProperty("database").GetString());
        Assert.False(root.GetProperty("tunnel").GetBoolean());
        Assert.True(root.GetProperty("uptimeHours").GetInt64() >= 0);
        Assert.Equal(1, root.GetProperty("projects").GetInt64());
        Assert.Equal(1, root.GetProperty("players").GetInt64());
        Assert.Equal(1, root.GetProperty("activePlayers30d").GetInt64());
        foreach (var forbidden in keys.Append(projectId).Append(steamId).Append("Secret Game Name"))
            Assert.DoesNotContain(forbidden, body);
    }

    [Fact]
    public async Task Telemetry_id_is_private_separate_from_install_id_and_survives_disable()
    {
        var config = Load();
        var installIdPath = Path.Combine(config.DataDirectory, "install-id");
        var installId = PrivateIdFile.LoadOrCreate(installIdPath);

        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(Context("telemetry", "enable")));
        Assert.True(Load().GetBoolean("telemetry.enabled"));
        var telemetryId = PrivateIdFile.Read(config.TelemetryIdPath);
        Assert.NotNull(telemetryId);
        Assert.Equal(4, telemetryId!.Value.ToString("D")[14] - '0'); // UUIDv4
        Assert.NotEqual(installId, telemetryId);
        Assert.Equal(installId, PrivateIdFile.Read(installIdPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config.TelemetryIdPath));

        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(Context("telemetry", "disable")));
        Assert.False(Load().GetBoolean("telemetry.enabled"));
        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(Context("telemetry", "enable")));
        Assert.Equal(telemetryId, PrivateIdFile.Read(config.TelemetryIdPath));
    }

    [Fact]
    public async Task Telemetry_enable_without_install_id_never_creates_one()
    {
        Assert.Equal(CliApp.Ok, await TelemetryCommands.RunAsync(Context("telemetry", "enable")));
        Assert.True(File.Exists(Load().TelemetryIdPath));
        Assert.False(File.Exists(Path.Combine(DataDir, "install-id")));
    }

    [Fact]
    public async Task Telemetry_enable_refuses_a_plain_http_remote_endpoint()
    {
        ConfigFiles.SetValue(ConfigDir, SettingDefinitions.Find("telemetry.endpoint")!, "http://stats.example.com/telemetry");
        var error = await Assert.ThrowsAsync<CliException>(() => TelemetryCommands.RunAsync(Context("telemetry", "enable")));
        Assert.Contains("HTTPS", error.Message);
        Assert.False(Load().GetBoolean("telemetry.enabled"));
        Assert.False(File.Exists(Load().TelemetryIdPath));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Telemetry_send_returns_false_without_throwing_on_non_2xx(HttpStatusCode status)
    {
        using var http = UsageTelemetry.CreateHttpClient(new RecordingHandler(status));
        Assert.False(await UsageTelemetry.SendAsync(http, new Uri("https://stats.example.test/telemetry"), Payload(),
            NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Telemetry_send_returns_false_without_throwing_on_network_failure()
    {
        using var http = UsageTelemetry.CreateHttpClient(new RecordingHandler(HttpStatusCode.OK, fail: true));
        Assert.False(await UsageTelemetry.SendAsync(http, new Uri("https://stats.example.test/telemetry"), Payload(),
            NullLogger.Instance, CancellationToken.None));
    }

    private static UsageTelemetryPayload Payload()
        => new(1, Guid.NewGuid(), "0.1.0", "linux", "x64", false, "sqlite", false, 0, 0, null, null);

    private sealed class RecordingHandler(HttpStatusCode status, bool fail = false) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? ContentType { get; private set; }
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail) throw new HttpRequestException("connection refused");
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            ContentType = request.Content.Headers.ContentType?.MediaType;
            FirstRequest.TrySetResult();
            return new HttpResponseMessage(status);
        }
    }
}
