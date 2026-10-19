using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Server.Endpoints;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Golden vectors in Fixtures/network-storage-fixtures/encrypted-request-vectors.json were
/// produced by the reference Node implementation (services/network-storage-encrypted-requests.js
/// and services/network-storage-auth-sessions.js). The server must decrypt and validate them
/// exactly as minted, with no re-encryption and no HTTP factory.
/// </summary>
public sealed class EncryptedRequestGoldenVectorTests
{
    private static readonly JsonElement Vectors = LoadVectors();

    private static JsonElement LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-storage-fixtures", "encrypted-request-vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    [Fact]
    public void ReferenceSteamEnvelope_DecryptsAndBindsToSteamId()
    {
        var steam = Vectors.GetProperty("steam");

        var result = EncryptedEndpointEnvelope.Decrypt(steam.GetProperty("envelope"), Text(Vectors, "apiKey"),
            Text(Vectors, "projectId"), Text(Vectors, "endpointSlug"), Text(steam, "steamId"), sessionId: null);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(steam.GetProperty("expectedCoins").GetInt32(), result.Payload.GetProperty("coins").GetInt32());
    }

    [Fact]
    public void ReferenceSessionToken_ValidatesAndBindsSessionEnvelope()
    {
        var session = Vectors.GetProperty("session");
        var secrets = new FixedSecret(Text(Vectors, "authSessionSecret"));
        var service = new NetworkStorageAuthSessionService(secrets);

        var validated = service.Validate(Text(Vectors, "projectId"), Text(session, "token"));

        Assert.True(validated.Ok, validated.Message);
        Assert.Equal(Text(session, "sessionId"), validated.Session!.Id);

        var result = EncryptedEndpointEnvelope.Decrypt(session.GetProperty("envelope"), Text(Vectors, "apiKey"),
            Text(Vectors, "projectId"), Text(Vectors, "endpointSlug"), steamId: null, sessionId: validated.Session.Id);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(session.GetProperty("expectedCoins").GetInt32(), result.Payload.GetProperty("coins").GetInt32());
    }

    private sealed class FixedSecret(string secret) : IAuthSessionSecretProvider
    {
        public string GetSecret() => secret;
    }
}
