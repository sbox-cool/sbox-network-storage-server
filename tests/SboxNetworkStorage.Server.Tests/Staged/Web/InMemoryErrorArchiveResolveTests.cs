using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Covers the resolution workflow that lets /admin/errors show only open issues:
// resolved errors drop out of ListRecentAsync but stay retrievable by id, and
// MarkResolvedAsync is idempotent (only the first resolve of a given id counts).
public sealed class InMemoryErrorArchiveResolveTests
{
    private static CapturedErrorDto Make(string id) =>
        new(id, DateTimeOffset.UtcNow, ".NET backend", "GET", "/x", 500, "InvalidOperationException", "req", "boom", "stack");

    [Fact]
    public async Task ListRecent_ExcludesResolved_ButGetStillReturnsIt()
    {
        var archive = new InMemoryErrorArchive();
        var open = Guid.NewGuid().ToString("D");
        var resolved = Guid.NewGuid().ToString("D");
        await archive.CaptureAsync(Make(open), CancellationToken.None);
        await archive.CaptureAsync(Make(resolved), CancellationToken.None);

        Assert.Equal(2, (await archive.ListRecentAsync(100, CancellationToken.None)).Count);

        Assert.True(await archive.MarkResolvedAsync(resolved, 7, "fixed", CancellationToken.None));

        var list = await archive.ListRecentAsync(100, CancellationToken.None);
        Assert.Equal(open, Assert.Single(list).Id);

        // Resolved errors must remain openable from a direct detail link.
        Assert.NotNull(await archive.GetAsync(resolved, CancellationToken.None));
    }

    [Fact]
    public async Task MarkResolved_UnknownId_ReturnsFalse()
    {
        var archive = new InMemoryErrorArchive();
        Assert.False(await archive.MarkResolvedAsync(Guid.NewGuid().ToString("D"), 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task MarkResolved_IsIdempotent_SecondCallReturnsFalse()
    {
        var archive = new InMemoryErrorArchive();
        var id = Guid.NewGuid().ToString("D");
        await archive.CaptureAsync(Make(id), CancellationToken.None);

        Assert.True(await archive.MarkResolvedAsync(id, 1, null, CancellationToken.None));
        Assert.False(await archive.MarkResolvedAsync(id, 1, null, CancellationToken.None));
    }
}
