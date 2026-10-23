using System.Text.Json;
using Json.Schema;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>S2: the shipped matrix is valid against its schema, embedded in the binary, and seeded as the issue requires.</summary>
public sealed class MatrixFileTests
{
    private const string EmbeddedName = "SboxNetworkStorage.Server.Compat.library-matrix.json";

    private static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(CompatTestPaths.Schema));

    private static EvaluationResults Validate(string json)
        => Schema.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

    private static JsonElement Shipped() => JsonDocument.Parse(File.ReadAllText(CompatTestPaths.Matrix)).RootElement;

    [Fact]
    public void ShippedMatrixValidatesAgainstItsSchema()
    {
        var result = Validate(File.ReadAllText(CompatTestPaths.Matrix));
        Assert.True(result.IsValid, "library-matrix.json does not match library-matrix.schema.json: " + JsonSerializer.Serialize(result.Details?.Where(d => !d.IsValid).Select(d => new { d.InstanceLocation, d.Errors })));
    }

    [Fact]
    public void MatrixIsEmbeddedInTheServerBinaryAndIsTheFileInTheRepository()
    {
        using var stream = typeof(SboxNetworkStorage.Server.Hosting.BuildInfo).Assembly.GetManifestResourceStream(EmbeddedName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        using var embedded = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal(Shipped().GetRawText(), embedded.RootElement.GetRawText());
    }

    [Fact]
    public void SeededWithTheLegacyBucketTheIncidentAndTheFirstVersionedLibrary()
    {
        var libraries = Shipped().GetProperty("libraries").EnumerateArray().ToDictionary(l => l.GetProperty("version").GetString()!);

        var legacy = libraries["legacy"];
        Assert.Equal("supported", legacy.GetProperty("status").GetString());
        Assert.Equal("<=309133", legacy.GetProperty("revisions").GetString());

        var incident = libraries["1.0.165702"];
        Assert.Equal("known-broken", incident.GetProperty("status").GetString());
        Assert.Equal(">=26.10", incident.GetProperty("engine").GetString());
        Assert.Equal("sbox-cool/sbox-network-storage#4", incident.GetProperty("issue").GetString());

        var first = libraries["1.1.0"];
        Assert.Equal("supported", first.GetProperty("status").GetString());
        // minServer is the next minor of the server release line this ships in (last release 0.4.0).
        Assert.Equal("0.5.0", first.GetProperty("minServer").GetString());
        Assert.Equal("1.1.0", Shipped().GetProperty("recommended").GetString());
        Assert.Equal("1.1.0", Shipped().GetProperty("minimum").GetString());
    }

    [Fact]
    public void SchemaRejectsABrokenMatrix()
    {
        Assert.False(Validate("""{ "schemaVersion": 1, "minimum": "1.1", "recommended": "1.1.0", "libraries": [], "deprecated": [], "blocked": [], "tested": [] }""").IsValid);
        Assert.False(Validate("""{ "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0", "libraries": [{ "version": "1.1.0", "status": "great" }], "deprecated": [], "blocked": [], "tested": [] }""").IsValid);
        Assert.False(Validate("""{ "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0", "libraries": [], "deprecated": [{ "range": "<1.2.0" }], "blocked": [], "tested": [] }""").IsValid);
        Assert.False(Validate("""{ "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0", "libraries": [], "deprecated": [], "blocked": [{ "range": "banana", "reason": "x" }], "tested": [] }""").IsValid);
        Assert.False(Validate("""{ "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0", "libraries": [], "deprecated": [], "blocked": [], "tested": [], "extra": 1 }""").IsValid);
        Assert.False(Validate("""{ "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0", "libraries": [{ "version": "1.1.0", "status": "deprecated" }], "deprecated": [], "blocked": [], "tested": [] }""").IsValid);
    }

    [Fact]
    public void SchemaAcceptsTestedRowsWithEngineAndOsAndRequiresThemForAPass()
    {
        const string wrapper = """{ "schemaVersion": 1, "minimum": "1.1.0", "recommended": "1.1.0", "libraries": [], "deprecated": [], "blocked": [], "tested": [ROW] }""";
        string Matrix(string row) => wrapper.Replace("ROW", row);

        Assert.True(Validate(Matrix("""
            { "server": "0.5.0", "commit": "abc1234", "kind": "self-hosted", "library": "1.1.0", "librarySource": "library-manager", "revision": 310500,
              "engine": "26.10.02", "os": "Windows 11 23H2", "database": ["sqlite", "postgres"], "result": "pass", "date": "2026-10-20",
              "evidence": ["sbox-cool/sbox-network-storage-server#54"] }
            """)).IsValid);
        Assert.True(Validate(Matrix("""
            { "server": "0.5.0", "library": "1.1.0", "database": ["sqlite"], "result": "ci-only", "evidence": ["https://github.com/sbox-cool/x/actions/runs/1"] }
            """)).IsValid);
        // A pass without engine, os, kind and source is not evidence.
        Assert.False(Validate(Matrix("""
            { "server": "0.5.0", "library": "1.1.0", "database": ["sqlite"], "result": "pass", "evidence": ["x#1"] }
            """)).IsValid);
        Assert.False(Validate(Matrix("""
            { "server": "0.5.0", "kind": "self-hosted", "library": "1.1.0", "librarySource": "git:abc1234", "engine": "26.10.02", "database": ["sqlite"], "result": "pass", "evidence": ["x#1"] }
            """)).IsValid);
        // The engine is the full build string, not free text.
        Assert.False(Validate(Matrix("""
            { "server": "0.5.0", "kind": "self-hosted", "library": "1.1.0", "librarySource": "git:abc1234", "engine": "latest", "os": "Windows", "database": ["sqlite"], "result": "pass", "evidence": ["x#1"] }
            """)).IsValid);
    }

    [Theory]
    [InlineData("git:026acfac737e351c4d58c5af2989d159c3a33fe3", true)]
    [InlineData("git:5c33cb5", true)]
    [InlineData("legacy", false)]
    [InlineData("git:main", false)]
    [InlineData("git:abc123", false)]
    public void UnversionedEvidenceRequiresAnExactSourceIdentity(string library, bool valid)
    {
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, minimum = "1.1.0", recommended = "1.1.0",
            libraries = Array.Empty<object>(), deprecated = Array.Empty<object>(), blocked = Array.Empty<object>(),
            tested = new[] { new { server = "0.1.0-dev", library, database = new[] { "sqlite" }, result = "ci-only", evidence = new[] { "x/y#1" } } }
        });
        Assert.Equal(valid, Validate(json).IsValid);
    }
}
