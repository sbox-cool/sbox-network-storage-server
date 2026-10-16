using System.Diagnostics;
using System.Reflection;
using System.Text;
using SboxNetworkStorage.EndpointSpike.Broker;
using SboxNetworkStorage.EndpointSpike.Plan;
using SboxNetworkStorage.EndpointSpike.Runtime;

namespace SboxNetworkStorage.EndpointSpike.Sandbox;

public sealed record SandboxStats(long WallMs, long CpuMs, long PeakRssBytes, long ProtocolBytes);

/// <summary>
/// Outcome of one sandboxed run. Exactly one of these holds: <see cref="LimitCode"/> is set (supervisor kill or broker
/// budget), <see cref="Error"/> is set (protocol or worker failure), or <see cref="Ok"/>/<see cref="Status"/>/<see cref="Body"/>
/// carry the executor result. Writes are returned for the caller to commit only when <see cref="Ok"/> is true.
/// </summary>
public sealed record SandboxResult(
    bool Ok,
    int Status,
    object? Body,
    IReadOnlyList<PendingWrite> Writes,
    string? LimitCode,
    string? Error,
    SandboxStats Stats)
{
    public bool Completed => LimitCode is null && Error is null;
}

/// <summary>
/// Runs the worker as a child process. Enforces CPU, RSS and wall budgets by polling the child and killing its tree
/// on breach, bounds each protocol line, and answers reads through the broker. This is process supervision, not an
/// OS sandbox: no filesystem, network, or syscall isolation is applied or certified.
/// </summary>
public static class Supervisor
{
    private static readonly string[] EnvironmentAllowlist = ["PATH", "DOTNET_ROOT", "TMPDIR"];

    public static async Task<SandboxResult> RunAsync(
        Dictionary<string, object?> wireRequest,
        RecordBroker broker,
        string? diagnostic,
        CancellationToken ct,
        WorkerLaunch? launch = null)
    {
        var inputBytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(wireRequest["input"]));
        if (inputBytes > RuntimeLimits.MaxInputBytes)
        {
            return Limited(RuntimeLimits.CodeInput, $"Input of {inputBytes} bytes exceeds the input budget.", new SandboxStats(0, 0, 0, 0));
        }

