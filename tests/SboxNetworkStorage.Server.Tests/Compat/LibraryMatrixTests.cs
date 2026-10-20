using System.Text.Json;
using Json.Schema;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>S3: every status derived from matrix ranges.</summary>
public sealed class LibraryMatrixEvaluationTests
{
    internal const string Fixture = """
        {
          "schemaVersion": 1,
          "minimum": "1.1.0",
          "recommended": "1.3.0",
          "libraries": [
            { "version": "legacy", "revisions": "<=309133", "status": "supported" },
            { "version": "1.3.0", "status": "supported" },
            { "version": "1.2.7", "revision": 400, "status": "known-broken", "engine": ">=26.10", "reason": "Breaks the editor on engine 26.10+", "issue": "sbox-cool/sbox-network-storage#4" },
            { "version": "1.2.8", "status": "deprecated", "sunset": "2027-01-01" },
            { "version": "1.2.9", "status": "unsupported", "reason": "withdrawn" }
          ],
          "deprecated": [
            { "range": "<1.2.0", "sunset": "2027-03-01" },
            { "range": ">=1.1.0 <1.1.9", "sunset": "2026-12-01" }
          ],
          "blocked": [
            { "range": "1.2.1", "reason": "Leaks keys in logs", "advisory": "GHSA-xxxx-yyyy-zzzz" }
          ],
          "tested": [
            { "server": "0.5.0", "library": "1.3.0", "revision": 312000, "engine": "26.10.02", "database": ["sqlite"], "result": "pass", "evidence": ["x#1"] }
          ]
        }
        """;

    internal static LibraryMatrix Matrix { get; } = LibraryMatrix.Parse(Fixture);

    internal static ClientIdentity Id(string? version, long? revision = null, string? engine = null)
        => new(version ?? ClientIdentity.Legacy, version is null ? null : ParseVersion(version), revision, "editor", null, engine);

    private static SboxNetworkStorage.Server.Updates.SemanticVersion ParseVersion(string version)
    {
        Assert.True(ClientIdentity.TryParseLibraryVersion(version, out var parsed), version);
        return parsed;
    }

    [Theory]
    // no header: the server cannot tell, and says so; the legacy bucket is supported and nothing is attached
    [InlineData(null, null, null, "unknown", null, null)]
    // at or above recommended
    [InlineData("1.3.0", null, null, "supported", null, null)]
    [InlineData("1.4.2", null, null, "supported", null, null)]
    [InlineData("1.3.1-rc.1", null, null, "supported", null, null)]
    // between minimum and recommended
    [InlineData("1.2.5", null, null, "outdated", "1.3.0", null)]
    [InlineData("1.2.0", null, null, "outdated", "1.3.0", null)]
    // deprecated ranges, with the sunset date; the earliest of two matching dates wins
    [InlineData("1.1.9", null, null, "deprecated", "1.3.0", "2027-03-01")]
    [InlineData("1.1.5", null, null, "deprecated", "1.3.0", "2026-12-01")]
    [InlineData("1.1.0", null, null, "deprecated", "1.3.0", "2026-12-01")]
    // an exact deprecated entry
    [InlineData("1.2.8", null, null, "deprecated", "1.3.0", "2027-01-01")]
    // blocked exact match
    [InlineData("1.2.1", null, null, "blocked", "1.3.0", null)]
    // an exact unsupported entry
    [InlineData("1.2.9", null, null, "unsupported", "1.3.0", null)]
    // below minimum beats the deprecated range it also falls in
    [InlineData("1.0.9", null, null, "unsupported", "1.3.0", null)]
    [InlineData("0.9.0", null, null, "unsupported", "1.3.0", null)]
    [InlineData("1.1.0-rc.1", null, null, "unsupported", "1.3.0", null)]
    // known-broken applies only on the engine range, matched by version or by revision
    [InlineData("1.2.7", null, "26.10.02", "unsupported", "1.3.0", null)]
    [InlineData("1.2.7", null, "26.10", "unsupported", "1.3.0", null)]
    [InlineData("1.2.7", null, "27.1.0", "unsupported", "1.3.0", null)]
    [InlineData("1.2.7", null, "26.9.9", "outdated", "1.3.0", null)]
    [InlineData("1.2.7", null, null, "outdated", "1.3.0", null)]
    [InlineData("1.2.6", 400, "26.10.02", "unsupported", "1.3.0", null)]
    public void StatusComesFromTheMatrixRanges(string? version, int? revision, string? engine, string status, string? latest, string? sunset)
    {
        var result = Matrix.Evaluate(Id(version, revision, engine));
        Assert.Equal(status, result.Status.ToWire());
        Assert.Equal(latest, result.Latest);
        Assert.Equal(sunset, result.Sunset);
    }

