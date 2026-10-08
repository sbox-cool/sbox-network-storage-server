using System.Threading;
using System.Threading.Tasks;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class InMemoryAppendRateLimiterTests
{
    [Fact]
    public async Task Allows_first_request()
    {
        var limiter = new InMemoryAppendRateLimiter();
        var result = await limiter.CheckAsync("p", "c", "player", 3, "123", CancellationToken.None);
        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Rejects_when_limit_exceeded()
    {
        var limiter = new InMemoryAppendRateLimiter();
        for (var i = 0; i < 3; i++)
        {
            var r = await limiter.CheckAsync("p", "c", "player", 3, "123", CancellationToken.None);
            Assert.True(r.Allowed);
        }

        var result = await limiter.CheckAsync("p", "c", "player", 3, "123", CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal("RATE_LIMIT_DAILY", result.Code);
        Assert.Contains("max 3 saves per day", result.Message);
    }

    [Fact]
    public async Task Tracks_players_separately()
    {
        var limiter = new InMemoryAppendRateLimiter();
        for (var i = 0; i < 2; i++)
        {
            Assert.True((await limiter.CheckAsync("p", "c", "player", 2, "a", CancellationToken.None)).Allowed);
        }

        Assert.True((await limiter.CheckAsync("p", "c", "player", 2, "b", CancellationToken.None)).Allowed);
        Assert.False((await limiter.CheckAsync("p", "c", "player", 2, "a", CancellationToken.None)).Allowed);
    }

    [Fact]
    public async Task Collection_mode_tracks_total_writes()
    {
        var limiter = new InMemoryAppendRateLimiter();
        for (var i = 0; i < 2; i++)
        {
            Assert.True((await limiter.CheckAsync("p", "c", "collection", 2, "a", CancellationToken.None)).Allowed);
        }
        Assert.False((await limiter.CheckAsync("p", "c", "collection", 2, "b", CancellationToken.None)).Allowed);
    }
}
