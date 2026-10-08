using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Parity.Normalization;

namespace SboxNetworkStorage.Parity.Results;

/// <summary>Normalized replay output of one server (<c>parity run --out</c>).</summary>
public sealed class RunResults
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Informational only; never compared.</summary>
    public string? Target { get; set; }

    /// <summary>Informational only; never compared.</summary>
    public string? ServerVersion { get; set; }

    public List<ScenarioResult> Scenarios { get; set; } = [];

    public static RunResults Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ParityException($"results file not found: {path}");
        }

        var results = JsonSerializer.Deserialize<RunResults>(File.ReadAllText(path), Json.Options)
            ?? throw new ParityException($"{path}: empty results file");
        if (results.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ParityException($"{path}: unsupported results schemaVersion {results.SchemaVersion}");
        }

        return results;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options) + "\n");
    }
}

public sealed class ScenarioResult
{
    public string Name { get; set; } = "";

    public string File { get; set; } = "";

    public List<StepResult> Steps { get; set; } = [];
}

public sealed class StepResult
{
    public string Id { get; set; } = "";

    public RecordedRequest Request { get; set; } = new();

    /// <summary>Null when the request could not be sent (transport failure in <see cref="Error"/>).</summary>
    public NormalizedResponse? Response { get; set; }

    public string? Error { get; set; }

    public List<string>? ExpectationFailures { get; set; }
}

/// <summary>The request as authored in the corpus (templates unexpanded, so no secrets are written).</summary>
public sealed class RecordedRequest
{
    public string Method { get; set; } = "";

    public string Path { get; set; } = "";

    public Dictionary<string, string>? Query { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public JsonNode? Body { get; set; }
}