    [Fact]
    public void BlockedCarriesTheReasonAndAdvisory()
    {
        var result = Matrix.Evaluate(Id("1.2.1"));
        Assert.Equal("Leaks keys in logs", result.Reason);
        Assert.Equal("GHSA-xxxx-yyyy-zzzz", result.Advisory);
    }

    [Fact]
    public void BlockedIsAnExactMatchNotAPrefix()
    {
        Assert.NotEqual(LibraryStatus.Blocked, Matrix.Evaluate(Id("1.2.10")).Status);
        Assert.NotEqual(LibraryStatus.Blocked, Matrix.Evaluate(Id("1.2.2")).Status);
        Assert.NotEqual(LibraryStatus.Blocked, Matrix.Evaluate(Id("1.2.1-rc.1")).Status);
    }

    [Fact]
    public void LatestIsTheNewestSupportedBuildNeverBelowRecommended()
    {
        Assert.Equal("1.3.0", Matrix.Latest);
        var onlyRecommended = Matrix with { Libraries = [], Recommended = "1.5.0" };
        Assert.Equal("1.5.0", onlyRecommended.Latest);
    }

    [Fact]
    public void LegacyBucketCanBeDeprecatedOrUnsupportedInTheMatrix()
    {
        var deprecated = Matrix with { Libraries = [new("legacy", null, "<=309133", null, null, "deprecated", "2027-06-01", null, null, null, null)] };
        var result = deprecated.Evaluate(Id(null));
        Assert.Equal(LibraryStatus.Deprecated, result.Status);
        Assert.Equal("2027-06-01", result.Sunset);
        Assert.Equal("1.3.0", result.Latest);

        var unsupported = Matrix with { Libraries = [new("legacy", null, "<=309133", null, null, "unsupported", null, null, null, null, null)] };
        Assert.Equal(LibraryStatus.Unsupported, unsupported.Evaluate(Id(null)).Status);
    }

    [Fact]
    public void ServerInfoBlockReportsTheMatrix()
    {
        var json = JsonSerializer.SerializeToElement(Matrix.ToServerInfo());
        var library = json.GetProperty("library");
        Assert.Equal("1.1.0", library.GetProperty("minimum").GetString());
        Assert.Equal("1.3.0", library.GetProperty("recommended").GetString());
        Assert.Equal("1.3.0", library.GetProperty("latest").GetString());
        Assert.Equal("supported", library.GetProperty("legacy").GetString());
        Assert.Equal("<1.2.0", library.GetProperty("deprecated")[0].GetProperty("range").GetString());
        Assert.Equal("2027-03-01", library.GetProperty("deprecated")[0].GetProperty("sunset").GetString());
        var blocked = Assert.Single(library.GetProperty("blocked").EnumerateArray());
        Assert.Equal("1.2.1", blocked.GetProperty("range").GetString());
        Assert.Equal("GHSA-xxxx-yyyy-zzzz", blocked.GetProperty("advisory").GetString());
        Assert.Equal("1.2.7", Assert.Single(library.GetProperty("knownBroken").EnumerateArray()).GetProperty("version").GetString());
        Assert.Equal(312000, Assert.Single(library.GetProperty("tested").EnumerateArray()).GetProperty("revision").GetInt64());
        Assert.Equal("https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json", json.GetProperty("matrixUrl").GetString());
    }

