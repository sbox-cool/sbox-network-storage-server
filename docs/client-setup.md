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

Use the local-owner [dashboard](admin-panel.md), or run the CLI on the server:

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
