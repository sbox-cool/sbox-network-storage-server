using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class CloudflaredInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbox-ns-connector-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Checksum_failure_keeps_existing_executable_and_installs_nothing()
    {
        var destination = Path.Combine(_dir, "cloudflared");
        await File.WriteAllTextAsync(destination, "existing");
        using var http = new HttpClient(new BytesHandler([1, 2, 3]));
        var installer = new CloudflaredInstaller(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(destination,
            new CloudflaredAsset("cloudflared-linux-amd64", new string('0', 64)), CancellationToken.None));
        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Verified_download_installs_exact_bytes_with_executable_owner_permissions()
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        using var http = new HttpClient(new BytesHandler(bytes));
        var destination = Path.Combine(_dir, "cloudflared");
        await new CloudflaredInstaller(http).InstallAsync(destination,
            new CloudflaredAsset("cloudflared-linux-amd64", Convert.ToHexString(SHA256.HashData(bytes))), CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(destination));
    }

    [Theory]
    [InlineData("../escaped", TarEntryType.RegularFile)]
    [InlineData("/tmp/escaped", TarEntryType.RegularFile)]
    [InlineData("cloudflared", TarEntryType.SymbolicLink)]
    [InlineData("cloudflared", TarEntryType.HardLink)]
    public async Task Verified_archive_cannot_install_path_or_link_escapes(string member, TarEntryType type)
    {
        var archive = Archive(member, type);
        using var http = new HttpClient(new BytesHandler(archive));
        var destination = Path.Combine(_dir, "cloudflared");
        await Assert.ThrowsAsync<InvalidDataException>(() => new CloudflaredInstaller(http).InstallAsync(destination,
            new CloudflaredAsset("cloudflared-darwin-arm64.tgz", Convert.ToHexString(SHA256.HashData(archive))), CancellationToken.None));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Verified_single_executable_archive_is_extracted()
    {
        var archive = Archive("cloudflared", TarEntryType.RegularFile);
        using var http = new HttpClient(new BytesHandler(archive));
        var destination = Path.Combine(_dir, "cloudflared");
        await new CloudflaredInstaller(http).InstallAsync(destination,
            new CloudflaredAsset("cloudflared-darwin-arm64.tgz", Convert.ToHexString(SHA256.HashData(archive))), CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public void All_shipping_platforms_have_pinned_assets_and_Windows_arm64_is_not_invented()
    {
        foreach (var platform in new[] { ("linux", Architecture.X64), ("linux", Architecture.Arm64),
                     ("osx", Architecture.X64), ("osx", Architecture.Arm64), ("win", Architecture.X64) })
        {
            var asset = CloudflaredInstaller.Asset(platform.Item1, platform.Item2);
            Assert.Equal(32, Convert.FromHexString(asset.Sha256).Length);
            Assert.Contains("/2026.10.0/", asset.Url.AbsoluteUri);
        }
        Assert.Throws<PlatformNotSupportedException>(() => CloudflaredInstaller.Asset("win", Architecture.Arm64));
    }

    private static byte[] Archive(string member, TarEntryType type)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(type, member);
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream([1, 2, 3]);
            else entry.LinkName = "../escaped";
            writer.WriteEntry(entry);
        }
        return buffer.ToArray();
    }

    internal sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