    [Fact]
    public void ServerInfoOmitsAdvisoryWhenThereIsNone()
    {
        var matrix = Matrix with { Blocked = [new("1.2.1", null, "bad", null)] };
        var blocked = JsonSerializer.SerializeToElement(matrix.ToServerInfo()).GetProperty("library").GetProperty("blocked")[0];
        Assert.False(blocked.TryGetProperty("advisory", out _));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"minimum":"banana","recommended":"1.1.0","libraries":[]}""")]
    [InlineData("""{"schemaVersion":1,"minimum":"1.1.0","recommended":"1.1.0","libraries":[],"blocked":[{"range":"nope","reason":"x"}]}""")]
    [InlineData("""{"schemaVersion":1,"minimum":"1.1.0","recommended":"1.1.0","libraries":[],"deprecated":[{"range":"<1.2.0"}]}""")]
    [InlineData("""{"schemaVersion":1,"minimum":"1.1.0","recommended":"1.1.0","libraries":[{"version":"x","status":"supported"}]}""")]
    [InlineData("""{"schemaVersion":1,"minimum":"1.1.0","recommended":"1.1.0","libraries":[{"version":"1.1.0","status":"fine"}]}""")]
    [InlineData("""{"schemaVersion":1,"minimum":"1.1.0","recommended":"1.1.0","libraries":[{"version":"1.1.0","status":"known-broken","engine":"banana"}]}""")]
    public void AnUnreadableMatrixIsRejectedAtLoad(string json)
        => Assert.Throws<InvalidDataException>(() => LibraryMatrix.Parse(json));

    [Theory]
    [InlineData("1.2.1", "1.2.1", true)]
    [InlineData("1.2.1", "1.2.2", false)]
    [InlineData("=1.2.1", "1.2.1", true)]
    [InlineData("<1.2.0", "1.1.9", true)]
    [InlineData("<1.2.0", "1.2.0", false)]
    [InlineData("<=1.2.0", "1.2.0", true)]
    [InlineData(">1.2.0", "1.2.1", true)]
    [InlineData(">=1.1.0 <1.3.0", "1.2.9", true)]
    [InlineData(">=1.1.0 <1.3.0", "1.3.0", false)]
    [InlineData(">=1.1.0 <1.3.0", "1.0.9", false)]
    [InlineData("<1.2.0", "1.2.0-rc.1", true)]
    public void LibraryRanges(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.Equal(expected, parsed.Contains(ParseVersion(version)));
    }

    [Theory]
    [InlineData("<=309133", 309133L, true)]
    [InlineData("<=309133", 309134L, false)]
    [InlineData(">100 <200", 150L, true)]
    public void RevisionRanges(string range, long revision, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.True(parsed.IsRevisionRange);
        Assert.Equal(expected, parsed.ContainsRevision(revision));
    }

    [Theory]
    [InlineData(">=26.10", "26.10.02", true)]
    [InlineData(">=26.10", "26.10", true)]
    [InlineData(">=26.10", "26.9.30", false)]
    [InlineData(">=26.10", "27.0.1", true)]
    [InlineData("<26.10", "26.9.30", true)]
    [InlineData("26.10", "26.10.0", true)]
    public void EngineRanges(string range, string engine, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.True(EngineBuild.TryParse(engine, out var build));
        Assert.Equal(expected, parsed.ContainsEngine(build));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData("<<1.0.0")]
    public void BrokenRangesDoNotParse(string range) => Assert.False(VersionRange.TryParse(range, out _));
}

/// <summary>S3 (optional bullet): a release-feed overlay can make the status stricter and never looser.</summary>
public sealed class LibraryCompatOverlayTests
{
    private static LibraryMatrix Base => LibraryMatrixEvaluationTests.Matrix;

    private static readonly string?[] Versions =
    [
        null, "0.9.0", "1.0.9", "1.1.0-rc.1", "1.1.0", "1.1.5", "1.1.9", "1.2.0", "1.2.1", "1.2.5", "1.2.7", "1.2.8", "1.2.9", "1.3.0", "1.4.0",
    ];

    private static int Rank(LibraryStatus status) => status == LibraryStatus.Unknown ? 0 : (int)status;

    private static void AssertNoLooser(LibraryMatrix merged)
    {
        foreach (var version in Versions)
        {
            foreach (var engine in new string?[] { null, "26.9.0", "26.10.02" })
            {
                var identity = LibraryMatrixEvaluationTests.Id(version, null, engine);
                var before = Base.Evaluate(identity).Status;
                var after = merged.Evaluate(identity).Status;
                Assert.True(Rank(after) >= Rank(before), $"{version ?? "legacy"} on {engine}: {before} became {after}");
            }
        }
    }

    [Fact]
    public void AnOverlayThatTriesToLoosenEverythingChangesNothing()
    {
        var overlay = new LibraryCompatOverlay(
            Minimum: "0.0.1",
            Recommended: "1.0.0",
            Legacy: "supported",
            Deprecated: [new MatrixRule("<1.2.0", "2030-01-01", null, null), new MatrixRule("garbage", "2026-01-01", null, null), new MatrixRule("<1.2.0", "not-a-date", null, null)],
            Blocked: []);

        var merged = Base.Merge(overlay);

        AssertNoLooser(merged);
        Assert.Equal(Base.Minimum, merged.Minimum);
        Assert.Equal(Base.Recommended, merged.Recommended);
        Assert.Equal(Base.Blocked, merged.Blocked);
        Assert.Equal(LibraryStatus.Supported, merged.LegacyStatus);
        // An announced sunset never moves.
        Assert.Equal("2027-03-01", merged.Deprecated.Single(d => d.Range == "<1.2.0").Sunset);
        Assert.Empty(merged.FeedDeprecated);
    }

    [Fact]
    public void ANewerMatrixIsRejectedNotGuessedAt()
    {
        var newer = LibraryMatrixEvaluationTests.Fixture.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var ex = Assert.Throws<InvalidDataException>(() => LibraryMatrix.Parse(newer));
        Assert.Contains("newer than this server supports", ex.Message);
    }

    [Fact]
    public void AMatrixWithoutASchemaVersionIsRejected()
    {
        var missing = LibraryMatrixEvaluationTests.Fixture.Replace("\"schemaVersion\": 1,", "");
        Assert.DoesNotContain("schemaVersion", missing);
        var ex = Assert.Throws<InvalidDataException>(() => LibraryMatrix.Parse(missing));
        Assert.Contains("missing schemaVersion", ex.Message);
    }

    [Fact]
    public void ANewerOverlayIsIgnoredWhole()
    {
        var merged = Base.Merge(new LibraryCompatOverlay(
            Blocked: [new MatrixRule("1.3.0", null, "Data loss on save", "GHSA-1111-2222-3333")],
            SchemaVersion: 2));

        Assert.Same(Base, merged);
        Assert.Equal(LibraryStatus.Supported, merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0")).Status);
        Assert.False(merged.OverlayApplied);
    }

    [Fact]
    public void AnAppliedOverlayFlagsTheServerInfoBlock()
    {
        var merged = Base.Merge(new LibraryCompatOverlay(Blocked: [new MatrixRule("1.3.0", null, "x", null)]));

        Assert.True(merged.OverlayApplied);
        Assert.False(Base.OverlayApplied);
    }

    [Fact]
    public void AnOverlayCannotRemoveABlockOrAnUnsupportedEntry()
    {
        var merged = Base.Merge(new LibraryCompatOverlay(Blocked: [], Deprecated: []));
        Assert.Equal(LibraryStatus.Blocked, merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.2.1")).Status);
        Assert.Equal(LibraryStatus.Unsupported, merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.2.9")).Status);
    }

    [Fact]
    public void AnOverlayCanBlockAVersionOnServersThatWereNotUpdated()
    {
        var merged = Base.Merge(new LibraryCompatOverlay(Blocked: [new MatrixRule("1.3.0", null, "Data loss on save", "GHSA-1111-2222-3333")]));

        var result = merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0"));
        Assert.Equal(LibraryStatus.Blocked, result.Status);
        Assert.Equal("GHSA-1111-2222-3333", result.Advisory);
        Assert.Equal(LibraryStatus.Supported, Base.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0")).Status);
        AssertNoLooser(merged);
    }

    [Fact]
    public void AnOverlayCanRaiseMinimumAndRecommended()
    {
        var merged = Base.Merge(new LibraryCompatOverlay(Minimum: "1.2.0", Recommended: "1.4.0"));
        Assert.Equal("1.2.0", merged.Minimum);
        Assert.Equal("1.4.0", merged.Recommended);
        Assert.Equal(LibraryStatus.Unsupported, merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.1.5")).Status);
        Assert.Equal(LibraryStatus.Outdated, merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0")).Status);
        Assert.Equal("1.4.0", merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0")).Latest);
        AssertNoLooser(merged);
    }

    [Fact]
    public void AFeedDeprecationDatesNewVersionsButNeverShortensAnAnnouncedSunset()
    {
        var merged = Base.Merge(new LibraryCompatOverlay(Deprecated:
        [
            new MatrixRule("<1.2.0", "2026-11-01", null, null),
            new MatrixRule("<1.3.0", "2026-10-15", null, null),
            new MatrixRule("1.2.5", "2026-10-30", null, null),
        ]));

        // The repeated range and every version the shipped matrix already deprecates keep the shipped date.
        Assert.Equal("2027-03-01", merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.1.9")).Sunset);
        Assert.Equal("2026-12-01", merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.1.5")).Sunset);
        Assert.Equal("2027-01-01", merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.2.8")).Sunset);
        // A version the shipped matrix does not deprecate takes the earliest matching feed date.
        var added = merged.Evaluate(LibraryMatrixEvaluationTests.Id("1.2.5"));
        Assert.Equal(LibraryStatus.Deprecated, added.Status);
        Assert.Equal("2026-10-15", added.Sunset);
        AssertNoLooser(merged);
    }

    [Fact]
    public void AnOverlayCanDeprecateOrUnsupportLegacyButNeverBlockIt()
    {
        var deprecated = Base.Merge(new LibraryCompatOverlay(Legacy: "deprecated", LegacySunset: "2027-09-01"));
        var result = deprecated.Evaluate(LibraryMatrixEvaluationTests.Id(null));
        Assert.Equal(LibraryStatus.Deprecated, result.Status);
        Assert.Equal("2027-09-01", result.Sunset);

        // A deprecation without a sunset date is not a deprecation.
        Assert.Equal(LibraryStatus.Supported, Base.Merge(new LibraryCompatOverlay(Legacy: "deprecated")).LegacyStatus);
        Assert.Equal(LibraryStatus.Unsupported, Base.Merge(new LibraryCompatOverlay(Legacy: "unsupported")).LegacyStatus);
        Assert.Equal(LibraryStatus.Supported, Base.Merge(new LibraryCompatOverlay(Legacy: "blocked")).LegacyStatus);
    }

    [Fact]
    public void MergingIsRepeatableAndDoesNotAccumulate()
    {
        var provider = new LibraryCompatProvider(Base);
        provider.SetOverlay(new LibraryCompatOverlay(Blocked: [new MatrixRule("1.3.0", null, "x", null)]));
        Assert.Equal(LibraryStatus.Blocked, provider.Current.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0")).Status);

        provider.SetOverlay(new LibraryCompatOverlay());
        Assert.Equal(LibraryStatus.Supported, provider.Current.Evaluate(LibraryMatrixEvaluationTests.Id("1.3.0")).Status);

        provider.SetOverlay(null);
        Assert.Same(provider.Embedded, provider.Current);
    }

    [Fact]
    public void OverlayParsesFromTheReleaseFeedShape()
    {
        var release = JsonSerializer.Deserialize<SboxNetworkStorage.Server.Updates.ReleaseInfo>("""
            { "version": "0.5.1", "migrationRequired": false, "security": false,
              "libraryCompat": { "minimum": "1.2.0", "blocked": [ { "range": "1.2.1", "reason": "r", "advisory": "GHSA-a" } ] } }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(release?.LibraryCompat);
        Assert.Equal("1.2.0", release.LibraryCompat.Minimum);
        Assert.Equal("GHSA-a", release.LibraryCompat.Blocked!.Single().Advisory);

