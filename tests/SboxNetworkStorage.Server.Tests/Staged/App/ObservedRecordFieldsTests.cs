using System.Collections.Generic;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ObservedRecordFieldsTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Collect_DiscoversTopLevelAndNestedLeafPaths()
    {
        var into = new Dictionary<string, string>();
        ObservedRecordFields.Collect(Json("""{ "playerName": "Bob", "records": { "heaviestWeightKg": 5.2, "fishCaught": 3 } }"""), into);

        Assert.Equal("string", into["playerName"]);
        Assert.Equal("number", into["records.heaviestWeightKg"]);
        Assert.Equal("number", into["records.fishCaught"]);
        // Parent objects are not leaves.
        Assert.False(into.ContainsKey("records"));
    }

    [Fact]
    public void Collect_PreservesExistingSchemaTypes()
    {
        // Schema declared records.heaviestWeightKg as "float"; observed must not overwrite it.
        var into = new Dictionary<string, string> { ["records.heaviestWeightKg"] = "float" };
        ObservedRecordFields.Collect(Json("""{ "records": { "heaviestWeightKg": 5 }, "playerName": "Bob" }"""), into);

        Assert.Equal("float", into["records.heaviestWeightKg"]); // unchanged
        Assert.Equal("string", into["playerName"]);             // newly discovered
    }

    [Fact]
    public void Collect_InfersTypesForScalarsAndArrays()
    {
        var into = new Dictionary<string, string>();
        ObservedRecordFields.Collect(Json("""{ "n": 1, "s": "x", "b": true, "a": [1,2], "z": null }"""), into);

        Assert.Equal("number", into["n"]);
        Assert.Equal("string", into["s"]);
        Assert.Equal("boolean", into["b"]);
        Assert.Equal("array", into["a"]);
        Assert.Equal("value", into["z"]);
    }

    [Fact]
    public void Collect_RespectsDepthCap()
    {
        var into = new Dictionary<string, string>();
        // maxDepth 1: leaves at object-depth 1 are kept; anything deeper is dropped.
        ObservedRecordFields.Collect(Json("""{ "a": { "shallow": 1, "b": { "deep": 2 } } }"""), into, maxDepth: 1);
        Assert.Equal("number", into["a.shallow"]);          // depth 1 leaf — kept
        Assert.False(into.ContainsKey("a.b.deep"));         // depth 2 leaf — dropped
    }
    [Fact]
    public void Collect_NonObjectRoot_AddsNothing()
    {
        var into = new Dictionary<string, string>();
        ObservedRecordFields.Collect(Json("""[1, 2, 3]"""), into);
        Assert.Empty(into);
    }

    [Fact]
    public void Collect_UnionAcrossMultipleSamples()
    {
        var into = new Dictionary<string, string>();
        ObservedRecordFields.Collect(Json("""{ "playerName": "A", "score": 1 }"""), into);
        ObservedRecordFields.Collect(Json("""{ "playerName": "B", "level": 2 }"""), into);

        // Fields from BOTH samples are present (some records may omit fields).
        Assert.True(into.ContainsKey("playerName"));
        Assert.True(into.ContainsKey("score"));
        Assert.True(into.ContainsKey("level"));
    }
}
