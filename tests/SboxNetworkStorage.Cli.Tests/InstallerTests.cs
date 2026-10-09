using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-installer-").FullName;

    [Theory]
    [InlineData("0.5.0/../../evil")]
    [InlineData("1.0;rm -rf /")]
    [InlineData("vv1.2.3")]
    [InlineData("1.2.3\nevil")]
    public async Task Shell_rejects_explicit_unsafe_versions_without_a_request(string version)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Shell(version);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("invalid release version", result.Error);
        Assert.False(File.Exists(RequestLog));
    }

    [Theory]
    [InlineData("0.5.0/../../evil", "")]
    [InlineData("1.2.3;evil", "canary")]
    public async Task Shell_rejects_unsafe_resolved_versions_before_asset_downloads(string version, string channel)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Shell(null, new() { ["SBOX_TEST_VERSION"] = version, ["SBOX_NS_CHANNEL"] = channel });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("invalid release version", result.Error);
        var requests = File.ReadAllLines(RequestLog);
        Assert.Single(requests);
        Assert.DoesNotContain("/releases/download/", requests[0]);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3-rc.1")]
    public async Task Shell_requires_openssl_before_downloading_a_valid_version(string version)
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Shell(version);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("openssl is required", result.Error);
        Assert.Contains("apt-get install openssl", result.Error);
        Assert.False(File.Exists(RequestLog));
    }

    [Fact]
    public async Task Shell_forbids_the_insecure_override_with_unattended_updates()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Shell("1.2.3", new()
        {
            ["SBOX_NS_INSECURE_SKIP_SIGNATURE"] = "1", ["SBOX_NS_AUTO_UPDATE"] = "1"
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("forbidden with unattended updates", result.Error);
        Assert.False(File.Exists(RequestLog));
    }

    [Fact]
    public async Task Manual_override_is_loud_and_still_rejects_archive_hash_mismatch()
    {
        if (OperatingSystem.IsWindows()) return;
        var archive = Path.Combine(_root, "archive");
        var sums = Path.Combine(_root, "sums");
        File.WriteAllText(archive, "tampered archive");
        File.WriteAllText(sums, new string('0', 64) + "  sbox-ns-1.2.3-osx-arm64.tar.gz\n");
        var binary = Path.Combine(_root, "share", "sbox-ns", "sbox-ns");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "previous binary");
        var result = await Shell("1.2.3", new()
        {
            ["SBOX_NS_INSECURE_SKIP_SIGNATURE"] = "1", ["SBOX_TEST_ARCHIVE"] = archive, ["SBOX_TEST_SUMS"] = sums
        }, downloadTools: true);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("DANGER", result.Error);
        Assert.Contains("checksum mismatch", result.Error);
        Assert.Equal("previous binary", File.ReadAllText(binary));
        Assert.Equal(2, File.ReadAllLines(RequestLog).Length);
    }

    [Theory]
    [InlineData("0.5.0/../../evil")]
    [InlineData("1.0;rm -rf /")]
    [InlineData("vv1.2.3")]
    [InlineData("1.2.3\nevil")]
    public async Task Windows_PowerShell_rejects_unsafe_versions_before_installation(string version)
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "install", "install.ps1"));
        CleanInstallerEnvironment(start);
        start.Environment["SBOX_NS_VERSION"] = version;
        var result = await Run(start);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid release version", result.Error);
    }

    [Fact]
    public async Task Windows_PowerShell_51_verifies_current_and_next_keys_and_rejects_bad_signatures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sums = Path.Combine(_root, "SHA256SUMS");
        var data = Encoding.UTF8.GetBytes(new string('a', 64) + "  archive.zip\n");
        File.WriteAllBytes(sums, data);
        foreach (var (name, key) in new[] { ("current", current), ("next", next), ("stranger", stranger) })
            File.WriteAllBytes(Path.Combine(_root, name + ".sig"),
                key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        File.WriteAllBytes(Path.Combine(_root, "invalid.sig"), [0x30, 0x01, 0x00]);
        var harness = Path.Combine(_root, "verify.ps1");
        File.WriteAllText(harness, $$"""
            $ErrorActionPreference = 'Stop'
            if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
                throw 'This test must exercise Windows PowerShell 5.1.'
            }
            # Load the actual installer; its entry point stops on the deliberately invalid
            # version before network/filesystem operations. No source is copied or rewritten.
            $env:SBOX_NS_VERSION = 'invalid'
            try { . {{PsQuote(Path.Combine(RepositoryRoot(), "install", "install.ps1"))}} }
            catch {
                if ($_ -notmatch 'Invalid release version') { throw }
            }
            # Ephemeral test keys only: never inserted into the shipped installer trust set.
            $script:PinnedReleaseKeys = @('{{EccBlob(current)}}', '{{EccBlob(next)}}')
            Assert-SboxReleaseSignature {{PsQuote(sums)}} {{PsQuote(Path.Combine(_root, "current.sig"))}}
            Assert-SboxReleaseSignature {{PsQuote(sums)}} {{PsQuote(Path.Combine(_root, "next.sig"))}}
            foreach ($signature in @('stranger.sig', 'invalid.sig', 'missing.sig')) {
                $rejected = $false
                try { Assert-SboxReleaseSignature {{PsQuote(sums)}} (Join-Path {{PsQuote(_root)}} $signature) }
                catch { $rejected = $true }
                if (-not $rejected) { throw "Accepted invalid signature: $signature" }
            }
            [IO.File]::AppendAllText({{PsQuote(sums)}}, 'tampered')
            $rejected = $false
            try { Assert-SboxReleaseSignature {{PsQuote(sums)}} {{PsQuote(Path.Combine(_root, "current.sig"))}} }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Accepted tampered checksum manifest.' }
            $script:PinnedReleaseKeys = @()
            $rejected = $false
            try { Assert-SboxReleaseSignature {{PsQuote(sums)}} {{PsQuote(Path.Combine(_root, "current.sig"))}} }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Accepted an empty pinned key set.' }
            Write-Output 'signature behavior verified'
            """);
        var start = new ProcessStartInfo("powershell.exe");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness })
            start.ArgumentList.Add(argument);
        CleanInstallerEnvironment(start);
        var result = await Run(start);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("signature behavior verified", result.Output);
    }

    private static string EccBlob(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        byte[] blob = [0x45, 0x43, 0x53, 0x31, 32, 0, 0, 0, .. parameters.Q.X!, .. parameters.Q.Y!];
        return Convert.ToBase64String(blob);
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private string RequestLog => Path.Combine(_root, "requests");

    private async Task<(int ExitCode, string Output, string Error)> Shell(string? version,
        Dictionary<string, string>? variables = null, bool downloadTools = false)
    {
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools")).FullName;
        Script(tools, "uname", "case \"$1\" in -s) echo Darwin ;; -m) echo arm64 ;; esac\n");
        Script(tools, "id", "echo 1000\n");
        Script(tools, "curl", """
            output='-'
            while [ "$#" -gt 0 ]; do
                case "$1" in -o) output="$2"; shift ;; https://*) url="$1" ;; esac
                shift
            done
            printf '%s\n' "$url" >> "$SBOX_TEST_REQUESTS"
            case "$url" in
                *api.github.com*) printf '{"tag_name":"v%s"}\n' "$SBOX_TEST_VERSION" ;;
                *sboxcool.com*) printf '{"version":"%s"}\n' "$SBOX_TEST_VERSION" ;;
                *.tar.gz) cp "$SBOX_TEST_ARCHIVE" "$output" ;;
                */SHA256SUMS) cp "$SBOX_TEST_SUMS" "$output" ;;
                *) exit 1 ;;
            esac
            """);
        LinkTool(tools, "grep");
        LinkTool(tools, "sed");
        if (downloadTools)
        {
            foreach (var tool in new[] { "mkdir", "mktemp", "rm", "cp", "sha256sum", "cut", "awk" })
                LinkTool(tools, tool);
        }
        var start = new ProcessStartInfo("/bin/sh");
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "install", "install.sh"));
        CleanInstallerEnvironment(start);
        start.Environment["PATH"] = tools; // Deliberately no OpenSSL, and no access to real download clients.
        start.Environment["HOME"] = _root;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(_root, "share");
        start.Environment["SBOX_TEST_REQUESTS"] = RequestLog;
        start.Environment["SBOX_TEST_VERSION"] = "1.2.3";
        if (version is not null) start.Environment["SBOX_NS_VERSION"] = version;
        if (variables is not null)
            foreach (var pair in variables) start.Environment[pair.Key] = pair.Value;
        return await Run(start);
    }

    private static void CleanInstallerEnvironment(ProcessStartInfo start)
    {
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("SBOX_NS_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        start.Environment.Remove("GITHUB_TOKEN");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static void Script(string folder, string name, string body)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void LinkTool(string folder, string name)
    {
        var directories = new[] { "/usr/bin", "/bin", "/usr/local/bin", "/opt/homebrew/bin" };
        var source = directories.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        // macOS ships shasum, not sha256sum.
        if (source is null && name == "sha256sum")
        {
            LinkTool(folder, "shasum");
            return;
        }
        Assert.True(source is not null, $"Required installer test tool {name} is missing.");
        File.CreateSymbolicLink(Path.Combine(folder, name), source!);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "install", "install.sh"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Cannot locate the checkout containing the installer.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