        var without = JsonSerializer.Deserialize<SboxNetworkStorage.Server.Updates.ReleaseInfo>(
            """{ "version": "0.5.1", "migrationRequired": false, "security": false }""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(without!.LibraryCompat);
    }
}

/// <summary>S3: the version-keyed behavior table starts empty and cannot outlive its expiry.</summary>
public sealed class LibraryCompatShimsTests
{
    [Fact]
    public void EveryShimStatesItsReasonIssueAndExpiryAndHasNotExpired()
    {
        foreach (var shim in LibraryCompatShims.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(shim.Reason), $"{shim.Id}: reason");
            Assert.Matches(@"^[\w.-]+/[\w.-]+#\d+$", shim.Issue);
            Assert.True(SboxNetworkStorage.Server.Updates.SemanticVersion.TryParse(shim.ExpiresInServer, out _), $"{shim.Id}: expiry version");
            Assert.False(LibraryCompatShims.IsExpired(shim, BuildInfo.Version), $"{shim.Id} expired in {shim.ExpiresInServer}: delete it.");
            Assert.True(shim.Range == "legacy" || VersionRange.TryParse(shim.Range, out var range) && range.IsLibraryRange, $"{shim.Id}: range");
        }
    }

    [Fact]
    public void TheTableIsEmptyAtFirst() => Assert.Empty(LibraryCompatShims.All);

    [Fact]
    public void AShimAppliesToItsRangeUntilTheExpiryVersion()
    {
        LibraryCompatShim[] shims =
        [
            new("old-dates", "<1.2.0", "1.1.x sends local dates", "sbox-cool/sbox-network-storage-server#1", "0.7.0"),
            new("legacy-quirk", "legacy", "Legacy builds omit the field", "sbox-cool/sbox-network-storage-server#2", "0.8.0"),
        ];
        var old = LibraryMatrixEvaluationTests.Id("1.1.5");
        var current = LibraryMatrixEvaluationTests.Id("1.3.0");
        var legacy = LibraryMatrixEvaluationTests.Id(null);

        Assert.True(LibraryCompatShims.Applies(shims, "old-dates", old, "0.6.9"));
        Assert.False(LibraryCompatShims.Applies(shims, "old-dates", current, "0.6.9"));
        Assert.False(LibraryCompatShims.Applies(shims, "old-dates", legacy, "0.6.9"));
        Assert.False(LibraryCompatShims.Applies(shims, "old-dates", old, "0.7.0"));
        Assert.False(LibraryCompatShims.Applies(shims, "old-dates", old, "0.7.0-rc.1"));
        Assert.True(LibraryCompatShims.Applies(shims, "legacy-quirk", legacy, "0.7.0"));
        Assert.False(LibraryCompatShims.Applies(shims, "legacy-quirk", old, "0.7.0"));
        Assert.False(LibraryCompatShims.Applies(shims, "unknown-id", old, "0.6.9"));
        Assert.True(LibraryCompatShims.IsExpired(shims[0], "not a version"));
    }
}

/// <summary>S3: the matrix compiled into the binary loads and says what the issues require.</summary>
public sealed class EmbeddedLibraryMatrixTests
{
    [Fact]
    public void EmbeddedMatrixLoadsAndIsTheSeededPolicy()
    {
        var matrix = LibraryMatrix.LoadEmbedded();

        Assert.Equal("1.1.0", matrix.Minimum);
        Assert.Equal(LibraryMatrix.SupportedSchemaVersion, matrix.SchemaVersion);
        Assert.Equal("1.1.0", matrix.Recommended);
        Assert.Equal("1.2.0", matrix.Latest);
        Assert.Equal(LibraryStatus.Supported, matrix.LegacyStatus);
        Assert.Equal("known-broken", Assert.Single(matrix.Libraries, l => l.Version == "1.0.165702").Status);
        Assert.Equal("0.5.0", Assert.Single(matrix.Libraries, l => l.Version == "1.1.0").MinServer);
        Assert.Empty(matrix.Blocked);
        Assert.Empty(matrix.Deprecated);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("1.1.0", "supported")]
    [InlineData("1.2.0", "supported")]
    [InlineData("1.0.9", "unsupported")]
    [InlineData("0.9.0", "unsupported")]
    public void EmbeddedMatrixGivesTheSeededStatuses(string? version, string status)
        => Assert.Equal(status, LibraryMatrix.LoadEmbedded().Evaluate(LibraryMatrixEvaluationTests.Id(version)).Status.ToWire());

    [Fact]
    public void EmbeddedMatrixTreatsTheIncidentBuildAsUnsupportedOnEngine26_10()
    {
        var matrix = LibraryMatrix.LoadEmbedded();
        Assert.Equal(LibraryStatus.Unsupported, matrix.Evaluate(LibraryMatrixEvaluationTests.Id("1.0.165702", 165702, "26.10.02")).Status);
    }
}
