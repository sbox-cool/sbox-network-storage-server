using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Server.Backups;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>sboxns Backups wire contract, the backup runner (retention, lock, offsite failure) and the identity command.</summary>
[Collection("Console")]
public sealed class BackupTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://sboxcool.test/api/network-storage/backups");
    private const string Nonce = "00112233445566778899aabbccddeeff";
    private const long Timestamp = 1791417600;

    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-backup-test-").FullName;
    private string ConfigDir => Path.Combine(_root, "config");
    private string DataDir => Path.Combine(_root, "data");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // pooled SQLite handles on Windows
    }

    [Fact]
    public void Signed_payload_matches_the_contract_and_verifies_with_the_install_identity()
    {
        using var identity = TunnelIdentity.LoadOrCreate(Path.Combine(_root, "identity"));
        var detail = $"5:{new string('a', 64)}";

        var signed = identity.SignBackup("upload", detail, Timestamp, Nonce);

        Assert.Equal(identity.Name, signed.Name);
        Assert.Equal(Convert.ToBase64String(identity.PublicKeySpki), signed.PublicKey);
        Assert.Equal(Timestamp, signed.Timestamp);
        Assert.Equal(Nonce, signed.Nonce);
        var payload = TunnelIdentity.BackupPayload("upload", signed.Name, Timestamp, Nonce, detail);
        Assert.Equal($"sbox-ns-backup-v1\nupload\n{identity.Name}\n{Timestamp}\n{Nonce}\n{detail}", Encoding.UTF8.GetString(payload));
        Assert.True(Verify(signed.PublicKey, payload, signed.Signature));
    }

    [Fact]
    public async Task Upload_flow_sends_signed_requests_and_puts_the_bytes_with_the_issued_headers()
    {
        using var identity = TunnelIdentity.LoadOrCreate(Path.Combine(_root, "identity"));
        var bytes = Encoding.UTF8.GetBytes("gzip-bytes-for-test");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fake = new FakeSboxNs(request => request.Path switch
        {
            "/status" => Json(HttpStatusCode.OK, """{"enabled":false,"status":"coming_soon","message":"sboxns Backups is coming soon.","plan":null,"usedBytes":0,"backupCount":0,"lastBackupAt":null}"""),
            "/upload" => Json(HttpStatusCode.OK, $$"""{"backupId":"abcdef0123456789","uploadUrl":"https://storage.test/object?sig=1","uploadHeaders":{"Content-Type":"application/gzip","x-amz-meta-test":"1"},"expiresAt":"2030-01-01T00:00:00Z"}"""),
            "/complete" => Json(HttpStatusCode.OK, $$$"""{"backup":{"backupId":"abcdef0123456789","createdAt":"2030-01-01T00:00:00Z","sizeBytes":{{{bytes.Length}}},"sha256":"{{{sha256}}}"}}"""),
            "/object" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => Json(HttpStatusCode.NotFound, """{"error":"NOT_FOUND","message":"no"}"""),
        });
        using var client = new SboxNsBackupClient(Endpoint, identity, fake);

        var status = await client.StatusAsync(CancellationToken.None);
        Assert.False(status.Enabled);
        Assert.Equal("coming_soon", status.Status);
        Assert.Null(status.Plan);

        var ticket = await client.RequestUploadAsync(bytes.Length, sha256, CancellationToken.None);
        using (var body = new MemoryStream(bytes))
            await client.UploadAsync(ticket, body, bytes.Length, CancellationToken.None);
        var backup = await client.CompleteAsync(ticket.BackupId, CancellationToken.None);
        Assert.Equal("abcdef0123456789", backup.BackupId);

        var upload = fake.Requests.Single(r => r.Path == "/upload");
        Assert.Equal("https://sboxcool.test/api/network-storage/backups/upload", upload.Url);
        Assert.Equal(identity.Name, upload.Json.GetProperty("name").GetString());
        Assert.Equal(sha256, upload.Json.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, upload.Json.GetProperty("sizeBytes").GetInt64());
        Assert.True(Verify(upload.Json.GetProperty("publicKey").GetString()!,
            TunnelIdentity.BackupPayload("upload", identity.Name, upload.Json.GetProperty("timestamp").GetInt64(),
                upload.Json.GetProperty("nonce").GetString()!, $"{bytes.Length}:{sha256}"),
            upload.Json.GetProperty("signature").GetString()!));

        var complete = fake.Requests.Single(r => r.Path == "/complete");
        Assert.Equal("abcdef0123456789", complete.Json.GetProperty("backupId").GetString());

        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put.Method);
        Assert.Equal("https://storage.test/object?sig=1", put.Url);
        Assert.Equal(bytes.Length, put.ContentLength);
        Assert.Equal("1", put.Headers["x-amz-meta-test"]);
        Assert.Equal("application/gzip", put.Headers["Content-Type"]);
        Assert.Equal(bytes, put.RawBody);
    }

    [Fact]
    public async Task Refusals_map_to_stable_error_codes()
    {
        using var identity = TunnelIdentity.LoadOrCreate(Path.Combine(_root, "identity"));
        var fake = new FakeSboxNs(request => request.Path switch
        {
            "/upload" => Json(HttpStatusCode.RequestEntityTooLarge, """{"error":"QUOTA_EXCEEDED","message":"too big","quotaBytes":10,"usedBytes":0}"""),
            "/complete" => Json(HttpStatusCode.Conflict, """{"error":"SIZE_MISMATCH","message":"size"}"""),
            _ => Json(HttpStatusCode.Unauthorized, "<html>not json</html>"),
        });
        using var client = new SboxNsBackupClient(Endpoint, identity, fake);

        var quota = await Assert.ThrowsAsync<BackupApiException>(() => client.RequestUploadAsync(1, new string('0', 64), CancellationToken.None));
        Assert.Equal("QUOTA_EXCEEDED", quota.Code);
        Assert.Equal(413, quota.HttpStatus);

        var mismatch = await Assert.ThrowsAsync<BackupApiException>(() => client.CompleteAsync("abcdef0123456789", CancellationToken.None));
        Assert.Equal("SIZE_MISMATCH", mismatch.Code);
        Assert.Equal(409, mismatch.HttpStatus);

        var status = await Assert.ThrowsAsync<BackupApiException>(() => client.StatusAsync(CancellationToken.None));
        Assert.Equal("HTTP_401", status.Code);
        Assert.DoesNotContain("html", status.Message);
    }

    [Fact]
    public async Task Runner_rotates_only_automatic_backups_and_leaves_manual_ones_alone()
    {
        var config = await ConfigWithDatabaseAsync(sboxNs: false, keepLocal: 2);
        var directory = DatabaseBackup.BackupDirectory(config);
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "sbox-ns-auto-20200101-000000.db", "sbox-ns-auto-20200102-000000.db", "sbox-ns-auto-20200103-000000.db", "sbox-ns-20200101-000000.db" })
            File.WriteAllText(Path.Combine(directory, name), "old");

        var result = await BackupRunner.RunAsync(config, null, CancellationToken.None);

        Assert.True(File.Exists(result.LocalFile));
        Assert.StartsWith("sbox-ns-auto-", Path.GetFileName(result.LocalFile));
        Assert.Equal(2, result.PrunedCount);
        Assert.Equal(2, BackupFiles.AutoBackups(config).Count());
        Assert.True(File.Exists(Path.Combine(directory, "sbox-ns-20200101-000000.db")));
        Assert.Null(result.Offsite);
        Assert.Null(result.OffsiteError);

        var state = BackupRunner.ReadState(config);
        Assert.Equal("ok", state.Outcome);
        Assert.Equal(Path.GetFileName(result.LocalFile), state.BackupFile);
        Assert.NotNull(state.SucceededAt);
    }

    [Fact]
    public async Task Offsite_failure_keeps_the_local_backup_and_is_reported_not_thrown()
    {
        var config = await ConfigWithDatabaseAsync(sboxNs: true, keepLocal: 7);
        var handler = new FailingHandler();

        var result = await BackupRunner.RunAsync(config, handler, CancellationToken.None);

        Assert.True(File.Exists(result.LocalFile));
        Assert.Null(result.Offsite);
        Assert.Equal("sboxns Backups could not be reached.", result.OffsiteError);
        Assert.Empty(Directory.GetFiles(DatabaseBackup.BackupDirectory(config), "*.gz"));
        var state = BackupRunner.ReadState(config);
        Assert.Equal("offsite-failed", state.Outcome);
        Assert.Equal("sboxns Backups could not be reached.", state.Error);
        Assert.Null(state.SucceededAt);
    }

    [Fact]
    public async Task A_second_run_while_the_lock_is_held_is_refused()
    {
        var config = Load(ConfigDir, DataDir);
        config.EnsureRuntimeDirectory();
        using var held = new FileStream(Path.Combine(config.RuntimeDirectory, StateLayout.BackupLockFile),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<BackupBusyException>(() => BackupRunner.RunAsync(config, null, CancellationToken.None));
    }

    [Fact]
    public async Task Identity_json_has_name_and_base64_spki_without_a_database()
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object> { ["updates.check"] = false });
        var output = await CaptureAsync(() => BackupCommands.RunAsync(Context("backup", "identity", "--json")));

        using var json = JsonDocument.Parse(output);
        var name = json.RootElement.GetProperty("name").GetString()!;
        var spki = Convert.FromBase64String(json.RootElement.GetProperty("publicKey").GetString()!);
        Assert.Equal(TunnelIdentity.DeriveName(spki), name);
        Assert.Equal(12, name.Length);
    }

    private async Task<EffectiveConfig> ConfigWithDatabaseAsync(bool sboxNs, int keepLocal)
    {
        ConfigFiles.WriteAll(ConfigDir, new Dictionary<string, object>
        {
            ["updates.check"] = false,
            ["backup.sboxns.enabled"] = sboxNs,
            ["backup.schedule.keep_local"] = keepLocal,
        });
        Assert.Equal(CliApp.Ok, await DatabaseCommands.RunAsync(Context("db", "migrate")));
        return Load(ConfigDir, DataDir);
    }

    private static EffectiveConfig Load(string configDir, string dataDir)
        => ConfigLoader.Load(configDir, dataDir, null, _ => null);

    private CliContext Context(params string[] args)
        => new(CliArguments.Parse([.. args, "--config-dir", ConfigDir, "--data-dir", DataDir]));

    private static async Task<string> CaptureAsync(Func<Task<int>> action)
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(CliApp.Ok, await action());
        }
        finally
        {
            Console.SetOut(original);
        }
        return output.ToString();
    }

    private static bool Verify(string publicKeyBase64, byte[] payload, string signatureBase64)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        return key.VerifyData(payload, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(string Method, string Path, string Url, string Body, byte[] RawBody,
        long? ContentLength, Dictionary<string, string> Headers)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }

    private sealed class FakeSboxNs(Func<CapturedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var raw = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
                headers[header.Key] = string.Join(",", header.Value);
            if (request.Content is not null)
                foreach (var header in request.Content.Headers)
                    headers[header.Key] = string.Join(",", header.Value);
            var action = "/" + request.RequestUri!.Segments[^1];
            var captured = new CapturedRequest(request.Method.Method, action, request.RequestUri.AbsoluteUri,
                Encoding.UTF8.GetString(raw), raw, request.Content?.Headers.ContentLength, headers);
            Requests.Add(captured);
            return respond(captured);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
