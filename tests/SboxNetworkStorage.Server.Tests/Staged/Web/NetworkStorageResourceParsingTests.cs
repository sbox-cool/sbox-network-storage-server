using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Regression tests for the dashboard "project card shows 2 collections, project page
/// shows none" split-brain. The card counts array elements tolerantly; the project
/// pages bound the same bytes to positional records strictly, so one resource with an
/// explicit null on a non-nullable member emptied the whole list.
/// </summary>
public sealed class NetworkStorageResourceParsingTests
{
    // A collection as the YAML source compiler writes it when the author left keys
    // blank: `maxRecords:` and `allowRecordDelete:` in YAML become explicit JSON nulls,
    // and the compiler copies any key that is not `undefined`.
    private const string CollectionsWithNulls = """
        [
          {
            "id": "col_players",
            "name": "players",
            "description": null,
            "collectionType": "per-steamid",
            "maxRecords": null,
            "allowRecordDelete": null
          },
          {
            "id": "col_skills",
            "name": "skills",
            "collectionType": "per-steamid",
            "maxRecords": 5
          }
        ]
        """;

    private const string EndpointsWithNulls = """
        [
          {
            "id": "ep_init",
            "name": "Init Player",
            "slug": "init-player",
            "method": "POST",
            "enabled": null,
            "exposure": "public",
            "internal": null,
            "deprecated": null,
            "requiresSecretKey": true
          }
        ]
        """;

    [Fact]
    public void Collections_with_null_value_type_fields_still_parse()
    {
        var collections = ParseList<CollectionResource>(CollectionsWithNulls);

        Assert.Equal(2, collections.Count);
        Assert.Equal("players", collections[0].Name);
        Assert.Equal("skills", collections[1].Name);
        Assert.Equal(5, collections[1].MaxRecords);
    }

    /// <summary>
    /// A blank YAML key means "unset", so the record's declared default must win.
    /// Binding it to 0 would cap the collection at zero records — worse than the crash
    /// it replaced.
    /// </summary>
    [Fact]
    public void A_null_maxRecords_falls_back_to_the_declared_default_not_zero()
    {
        var collections = ParseList<CollectionResource>(CollectionsWithNulls);

        Assert.Equal(1, collections[0].MaxRecords);
        Assert.False(collections[0].AllowRecordDelete);
    }

    [Fact]
    public void Endpoints_with_null_value_type_fields_still_parse()
    {
        var endpoints = ParseList<EndpointResource>(EndpointsWithNulls);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("init-player", endpoint.Slug);
        Assert.True(endpoint.RequiresSecretKey);
        Assert.False(endpoint.Internal);
        Assert.False(endpoint.Deprecated);
    }

    [Fact]
    public void A_single_malformed_entry_does_not_discard_its_siblings()
    {
        const string json = """
            [
              { "id": "a", "name": "good-one", "collectionType": "global" },
              "this is not an object",
              { "id": "b", "name": "good-two", "collectionType": "global" }
            ]
            """;

        var collections = ParseList<CollectionResource>(json);

        Assert.Equal(2, collections.Count);
        Assert.Equal(["good-one", "good-two"], collections.Select(c => c.Name));
    }

    [Fact]
    public void Blank_and_non_array_payloads_read_as_empty_not_as_a_crash()
    {
        Assert.Empty(ParseList<CollectionResource>(null));
        Assert.Empty(ParseList<CollectionResource>(""));
        Assert.Empty(ParseList<CollectionResource>("{}"));
        Assert.Empty(ParseList<CollectionResource>("not json at all"));
    }

    /// <summary>
    /// The tolerant count the workspace card computes and the typed list the project
    /// page renders must agree — disagreeing is the bug this suite exists for.
    /// </summary>
    [Fact]
    public void Typed_list_count_matches_the_tolerant_array_count_used_by_the_project_card()
    {
        using var document = JsonDocument.Parse(CollectionsWithNulls);
        var cardCount = document.RootElement.GetArrayLength();

        Assert.Equal(cardCount, ParseList<CollectionResource>(CollectionsWithNulls).Count);
    }

    /// <summary>
    /// CollectionResource declares `string CollectionType` as non-nullable, but nullable
    /// annotations are not enforced at runtime — JSON binding will happily put null
    /// there, and CollectionDataOverviewBuilder then called `.Length` on it. That was a
    /// NullReferenceException on
    /// /tools/network-storage/{projectId}/collections/{collectionId} for any collection
    /// whose stored definition omitted the field.
    /// </summary>
    [Fact]
    public void Missing_collectionType_is_normalized_rather_than_left_null()
    {
        const string json = """
            [ { "id": "players", "name": "players" } ]
            """;

        var collection = Assert.Single(ParseList<CollectionResource>(json));

        Assert.Equal("per-steamid", collection.CollectionType);
    }

    [Fact]
    public void Explicitly_null_collectionType_is_normalized_too()
    {
        const string json = """
            [ { "id": "players", "name": "players", "collectionType": null } ]
            """;

        var collection = Assert.Single(ParseList<CollectionResource>(json));

        Assert.Equal("per-steamid", collection.CollectionType);
    }

    [Fact]
    public void A_collection_missing_its_name_falls_back_to_its_id()
    {
        const string json = """
            [ { "id": "players" } ]
            """;

        var collection = Assert.Single(ParseList<CollectionResource>(json));

        Assert.Equal("players", collection.Name);
        Assert.Equal("players", collection.Id);
    }

    [Fact]
    public void Endpoints_missing_method_or_name_are_normalized()
    {
        const string json = """
            [ { "id": "ep_1", "slug": "init-player" } ]
            """;

        var endpoint = Assert.Single(ParseList<EndpointResource>(json));

        Assert.Equal("POST", endpoint.Method);
        Assert.Equal("init-player", endpoint.Name);
        Assert.Equal("init-player", endpoint.Slug);
    }

    [Fact]
    public void Normalization_never_overwrites_a_real_value()
    {
        const string json = """
            [ { "id": "c1", "name": "Loadouts", "collectionType": "global" } ]
            """;

        var collection = Assert.Single(ParseList<CollectionResource>(json));

        Assert.Equal("Loadouts", collection.Name);
        Assert.Equal("global", collection.CollectionType);
    }

    // Exercises the real production helper (SboxNetworkStorage.Infrastructure exposes internals
    // to this test assembly), so these tests cannot drift from the shipped behavior.
    private static List<T> ParseList<T>(string? json)
        => ResilientResourceJson.DeserializeList<T>(json);
}
