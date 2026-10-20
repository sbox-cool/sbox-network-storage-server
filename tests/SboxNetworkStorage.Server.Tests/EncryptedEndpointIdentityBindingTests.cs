using System.Text.Json;
using SboxNetworkStorage.Server.Endpoints;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Identity binding of an encrypted envelope against the auth session it was sent with. The check is shared by v1
/// and v2, so these tests pin that an envelope may only name the session's own player.
/// </summary>
public sealed class EncryptedEndpointIdentityBindingTests
{
    private const string SessionSteamId = "76561198000000001";
    private const string OtherSteamId = "76561198000000002";
    private const string SessionId = "session-1";

    [Fact]
    public void SteamEnvelope_NamingAnotherPlayer_IsRefusedWhenSessionPresent()
    {
        using var envelope = JsonDocument.Parse($$"""{"steamId":"{{OtherSteamId}}"}""");

        var result = EncryptedEndpointEnvelope.ValidateIdentityBinding(envelope.RootElement, SessionSteamId, SessionId);

        AssertRefused(result, 403, "AUTH_SESSION_STEAMID_MISMATCH");
    }

    [Fact]
    public void SessionEnvelope_CarryingAnotherSteamId_IsRefused()
    {
        using var envelope = JsonDocument.Parse($$"""{"sessionId":"{{SessionId}}","steamId":"{{OtherSteamId}}"}""");

        var result = EncryptedEndpointEnvelope.ValidateIdentityBinding(envelope.RootElement, SessionSteamId, SessionId);

        AssertRefused(result, 403, "AUTH_SESSION_STEAMID_MISMATCH");
    }

    [Fact]
    public void SteamEnvelope_NamingTheSessionPlayer_IsAccepted()
    {
        using var envelope = JsonDocument.Parse($$"""{"steamId":"{{SessionSteamId}}"}""");

        Assert.Null(EncryptedEndpointEnvelope.ValidateIdentityBinding(envelope.RootElement, SessionSteamId, SessionId));
    }

    [Fact]
    public void SteamEnvelope_WithoutSession_MustMatchTheAuthenticatedSteamId()
    {
        using var envelope = JsonDocument.Parse($$"""{"steamId":"{{OtherSteamId}}"}""");

        var result = EncryptedEndpointEnvelope.ValidateIdentityBinding(envelope.RootElement, SessionSteamId, sessionId: null);

        AssertRefused(result, 401, "REQUEST_SIGNATURE_INVALID");
    }

    private static void AssertRefused(EncryptedEndpointEnvelope.Result? result, int status, string code)
    {
        Assert.True(result.HasValue, "The envelope should have been refused.");
        Assert.False(result.Value.Ok);
        Assert.Equal(status, result.Value.Status);
        Assert.Equal(code, result.Value.Code);
    }
}
