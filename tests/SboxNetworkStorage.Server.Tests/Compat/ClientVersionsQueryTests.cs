using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>The Clients report groups stored aggregates and never treats legacy clients as below a minimum.</summary>
public sealed class ClientVersionsQueryTests
{
    private sealed class FakeStore(IReadOnlyList<ClientVersionRow> rows) : IClientVersionStore
    {
        public Task WriteClientVersionsAsync(IReadOnlyList<ClientVersionDelta> batch, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ClientVersionRow>> ReadClientVersionsAsync(string projectId, int sinceDay, CancellationToken ct) => Task.FromResult(rows);
        public Task PurgeClientVersionsAsync(int beforeDay, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ObservedLibrary>> ReadObservedLibrariesAsync(int sinceDay, CancellationToken ct) => Task.FromResult<IReadOnlyList<ObservedLibrary>>([]);
        public Task WriteClientOutcomesAsync(IReadOnlyList<ClientOutcomeDelta> batch, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ClientOutcomeTotals>> ReadClientOutcomeTotalsAsync(long sinceHour, CancellationToken ct) => Task.FromResult<IReadOnlyList<ClientOutcomeTotals>>([]);
        public Task PurgeClientOutcomesAsync(long beforeHour, CancellationToken ct) => Task.CompletedTask;
    }

    private static ClientVersionRow Row(string version, long requests, long? revision = null, string clientType = "game", long? game = 7)
        => new(new ClientVersionKey("proj_example", 1000, version, revision, clientType, game), requests, 1_700_000_000_000, new byte[PlayerCountSketch.Size]);

    private static ClientVersionsQuery Query(IReadOnlyList<ClientVersionRow> rows)
        => new(new FakeStore(rows), new LibraryCompatProvider(), new ClientVersionTracker(TimeProvider.System), TimeProvider.System);

    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.2.0", "1.1.0", false)]
    [InlineData("legacy", "1.1.0", false)]   // no version: never below a minimum, never refused
    [InlineData("garbage", "1.1.0", false)]  // unparseable: same bucket as legacy
    [InlineData("1.0", "1.1.0", false)]      // the old constant is not SemVer
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0.0", "garbage", false)]  // an unreadable minimum blocks nothing
    [InlineData("1.0.0", "", false)]
    public void BelowMinimum_never_matches_legacy_or_unparseable_versions(string version, string? minimum, bool expected)
        => Assert.Equal(expected, ClientVersionsQuery.BelowMinimum(version, minimum));

    [Fact]
    public async Task Report_groups_by_identity_and_keeps_legacy_out_of_the_affected_share()
    {
        var query = Query([Row("1.1.0", 8), Row("1.1.0", 2), Row("0.9.0", 5), Row("legacy", 5)]);
        var report = await query.ReadAsync("proj_example", 30, "1.1.0", CancellationToken.None);

        Assert.Equal(20, report.Requests);
        Assert.Equal("1.1.0", report.Minimum);
        var current = report.Versions.Single(v => v.LibraryVersion == "1.1.0");
        Assert.Equal(10, current.Requests);
        Assert.Equal("supported", current.Status);
        var old = report.Versions.Single(v => v.LibraryVersion == "0.9.0");
        Assert.Equal("unsupported", old.Status);
        var legacy = report.Versions.Single(v => v.LibraryVersion == "legacy");
        Assert.Equal("unknown", legacy.Status);
        Assert.Equal(25, report.AffectedPercent);   // only the 5 old-library requests
        Assert.Equal(25, report.OutdatedPercent);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task Without_a_minimum_only_the_matrix_marks_versions_affected()
    {
        var query = Query([Row("1.1.0", 8), Row("0.9.0", 2)]);
        var report = await query.ReadAsync("proj_example", 30, null, CancellationToken.None);

        Assert.Equal("unsupported", report.Versions.Single(v => v.LibraryVersion == "0.9.0").Status);
        Assert.Equal(20, report.AffectedPercent);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task Report_flags_truncation_when_the_store_returns_more_than_the_read_limit()
    {
        // The store reads one row past its limit so the tail cut is detectable; every row is its
        // own group here, so the report carries exactly the limit and says so.
        var sketch = new byte[PlayerCountSketch.Size];
        var rows = Enumerable.Range(0, RelationalNetworkStorageStore.ReadLimit + 1)
            .Select(i => new ClientVersionRow(new ClientVersionKey("proj_example", 1000, "1.1.0", null, "game", i), 1, 1_700_000_000_000, sketch))
            .ToArray();
        var report = await Query(rows).ReadAsync("proj_example", 30, null, CancellationToken.None);

        Assert.True(report.Truncated);
        Assert.Equal(RelationalNetworkStorageStore.ReadLimit, report.Versions.Count);
        Assert.Equal(RelationalNetworkStorageStore.ReadLimit, report.Requests);
    }
}
