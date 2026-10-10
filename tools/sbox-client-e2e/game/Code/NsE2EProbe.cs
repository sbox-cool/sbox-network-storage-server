using System;
using System.Threading.Tasks;

/// <summary>
/// Runs once per scene: configure Network Storage against a local sbox-ns, write a document, read it back.
/// Configure with launch flags: +nse2e_project proj_x +nse2e_key sbox_ns_x [+nse2e_url http://127.0.0.1:8080].
/// Output lines are prefixed with [NSE2E]; the final line is "[NSE2E] RESULT PASS|FAIL".
/// </summary>
public sealed class NsE2EProbe : GameObjectSystem<NsE2EProbe>
{
	[ConVar( "nse2e_project", ConVarFlags.Hidden )] public static string ProjectId { get; set; } = "";
	[ConVar( "nse2e_key", ConVarFlags.Hidden )] public static string PublicKey { get; set; } = "";
	[ConVar( "nse2e_url", ConVarFlags.Hidden )] public static string BaseUrl { get; set; } = "http://127.0.0.1:8080";

	static bool _ran;

	public NsE2EProbe( Scene scene ) : base( scene )
	{
		if ( _ran ) return;
		_ran = true;
		_ = Run();
	}

	static async Task Run()
	{
		try
		{
			Log.Info( $"[NSE2E] start dedicated={Application.IsDedicatedServer} steamId={Game.SteamId}" );
			NetworkStorage.Configure( ProjectId, PublicKey, BaseUrl );
			Log.Info( $"[NSE2E] apiRoot={NetworkStorage.ApiRoot}" );

			var gold = Game.Random.Int( 1, 1_000_000 );
			// Dedicated server: the library sends the +network_storage_secret_key secret key, so this is a trusted write.
			var saved = await NetworkStorage.SaveDocument( "players", "76561198000000077", new { gold, playerName = "dedicated" } );
			NetworkStorage.TryGetLastEndpointError( "storage-save", out var saveCode, out var saveMessage );
			Log.Info( $"[NSE2E] save={(saved.HasValue ? saved.Value.GetRawText() : "null")} err={saveCode} {saveMessage}" );

			var read = await NetworkStorage.GetDocument( "players", "76561198000000077" );
			NetworkStorage.TryGetLastEndpointError( "storage", out var getCode, out var getMessage );
			var json = read.HasValue ? read.Value.GetRawText() : "null";
			Log.Info( $"[NSE2E] get={json} err={getCode} {getMessage}" );
			Log.Info( json.Contains( gold.ToString() ) ? "[NSE2E] RESULT PASS" : "[NSE2E] RESULT FAIL" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[NSE2E] RESULT FAIL exception {e}" );
		}
	}
}
