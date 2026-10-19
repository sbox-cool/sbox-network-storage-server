using System.Diagnostics;
using Json.Schema;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>S2: scripts/compat-tested-with.ps1 prints the "Tested with" table for a release.</summary>
public sealed class TestedWithScriptTests : IDisposable
{
    private readonly string matrixPath = Path.Combine(Path.GetTempPath(), $"library-matrix-{Guid.NewGuid():N}.json");

    private const string Matrix = """
        {
          "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0",
          "libraries": [ { "version": "1.1.0", "status": "supported" } ],
          "deprecated": [], "blocked": [],
          "tested": [
            { "server": "0.5.0", "commit": "6386646", "kind": "self-hosted", "library": "1.1.0", "librarySource": "library-manager", "revision": 310500,
              "engine": "26.10.02", "os": "Windows 11 23H2", "database": ["sqlite", "postgres"], "result": "pass", "date": "2026-10-20",
              "evidence": ["sbox-cool/sbox-network-storage-server#54", "https://github.com/sbox-cool/sbox-network-storage/actions/runs/123"] },
            { "server": "0.5.0", "library": "1.0.9", "database": ["sqlite"], "result": "ci-only", "evidence": ["sbox-cool/sbox-network-storage#5"] },
            { "server": "0.5.1", "library": "1.1.0", "database": ["sqlite"], "result": "ci-only", "evidence": ["x/y#1"] },
            { "server": "0.6.0", "commit": "abcdef0123", "kind": "hosted", "library": "1.1.0", "librarySource": "git:abcdef0", "engine": "26.11.01", "os": "Ubuntu 24.04",
              "database": ["postgres"], "result": "fail", "evidence": ["x/y#2"] }
          ]
        }
        """;

    public TestedWithScriptTests() => File.WriteAllText(matrixPath, Matrix);

    public void Dispose() => File.Delete(matrixPath);

    private static string? FindPowerShell()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : new[] { "pwsh" })
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private (int ExitCode, string Output) Run(params string[] arguments)
    {
        var shell = FindPowerShell();
        Skip.If(shell is null, "PowerShell is not installed on this machine.");
        var info = new ProcessStartInfo(shell!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(CompatTestPaths.RepoRoot, "scripts", "compat-tested-with.ps1") }.Concat(arguments))
            info.ArgumentList.Add(argument);
        if (OperatingSystem.IsWindows()) { info.ArgumentList.Insert(0, "Bypass"); info.ArgumentList.Insert(0, "-ExecutionPolicy"); }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Replace("\r\n", "\n"));
    }

    [SkippableFact]
    public void FixtureMatrixIsValidAgainstTheSchema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(CompatTestPaths.Schema), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var result = schema.Evaluate(JsonDocument.Parse(Matrix).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid);
    }

    [SkippableFact]
    public void PrintsTheTableForTheGivenServerVersion()
    {
        var (exit, output) = Run("-Version", "0.5.0", "-Matrix", matrixPath);

        Assert.Equal(0, exit);
        Assert.StartsWith("### Tested with\n\n| Library | Revision | Engine | OS | Server kind | Database | Result | Evidence |\n|---|---|---|---|---|---|---|---|\n", output);
        Assert.Contains("| 1.1.0 | 310500 | 26.10.02 | Windows 11 23H2 | self-hosted | sqlite, postgres | pass | [sbox-cool/sbox-network-storage-server#54](https://github.com/sbox-cool/sbox-network-storage-server/issues/54), <https://github.com/sbox-cool/sbox-network-storage/actions/runs/123> |", output);
        Assert.Contains("| 1.0.9 | - | - | - | - | sqlite | ci-only | [sbox-cool/sbox-network-storage#5](https://github.com/sbox-cool/sbox-network-storage/issues/5) |", output);
        Assert.True(output.IndexOf("| 1.1.0 |", StringComparison.Ordinal) < output.IndexOf("| 1.0.9 |", StringComparison.Ordinal), "newest library first");
        Assert.DoesNotContain("0.5.1", output);
        Assert.DoesNotContain("26.11.01", output);
        Assert.DoesNotContain("Warning", output);
    }

    [SkippableFact]
    public void MatchesRowsRecordedAgainstTheReleaseCommit()
    {
        var (_, byCommit) = Run("-Version", "0.6.0-nonexistent", "-Commit", "abcdef01", "-Matrix", matrixPath);
        Assert.Contains("| 1.1.0 | - | 26.11.01 | Ubuntu 24.04 | hosted | postgres | fail |", byCommit);
        Assert.Contains("Warning", byCommit);   // a fail is not a pass for the recommended library
    }

    [SkippableFact]
    public void ReleaseWithNoRowsSaysSoAndWarns()
    {
        var (exit, output) = Run("-Version", "9.9.9", "-Matrix", matrixPath);
        Assert.Equal(0, exit);
        Assert.Contains("No test evidence is recorded for server 9.9.9", output);
        Assert.Contains("Warning", output);
    }

    [SkippableFact]
    public void GateAcceptsMatchingCiOnlyEvidenceAndFailsWithoutAnyEvidence()
    {
        // A pass row for the recommended library: the gate passes without the CI-only banner.
        var (passExit, passOutput) = Run("-Version", "0.5.0", "-Gate", "-Matrix", matrixPath);
        Assert.Equal(0, passExit);
        Assert.DoesNotContain("**CI-only release:**", passOutput);

        // Only a matching-commit ci-only row for the recommended library: the gate passes and says so.
        var (ciExit, ciOutput) = Run("-Version", "0.5.1", "-Gate", "-Matrix", matrixPath);
        Assert.Equal(0, ciExit);
        Assert.Contains("**CI-only release:**", ciOutput);

        // No evidence at all for the release: the gate fails.
        Assert.Equal(2, Run("-Version", "9.9.9", "-Gate", "-Matrix", matrixPath).ExitCode);
    }

    [SkippableFact]
    public void ShippedMatrixWorksWithTheScript()
    {
        var (exit, output) = Run("-Version", "0.5.0");
        Assert.Equal(0, exit);
        Assert.StartsWith("### Tested with", output);
    }
}
