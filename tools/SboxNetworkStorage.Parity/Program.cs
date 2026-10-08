using SboxNetworkStorage.Parity;
using SboxNetworkStorage.Parity.Cli;

const string Help = """
    sbox-ns-parity: differential replay harness for Network Storage servers.

    Modes:
      run      --target URL --project-id ID --public-key K --secret-key S
               [--corpus tests/parity/corpus] [--steam-id ID] [--steam-id-2 ID] [--out results.json]
               Replays every corpus scenario in order against one server and records normalized responses.

      diff     --stable results-a.json --candidate results-b.json [--allow tests/parity/intentional-differences.json]
               Compares two runs; prints request, both normalized responses and a JSON diff for each
               mismatch. Exits 1 on any difference not listed in --allow.

      check    --target URL --project-id ID --public-key K --secret-key S [--expect tests/parity/expectations]
               [--corpus ...] [--allow ...] [--out results.json]
               Replays and asserts corpus status codes, required fields and values. If --expect is supplied,
               also compares reviewed golden normalized responses. Exits 1 on any failure or 5xx.

      snapshot --from results.json --out tests/parity/expectations
               Writes golden expectations from a reviewed run (refuses runs with expectation failures).

    Exit codes: 0 success, 1 differences or failed expectations, 2 usage or connection error.
    """;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine(Help);
    return args.Length == 0 ? Commands.Usage : Commands.Ok;
}

try
{
    var options = new Options(args.Skip(1));
    return args[0] switch
    {
        "run" => await Commands.RunAsync(options),
        "diff" => Commands.Diff(options),
        "check" => await Commands.CheckAsync(options),
        "snapshot" => Commands.Snapshot(options),
        _ => throw new ParityException($"unknown mode '{args[0]}' (run, diff, check, snapshot)"),
    };
}
catch (ParityException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return Commands.Usage;
}
