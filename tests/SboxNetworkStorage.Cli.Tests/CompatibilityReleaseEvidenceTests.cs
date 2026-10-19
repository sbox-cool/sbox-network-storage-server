using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace SboxNetworkStorage.Cli.Tests;

/// <summary>scripts/compat-release.py validates the committed matrix and gates releases on exact-commit evidence.</summary>
public sealed class CompatibilityReleaseEvidenceTests
{
    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !(Directory.Exists(Path.Combine(root.FullName, "compat")) && File.Exists(Path.Combine(root.FullName, "scripts", "compat-release.py"))))
            root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    /// <summary>
    /// Windows CI has no bare <c>python3</c>: prefer the <c>py -3</c> launcher, then <c>python3</c>,
    /// then <c>python</c>. The first interpreter that runs <c>--version</c> wins.
    /// </summary>
    private static string[]? PythonCommand()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { new[] { "py", "-3" }, new[] { "python3" }, new[] { "python" } }
            : new[] { new[] { "python3" }, new[] { "python" } };
        foreach (var candidate in candidates)
        {
            try
            {
                var probe = new ProcessStartInfo(candidate[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in candidate.Skip(1)) probe.ArgumentList.Add(arg);
                probe.ArgumentList.Add("--version");
                using var process = Process.Start(probe);
                if (process is null) continue;
                if (process.WaitForExit(15_000) && process.ExitCode == 0) return candidate;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            catch
            {
                // Try the next interpreter.
            }
        }

        return null;
    }

    private static async Task<(int Exit, string Output)> RunAsync(string[] python, params string[] args)
    {
        var start = new ProcessStartInfo(python[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in python.Skip(1)) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "compat-release.py"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python 3 is required for compatibility release gates.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }

    [Fact]
    public async Task Matrix_is_schema_valid_and_release_requires_exact_commit_evidence()
    {
        var python = PythonCommand();
        if (python is null) return; // No Python 3 on this machine; InstallerTests uses the same early return.
        var valid = await RunAsync(python!, Array.Empty<string>());
        Assert.Equal(0, valid.Exit);
        var missing = await RunAsync(python!, ["--server", "0.5.0", "--commit", new string('a', 40)]);
        Assert.NotEqual(0, missing.Exit);
        Assert.Contains("No matching-commit evidence row", missing.Output);
        var path = Path.GetTempFileName();
        try
        {
            var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "compat", "library-matrix.json")))!;
            var recommended = matrix["recommended"]!.GetValue<string>();
            matrix["tested"] = new JsonArray(new JsonObject
            {
                ["server"] = "0.5.0", ["commit"] = new string('a', 40), ["kind"] = "self-hosted",
                ["library"] = recommended, ["librarySource"] = "library-manager", ["revision"] = 310500,
                ["engine"] = "26.10.02", ["os"] = "Windows 11 23H2", ["database"] = new JsonArray("sqlite", "postgres"),
                ["result"] = "pass", ["date"] = "2026-10-20",
                ["evidence"] = new JsonArray("https://github.com/sbox-cool/sbox-network-storage-server/issues/54")
            });
            File.WriteAllText(path, matrix.ToJsonString());
            var wrongCommit = await RunAsync(python!, ["--matrix", path, "--server", "0.5.0", "--commit", new string('b', 40)]);
            Assert.NotEqual(0, wrongCommit.Exit);
            var rightCommit = await RunAsync(python!, ["--matrix", path, "--server", "0.5.0", "--commit", new string('a', 40)]);
            Assert.Equal(0, rightCommit.Exit);
            Assert.Contains("26.10.02", rightCommit.Output);
            var source = Path.Combine(RepositoryRoot(), "compat", "library-matrix.json");
            Assert.Equal(0, (await RunAsync(python!, ["--matrix", path, "--policy-source", source])).Exit);
            matrix["recommended"] = "9.9.9";
            File.WriteAllText(path, matrix.ToJsonString());
            var changedPolicy = await RunAsync(python!, ["--matrix", path, "--policy-source", source]);
            Assert.NotEqual(0, changedPolicy.Exit);
            Assert.Contains("source policy", changedPolicy.Output);
            matrix["recommended"] = recommended;
            matrix["tested"]![0]!["result"] = "ci-only";
            File.WriteAllText(path, matrix.ToJsonString());
            var ciOnly = await RunAsync(python!, ["--matrix", path, "--server", "0.5.0", "--commit", new string('a', 40)]);
            Assert.Equal(0, ciOnly.Exit);
            Assert.Contains("CI-only release", ciOnly.Output);
            Assert.DoesNotContain("CI-only release", rightCommit.Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Published_library_metadata_is_accepted_but_ranges_versions_and_sunsets_are_validated()
    {
        var python = PythonCommand();
        if (python is null) return; // No Python 3 on this machine; InstallerTests uses the same early return.
        var path = Path.GetTempFileName();
        var baselinePath = Path.GetTempFileName();
        try
        {
            var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "compat", "library-matrix.json")))!;
            matrix["libraries"]!.AsArray().Add(new JsonObject
            {
                ["version"] = "1.2.0", ["revision"] = 310500, ["released"] = "2025-10-02",
                ["minServer"] = "0.5.0", ["status"] = "supported"
            });
            matrix["deprecated"] = new JsonArray(new JsonObject
            {
                ["range"] = "<1.2.0", ["sunset"] = "2026-04-02", ["reason"] = "Superseded by 1.2.0"
            });
            File.WriteAllText(path, matrix.ToJsonString());
            File.WriteAllText(baselinePath, matrix.ToJsonString());
            Assert.Equal(0, (await RunAsync(python!, ["--matrix", path])).Exit);
            matrix["deprecated"]![0]!["range"] = ">1.0.0 ===2.0.0";
            File.WriteAllText(path, matrix.ToJsonString());
            var malformed = await RunAsync(python!, ["--matrix", path]);
            Assert.NotEqual(0, malformed.Exit);
            Assert.Contains("range token", malformed.Output);
            matrix["deprecated"]![0]!["range"] = ">=1.1.0  <1.2.0"; // double space: accepted, like VersionRange.TryParse
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.Equal(0, (await RunAsync(python!, ["--matrix", path])).Exit);
            matrix["deprecated"]![0]!["sunset"] = "2026-02-30";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.NotEqual(0, (await RunAsync(python!, ["--matrix", path])).Exit);
            matrix["deprecated"]![0]!["sunset"] = "2026-01-01";
            File.WriteAllText(path, matrix.ToJsonString());
            var shortened = await RunAsync(python!, ["--matrix", path, "--baseline", baselinePath]);
            Assert.NotEqual(0, shortened.Exit);
            Assert.Contains("sunset", shortened.Output);
            matrix["deprecated"]![0]!["sunset"] = "2026-04-02";
            matrix["libraries"]!.AsArray()[matrix["libraries"]!.AsArray().Count - 1]!["minServer"] = "2147483648.0.0";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.NotEqual(0, (await RunAsync(python!, ["--matrix", path])).Exit);
            matrix["libraries"]!.AsArray()[matrix["libraries"]!.AsArray().Count - 1]!["minServer"] = "";
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.NotEqual(0, (await RunAsync(python!, ["--matrix", path])).Exit);
        }
        finally { File.Delete(path); File.Delete(baselinePath); }
    }

    [Fact]
    public async Task Deprecated_library_sunsets_honor_the_six_month_and_ninety_day_windows()
    {
        var python = PythonCommand();
        if (python is null) return; // No Python 3 on this machine; InstallerTests uses the same early return.
        var path = Path.GetTempFileName();
        try
        {
            var matrix = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "compat", "library-matrix.json")))!;
            matrix["libraries"]!.AsArray().Add(new JsonObject
            {
                ["version"] = "1.2.0", ["revision"] = 310500, ["released"] = "2026-01-15",
                ["minServer"] = "0.5.0", ["status"] = "supported"
            });
            var entry = matrix["libraries"]!.AsArray().First(node => node!["version"]!.GetValue<string>() == "1.1.0");
            entry!["status"] = "deprecated";
            entry!["sunset"] = "2026-07-15"; // exactly six months after the 1.2.0 successor shipped
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.Equal(0, (await RunAsync(python!, ["--matrix", path])).Exit);
            entry!["sunset"] = "2026-07-14"; // one day short of the six-month window
            File.WriteAllText(path, matrix.ToJsonString());
            var early = await RunAsync(python!, ["--matrix", path]);
            Assert.NotEqual(0, early.Exit);
            Assert.Contains("1.1.0", early.Output);
            Assert.Contains("six months", early.Output);
            entry!["sunset"] = "2026-07-29"; // inside six months but short of announced + 90 days
            entry!["announced"] = "2026-05-01";
            File.WriteAllText(path, matrix.ToJsonString());
            var rushed = await RunAsync(python!, ["--matrix", path]);
            Assert.NotEqual(0, rushed.Exit);
            Assert.Contains("90 days", rushed.Output);
            entry!["sunset"] = "2026-07-30"; // announced + 90 days exactly
            File.WriteAllText(path, matrix.ToJsonString());
            Assert.Equal(0, (await RunAsync(python!, ["--matrix", path])).Exit);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Rollback_reports_use_the_target_binary_compatibility_shape()
    {
        var python = PythonCommand();
        if (python is null) return; // No Python 3 on this machine; InstallerTests uses the same early return.
        var reportPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(reportPath, """{"protocol":1,"version":"0.5.1","schemaVersion":5,"supportedSchemaVersion":5,"minBinarySchema":4,"compatible":true,"migrationRequired":false,"layoutGeneration":"1"}""");
            var notes = await RunAsync(python!, ["--rollback-report", reportPath]);
            Assert.Equal(0, notes.Exit);
            Assert.Contains("Rollback compatibility", notes.Output);
            Assert.Contains("0.5.1", notes.Output);
            // A target that supports a newer schema than the probed database is safe: warn, do not refuse.
            File.WriteAllText(reportPath, """{"protocol":1,"version":"0.5.2","schemaVersion":4,"supportedSchemaVersion":5,"minBinarySchema":4,"compatible":true}""");
            var newer = await RunAsync(python!, ["--rollback-report", reportPath]);
            Assert.Equal(0, newer.Exit);
            Assert.Contains("newer schema", newer.Output);
            File.WriteAllText(reportPath, """{"protocol":1,"version":"0.5.1","schemaVersion":5,"supportedSchemaVersion":5,"minBinarySchema":9,"compatible":true}""");
            Assert.NotEqual(0, (await RunAsync(python!, ["--rollback-report", reportPath])).Exit);
        }
        finally { File.Delete(reportPath); }
    }
}
