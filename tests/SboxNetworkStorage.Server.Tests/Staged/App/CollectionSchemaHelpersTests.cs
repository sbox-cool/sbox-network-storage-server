using System.Collections.Generic;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class CollectionSchemaHelpersTests
{
    [Fact]
    public void CountFields_LegacyStringSchemaValues_ReturnsTopLevelKeyCount()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"score":"number","health":"number"}""")!;

        Assert.Equal(2, CollectionSchemaHelpers.CountFields(schema));
    }

    [Fact]
    public void CountFields_WrappedPropertiesSchema_ReturnsPropertyCount()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """
            {
              "type": "object",
              "properties": {
                "gold": { "type": "number" },
                "fishCaught": { "type": "number" }
              }
            }
            """)!;

        Assert.Equal(2, CollectionSchemaHelpers.CountFields(schema));
    }

    [Fact]
    public void CountFields_BareObjectFieldDefinitions_ReturnsFieldCount()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """
            {
              "gold": { "type": "number" },
              "fishCaught": { "type": "number" }
            }
            """)!;

        Assert.Equal(2, CollectionSchemaHelpers.CountFields(schema));
    }

    [Fact]
    public void GetFieldPaths_WrappedNestedSchema_ReturnsLeafPathsOnly()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """
            {
              "type": "object",
              "properties": {
                "score": { "type": "number" },
                "player": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "level": { "type": "number" }
                  }
                }
              }
            }
            """)!;

        var paths = CollectionSchemaHelpers.GetFieldPaths(schema);

        Assert.Contains("score", paths);
        Assert.Contains("player.name", paths);
        Assert.Contains("player.level", paths);
        Assert.DoesNotContain("player", paths); // parent node is not a leaf
    }

    [Fact]
    public void GetFieldMeta_WrappedNestedSchema_MarksParentsAndTypesLeaves()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """
            {
              "type": "object",
              "properties": {
                "score": { "type": "number" },
                "player": { "type": "object", "properties": { "name": { "type": "string" } } }
              }
            }
            """)!;

        var meta = CollectionSchemaHelpers.GetFieldMeta(schema);

        Assert.Contains(meta, n => n.Path == "player" && n.HasChildren && n.Type == "object");
        Assert.Contains(meta, n => n.Path == "score" && !n.HasChildren && n.Type == "number");
        Assert.Contains(meta, n => n.Path == "player.name" && !n.HasChildren && n.Type == "string");
    }

    [Fact]
    public void GetFieldPaths_LegacyStringValuedSchema_YieldsLeafFields()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"score":"number","gold":"number"}""")!;

        var paths = CollectionSchemaHelpers.GetFieldPaths(schema);

        Assert.Equal(new[] { "score", "gold" }, paths);
        var meta = CollectionSchemaHelpers.GetFieldMeta(schema);
        Assert.Contains(meta, n => n.Path == "score" && n.Type == "number" && !n.HasChildren);
    }

    [Fact]
    public void GetFieldPaths_BareObjectSchema_ReturnsFields()
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{ "gold": { "type": "number" }, "fishCaught": { "type": "number" } }""")!;

        var paths = CollectionSchemaHelpers.GetFieldPaths(schema);

        Assert.Equal(new[] { "gold", "fishCaught" }, paths);
    }

    [Fact]
    public void GetFieldMeta_EmptyOrNullSchema_ReturnsEmpty()
    {
        Assert.Empty(CollectionSchemaHelpers.GetFieldMeta(null));
        Assert.Empty(CollectionSchemaHelpers.GetFieldPaths(new Dictionary<string, object>()));
    }
}
