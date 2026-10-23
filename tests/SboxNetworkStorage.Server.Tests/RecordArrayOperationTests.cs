using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Server.Tests;

public sealed class RecordArrayOperationTests
{
    private const string Original = """{"inventory":[{"id":"a","count":1,"tags":["old"]},{"id":"b","count":7}]}""";

    [Theory]
    [InlineData("""{"op":"set","path":"inventory.0.count","value":9}""", "count", "9")]
    [InlineData("""{"op":"inc","path":"inventory.0.count","value":1}""", "count", "2")]
    [InlineData("""{"op":"push","path":"inventory.0.tags","value":"new"}""", "tags", "[\"old\",\"new\"]")]
    [InlineData("""{"op":"pull","path":"inventory.0.tags","match":{"value":"old"}}""", "tags", "[]")]
    [InlineData("""{"op":"remove","path":"inventory.0.tags","value":"old"}""", "tags", "[]")]
    [InlineData("""{"op":"merge","path":"inventory.0","value":{"count":9}}""", "count", "9")]
    [InlineData("""{"op":"set_if_null","path":"inventory.0.note","value":"new"}""", "note", "\"new\"")]
    public void Indexed_operations_preserve_the_array_siblings_and_original(string operation, string field, string expected)
    {
        var source = Object(Original);
        var result = RecordOperations.Apply(source, Operations("[" + operation + "]"));
        Assert.True(result.Ok, result.Error);
        var document = JsonSerializer.SerializeToElement(result.Data);
        var inventory = document.GetProperty("inventory");
        Assert.Equal(JsonValueKind.Array, inventory.ValueKind);
        Assert.Equal(expected, inventory[0].GetProperty(field).GetRawText());
        Assert.Equal("a", inventory[0].GetProperty("id").GetString());
        Assert.Equal("b", inventory[1].GetProperty("id").GetString());
        Assert.Equal(7, inventory[1].GetProperty("count").GetInt32());
        Assert.Equal(Original, JsonSerializer.Serialize(source));
    }

    [Fact]
    public void Consecutive_index_appends_without_changing_existing_elements()
    {
        var result = RecordOperations.Apply(Object(Original), Operations("""[{"op":"set","path":"inventory.2","value":{"id":"c"}}]"""));
        Assert.True(result.Ok, result.Error);
        var list = JsonSerializer.SerializeToElement(result.Data).GetProperty("inventory");
        Assert.Equal("a", list[0].GetProperty("id").GetString());
        Assert.Equal("b", list[1].GetProperty("id").GetString());
        Assert.Equal("c", list[2].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("inventory.2147483647")]
    [InlineData("inventory.-1")]
    [InlineData("inventory.01")]
    public void Invalid_or_sparse_indices_fail_without_mutating_the_source(string path)
    {
        var source = Object(Original);
        var result = RecordOperations.Apply(source, Operations(JsonSerializer.Serialize(new[] { new { op = "set", path, value = 9 } })));
        Assert.False(result.Ok);
        Assert.Null(result.Data);
        Assert.Equal(Original, JsonSerializer.Serialize(source));
    }

    private static Dictionary<string, object?> Object(string json) => (Dictionary<string, object?>)Parse(json)!;
    private static List<object?> Operations(string json) => (List<object?>)Parse(json)!;
    private static object? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EndpointExpression.FromJson(document.RootElement);
    }
}
