using Sandbox;
using System;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Drives the real s&amp;box Network Storage client library inside a headless engine (TestAppSystem)
/// against a local sbox-ns whose project was bootstrapped from the parity corpus (run.sh does this).
/// Env: NS_E2E_BASEURL, NS_E2E_PROJECT, NS_E2E_KEY (public key). The project has auth disabled:
/// headless runs have no s&amp;box player token.
/// </summary>
[TestClass]
public class ClientAlignmentTests
{
	static string Env( string name, string fallback = null )
		=> Environment.GetEnvironmentVariable( name ) is { Length: > 0 } v ? v : fallback
			?? throw new InvalidOperationException( $"{name} is not set" );

	[TestInitialize]
	public void Configure()
		=> NetworkStorage.Configure( Env( "NS_E2E_PROJECT" ), Env( "NS_E2E_KEY" ), Env( "NS_E2E_BASEURL", "http://127.0.0.1:8080" ) );

	static string Raw( JsonElement? value ) => value.HasValue ? value.Value.GetRawText() : "null";

	static void AssertCallSucceeded( string tag, JsonElement? value )
	{
		NetworkStorage.TryGetLastEndpointError( tag, out var code, out var message );
		Console.WriteLine( $"{tag} -> {Raw( value )} err={code} {message}" );
		Assert.IsTrue( value.HasValue, $"{tag} returned null: {code} {message}" );
	}

	[TestMethod]
	public void HttpAllowlist_PermitsTheLoopbackPortTheServerUses()
	{
		// Game code may only reach loopback on 80/443/8080/8443 unless -allowlocalhttp or a standalone build.
		Assert.IsTrue( Http.IsAllowed( new Uri( Env( "NS_E2E_BASEURL", "http://127.0.0.1:8080" ) + "/v3/x" ) ) );
	}

	[TestMethod]
	public async Task RevisionInit_WithoutASyncedGamePackage_IsOk()
	{
		// Self-hosted projects often never run package-sync; the handshake must still succeed.
		var result = await NetworkStorageRevisionInit.SendInitAsync();
		Console.WriteLine( $"revision-init ok={result.Ok} outdated={result.IsOutdatedRevision} current={result.CurrentRevision} message={result.Message}" );
		Assert.IsTrue( result.Ok, result.Message );
		Assert.IsFalse( result.IsOutdatedRevision );
	}

	[TestMethod]
	public async Task Documents_SaveUpdateReadDelete()
	{
		const string collection = "players";
		var doc = Game.SteamId.ToString();
		var gold = Random.Shared.Next( 1, 1_000_000 );

		AssertCallSucceeded( "storage-save", await NetworkStorage.SaveDocument( collection, doc, new { gold, playerName = "e2e" } ) );
		AssertCallSucceeded( "storage-save", await NetworkStorage.UpdateDocument( collection, doc,
			NetworkStorageOperation.Increment( "gold", 25, source: "e2e", reason: "alignment" ),
			NetworkStorageOperation.Set( "playerName", "e2e-updated" ) ) );

		var read = await NetworkStorage.GetDocument( collection, doc );
		AssertCallSucceeded( "storage", read );
		Assert.AreEqual( gold + 25, read.Value.GetProperty( "gold" ).GetInt32(), "ops were not applied on the server" );
		Assert.AreEqual( "e2e-updated", read.Value.GetProperty( "playerName" ).GetString() );

		await NetworkStorage.DeleteDocument( collection, doc );
		Assert.IsFalse( (await NetworkStorage.GetDocument( collection, doc )).HasValue, "document still readable after delete" );
	}

	[TestMethod]
	public async Task Endpoints_SaveThenLoadProfile()
	{
		var name = "e2e-" + Random.Shared.Next( 1, 1_000_000 );
		AssertCallSucceeded( "save-profile", await NetworkStorage.CallEndpoint( "save-profile", new { playerName = name } ) );

		var loaded = await NetworkStorage.CallEndpoint( "load-profile" );
		AssertCallSucceeded( "load-profile", loaded );
		StringAssert.Contains( Raw( loaded ), name );
	}

	[TestMethod]
	public async Task Endpoints_ConditionRejectionReachesTheClientAsAFailure()
	{
		// The server answers 400 INVALID_AMOUNT (as the hosted service does); s&box Http hides 4xx bodies,
		// so the library records HTTP_ERROR and returns null.
		var result = await NetworkStorage.CallEndpoint( "mine-ore", new { amount = -1 } );
		NetworkStorage.TryGetLastEndpointError( "mine-ore", out var code, out var message );
		Console.WriteLine( $"mine-ore(-1) -> {Raw( result )} err={code} {message}" );
		Assert.IsFalse( result.HasValue );
		Assert.AreEqual( "HTTP_ERROR", code );
		StringAssert.Contains( message, "400" );
	}

	[TestMethod]
	public async Task GameValues_AreReadable()
	{
		var values = await NetworkStorage.GetGameValues();
		Console.WriteLine( $"values -> {Raw( values )[..Math.Min( 300, Raw( values ).Length )]}" );
		Assert.IsTrue( values.HasValue );
		StringAssert.Contains( Raw( values ), "upgrades" );
	}

	[TestMethod]
	public async Task Queries_PublicLeaderboardRuns()
	{
		var result = await NetworkStorage.RunQuery( "top_miners" );
		Console.WriteLine( $"top_miners -> {Raw( result )[..Math.Min( 300, Raw( result ).Length )]}" );
		Assert.IsTrue( result.HasValue );
	}
}
