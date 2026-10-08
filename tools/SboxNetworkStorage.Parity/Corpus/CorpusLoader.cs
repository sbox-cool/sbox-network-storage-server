using System.Text.Json;

namespace SboxNetworkStorage.Parity.Corpus;

public static class CorpusLoader
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>Loads every <c>*.json</c> corpus file in name order (files are numbered to sequence shared state).</summary>
    public static IReadOnlyList<CorpusFile> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new ParityException($"corpus folder not found: {directory}");
        }

        var files = new List<CorpusFile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            CorpusFile? file;
            try
            {
                file = JsonSerializer.Deserialize<CorpusFile>(File.ReadAllText(path), Json.Options);
            }
            catch (JsonException ex)
            {
                throw new ParityException($"{path}: invalid corpus JSON: {ex.Message}");
            }

            if (file is null || file.SchemaVersion != SupportedSchemaVersion)
            {
                throw new ParityException($"{path}: schemaVersion must be {SupportedSchemaVersion}");
            }

            file.FileName = Path.GetFileName(path);
            foreach (var scenario in file.Scenarios)
            {
                Validate(path, scenario);
                if (!names.Add(scenario.Name))
                {
                    throw new ParityException($"{path}: duplicate scenario name '{scenario.Name}'");
                }
            }

            files.Add(file);
        }

        if (files.Count == 0)
        {
            throw new ParityException($"corpus folder has no scenario files: {directory}");
        }

        return files;
    }

    private static void Validate(string path, Scenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            throw new ParityException($"{path}: scenario without a name");
        }

        if (scenario.Steps.Count == 0)
        {
            throw new ParityException($"{path}: scenario '{scenario.Name}' has no steps");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in scenario.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add(step.Id))
            {
                throw new ParityException($"{path}: scenario '{scenario.Name}' has a missing or duplicate step id '{step.Id}'");
            }

            if (!step.Path.StartsWith('/'))
            {
                throw new ParityException($"{path}: {scenario.Name}/{step.Id}: path must start with '/'");
            }

            if (step.Expect?.Status is not (>= 100 and <= 599))
            {
                throw new ParityException($"{path}: {scenario.Name}/{step.Id}: expect.status is required");
            }

            var bodies = (step.Body.HasValue ? 1 : 0) + (step.RawBody is null ? 0 : 1) + (step.GeneratedBody is null ? 0 : 1);
            if (bodies > 1)
            {
                throw new ParityException($"{path}: {scenario.Name}/{step.Id}: use only one of body, rawBody, generatedBody");
            }
        }
    }
}
