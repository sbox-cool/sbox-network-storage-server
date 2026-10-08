using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SboxNetworkStorage.Server.Tunnels;

public sealed record CloudflaredAsset(string Name, string Sha256)
{
    public Uri Url => new($"https://github.com/cloudflare/cloudflared/releases/download/{CloudflaredInstaller.Version}/{Name}");
}

public sealed class CloudflaredInstaller(HttpClient http, CloudflaredAsset? assetOverride = null)
{
    public const string Version = "2026.10.0";
    // GitHub release asset digests for tag 2026.10.0, not locally guessed hashes.
    public static CloudflaredAsset Asset(string os, Architecture architecture) => (os, architecture) switch
    {
        ("linux", Architecture.X64) => new("cloudflared-linux-amd64", "d33ff2d14475178d2012c2c56beba87389ac5ded27649519f198a7d3134a99db"),
        ("linux", Architecture.Arm64) => new("cloudflared-linux-arm64", "e6422b9d4f72d3194bc5a38676f13667c06666523217b842a877d72a80b5ac08"),
        ("osx", Architecture.X64) => new("cloudflared-darwin-amd64.tgz", "903845b81828c8cb3c5d13d816a2de71c06a3da5785469df8eb0e1b736d92f9f"),
        ("osx", Architecture.Arm64) => new("cloudflared-darwin-arm64.tgz", "a2f79ff7b9420aa537d74af239f376da170bbabeb529aec416002adac6a72e70"),
        ("win", Architecture.X64) => new("cloudflared-windows-amd64.exe", "86aee4017b26625cee8484c113558f48effa4cd47f7aa05fcf425604e5d2b23c"),
        _ => throw new PlatformNotSupportedException("Tunnel connectors support Linux and macOS x64/arm64, and Windows x64.")
    };

    public static CloudflaredAsset CurrentAsset => Asset(OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "win",
        RuntimeInformation.ProcessArchitecture);

    public static string ExecutablePath(string configDirectory)
        => Path.Combine(configDirectory, "connectors", Version, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    public Task<string> InstallAsync(string configDirectory, CancellationToken ct)
        => InstallAsync(ExecutablePath(configDirectory), assetOverride ?? CurrentAsset, ct);

    public async Task<string> InstallAsync(string destination, CloudflaredAsset asset, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var download = Path.Combine(directory, $".download-{Guid.NewGuid():N}");
        var staged = Path.Combine(directory, $".connector-{Guid.NewGuid():N}");
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > 256L * 1024 * 1024) throw new InvalidDataException("Connector download is too large.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256)))
                    throw new InvalidDataException("cloudflared SHA-256 mismatch; nothing was installed or configured.");
            }
            // Never extract an archive until its complete compressed bytes have passed the pinned checksum.
            if (asset.Name.EndsWith(".tgz", StringComparison.Ordinal))
                await ExtractExecutableAsync(download, staged, ct);
            else
                File.Move(download, staged);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(destination))
            {
                await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var candidate = File.OpenRead(staged);
                var existingHash = await SHA256.HashDataAsync(existing, ct);
                var candidateHash = await SHA256.HashDataAsync(candidate, ct);
                if (CryptographicOperations.FixedTimeEquals(existingHash, candidateHash))
                    return destination; // Windows cannot replace a running executable; identical pinned bytes need no replacement.
            }
            File.Move(staged, destination, overwrite: true);
            return destination;
        }
        finally
        {
            File.Delete(download);
            File.Delete(staged);
        }
    }

    public static async Task ExtractExecutableAsync(string archive, string destination, CancellationToken ct)
    {
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var found = false;
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.Name is not ("cloudflared" or "./cloudflared") || found
                || entry.Length is <= 0 or > 256L * 1024 * 1024 || entry.DataStream is null)
                throw new InvalidDataException("Unexpected entry in cloudflared archive.");
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            await entry.DataStream.CopyToAsync(output, ct);
            found = true;
        }
        if (!found) throw new InvalidDataException("cloudflared archive has no executable.");
    }
}
