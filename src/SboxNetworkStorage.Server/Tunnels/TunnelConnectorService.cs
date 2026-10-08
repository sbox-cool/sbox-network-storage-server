using System.Diagnostics;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Tunnels;

public sealed class TunnelConnectorService(EffectiveConfig config, TunnelConnectorState state,
    ILogger<TunnelConnectorService> logger) : BackgroundService
{
    public static ProcessStartInfo StartInfo(string executable, string token)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        // 2026.10.0 tunnelFlags define --no-autoupdate; run accepts TUNNEL_TOKEN.
        info.ArgumentList.Add("--no-autoupdate");
        info.ArgumentList.Add("tunnel");
        info.ArgumentList.Add("run");
        info.Environment["TUNNEL_TOKEN"] = token;
        info.Environment.Remove("TUNNEL_TOKEN_FILE");
        info.Environment.Remove("TUNNEL_CRED_FILE");
        return info;
    }

    public static TimeSpan RestartDelay(int failures) => TimeSpan.FromSeconds(1 << Math.Min(Math.Max(failures - 1, 0), 6));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        try
        {
            if (!config.GetBoolean("tunnel.enabled"))
            {
                state.Set(config, "disabled", null);
                return; // Enabling requires restart to replace the public listener with loopback first.
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                var fresh = ConfigLoader.Load(config.ConfigDirectory, config.DataDirectory);
                if (!fresh.IsValid || !fresh.GetBoolean("tunnel.enabled")
                    || fresh.GetString("server.listen") != config.GetString("server.listen")
                    || fresh.GetString("tunnel.name") != config.GetString("tunnel.name"))
                {
                    state.Set(fresh, "stopped (restart required)", null);
                    return;
                }
                using var process = new Process();
                var began = Stopwatch.GetTimestamp();
                try
                {
                    var path = CloudflaredInstaller.ExecutablePath(config.ConfigDirectory);
                    var token = File.ReadAllText(TunnelManager.TokenPath(config)).Trim();
                    if (token.Length == 0) throw new InvalidDataException("Missing tunnel token.");
                    TunnelFiles.Restrict(TunnelManager.TokenPath(config));
                    process.StartInfo = StartInfo(path, token);
                    using var outputCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
                    if (!process.Start()) throw new InvalidOperationException("Connector did not start.");
                    try
                    {
                        state.Set(config, "running", process.Id);
                        logger.LogInformation("cloudflared {Version} started (PID {Pid})", CloudflaredInstaller.Version, process.Id);
                        stdout = DrainAsync(process.StandardOutput, process.Id, outputCancellation.Token);
                        stderr = DrainAsync(process.StandardError, process.Id, outputCancellation.Token);
                        while (!process.HasExited)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                            var desired = ConfigLoader.Load(config.ConfigDirectory, config.DataDirectory);
                            if (!desired.IsValid || !desired.GetBoolean("tunnel.enabled")
                                || desired.GetString("tunnel.name") != config.GetString("tunnel.name")
                                || desired.GetString("server.listen") != config.GetString("server.listen"))
                            {
                                StopChild(process);
                                state.Set(desired, "stopped (restart required)", null);
                                return;
                            }
                            state.Set(config, state.Current.ConnectorState, process.Id);
                        }
                        await process.WaitForExitAsync(stoppingToken);
                        logger.LogWarning("cloudflared exited with code {ExitCode}; restarting", process.ExitCode);
                    }
                    finally
                    {
                        StopChild(process);
                        outputCancellation.Cancel();
                        try { await Task.WhenAll(stdout, stderr); }
                        catch (OperationCanceledException) { }
                    }
                    if (Stopwatch.GetElapsedTime(began) >= TimeSpan.FromMinutes(1)) failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Child errors and output can contain credential material. Never log their messages.
                    logger.LogWarning("cloudflared could not run ({ErrorType}); retrying", ex.GetType().Name);
                }
                failures = Math.Min(failures + 1, 7);
                state.Set(config, "backoff", null);
                var delay = RestartDelay(failures);
                for (var elapsed = TimeSpan.Zero; elapsed < delay; elapsed += TimeSpan.FromSeconds(2))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    state.Set(config, "backoff", null);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { state.Set(ConfigLoader.Load(config.ConfigDirectory, config.DataDirectory), "stopped", null); }
    }

    private async Task DrainAsync(StreamReader reader, int pid, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // Only recognize a fixed readiness event. All free-form child output is redacted entirely,
            // including decoded token fields, environment dumps, and unknown future credential formats.
            if (line.Contains("Registered tunnel connection", StringComparison.Ordinal))
            {
                state.Set(config, "connected", pid);
                logger.LogInformation("cloudflared registered a tunnel connection");
            }
        }
    }

    private static void StopChild(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException) { }
    }
}
