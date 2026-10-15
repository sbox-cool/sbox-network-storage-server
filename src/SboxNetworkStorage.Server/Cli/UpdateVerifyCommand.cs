using System.Text.Json;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// <c>update verify</c>: the post-update regression verdict for this binary, from the per-library outcome census.
/// Runs as the service user when invoked as root (the database is theirs), like the other database commands.
/// Exit 0 when no library regressed (including insufficient data and no baseline), 5 when one did.
/// </summary>
internal static class UpdateVerifyCommand
{
    public const int RegressionExitCode = 5;
    public const int DefaultHours = PostUpdateVerifier.DefaultWindowHours;
    private const int MaxHours = 168;

    public static async Task<int> RunAsync(CliContext context)
    {
        var hours = ParseHours(context.Args.Option("hours"));
        var json = context.Args.Flag("json");
        var config = context.LoadValidConfig();

        var childArguments = new List<string> { "update", "verify", "--hours", hours.ToString() };
        if (json) childArguments.Add("--json");
        if (await RuntimeCommand.TryRunAsync(config, childArguments) is { } exitCode) return exitCode;

        await using var services = CliServices.Build(config);
        var verifier = new PostUpdateVerifier(services.GetRequiredService<IClientVersionStore>(), services.GetRequiredService<TimeProvider>());
        var verdict = await verifier.VerifyAsync(BuildInfo.Version, hours, VerdictThresholds.Default, CancellationToken.None);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(verdict, PostUpdateVerdict.Json));
        }
        else
        {
            WriteText(verdict);
        }

        return verdict.Status == VerdictStatus.Regression ? RegressionExitCode : CliApp.Ok;
    }

    internal static int ParseHours(string? text)
    {
        if (text is null) return DefaultHours;
        if (!int.TryParse(text, out var hours) || hours < 1 || hours > MaxHours)
            throw new CliException($"--hours must be a whole number from 1 to {MaxHours}", CliApp.Usage);
        return hours;
    }

    private static void WriteText(PostUpdateVerdict verdict)
    {
        Console.WriteLine($"sbox-ns {verdict.Version}: last {verdict.WindowHours} h, baseline {verdict.BaselineVersion ?? "none"} (previous 7 days)");
        Console.WriteLine($"Verdict: {Describe(verdict.Status)}");
        if (verdict.Libraries.Count == 0)
        {
            Console.WriteLine("No library traffic on this binary in the window.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{"Library",-12}{"Requests",10}{"Errors",9}{"Base req",10}{"Base err",10}  Outcome");
        foreach (var library in verdict.Libraries)
        {
            Console.WriteLine($"{library.Library,-12}{library.PostRequests,10}{Percent(library.PostErrorRate),9}{library.BaselineRequests,10}{Percent(library.BaselineErrorRate),10}  {library.Outcome}");
        }

        if (verdict.Status == VerdictStatus.Regression)
        {
            Console.WriteLine();
            Console.WriteLine("Roll back (most data-preserving first):");
            foreach (var step in RollbackAdvice.Steps(verdict.BaselineVersion)) Console.WriteLine("  " + step);
        }
    }

    private static string Describe(VerdictStatus status) => status switch
    {
        VerdictStatus.Regression => "REGRESSION: a library's error rate rose after this update",
        VerdictStatus.Ok => "OK: no regression detected",
        VerdictStatus.InsufficientData => "insufficient data: not enough requests on either side to judge",
        VerdictStatus.NoBaseline => "no baseline: no previous binary has traffic in the last 7 days",
        _ => status.ToString(),
    };

    private static string Percent(decimal rate) => $"{rate * 100m:0.0}%";
}
