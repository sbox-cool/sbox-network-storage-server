# Pointing your s&box game at a self-hosted server

sbox Network Storage Server speaks the same HTTP API as the managed service at
[sbox.cool](https://sbox.cool/tools/network-storage). The published s&box
client library works without changes: you only change its base URL.

- s&box library: <https://sbox.game/sboxcool/network-storage>
- Client source: <https://github.com/sbox-cool/sbox-network-storage>
- API and library docs: <https://sbox.cool/wiki/network-storage-v3>

The client appends the API root itself: released libraries call `/v3/...`, and the library default is planned to change to `/api/...` (counterpart #36, not shipped). The root is configurable. Servers that do not serve `/api` need the root set to `v3` explicitly; the library does not switch roots at runtime. The base URL is just the scheme, host and port of your server, with no path and no trailing slash. See [route root and storage versions](compatibility.md#route-root-and-storage-versions) for the surface rules.

## 1. Find your server URL

| How players reach the server | Base URL |
| --- | --- |
| Domain with HTTPS (`tls.mode` = `acme` or `certificate`, or a reverse proxy) | `https://ns.example.com` |
| Domain over plain HTTP | `http://ns.example.com:8080` |
| Same machine as the game (development) | `http://localhost:8080` (ports 80, 443, 8080 or 8443 only) |

s&box game code refuses a base URL whose host is an IP address, so a bare
`http://203.0.113.10:8080` does not work: give the server a domain name (a DNS
record, `sbox-ns tunnel enable` or `sbox-ns dns enable`). See
[Client reachability](#client-reachability).

Check the server responds from the machine running s&box:

```sh
curl -i http://your-server:8080/health
curl http://your-server:8080/api/server-info
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
direct document API (`GET`, `POST` and `DELETE /v3/storage/{projectId}/{collection}/{key}`, plus `/probe`;
`/api/storage/...` reaches the same handlers from clients that send `x-ns-library-version: 1.2.0` or newer) therefore treats public-key calls as
untrusted:

Collection IDs must contain 1–128 ASCII letters, digits, `_` or `-`. Record
keys must contain 1–256 ASCII letters, digits, `_`, `:` or `-`. The server
validates each path segment through the end of the value: a trailing line feed
(`\n`, including `%0A` when URL-encoded) is invalid, not trimmed or treated as
a line terminator. A direct record request with such a key is rejected with
`404` rather than stored as a separate record.

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
- A delegated request (`x-on-behalf-of` with `x-on-behalf-of-token` and `x-proxy-signature`) acts as the player only when Facepunch also verifies the player's own token for that Steam ID. A valid host token and proxy signature alone are not enough. On the auth-session routes the requested player (`x-steam-id`, `steamId` query, or `steamId` body) must equal that verified player; `x-sbox-steam-id` names the proxy host and is verified by the host token, not compared to the player.
- With s&box authentication turned off (development), the Steam ID the client
  sends is trusted as is, but the `accessMode` and `allowRecordDelete` rules
  still apply.

Secret keys (dedicated servers, tools) count as trusted backend calls: they need
`collections` execute permission and may read, write and delete in any
collection without s&box tokens.

### Indexed record operations

Operation paths can cross existing arrays: an `inc` at `inventory.0.count` updates the first
item without replacing the array or its siblings. This applies to `set`, `inc`, `push`, `pull`,
`remove`/`delete`, `merge` and `set_if_null`, both in direct document operations and endpoint
write steps. Numeric dictionary keys remain ordinary keys.

Array indices use canonical nonnegative integers (`0`, not `01` or `-1`). A write may append
at the next consecutive index, but sparse gaps are rejected as an invalid operation rather
than allocating attacker-selected amounts of memory. Rejected operations do not persist a
partially modified document.

### Document write mode

A record write to the direct document route (`POST /v3/storage/...` or `/api/storage/...`)
may send an `x-ns-write-mode` header. Without the header, a body with a top-level `ops`
array is applied as operations and any other body replaces the document, as before. With
`x-ns-write-mode: replace`, the body replaces the document even when it has an `ops` array.
With `x-ns-write-mode: ops`, the body must be an operations request, and a body without an
`ops` array returns `400 INVALID_OPERATION`. The values are case-sensitive; any other value
returns `400 INVALID_WRITE_MODE` and nothing is written. Until a library release sends the
header, game code cannot set it.

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
| `400 INVALID_OPERATION` | An `ops` write has an invalid operation, or its `ops` array is missing or empty. | Fix the operations; see [indexed record operations](#indexed-record-operations). |
| `400 INVALID_WRITE_MODE` | The `x-ns-write-mode` header is not `replace` or `ops`. | Send `replace`, `ops`, or no header. |

In the game, read the last error of an endpoint call by its slug:

```csharp
var result = await NetworkStorage.CallEndpoint( "grant-coins", new { } );
if ( !result.HasValue )
{
    NetworkStorage.TryGetLastEndpointError( "grant-coins", out var code, out var message );
    Log.Warning( $"{code}: {message}" );
}
```

s&box passes the status and body of non-2xx responses to game code (verified on
s&box 26.10.13c with `Http.RequestAsync`). A 403 body is, for example,
`{"ok":false,"error":"ENDPOINT_ONLY",...}`, and a 503 includes a `Retry-After: 1` header.
The library maps these bodies to the codes in the table above. The dashboard's **Logs**
tab shows the status for the same request.

On the server, `sbox-ns logs -f` follows the server log.

## Client reachability

Checked with `Http.IsAllowed` in the s&box 26.10.13 editor (play mode), plus
real requests to a local server. The editor Sync Tool is not restricted, so a
URL can sync fine while game requests to it fail with
`Access to '...' is not allowed`.

| Server URL type | Example | Game code in the editor |
| --- | --- | --- |
| HTTPS domain | `https://ns.example.com` | allowed (any port) |
| HTTP domain | `http://ns.example.com:8080` | allowed (any port) |
| IP address | `http://203.0.113.10:8080`, `https://203.0.113.10` | **refused**, public or private |
| IPv6 literal | `http://[2001:db8::1]:8080` (brackets required; without them the URL does not parse) | **refused** — dashboard and `curl -g` only, never game code |
| localhost / `127.0.0.1` / `[::1]` | `http://localhost:8080` | allowed on ports 80, 443, 8080 and 8443 only |

A domain that does not resolve was also refused, so create the DNS record
before testing. Published games outside the editor are untested. Please report what works for
you in an issue. A domain with HTTPS is the safe choice
(see [self-hosting.md](self-hosting.md#https)); `client_snippet` in
[mcp.md](mcp.md) warns when the configured base URL would be refused.

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
