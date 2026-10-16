using System.Text;
using SboxNetworkStorage.EndpointSpike.Compiler;
using SboxNetworkStorage.EndpointSpike.Plan;
using SboxNetworkStorage.EndpointSpike.Runner;
using SboxNetworkStorage.EndpointSpike.Sandbox;
using SboxNetworkStorage.EndpointSpike.Stores;

// Payload ceiling for the spike store. Raised above the production 64 KiB default so the
// materialized-bytes budget (4 MiB) is reachable from a real record read.
const int SpikePayloadBytes = 8 * 1024 * 1024;

return await Dispatch(args);

static async Task<int> Dispatch(string[] args)
{
    if (args.Length == 0)
    {
        return Usage();
    }

    switch (args[0])
    {
        case "--worker":
            return await RunWorkerAsync(args);
        case "compile" when args.Length == 2:
            return Compile(args[1]);
        case "scenarios" when args.Length == 4:
            return await RunScenariosAsync(args[1], args[2], args[3]);
        default:
            return Usage();
    }
}

static async Task<int> RunWorkerAsync(string[] args)
{
    string? diagnostic = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--worker-diagnostic" && i + 1 < args.Length)
        {
            diagnostic = args[++i];
        }
        else
        {
            return Usage();
        }
    }
    if (diagnostic is not null && Array.IndexOf(Worker.DiagnosticModes, diagnostic) < 0)
    {
        Console.Error.WriteLine($"unknown diagnostic mode: {diagnostic}");
        return 2;
    }
    return await Worker.RunAsync(diagnostic);
}

static int Compile(string path)
{
    var outcome = NsEndpointCompiler.Compile(File.ReadAllText(path, Encoding.UTF8));
    if (outcome.Ok)
    {
        var endpoint = outcome.Endpoint!;
        Console.WriteLine(CanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["endpointId"] = endpoint.EndpointId,
            ["languageId"] = CompiledEndpoint.LanguageId,
            ["compilerVersion"] = CompiledEndpoint.CompilerVersion,
            ["sourceSha256"] = endpoint.SourceSha256,
            ["planSha256"] = endpoint.PlanSha256,
            ["staticOps"] = (double)endpoint.StaticOps,
            ["plan"] = endpoint.Plan,
        }));
        return 0;
    }

    var error = outcome.Error!;
    Console.WriteLine(CanonicalJson.Serialize(new Dictionary<string, object?>
    {
        ["ok"] = false,
        ["code"] = error.Code,
        ["message"] = error.Message,
        ["line"] = (double)error.Line,
        ["column"] = (double)error.Column,
    }));
    return 1;
}

static async Task<int> RunScenariosAsync(string storeFlag, string target, string path)
{
    var outcome = NsEndpointCompiler.Compile(File.ReadAllText(path, Encoding.UTF8));
    if (!outcome.Ok)
    {
        return Compile(path);
    }
    var compiled = outcome.Endpoint!;

    var opened = storeFlag switch
    {
        "--sqlite" => await StoreFactory.OpenSqliteAsync(target, SpikePayloadBytes, CancellationToken.None),
        "--postgres" => await StoreFactory.OpenPostgresAsync(target, SpikePayloadBytes, CancellationToken.None),
        _ => null,
    };
    if (opened is null)
    {
        return Usage();
    }

    var (evidence, allPassed) = await Harness.RunAllAsync(opened, compiled, CancellationToken.None);
    foreach (var line in evidence)
    {
        Console.WriteLine(CanonicalJson.Serialize(line));
    }
    Console.Error.WriteLine(allPassed ? "scenarios: all passed" : "scenarios: FAILED");
    return allPassed ? 0 : 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  sbox-ns-endpoint-spike compile <file.ns.js>");
    Console.Error.WriteLine("  sbox-ns-endpoint-spike scenarios (--sqlite <path> | --postgres <host[:port]/db>) <file.ns.js>");
    Console.Error.WriteLine("  sbox-ns-endpoint-spike --worker [--worker-diagnostic <spin|allocate|sleep|flood>]");
    return 2;
}