        var psi = BuildStartInfo(diagnostic, launch);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Worker process failed to start.");
        var state = new MonitorState();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorAsync(process, clock, state, stop.Token);
        var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);

        var reader = new BoundedLineReader(process.StandardOutput.BaseStream, RuntimeLimits.MaxProtocolLineBytes);
        long protocolBytes = 0;
        SandboxResult? result = null;
        string? error = null;

        try
        {
            await WriteLineAsync(process, CanonicalJson.Serialize(wireRequest), ct);
            while (result is null)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }
                protocolBytes += Encoding.UTF8.GetByteCount(line);
                var message = PlanReader.ParseObject(line);
                switch (message["t"] as string)
                {
                    case "read":
                        var value = await broker.ReadAsync(
                            RequireString(message, "collection"), RequireString(message, "key"), ct);
                        await WriteLineAsync(process, CanonicalJson.Serialize(
                            new Dictionary<string, object?> { ["t"] = "record", ["value"] = value }), ct);
                        break;
                    case "result":
                        result = ParseResult(message, broker, state, clock, protocolBytes);
                        break;
                    case "error":
                        error = $"{message["code"]}: {message["message"]}";
                        break;
                    default:
                        error = "Unknown protocol message type.";
                        break;
                }
                if (error is not null)
                {
                    break;
                }
            }
        }
        catch (BudgetExceededException ex)
        {
            state.Breach(ex.Code, process);
        }
        catch (ProtocolLimitException)
        {
            state.Breach(RuntimeLimits.CodeOutput, process);
        }
        catch (IOException)
        {
            error ??= "WORKER_IO_FAILED";
        }
        catch (FormatException ex)
        {
            error = $"PROTOCOL_INVALID: {ex.Message}";
        }

        if (!process.HasExited)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Worker already gone; the monitor or exit wait below settles the outcome.
            }
        }
        await process.WaitForExitAsync(ct);
        stop.Cancel();
        await monitor;
        await stderrDrain;

        var stats = new SandboxStats(clock.ElapsedMilliseconds, state.CpuMs(process), state.PeakRss, protocolBytes);
        if (state.Code is not null)
        {
            return Limited(state.Code, state.Message ?? "Limit breached.", stats);
        }
        if (error is not null)
        {
            return Failed(error, stats);
        }
        if (result is null)
        {
            return Failed("WORKER_EXITED_WITHOUT_RESULT", stats);
        }
        return result with { Stats = stats };
    }

    private static ProcessStartInfo BuildStartInfo(string? diagnostic, WorkerLaunch? launch)
    {
        var host = launch?.FileName ?? Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
        var psi = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        if (launch is null && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Entry assembly unavailable."));
        }
        foreach (var prefix in launch?.PrefixArguments ?? [])
        {
            psi.ArgumentList.Add(prefix);
        }
        psi.ArgumentList.Add("--worker");
        if (diagnostic is not null)
        {
            psi.ArgumentList.Add("--worker-diagnostic");
            psi.ArgumentList.Add(diagnostic);
        }

        psi.Environment.Clear();
        foreach (var key in EnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
            {
                psi.Environment[key] = value;
            }
        }
        psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return psi;
    }

    private static SandboxResult ParseResult(
        Dictionary<string, object?> message,
        RecordBroker broker,
        MonitorState state,
        System.Diagnostics.Stopwatch clock,
        long protocolBytes)
    {
        var ok = message["ok"] is true;
        var status = message["status"] is double s ? (int)s : 500;
        var body = message["body"];
        var writes = ParseWrites(message["writes"]);

        var bodyBytes = Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(body));
        if (bodyBytes > RuntimeLimits.MaxOutputBytes)
        {
            return Limited(RuntimeLimits.CodeOutput, $"Output of {bodyBytes} bytes exceeds the output budget.",
                new SandboxStats(clock.ElapsedMilliseconds, 0, state.PeakRss, protocolBytes));
        }
        if (ok)
        {
            broker.Accept(writes);
        }

        return new SandboxResult(ok, status, body, ok ? writes : [], null, null,
            new SandboxStats(clock.ElapsedMilliseconds, 0, state.PeakRss, protocolBytes));
    }

    private static List<PendingWrite> ParseWrites(object? raw)
    {
        var writes = new List<PendingWrite>();
        if (raw is not List<object?> items)
        {
            return writes;
        }
        foreach (var item in items)
        {
            var entry = item as Dictionary<string, object?> ?? throw new FormatException("Write entry must be an object.");
            var data = entry["data"] as Dictionary<string, object?> ?? throw new FormatException("Write data must be an object.");
            writes.Add(new PendingWrite(RequireString(entry, "collection"), RequireString(entry, "key"), data));
        }
        return writes;
    }

    private static string RequireString(Dictionary<string, object?> message, string name) =>
        message.TryGetValue(name, out var value) && value is string text
            ? text
            : throw new FormatException($"Protocol field '{name}' must be a string.");

    private static async Task WriteLineAsync(Process process, string line, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    private static SandboxResult Limited(string code, string message, SandboxStats stats) =>
        new(false, 0, null, [], code, message, stats);

    private static SandboxResult Failed(string error, SandboxStats stats) =>
        new(false, 0, null, [], null, error, stats);

    private static async Task MonitorAsync(Process process, System.Diagnostics.Stopwatch clock, MonitorState state, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }
                process.Refresh();
                var cpuMs = (long)process.TotalProcessorTime.TotalMilliseconds;
                var rss = process.WorkingSet64;
                state.Observe(rss);
                if (cpuMs > RuntimeLimits.WorkerCpuMs)
                {
                    state.Breach(RuntimeLimits.CodeCpu, process, $"CPU {cpuMs} ms exceeds {RuntimeLimits.WorkerCpuMs} ms.");
                    return;
                }
                if (rss > RuntimeLimits.WorkerRssBytes)
                {
                    state.Breach(RuntimeLimits.CodeRss, process, $"RSS {rss} bytes exceeds {RuntimeLimits.WorkerRssBytes} bytes.");
                    return;
                }
                if (clock.ElapsedMilliseconds > RuntimeLimits.WorkerWallMs)
                {
                    state.Breach(RuntimeLimits.CodeWall, process, $"Wall time exceeds {RuntimeLimits.WorkerWallMs} ms.");
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    /// <summary>Shared between the monitor and the IPC loop. First breach wins.</summary>
    private sealed class MonitorState
    {
        private readonly object _gate = new();
        private long _peakRss;

        public string? Code { get; private set; }
        public string? Message { get; private set; }
        public long PeakRss { get { lock (_gate) { return _peakRss; } } }

        public void Observe(long rss)
        {
            lock (_gate)
            {
                if (rss > _peakRss)
                {
                    _peakRss = rss;
                }
            }
        }

        public void Breach(string code, Process process, string? message = null)
        {
            lock (_gate)
            {
                if (Code is not null)
                {
                    return;
                }
                Code = code;
                Message = message ?? code;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        /// <summary>Reads CPU after exit so a breach that completed between polls is still charged.</summary>
        public long CpuMs(Process process)
        {
            try
            {
                var cpu = (long)process.TotalProcessorTime.TotalMilliseconds;
                if (cpu > RuntimeLimits.WorkerCpuMs && Code is null)
                {
                    Code = RuntimeLimits.CodeCpu;
                    Message = $"CPU {cpu} ms exceeds {RuntimeLimits.WorkerCpuMs} ms.";
                }
                return cpu;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }
}
