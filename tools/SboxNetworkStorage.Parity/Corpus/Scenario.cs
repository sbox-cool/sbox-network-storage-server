using System.Text.Json;
using System.Text.Json.Serialization;

namespace SboxNetworkStorage.Parity.Corpus;

/// <summary>One corpus file: a versioned, ordered list of scenarios.</summary>
public sealed class CorpusFile
{
    public int SchemaVersion { get; set; }

    public string? Description { get; set; }

    public List<Scenario> Scenarios { get; set; } = [];

    /// <summary>File name relative to the corpus folder; set by the loader.</summary>
    [JsonIgnore]
    public string FileName { get; set; } = "";
}

/// <summary>An ordered sequence of HTTP steps that share server state.</summary>
public sealed class Scenario
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Client library source the request shapes were taken from.</summary>
    public string? Source { get; set; }

    public List<Step> Steps { get; set; } = [];
}

public sealed class Step
{
    public string Id { get; set; } = "";

    public string Method { get; set; } = "GET";

    /// <summary>Path template, e.g. <c>/v3/storage/${projectId}/players/${steamId}</c>.</summary>
    public string Path { get; set; } = "";

    public Dictionary<string, string>? Headers { get; set; }

    public Dictionary<string, string>? Query { get; set; }

    /// <summary>JSON request body; string values are template-expanded.</summary>
    public JsonElement? Body { get; set; }

    /// <summary>Verbatim request body (for malformed JSON cases); template-expanded.</summary>
    public string? RawBody { get; set; }

    /// <summary>Generates a large body instead of <see cref="Body"/> (oversized payload cases).</summary>
    public GeneratedBody? GeneratedBody { get; set; }

    /// <summary>Content type for <see cref="RawBody"/>/<see cref="GeneratedBody"/>; JSON bodies default to application/json.</summary>
    public string? ContentType { get; set; }

    /// <summary>Response paths whose array order is not significant (sorted before comparison).</summary>
    public List<string>? OrderInsensitivePaths { get; set; }

    /// <summary>Extra response paths masked before comparison (scenario-specific volatile values).</summary>
    public List<string>? MaskPaths { get; set; }

    /// <summary>Response values captured for later steps: name → JSON path. Use as <c>${capture.name}</c>.</summary>
    public Dictionary<string, string>? Capture { get; set; }

    public Expectation? Expect { get; set; }

    public string? Note { get; set; }
}

public sealed class GeneratedBody
{
    /// <summary>Approximate serialized size in bytes.</summary>
    public int Bytes { get; set; }

    /// <summary>Field that receives the padding string.</summary>
    public string Field { get; set; } = "blob";
}

public sealed class Expectation
{
    public int? Status { get; set; }

    /// <summary>JSON paths that must exist in the response body.</summary>
    public List<string>? RequiredFields { get; set; }

    /// <summary>JSON path → expected value (string values are template-expanded).</summary>
    [JsonPropertyName("equals")]
    public Dictionary<string, JsonElement>? FieldEquals { get; set; }
}
