# Pointing your s&box game at a self-hosted server

sbox Network Storage Server speaks the same HTTP API as the managed service at
[sbox.cool](https://sbox.cool/tools/network-storage). The published s&box
client library works without changes: you only change its base URL.

- s&box library: <https://sbox.game/sboxcool/network-storage>
- Client source: <https://github.com/sbox-cool/sbox-network-storage>
- API and library docs: <https://sbox.cool/wiki/network-storage-v3>

The client appends the API version itself (`/v3/...`), so the base URL is just
the scheme, host and port of your server, with no path and no trailing slash.

## 1. Find your server URL

| How players reach the server | Base URL |
| --- | --- |
| Domain with HTTPS (`tls.mode` = `acme` or `certificate`, or a reverse proxy) | `https://ns.example.com` |
| Domain over plain HTTP | `http://ns.example.com:8080` |
| Public IP and port | `http://203.0.113.10:8080` |
| Same machine as the game (development) | `http://localhost:8080` |

Check the server responds from the machine running s&box:

```sh
curl -i http://your-server:8080/health
curl http://your-server:8080/v3/server-info
```

Make sure the port is open in your firewall and forwarded on your router if the
server is behind NAT.

## 2. Create a project and API keys

Use the local-owner [dashboard](admin-panel.md#connect-a-game), or run the CLI
on the server. A project created in the dashboard gets a public key, and its
overview shows the filled-in `NetworkStorage.Configure` line and Setup window
values to copy.

On the CLI:

```sh
sbox-ns project create "My Game"                          # prints the project ID
sbox-ns key create <projectId> --type public --label game
sbox-ns key create <projectId> --type secret --label editor
```

## 3. Configure the editor

1. Open your game in the s&box editor.
2. Open **Editor > Network Storage > Setup**.
3. Enter the **Project ID**, **Public API Key** and **Secret Key** from step 2.
4. Set **Base URL** to your server URL from step 1.
5. Leave **CDN URL** empty unless you serve a CDN in front of your server.
6. Save, then use **Editor > Network Storage > Sync Tool** to push YAML Source
   definitions for collections, endpoints and workflows (`.yml` preferred;
   `.yaml` also accepted).

The Setup window stores the base URL in your project's Network Storage
credentials, and the generated config is picked up automatically by
`NetworkStorage.AutoConfigure()` at runtime.

### Staged pushes and editor play sessions

The Sync Tool's **Push Staged** sends `x-ns-publish-target: next`. Endpoint and
collection definitions go into `revision-overrides.json`, leaving live game
definitions unchanged. Workflows and saved tests remain live. Staging requires
a synced game package with a numeric current revision; without one, the push
goes live and reports `publishTarget: live` and `stagedFallback` in its response.

Management lists with `includeStaged=true` include live definitions and staged
copies marked `revisionTarget: next`. Explicit `revisionTarget=live` returns
only live definitions. Editor play sessions and management endpoint tests
targeting `next` use staged endpoint and collection definitions, falling back
to live definitions for anything not staged. Player records are shared, not a
separate staging database: a next-targeted runtime call can change real data.
Dry-run management tests do not persist those writes.

Syncing a new game package revision promotes staged definitions into live and
clears the overrides. Use the normal live target for changes that should take
effect immediately.

The command-line sync tool's `PUT endpoints?replaceAll=true` replaces the live
endpoint set after a successful push: omitted endpoints are deleted. Without
that query parameter, pushes only upsert the supplied endpoints. Staged pushes
never delete live definitions.

## 4. Or configure in code

```csharp
NetworkStorage.Configure( projectId, publicKey, "https://ns.example.com" );
```

`Configure` also accepts an optional API version and CDN URL, exactly as with the
managed service. Only pass the public API key in game code; the secret key
belongs in the editor only.

## Collection access from game clients

The public key ships inside every game build, so anyone can extract it. The
direct document API (`GET`, `POST` and `DELETE /v3/storage/{projectId}/{collection}/{key}`,
the `/api/storage` alias and `/probe`) therefore treats public-key calls as
untrusted:

- The collection must declare `accessMode: public`. Any other value, or no
  `accessMode` at all, is endpoint-only and answers `403 ENDPOINT_ONLY`. Route
  those reads and writes through endpoints or queries. Collections created in
  the dashboard start as endpoint-only.
- Deleting needs `allowRecordDelete: true`, otherwise `403 RECORD_DELETE_DISABLED`.
- Writing or deleting in a collection that is not declared at all answers `404`.
- When the project requires s&box authentication, the call must carry a valid
  s&box token (`x-sbox-token` with `x-steam-id`) or auth session, otherwise
  `401 SBOX_AUTH_FAILED`. Writes and deletes in a per-player collection must then
  target the player's own document: the key is the verified Steam ID, or starts
  with `{steamId}_` for save slots. Anything else answers `403 FORBIDDEN`. Reads of
  public collections are not limited to the player's own documents.
- With s&box authentication turned off (development), the Steam ID the client
  sends is trusted as is, but the `accessMode` and `allowRecordDelete` rules
  still apply.

Secret keys (dedicated servers, tools) count as trusted backend calls: they need
`collections` execute permission and may read, write and delete in any
collection without s&box tokens.

## Error codes

The project's **Logs** and **Errors** tabs in the [dashboard](admin-panel.md#request-log-and-errors)
list rejected requests and explain them. The common ones:

| Status and code | Cause | Fix |
| --- | --- | --- |
| `401 UNAUTHORIZED` | The API key is missing, wrong or disabled. | Check the public key passed to `NetworkStorage.Configure`. |
| `401 SBOX_AUTH_FAILED` | The project requires s&box authentication and the player's token was missing or did not verify. | Run the game through s&box, or turn off **Require s&box authentication** for development. |
| `403 ENDPOINT_ONLY` | The collection is not `accessMode: public`. | Call it through an endpoint, or set `accessMode: public`. |
| `403 RECORD_DELETE_DISABLED` | The collection does not allow deletes from game clients. | Set `allowRecordDelete: true`, or delete through an endpoint. |
| `403 FORBIDDEN` | A player wrote another player's record, or a secret key lacks the permission. | Use the player's own key, or grant the permission on the secret key. |
| `404` | The project, collection, endpoint or record does not exist. | Save or sync the definition and check the ID in the game code. |
| `409 STALE_SAVE` | A save was based on older data than the stored record. | Reload the record and retry. |
| `SAVE_NOT_CONFIRMED` | The client could not confirm a save reached the server. | Check the player's connection and the request log around that time. |

In the game, read the last error of an endpoint call by its slug:

```csharp
var result = await NetworkStorage.CallEndpoint( "grant-coins", new { } );
if ( !result.HasValue )
{
    NetworkStorage.TryGetLastEndpointError( "grant-coins", out var code, out var message );
    Log.Warning( $"{code}: {message}" );
}
```

s&box hides the body of 4xx responses from game code, so the code there can be
`HTTP_ERROR` with the status in the message. The dashboard's **Logs** tab shows
the status for the same request.

On the server, `sbox-ns logs -f` follows the server log.

## Client reachability

Whether s&box allows game clients to call a given kind of URL has **not been
tested yet** against a self-hosted server. Every row below is untested. Please
report what works for you in an issue so this table can be filled in.

| Server URL type | Example | Status |
| --- | --- | --- |
| HTTPS domain | `https://ns.example.com` | **untested** |
| HTTP domain | `http://ns.example.com:8080` | **untested** |
| IP address and port | `http://203.0.113.10:8080` | **untested** |
| localhost | `http://localhost:8080` | **untested** |

If plain HTTP or bare IP addresses turn out to be blocked by the s&box sandbox,
put the server behind a reverse proxy with a real domain and a TLS certificate
(see [self-hosting.md](self-hosting.md#https)).

## Moving an existing project from the managed service

1. Create the project and keys on your server with `sbox-ns project create` and `sbox-ns key create`.
2. Point the editor Setup window at your server (above) and sync your
   `Editor/Network Storage/` source files with the Sync Tool to recreate
   collections, endpoints and workflows.
3. Publish a new build of your game.

Player data stored on the managed service is not copied by syncing. Moving
existing player records is not covered by this guide yet.
The synced sources describe schemas and server-side logic, not player save
files. Runtime records live in your server's SQLite or PostgreSQL database.

Players running an older build keep talking to whichever server that build was
configured for.
