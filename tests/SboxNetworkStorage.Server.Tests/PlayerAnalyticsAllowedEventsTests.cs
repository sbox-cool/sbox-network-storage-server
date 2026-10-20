using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// The owner's custom analytics event allowlist: defaults, stored-list reading, owner form parsing,
/// and publication in the signed security config that the game library enforces.
/// </summary>
public sealed class PlayerAnalyticsAllowedEventsTests : IDisposable
{
    private static readonly string[] ReferenceDefaults = ["death", "respawn", "cast_line", "caught_fish", "sold_fish"];
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sbox-ns-analytics-" + Guid.NewGuid().ToString("N"));
    private readonly RequestEncryptionKeyStore _store;

    public PlayerAnalyticsAllowedEventsTests()
    {
        Directory.CreateDirectory(_folder);
        var keysPath = Path.Combine(_folder, "request_encryption_keys.json");
        RequestEncryptionKeyring.EnsureAndLoad(keysPath, Now);
        _store = new RequestEncryptionKeyStore(keysPath);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Unset_list_uses_the_reference_defaults()
    {
        Assert.Equal(ReferenceDefaults, PlayerAnalyticsAllowedEvents.ReadStored(null));
        Assert.Equal(ReferenceDefaults, PlayerAnalyticsAllowedEvents.ReadStored(JsonNode.Parse("""{ "enabled": true }""")));
    }

    [Fact]
    public void Stored_list_is_normalized_deduplicated_and_invalid_entries_are_dropped()
    {
        var analytics = JsonNode.Parse("""{ "allowedEventTypes": ["Shop Opened", "shop_opened", "café", "", 7, "x"] }""");

        Assert.Equal(new[] { "shop_opened", "x" }, PlayerAnalyticsAllowedEvents.ReadStored(analytics));
    }

    [Fact]
    public void Empty_stored_list_means_no_custom_events()
    {
        Assert.Empty(PlayerAnalyticsAllowedEvents.ReadStored(JsonNode.Parse("""{ "allowedEventTypes": [] }""")));
    }

    [Fact]
    public void Owner_form_is_split_by_line_or_comma_trimmed_lowercased_and_deduplicated()
    {
        Assert.Equal(
            new[] { "shop_opened", "gold.earned", "a" },
            PlayerAnalyticsAllowedEvents.ParseForm("Shop_Opened\r\n gold.earned, shop_opened ,a\n"));
    }

    [Fact]
    public void Empty_owner_form_is_an_empty_list()
    {
        var parsed = PlayerAnalyticsAllowedEvents.ParseForm("  \r\n");
        Assert.NotNull(parsed);
        Assert.Empty(parsed);
    }

    [Theory]
    [InlineData("shop opened")]
    [InlineData("café")]
    [InlineData("-leading")]
    [InlineData("a/b")]
    public void Owner_form_refuses_names_the_library_would_rewrite(string name)
    {
        Assert.Null(PlayerAnalyticsAllowedEvents.ParseForm(name));
    }

    [Fact]
    public void Owner_form_enforces_name_length_and_count_limits()
    {
        Assert.NotNull(PlayerAnalyticsAllowedEvents.ParseForm(new string('a', 48)));
        Assert.Null(PlayerAnalyticsAllowedEvents.ParseForm(new string('a', 49)));

        var hundred = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"event_{i}"));
        Assert.Equal(100, PlayerAnalyticsAllowedEvents.ParseForm(hundred)!.Count);

        var tooMany = string.Join("\n", Enumerable.Range(0, 101).Select(i => $"event_{i}"));
        Assert.Null(PlayerAnalyticsAllowedEvents.ParseForm(tooMany));
    }

    [Fact]
    public void Owner_settings_default_each_flag_to_true_like_the_library()
    {
        var settings = PlayerAnalyticsOwnerSettings.From(null);

        Assert.True(settings.Enabled);
        Assert.True(settings.CaptureSessions);
        Assert.True(settings.ManagedLibraryEvents);
        Assert.Equal(ReferenceDefaults, settings.AllowedEventTypes);
    }

    [Fact]
    public void Security_config_publishes_the_reference_defaults_when_unset()
    {
        var project = JsonDocument.Parse("""{ "id": "demo-project" }""").RootElement.Clone();

        var config = NetworkStorageSecurityConfigBuilder.Build("demo-project", project, Now, _store);

        var published = config.GetProperty("settings").GetProperty("analytics").GetProperty("allowedEventTypes");
        Assert.Equal(ReferenceDefaults, published.EnumerateArray().Select(entry => entry.GetString()!));
    }

    [Fact]
    public void Security_config_publishes_the_owner_list_and_keeps_other_analytics_keys()
    {
        var project = JsonDocument.Parse("""
            {
              "id": "demo-project",
              "analytics": { "enabled": true, "captureSessions": false, "allowedEventTypes": ["shop_opened", "shop_opened", "gold.earned"] }
            }
            """).RootElement.Clone();

        var config = NetworkStorageSecurityConfigBuilder.Build("demo-project", project, Now, _store);

        var analytics = config.GetProperty("settings").GetProperty("analytics");
        Assert.False(analytics.GetProperty("captureSessions").GetBoolean());
        Assert.Equal(new[] { "shop_opened", "gold.earned" }, analytics.GetProperty("allowedEventTypes").EnumerateArray().Select(entry => entry.GetString()!));
    }
}
