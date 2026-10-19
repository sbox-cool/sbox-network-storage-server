using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.GameLogs;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace SboxNetworkStorage.Server.Tests.GameLogs;

/// <summary>
/// Throughput check for epic #111 (L1): 10,000 entries/min sustained on SQLite with no drops.
/// Gated by NS_GAMELOG_BENCH=1 because it takes about a minute; it is not a CI gate.
/// Entries go through the real HTTP ingest path, the bounded queue and the batch writer.
/// </summary>
public sealed class GameLogThroughputBenchmark(ITestOutputHelper output)
{
    private const string Route = "/v3/game-logs";
    private const string Actor = "76561198000000001";
    private const int BatchEntries = 500;
    private const int TargetPerMinute = 10_000;
    private const int PacedMinutes = 1;

    [SkippableFact]
    public async Task Sustains_ten_thousand_entries_per_minute_on_sqlite_without_drops()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("NS_GAMELOG_BENCH") == "1",
            "Set NS_GAMELOG_BENCH=1 to run the game-log throughput benchmark.");

        using var factory = new SqliteHostFactory();
        var project = await factory.CreateProjectAsync(requireSboxAuth: true);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
            // Raise the project budget so the benchmark measures the writer, not the rate limiter.
            await projects.UpdateProjectSettingsAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "game-logs",
                new Dictionary<string, string>
                {
                    ["gameLogsEnabled"] = "on",
                    ["gameLogs_default_enabled"] = "on",
                    ["gameLogsRateLimitPerMinute"] = "1000000",
                }, CancellationToken.None);
        }

        using var client = factory.CreateClient();
        var queue = factory.Services.GetRequiredService<GameLogQueue>();
        var batchesPerMinute = TargetPerMinute / BatchEntries;
        var host = $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} logical CPUs";
        output.WriteLine($"Host: {host}; SQLite file store; in-process HTTP to the real ingest pipeline.");

        // Phase 1: paced at the target rate; one 500-entry batch every 3 s for PacedMinutes.
        var sent = 0;
        var paced = Stopwatch.StartNew();
        var interval = TimeSpan.FromMinutes(1) / batchesPerMinute;
        for (var i = 0; i < PacedMinutes * batchesPerMinute; i++)
        {
            var wait = interval * i - paced.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            await PostAsync(client, project, sent);
            sent += BatchEntries;
        }

        var pacedPosted = paced.Elapsed;
        var pacedTotal = sent;
        await WaitForWrittenAsync(queue, project.ProjectId, pacedTotal, TimeSpan.FromMinutes(2));
        var pacedWritten = paced.Elapsed;
        var afterPaced = queue.Snapshot(project.ProjectId);
        output.WriteLine($"Paced: {pacedTotal} entries posted over {pacedPosted.TotalSeconds:F1}s " +
                         $"(target {TargetPerMinute}/min); all written at {pacedWritten.TotalSeconds:F1}s " +
                         $"= {pacedTotal / pacedWritten.TotalMinutes:F0} entries/min end-to-end; " +
                         $"dropped={afterPaced.DroppedQueueFull}, writeFailed={afterPaced.WriteFailed}, rejected={afterPaced.Rejected}.");

        // Phase 2: the same volume back to back, to see the ceiling and the drop counter under load.
        var burst = Stopwatch.StartNew();
        for (var i = 0; i < batchesPerMinute; i++)
        {
            await PostAsync(client, project, sent);
            sent += BatchEntries;
        }

        await WaitForWrittenAsync(queue, project.ProjectId, sent, TimeSpan.FromMinutes(2));
        var burstWritten = burst.Elapsed;
        var final = queue.Snapshot(project.ProjectId);
        var burstTotal = sent - pacedTotal;
        output.WriteLine($"Burst: {burstTotal} entries back to back; all written at {burstWritten.TotalSeconds:F1}s " +
                         $"= {burstTotal / burstWritten.TotalMinutes:F0} entries/min end-to-end; " +
                         $"dropped={final.DroppedQueueFull - afterPaced.DroppedQueueFull}, " +
                         $"writeFailed={final.WriteFailed - afterPaced.WriteFailed}.");

        Assert.Equal(0, afterPaced.DroppedQueueFull);
        Assert.Equal(0, final.DroppedQueueFull);
        Assert.Equal(0, final.WriteFailed);
        Assert.Equal(sent, final.Written);
    }

    private static async Task PostAsync(HttpClient client, SelfHostProject project, int offset)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Route}/{project.ProjectId}")
        {
            Content = new StringContent(BuildBatch(offset), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", project.SecretKey);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted && !body.Contains("\"code\""),
            $"{(int)response.StatusCode}: {body[..Math.Min(400, body.Length)]}");
    }

    private static string BuildBatch(int offset)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < BatchEntries; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"event\":\"client.bench\",\"actor\":\"").Append(Actor)
              .Append("\",\"attrs\":{\"team\":\"red\",\"n\":").Append(offset + i).Append("}}");
        }

        return sb.Append(']').ToString();
    }

    private static async Task WaitForWrittenAsync(GameLogQueue queue, string projectId, int expected, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (queue.Snapshot(projectId).Written < expected)
        {
            if (clock.Elapsed > timeout)
                throw new TimeoutException($"Only {queue.Snapshot(projectId).Written} of {expected} entries were written in {timeout}: {queue.Snapshot(projectId)}.");
            await Task.Delay(20);
        }
    }
}
