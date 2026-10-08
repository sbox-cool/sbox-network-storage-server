using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

public sealed class NetworkStorageAuthSessionServiceTests
{
    private const string Secret = "unit-test-auth-session-secret";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-06-16T00:00:00Z");

    private static NetworkStorageAuthSessionService Service(out FixedTimeProvider time, string secret = Secret)
    {
        time = new FixedTimeProvider(FixedNow);
        return new NetworkStorageAuthSessionService(new FixedSecret(secret), time);
    }

    [Fact]
    public void Create_ProducesStatelessToken_ThatValidates()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        Assert.True(created.Ok);
        Assert.NotNull(created.Token);
        Assert.StartsWith("sbox_sess_", created.Token);
        Assert.Contains('.', created.Token);
        Assert.Equal(3600, created.TtlSeconds);
        Assert.Equal("76561198000000001", created.Session!.SteamId);
        Assert.Equal(42, created.Session.UserId);
        Assert.Null(created.Session.RevokedAt);

        var validated = service.Validate("proj-1", created.Token!);
        Assert.True(validated.Ok);
        Assert.Equal("76561198000000001", validated.Session!.SteamId);
        Assert.Equal(42, validated.Session.UserId);
    }

    [Fact]
    public void Validate_RejectsTokenForDifferentProject()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var result = service.Validate("proj-2", created.Token!);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
        Assert.Equal("Auth session project does not match this request.", result.Message);
    }

    [Fact]
    public void Validate_RejectsExpiredToken()
    {
        var service = Service(out var time);
        var created = service.Create(42, "proj-1", "76561198000000001", 60);

        time.Now = FixedNow.AddSeconds(61);
        var result = service.Validate("proj-1", created.Token!);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_EXPIRED", result.Code);
    }

    [Fact]
    public void Validate_RejectsTamperedSignature()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);
        var tampered = created.Token![..^2] + (created.Token[^1] == 'A' ? "BB" : "AA");

        var result = service.Validate("proj-1", tampered);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public void Validate_RejectsSteamIdMismatch_WhenSteamIdSupplied()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var result = service.Validate("proj-1", created.Token!, steamId: "76561198000000002");

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_STEAMID_MISMATCH", result.Code);
    }

    [Fact]
    public void Validate_RejectsMalformedToken()
    {
        var service = Service(out _);
        Assert.Equal("AUTH_SESSION_INVALID", service.Validate("proj-1", "not-a-session-token").Code);
        Assert.Equal("AUTH_SESSION_INVALID", service.Validate("proj-1", "sbox_sess_nodothere").Code);
        Assert.Equal("AUTH_SESSION_INVALID", service.Validate("proj-1", "").Code);
    }

    [Fact]
    public void Validate_RejectsTokenSignedWithDifferentSecret()
    {
        var minter = Service(out _, secret: "secret-A");
        var created = minter.Create(42, "proj-1", "76561198000000001", 3600);

        var verifier = new NetworkStorageAuthSessionService(new FixedSecret("secret-B"), new FixedTimeProvider(FixedNow));
        var result = verifier.Validate("proj-1", created.Token!);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public void Refresh_MintsNewTokenForSamePlayer()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var refreshed = service.Refresh("proj-1", created.Token!, 1800);

        Assert.True(refreshed.Ok);
        Assert.NotNull(refreshed.Token);
        Assert.Equal(1800, refreshed.TtlSeconds);
        Assert.Equal("76561198000000001", refreshed.Session!.SteamId);
        Assert.Equal(42, refreshed.Session.UserId);
        Assert.True(service.Validate("proj-1", refreshed.Token!).Ok);
    }

    [Fact]
    public void Refresh_RejectsInvalidToken()
    {
        var service = Service(out _);
        var result = service.Refresh("proj-1", "sbox_sess_bogus.sig", 3600);
        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public void Revoke_ReturnsSessionWithRevokedTimestamp()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var result = service.Revoke("proj-1", created.Token!);

        Assert.True(result.Ok);
        Assert.NotNull(result.Session!.RevokedAt);
    }

    [Theory]
    [InlineData(10, 60)]      // below min clamps up
    [InlineData(3600, 3600)]  // within range
    [InlineData(999999, 86400)] // above max clamps down
    public void NormalizeTtl_ClampsToBounds(int input, int expected)
        => Assert.Equal(expected, NetworkStorageAuthSessionService.NormalizeTtl(input));

    private sealed class FixedSecret(string secret) : IAuthSessionSecretProvider
    {
        public string GetSecret() => secret;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
