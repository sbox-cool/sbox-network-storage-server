using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Regression coverage for the API-key permissions parser. The storage_api_keys
// .permissions jsonb column stores the map double-encoded (a JSON string wrapping
// a JSON object, e.g. "{\"endpoints\":\"rwx\"}"). Deserializing that straight into
// Dictionary<string,string> threw JsonException at Path $ and 500'd the Network
// Storage project page. The parser must unwrap the real shape (preserving the
// permissions), accept a plain object for forward-compatibility, and never throw.
public sealed class ApiKeyPermissionsParserTests
{
    [Fact]
    public void Parse_DoubleEncodedString_UnwrapsToMap()
    {
        // Exactly the production shape returned by Npgsql for the jsonb column.
        const string raw = "\"{\\\"endpoints\\\":\\\"rwx\\\",\\\"queries\\\":\\\"rwx\\\",\\\"workflows\\\":\\\"rw\\\"}\"";

        var result = ApiKeyPermissionsParser.Parse(raw);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal("rwx", result["endpoints"]);
        Assert.Equal("rwx", result["queries"]);
        Assert.Equal("rw", result["workflows"]);
    }

    [Fact]
    public void Parse_RoundTripsTheRealColumnValue()
    {
        // Mirror the write path (JsonSerializer.Serialize) feeding a jsonb string
        // value, then the canonical text Npgsql hands back on read.
        var original = new Dictionary<string, string>
        {
            ["endpoints"] = "rwx",
            ["collections"] = "rw"
        };
        var innerJson = JsonSerializer.Serialize(original);          // {"endpoints":"rwx",...}
        var storedAsJsonbString = JsonSerializer.Serialize(innerJson); // "{\"endpoints\":\"rwx\",...}"

        var result = ApiKeyPermissionsParser.Parse(storedAsJsonbString);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Parse_PlainObject_ReturnsMap()
    {
        var result = ApiKeyPermissionsParser.Parse("""{ "endpoints": "rwx", "rate_limits": "rw" }""");

        Assert.NotNull(result);
        Assert.Equal("rwx", result!["endpoints"]);
        Assert.Equal("rw", result["rate_limits"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]      // JSON null
    [InlineData("\"null\"")]  // string "null" -> not an object
    [InlineData("\"\"")]      // empty wrapped string
    public void Parse_NullOrBlankOrJsonNull_ReturnsNull(string? raw)
    {
        Assert.Null(ApiKeyPermissionsParser.Parse(raw));
    }

    [Theory]
    [InlineData("[\"read\",\"write\"]")] // array root
    [InlineData("42")]                    // number root
    [InlineData("true")]                  // bool root
    [InlineData("\"[1,2,3]\"")]          // string wrapping a non-object
    public void Parse_NonObjectShapes_ReturnNull(string raw)
    {
        Assert.Null(ApiKeyPermissionsParser.Parse(raw));
    }

    [Theory]
    [InlineData("{ not valid json")]
    [InlineData("\"{ broken inner\"")] // valid outer string, malformed inner json
    public void Parse_MalformedJson_ReturnsNullWithoutThrowing(string raw)
    {
        Assert.Null(ApiKeyPermissionsParser.Parse(raw));
    }

    [Fact]
    public void Parse_NonStringValues_CoercedToRawTextNeverThrows()
    {
        // A defensive guard: a stray non-string permission value must not throw.
        var result = ApiKeyPermissionsParser.Parse("""{ "endpoints": "rwx", "limit": 5, "nested": { "a": 1 } }""");

        Assert.NotNull(result);
        Assert.Equal("rwx", result!["endpoints"]);
        Assert.Equal("5", result["limit"]);
        Assert.Equal("""{ "a": 1 }""", result["nested"]);
    }

    // The SpaceTimeDB read path hands the parser an already-decoded JsonElement
    // (the reducer row's permissions_json). The JsonElement overload must accept
    // the same shapes as the string overload and never throw, so a wrapped or
    // malformed value degrades to null instead of dropping every key for a project.
    [Fact]
    public void Parse_JsonElementObject_ReturnsMap()
    {
        using var document = JsonDocument.Parse("""{ "endpoints": "rwx", "queries": "x" }""");

        var result = ApiKeyPermissionsParser.Parse(document.RootElement);

        Assert.NotNull(result);
        Assert.Equal("rwx", result!["endpoints"]);
        Assert.Equal("x", result["queries"]);
    }

    [Fact]
    public void Parse_JsonElementDoubleEncodedString_UnwrapsToMap()
    {
        // A JSON string token whose text is itself a JSON object — the real shape
        // SpaceTimeDB returns when permissions_json was stored double-encoded.
        using var document = JsonDocument.Parse("\"{\\\"endpoints\\\":\\\"rwx\\\"}\"");

        var result = ApiKeyPermissionsParser.Parse(document.RootElement);

        Assert.NotNull(result);
        Assert.Equal("rwx", result!["endpoints"]);
    }

    [Theory]
    [InlineData("null")]            // JSON null element
    [InlineData("[1,2,3]")]         // array element
    [InlineData("\"[1,2,3]\"")]     // string wrapping a non-object
    [InlineData("\"{ broken\"")]    // string wrapping malformed json
    public void Parse_JsonElementNonObjectOrMalformed_ReturnsNull(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(ApiKeyPermissionsParser.Parse(document.RootElement));
    }

    [Fact]
    public void Parse_DefaultJsonElement_ReturnsNull()
    {
        // A missing permissions_json property deserializes to an Undefined element.
        Assert.Null(ApiKeyPermissionsParser.Parse(default(JsonElement)));
    }
}
