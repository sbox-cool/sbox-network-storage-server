using System.Text.Json;

namespace SboxNetworkStorage.Parity.Results;

/// <summary>
/// Golden expectations: the reviewed normalized responses, one file per corpus
/// file (same name), so corpus and golden changes stay side by side in review.
/// </summary>
public static class GoldenStore
{
    public static void Save(RunResults results, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var group in results.Scenarios.GroupBy(s => s.File))
        {
            var golden = new RunResults
            {
                Scenarios = group.Select(s => new ScenarioResult
                {
                    Name = s.Name,
                    File = s.File,
                    Steps = s.Steps.Select(step => new StepResult
                    {
                        Id = step.Id,
                        Request = new RecordedRequest { Method = step.Request.Method, Path = step.Request.Path },
                        Response = step.Response,
                        Error = step.Error,
                    }).ToList(),
                }).ToList(),
            };
            golden.Save(Path.Combine(directory, group.Key));
        }
    }

    public static RunResults Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new ParityException($"expectations folder not found: {directory}");
        }

        var merged = new RunResults();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var golden = RunResults.Load(path);
            foreach (var scenario in golden.Scenarios)
            {
                scenario.File = Path.GetFileName(path);
                merged.Scenarios.Add(scenario);
            }
        }

        if (merged.Scenarios.Count == 0)
        {
            throw new ParityException($"expectations folder has no golden files: {directory}");
        }

        return merged;
    }
}
