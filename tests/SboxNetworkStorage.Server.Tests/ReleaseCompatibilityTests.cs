using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Update preflight: a release's signed testedLibraries against the library builds this server's games sent.
/// Only a published fail for a library with real traffic blocks; missing evidence only warns.
/// </summary>
public sealed class ReleaseCompatibilityTests
{
    private static TestedLibrary Tested(string library, long? revision, string result) =>
        new(library, revision, "git:abc", "engine", "dedicated", result);

    [Fact]
    public void A_failing_library_with_traffic_blocks_and_a_tiny_share_is_ignored()
    {
        var review = ReleaseCompatibility.Review(
            [Tested("1.1.0", null, "fail"), Tested("1.0.9", null, "fail")],
            [new ObservedLibrary("1.1.0", null, 990), new ObservedLibrary("1.0.9", null, 9)]);

        Assert.True(review.Blocked);
        Assert.Equal("1.1.0 (99.1% of requests)", Assert.Single(review.Failed));
        Assert.Empty(review.Untested);
    }

    [Fact]
    public void Legacy_traffic_without_a_revision_is_covered_by_a_tested_legacy_revision()
    {
        var review = ReleaseCompatibility.Review(
            [Tested("legacy", 422519, "ci-only"), Tested("1.1.0", 423774, "ci-only")],
            [new ObservedLibrary("legacy", null, 600), new ObservedLibrary("1.1.0", null, 400)]);

        Assert.False(review.Blocked);
        Assert.Empty(review.Untested);
    }

    [Fact]
    public void A_known_revision_must_match_exactly_and_untested_builds_only_warn()
    {
        var review = ReleaseCompatibility.Review(
            [Tested("1.1.0", 423774, "ci-only")],
            [new ObservedLibrary("1.1.0", 500000, 700), new ObservedLibrary("1.2.0", null, 300)]);

        Assert.False(review.Blocked);
        Assert.Equal(["1.1.0 r500000 (70.0% of requests)", "1.2.0 (30.0% of requests)"], review.Untested);
    }

    [Fact]
    public void Releases_without_evidence_and_servers_without_traffic_are_not_judged()
    {
        Assert.Null(ReleaseCompatibility.ParseTestedLibraries("""{"version":"0.5.4","security":false}"""));
        Assert.Null(ReleaseCompatibility.ParseTestedLibraries("not json"));
        var tested = ReleaseCompatibility.ParseTestedLibraries(
            """{"version":"0.5.5","testedLibraries":[{"library":"legacy","revision":422519,"source":"git:026acfa","engine":"e","target":"dedicated","result":"ci-only"}]}""");
        Assert.Equal(Tested("legacy", 422519, "ci-only") with { Source = "git:026acfa", Engine = "e" }, Assert.Single(tested!));

        var review = ReleaseCompatibility.Review([Tested("1.1.0", null, "fail")], []);
        Assert.False(review.Blocked);
        Assert.Empty(review.Untested);
    }
}
