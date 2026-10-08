using SboxNetworkStorage.Parity.Compare;
using SboxNetworkStorage.Parity.Corpus;
using SboxNetworkStorage.Parity.Replay;
using SboxNetworkStorage.Parity.Results;

namespace SboxNetworkStorage.Parity.Cli;

public static class Commands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;

    private const string DefaultCorpus = "tests/parity/corpus";

    public static async Task<int> RunAsync(Options options)
    {
        var replay = Replay.FromOptions(options);
        var output = options.Optional("out");
        options.RejectUnknown();
        var results = await replay.ExecuteAsync();
        if (output is not null)
        {
            results.Save(Path.GetFullPath(output));
            Console.WriteLine($"wrote {output}");
        }

        return PrintRunSummary(results) == 0 ? Ok : Failed;
    }

    public static int Diff(Options options)
    {
        var stablePath = Options.ResolvePath(options.Required("stable"));
        var candidatePath = Options.ResolvePath(options.Required("candidate"));
        var allowPath = options.Optional("allow") is { } allow ? Options.ResolvePath(allow) : null;
        options.RejectUnknown();

        var stable = RunResults.Load(stablePath);
        var candidate = RunResults.Load(candidatePath);
        var mismatches = ResultComparer.Compare(stable, candidate, IntentionalDifferences.Load(allowPath));
        Console.WriteLine($"stable:    {stablePath} ({stable.Target}, {stable.ServerVersion ?? "unknown version"})");
        Console.WriteLine($"candidate: {candidatePath} ({candidate.Target}, {candidate.ServerVersion ?? "unknown version"})");
        return Report(mismatches, stable, "stable", "candidate");
    }

    public static async Task<int> CheckAsync(Options options)
    {
        var expectPath = options.Optional("expect") is { } expect ? Options.ResolvePath(expect) : null;
        var allowPath = options.Optional("allow") is { } allow ? Options.ResolvePath(allow) : null;
        var golden = expectPath is null ? null : GoldenStore.Load(expectPath);
        var replay = Replay.FromOptions(options);
        var output = options.Optional("out");
        options.RejectUnknown();
        var results = await replay.ExecuteAsync();
        if (output is not null)
        {
            results.Save(Path.GetFullPath(output));
        }

        var expectationFailures = PrintRunSummary(results);
        if (golden is null)
        {
            return expectationFailures == 0 ? Ok : Failed;
        }
        var mismatches = ResultComparer.Compare(golden, results, IntentionalDifferences.Load(allowPath));

        Console.WriteLine($"golden expectations: {expectPath}");
        var reportCode = Report(mismatches, golden, "expected", "actual");
        return expectationFailures > 0 || reportCode != Ok ? Failed : Ok;
    }

    public static int Snapshot(Options options)
    {
        var from = Options.ResolvePath(options.Required("from"));
        var output = Path.GetFullPath(options.Required("out"));
        options.RejectUnknown();

        var results = RunResults.Load(from);
        var failing = results.Scenarios.SelectMany(s => s.Steps)
            .Count(s => s.ExpectationFailures is { Count: > 0 } || s.Response is null || s.Response.Status >= 500);
        if (failing > 0)
        {
            Console.Error.WriteLine($"refusing to snapshot: {failing} step(s) in {from} failed expectations, had no response, or returned 5xx");
            return Failed;
        }

        GoldenStore.Save(results, output);
        Console.WriteLine($"wrote golden expectations for {results.Scenarios.Count} scenario(s) to {output}");
        return Ok;
    }

    private sealed record Replay(Uri Target, TemplateContext Context, IReadOnlyList<CorpusFile> Corpus, TimeSpan Timeout)
    {
        public static Replay FromOptions(Options options) => new(
            new Uri(options.Required("target").TrimEnd('/') + "/"),
            new TemplateContext(
                options.Required("project-id"),
                options.Required("public-key"),
                options.Required("secret-key"),
                options.Optional("steam-id", "76561198000000001"),
                options.Optional("steam-id-2", "76561198000000002")),
            CorpusLoader.Load(Options.ResolvePath(options.Optional("corpus", DefaultCorpus))),
            TimeSpan.FromSeconds(int.Parse(options.Optional("timeout-seconds", "60"), System.Globalization.CultureInfo.InvariantCulture)));

        public async Task<RunResults> ExecuteAsync()
        {
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
            using var http = new HttpClient(handler) { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("sbox-ns-parity/1.0");
            return await new ScenarioRunner(http, Target, Context, Console.Out).RunAsync(Corpus, CancellationToken.None);
        }
    }

    /// <summary>Prints scenario/step counts, 5xx responses and expectation failures; returns the failure count.</summary>
    private static int PrintRunSummary(RunResults results)
    {
        var steps = results.Scenarios.SelectMany(s => s.Steps.Select(step => (Scenario: s.Name, Step: step))).ToList();
        var serverErrors = steps.Where(s => s.Step.Response is { Status: >= 500 }).ToList();
        var failures = steps.Where(s => s.Step.ExpectationFailures is { Count: > 0 }).ToList();

        Console.WriteLine();
        Console.WriteLine($"{results.Scenarios.Count} scenario(s), {steps.Count} step(s); {serverErrors.Count} 5xx response(s); {failures.Count} step(s) failed expectations");
        foreach (var (scenario, step) in serverErrors)
        {
            Console.WriteLine($"  5xx {scenario}/{step.Id}: {ResultComparer.DescribeRequest(step.Request)}");
            Console.WriteLine($"    response: {ResultComparer.Describe(step.Response)}");
        }

        foreach (var (scenario, step) in failures)
        {
            Console.WriteLine($"  FAIL {scenario}/{step.Id}: {string.Join("; ", step.ExpectationFailures!)}");
            Console.WriteLine($"    request: {ResultComparer.DescribeRequest(step.Request)}");
            Console.WriteLine($"    response: {ResultComparer.Describe(step.Response)}");
        }

        return failures.Count + serverErrors.Count;
    }

    private static int Report(List<StepMismatch> mismatches, RunResults reference, string leftLabel, string rightLabel)
    {
        var compared = reference.Scenarios.Sum(s => s.Steps.Count);
        var unlisted = mismatches.Where(m => m.Unlisted.Count > 0).ToList();
        var allowedOnly = mismatches.Count(m => m.Unlisted.Count == 0);

        foreach (var mismatch in unlisted)
        {
            Console.WriteLine();
            Console.WriteLine($"✗ {mismatch.Scenario} / {mismatch.Step}");
            if (mismatch.Request is not null)
            {
                Console.WriteLine($"  request: {ResultComparer.DescribeRequest(mismatch.Request)}");
            }

            if (mismatch.Stable is not null || mismatch.Candidate is not null)
            {
                Console.WriteLine($"  {leftLabel}: {ResultComparer.Describe(mismatch.Stable)}");
                Console.WriteLine($"  {rightLabel}: {ResultComparer.Describe(mismatch.Candidate)}");
            }

            Console.WriteLine("  differences:");
            foreach (var difference in mismatch.Unlisted)
            {
                Console.WriteLine($"    {difference}");
            }

            foreach (var (difference, reason) in mismatch.Allowed)
            {
                Console.WriteLine($"    (allowed: {reason}) {difference}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"compared {reference.Scenarios.Count} scenario(s), {compared} step(s): " +
            $"{unlisted.Count} step(s) with unlisted differences, {allowedOnly} step(s) with only intentional differences");
        return unlisted.Count == 0 ? Ok : Failed;
    }
}
