using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>
/// Root-run update code must not trust anything the service account can write, and must refuse
/// any release whose SHA256SUMS is not signed by a pinned key.
/// </summary>
public sealed class UpdateTrustTests : IDisposable
{
    private const string Version = "1.2.0";
    private const UnixFileMode UserAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-trust-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // --- trust anchors are not configuration ---------------------------------------------------

    [Fact]
    public async Task Config_keys_cannot_redirect_the_release_sources()
    {
        var config = Path.Combine(_root, "etc");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, SettingDefinitions.UpdatesFile),
            "[updates]\nfeed_url = \"https://attacker.example/feed\"\ngithub_repo = \"attacker/fork\"\n");

        var effective = ConfigLoader.Load(config, _root, null, _ => null);

        Assert.True(effective.IsValid, string.Join("; ", effective.Issues));
        Assert.Equal(["updates.feed_url", "updates.github_repo"], effective.IgnoredSettings.Select(i => i.Message.Split('\'')[1]).Order());
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(url =>
        {
            requests.Add(url);
            return Json("""{"version":"1.4.0"}""");
        }));
        var feed = new ReleaseFeed(http, effective);

        await feed.GetFromFeedAsync(CancellationToken.None);

        Assert.Equal(["https://sboxcool.com/api/network-storage/releases/latest?channel=stable"], requests);
        Assert.Equal("https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v1.4.0/SHA256SUMS", feed.AssetUrl("1.4.0", "SHA256SUMS"));
        Assert.DoesNotContain(requests.Concat([feed.AssetUrl("1.4.0", "x")]), r => r.Contains("attacker", StringComparison.Ordinal));
    }

    [Fact]
    public void Cosign_identity_accepts_only_the_release_workflow_on_a_version_tag()
    {
        var pattern = new System.Text.RegularExpressions.Regex(UpdateTrust.Compiled.CosignIdentityPattern);
        const string repo = "https://github.com/sbox-cool/sbox-network-storage-server";

        Assert.Matches(pattern, $"{repo}/.github/workflows/release.yml@refs/tags/v1.2.0");
        Assert.DoesNotMatch(pattern, $"{repo}/.github/workflows/release.yml@refs/heads/main");
        Assert.DoesNotMatch(pattern, $"{repo}/.github/workflows/other.yml@refs/tags/v1.2.0");
        Assert.DoesNotMatch(pattern, "https://github.com/attacker/fork/.github/workflows/release.yml@refs/tags/v1.2.0");
    }

    // --- signature verification ---------------------------------------------------------------

    [Fact]
    public async Task Signed_release_installs_and_either_pinned_key_is_accepted()
    {
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(current, next);

        Assert.Equal("1.2.0-binary", await Stage(trust, Release(signer: current)));
        Assert.Equal("1.2.0-binary", await Stage(trust, Release(signer: next)));
    }

    [Fact]
    public async Task Cosign_absent_still_requires_a_valid_pinned_signature()
    {
        using var pinned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        static (int, string) MissingCosign(string file, IReadOnlyList<string> arguments) => (127, "not found");

        Assert.Equal("1.2.0-binary", await Stage(Trust(pinned), Release(pinned), MissingCosign));
        var error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(pinned), Release(stranger), MissingCosign));
        Assert.Contains("signature verification of SHA256SUMS failed", error.Message);
        var unsigned = Release(pinned);
        unsigned.Signature = null;
        error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(pinned), unsigned, MissingCosign));
        Assert.Contains("Nothing was installed", error.Message);
    }

    [Fact]
    public async Task Tampered_sums_abort()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = Release(signer: key);
        release.Sums = release.Sums.Replace(release.Sums[..8], "deadbeef", StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(key), release));

        Assert.Contains("signature verification of SHA256SUMS failed", error.Message);
    }

    [Fact]
    public async Task Missing_signature_asset_aborts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = Release(signer: key);
        release.Signature = null;

        var error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(key), release));

        Assert.Contains("signature", error.Message);
        Assert.Contains("Nothing was installed", error.Message);
    }

    [Fact]
    public async Task Validly_signed_sums_with_a_wrong_archive_hash_abort()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = Release(signer: key);
        release.Archive = [.. release.Archive, 0];

        var error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(key), release));

        Assert.Contains("checksum mismatch", error.Message);
    }

    [Fact]
    public async Task Key_that_is_not_pinned_is_rejected()
    {
        using var pinned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(pinned), Release(signer: stranger)));

        Assert.Contains("signature verification of SHA256SUMS failed", error.Message);
    }

    [Fact]
    public async Task A_build_without_pinned_keys_installs_nothing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var error = await Assert.ThrowsAsync<CliException>(() => Stage(Trust(), Release(signer: key)));

        Assert.Contains("no pinned release signing key", error.Message);
    }

    [Fact]
    public void Pinned_keys_file_parses_pem_blocks_and_ignores_comments()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = $"# comment\n{key.ExportSubjectPublicKeyInfoPem()}\nmore text\n";

        var keys = ReleaseSignature.ParseKeys(pem);

        Assert.Equal([key.ExportSubjectPublicKeyInfo()], keys);
        Assert.Empty(ReleaseSignature.ParseKeys("# no keys pinned"));
    }

    [Fact]
    public async Task Unattended_update_refuses_the_insecure_signature_override()
    {
        Environment.SetEnvironmentVariable(AutoUpdateCommand.InsecureSkipSignatureVariable, "1");
        try
        {
            var error = await Assert.ThrowsAsync<CliException>(() =>
                AutoUpdateCommand.RunAsync(new CliContext(CliArguments.Parse(["update", "--auto"]))));
            Assert.Contains("never skip signature verification", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AutoUpdateCommand.InsecureSkipSignatureVariable, null);
        }
    }

    // --- root-only state and rollback ---------------------------------------------------------

    [Fact]
    public void State_folder_that_the_service_could_write_is_refused()
    {
        if (OperatingSystem.IsWindows()) return; // folder-trust checks are Unix permissions; Windows skips them by design.
        var folder = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        File.SetUnixFileMode(folder, UserAll | UnixFileMode.GroupWrite);

        var error = Assert.Throws<CliException>(() => new UpdateState(folder, FolderTrust.Host).Ensure());

        Assert.Contains("writable by group or others", error.Message);
    }

    [Fact]
    public void State_folder_owned_by_the_service_user_is_refused_for_root()
    {
        if (OperatingSystem.IsWindows()) return; // folder-trust checks are Unix permissions; Windows skips them by design.
        var folder = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        File.SetUnixFileMode(folder, UserAll);
        var asRoot = new FolderTrust(effectiveUser: 0, ownerOf: _ => 995);

        var error = Assert.Throws<CliException>(() => new UpdateState(folder, asRoot).Ensure());

        Assert.Contains("must be owned by root", error.Message);
        new UpdateState(folder, new FolderTrust(0, _ => 0)).Ensure();
    }

    [Fact]
    public void State_folder_that_is_a_symlink_is_refused()
    {
        if (OperatingSystem.IsWindows()) return; // folder-trust checks are Unix permissions; Windows skips them by design.
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        File.SetUnixFileMode(real, UserAll);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);

        var error = Assert.Throws<CliException>(() => new UpdateState(link, FolderTrust.Host).Ensure());

        Assert.Contains("symbolic link", error.Message);
    }

    [Fact]
    public void Records_are_read_only_from_the_state_folder()
    {
        var state = new UpdateState(Path.Combine(_root, "state"), FolderTrust.Host);
        var legacy = Path.Combine(_root, "var", "default", "updates");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "last-update.json"),
            """{"FromVersion":"1.0.0","ToVersion":"1.1.0","BinaryPath":"/etc/sudoers","PreviousBinaryPath":"/tmp/evil","BackupPath":null,"UpdatedAt":"2026-10-01T00:00:00Z"}""");

        Assert.Null(state.Read("default"));

        state.Write("default", new UpdateRecord("1.0.0", "1.1.0", "/usr/local/bin/sbox-ns", state.PreviousBinaryPath, null, DateTimeOffset.UtcNow));
        Assert.Equal("1.1.0", state.Read("default")!.ToVersion);
    }

    [Fact]
    public void Binary_swap_refuses_a_symlinked_target_and_leaves_its_destination_alone()
    {
        if (OperatingSystem.IsWindows()) return; // folder-trust checks are Unix permissions; Windows skips them by design.
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var victim = Path.Combine(_root, "victim");
        File.WriteAllText(victim, "precious");
        var target = Path.Combine(bin, "sbox-ns");
        File.CreateSymbolicLink(target, victim);
        var source = Path.Combine(_root, "new");
        File.WriteAllText(source, "new binary");

        var error = Assert.Throws<CliException>(() => BinarySwap.Replace(source, target));

        Assert.Contains("symbolic link", error.Message);
        Assert.Equal("precious", File.ReadAllText(victim));
    }

    [Fact]
    public void Binary_swap_refuses_a_folder_the_service_could_write()
    {
        if (OperatingSystem.IsWindows()) return; // folder-trust checks are Unix permissions; Windows skips them by design.
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        File.SetUnixFileMode(bin, UserAll | UnixFileMode.OtherWrite);
        var target = Path.Combine(bin, "sbox-ns");
        File.WriteAllText(target, "old");
        var source = Path.Combine(_root, "new");
        File.WriteAllText(source, "new binary");

        Assert.Throws<CliException>(() => BinarySwap.Replace(source, target));

        Assert.Equal("old", File.ReadAllText(target));
    }

    // --- helpers ------------------------------------------------------------------------------

    private static UpdateTrust Trust(params ECDsa[] keys)
        => new(UpdateTrust.DefaultRepository, UpdateTrust.DefaultFeedUrl, [.. keys.Select(k => k.ExportSubjectPublicKeyInfo())]);

    private async Task<string> Stage(UpdateTrust trust, FakeRelease release,
        Func<string, IReadOnlyList<string>, (int ExitCode, string Output)>? runCapture = null)
    {
        var archiveName = $"sbox-ns-{Version}-{BuildInfo.RuntimeIdentifier}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        using var http = new HttpClient(new Handler(url => url.EndsWith("/SHA256SUMS", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(release.Sums)) }
            : url.EndsWith("/" + ReleaseSignature.AssetName, StringComparison.Ordinal)
                ? release.Signature is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Signature) }
                : url.EndsWith("/" + archiveName, StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Archive) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var feed = new ReleaseFeed(http, ConfigLoader.Load(_root, _root, null, _ => null), trust);
        var work = Directory.CreateDirectory(Path.Combine(_root, "work-" + Guid.NewGuid().ToString("N"))).FullName;

        var staged = await UpdateCommands.StageReleaseAsync(http, feed, Version, "/usr/local/bin/sbox-ns", work, runCapture);

        return File.ReadAllText(staged);
    }

    private static FakeRelease Release(ECDsa signer)
    {
        var archiveName = $"sbox-ns-{Version}-{BuildInfo.RuntimeIdentifier}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        var archive = Pack("sbox-ns", $"{Version}-binary");
        var sums = $"{Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}  {archiveName}\n";
        var signature = signer.SignData(Encoding.UTF8.GetBytes(sums), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new FakeRelease { Archive = archive, Sums = sums, Signature = signature };
    }

    private static byte[] Pack(string fileName, string content)
    {
        using var output = new MemoryStream();
        if (OperatingSystem.IsWindows())
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = zip.CreateEntry(fileName).Open())
            {
                entry.Write(Encoding.UTF8.GetBytes(content));
            }

            return output.ToArray();
        }

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, fileName) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }

        return output.ToArray();
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeRelease
    {
        public required byte[] Archive { get; set; }

        public required string Sums { get; set; }

        public required byte[]? Signature { get; set; }
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request.RequestUri!.ToString()));
    }
}
