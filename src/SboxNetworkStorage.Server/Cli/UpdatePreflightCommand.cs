using System.Text.Json;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// Compatibility preflight for an update: the release's signed <c>release.json</c> test evidence, checked against the
/// library builds this server served in the last <see cref="WindowDays"/> days. Only a published <c>fail</c> blocks an
/// install; missing evidence or an unreadable census only warns.
/// </summary>
internal static class UpdatePreflightCommand
{
    public const int WindowDays = 30;

    /// <summary><c>sbox-ns update observed-libraries --json</c>: the service user's view of the census, read by the preflight.</summary>
    public static async Task<int> RunObservedAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        if (await RuntimeCommand.TryRunAsync(config, ["update", "observed-libraries", "--json"]) is { } exitCode)
        {
            return exitCode;
        }

        await using var services = CliServices.Build(config);
        var today = DateOnly.FromDateTime(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime).DayNumber;
        var observed = await services.GetRequiredService<IClientVersionStore>().ReadObservedLibrariesAsync(today - WindowDays + 1, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(observed, ReleaseCompatibility.Json));
        return CliApp.Ok;
    }

    /// <summary>
    /// Downloads and verifies the release's evidence, then reviews it. Null when there is nothing to review (no evidence
    /// published, or it cannot be read): the caller proceeds, because the install verifies the same signed SHA256SUMS anyway.
    /// </summary>
    public static async Task<CompatibilityReview?> ReviewAsync(HttpClient http, ReleaseFeed feed, string version, IUpdateHost host,
        string binary, IReadOnlyList<ServerInstance> instances)
    {
        var work = Directory.CreateTempSubdirectory("sbox-ns-preflight-");
        try
        {
            var sums = Path.Combine(work.FullName, "SHA256SUMS");
            var signature = Path.Combine(work.FullName, ReleaseSignature.AssetName);
            await UpdateCommands.DownloadAsync(http, feed.AssetUrl(version, "SHA256SUMS"), sums);
            await UpdateCommands.DownloadRequiredAsync(http, feed.AssetUrl(version, ReleaseSignature.AssetName), signature);
            UpdateCommands.VerifyReleaseSignature(sums, signature, feed.Trust);
            var listed = File.ReadAllLines(sums)
                .Select(line => line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
                .Any(parts => parts.Length == 2 && parts[1].TrimStart('*').Trim() == "release.json");
            if (!listed)
            {
                Console.WriteLine($"No compatibility evidence is published for {version}.");
                return null;
            }

            var evidence = Path.Combine(work.FullName, "release.json");
            await UpdateCommands.DownloadAsync(http, feed.AssetUrl(version, "release.json"), evidence);
            UpdateCommands.VerifyChecksum(evidence, "release.json", sums);
            var tested = ReleaseCompatibility.ParseTestedLibraries(await File.ReadAllTextAsync(evidence));
            if (tested is null)
            {
                Console.WriteLine($"No compatibility evidence is published for {version} (release.json has no testedLibraries).");
                return null;
            }

            var observed = new List<ObservedLibrary>();
            foreach (var instance in instances)
            {
                var rows = await host.ReadObservedLibrariesAsync(binary, instance, CancellationToken.None);
                if (rows is null)
                {
                    Console.Error.WriteLine($"warning: cannot read the library mix of {instance.Name}; the compatibility check is skipped.");
                    return null;
                }

                observed.AddRange(rows);
            }

            var merged = observed
                .GroupBy(library => (library.LibraryVersion, library.LibraryRevision))
                .Select(group => new ObservedLibrary(group.Key.LibraryVersion, group.Key.LibraryRevision, group.Sum(library => library.Requests)))
                .ToList();
            return ReleaseCompatibility.Review(tested, merged);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or CliException)
        {
            Console.Error.WriteLine($"warning: compatibility evidence for {version} is unavailable ({ex.Message}); continuing without it.");
            return null;
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
