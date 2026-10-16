using System.Text;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.EndpointSpike.Plan;

namespace SboxNetworkStorage.EndpointSpike.Sandbox;

/// <summary>
/// Child-process entry. Reads one request line, runs the real <see cref="EndpointStepExecutor"/> with a fixed
/// execution time, answers record reads over the same pipe, and reports writes inside the final result.
/// Stdout carries protocol lines only.
/// </summary>
public static class Worker
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Diagnostic modes exercise the supervisor's kill paths. They are reachable only from the CLI, never from endpoint source.</summary>
    public static readonly string[] DiagnosticModes = ["spin", "allocate", "sleep", "flood"];

    public static async Task<int> RunAsync(string? diagnostic)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };

        var requestLine = input.ReadLine() ?? throw new IOException("Supervisor closed the channel before sending a request.");
        var request = PlanReader.ParseObject(requestLine);

        if (diagnostic is not null)
        {
            RunDiagnostic(diagnostic);
            return 1;
        }

        try
        {
            var plan = (Dictionary<string, object?>)request["plan"]!;
            var writes = new List<object?>();
            var executionTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(double)request["execTimeMs"]!);
            var inputValues = (Dictionary<string, object?>)request["input"]!;
            var gameValues = (Dictionary<string, object?>)request["gameValues"]!;

            var engineRequest = new EndpointExecutionRequest(
                Input: inputValues,
                SteamId: (string)request["steamId"]!,
                PlayerKey: (string)request["playerKey"]!,
                GameValues: gameValues,
                ProjectId: (string)request["projectId"]!,
                UserId: (string)request["userId"]!,
                HasSecretKey: (bool)request["hasSecretKey"]!,
                IsDedicatedServer: (bool)request["isDedicatedServer"]!,
                ExecTime: executionTime,
                ReadRecord: (collection, key) => ReadRecord(input, output, collection, key),
                WriteRecord: (collection, key, data) => writes.Add(new Dictionary<string, object?>
                {
                    ["collection"] = collection,
                    ["key"] = key,
                    ["data"] = data,
                }),
                SkipWebhooks: true,
                SkipSleep: true);

            var result = await new EndpointStepExecutor().ExecuteAsync(plan, engineRequest);
            WriteLine(output, new Dictionary<string, object?>
            {
                ["t"] = "result",
                ["ok"] = result.Ok,
                ["status"] = (double)result.Status,
                ["body"] = result.Body,
                ["writes"] = writes,
            });
            return 0;
        }
        catch (Exception ex) when (ex is not IOException)
        {
            WriteLine(output, new Dictionary<string, object?>
            {
                ["t"] = "error",
                ["code"] = "EXECUTOR_FAULT",
                ["message"] = ex.GetType().Name,
            });
            return 2;
        }
    }

    private static object? ReadRecord(StreamReader input, StreamWriter output, string collection, string key)
    {
        WriteLine(output, new Dictionary<string, object?> { ["t"] = "read", ["collection"] = collection, ["key"] = key });
        var reply = PlanReader.ParseObject(input.ReadLine() ?? throw new IOException("Supervisor closed the channel during a read."));
        if (reply["t"] as string != "record")
        {
            throw new InvalidDataException("Expected a record reply from the supervisor.");
        }
        return reply["value"];
    }

    private static void WriteLine(StreamWriter output, Dictionary<string, object?> message) =>
        output.WriteLine(CanonicalJson.Serialize(message));

    private static void RunDiagnostic(string mode)
    {
        switch (mode)
        {
            case "spin":
                // Pure CPU burn; the supervisor must kill on the CPU budget.
                long counter = 0;
                while (true)
                {
                    counter++;
                }
            case "allocate":
                // Touch every page so the working set grows; the supervisor must kill on the RSS budget.
                var retained = new List<byte[]>();
                while (true)
                {
                    var block = new byte[8 * 1024 * 1024];
                    Array.Fill(block, (byte)1);
                    retained.Add(block);
                }
            case "sleep":
                // Idle past the wall budget; the supervisor must kill on wall time.
                Thread.Sleep(Timeout.Infinite);
                break;
            case "flood":
                // Stream bytes without a newline; the supervisor must kill on the protocol line ceiling.
                using (var stdout = Console.OpenStandardOutput())
                {
                    var chunk = new byte[1024 * 1024];
                    Array.Fill(chunk, (byte)'x');
                    while (true)
                    {
                        stdout.Write(chunk, 0, chunk.Length);
                    }
                }
            default:
                throw new ArgumentException($"Unknown diagnostic mode '{mode}'.");
        }
    }
}
