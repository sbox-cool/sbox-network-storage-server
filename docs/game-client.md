# Game client handshakes

This page documents what the unmodified Network Storage client library says
to your server on top of the base URL. For pointing the game and editor at
your server, see [client-setup.md](client-setup.md). For which URL types
s&box clients can reach, see the reachability table there (all rows
currently untested); this page does not repeat it.

Client library source: `github.com/sbox-cool/sbox-network-storage`
(`Code/Core/NetworkStorageRevisionInit.cs` for the handshake below).
Library page: `sbox.game/sboxcool/network-storage`. API and library docs:
`sbox.cool/wiki/network-storage-v3`.

## Base URL rule

The client appends the API version itself (`/v3/...`), so the base URL is
only scheme, host and port with no path and no trailing slash, for example
`http://your-server:8080`. Verify the server answers from the machine
running s&box:

```sh
curl -i http://your-server:8080/health
curl http://your-server:8080/v3/server-info
```

## Server info handshake

`GET /v3/server-info` is public and returns server identity, answered
natively without a database write:

```json
{
  "kind": "self-hosted",
  "product": "sbox-network-storage-server",
  "version": "0.0.0-dev",
  "apiVersions": ["v3"],
  "database": "sqlite",
  "capabilities": ["records", "global-records", "endpoints", "workflows", "queries", "game-values", "rate-limits", "auth-sessions", "analytics", "package-sync", "https-tunnel"],
  "tunnel": { "enabled": false, "name": "", "hostname": "", "registry": "", "connectorState": "disabled", "connectorVersion": "", "processId": null, "updatedAt": "2026-10-10T00:00:00+00:00" },
  "update": null
}
```

`database` is the configured provider name. `tunnel` describes the
`sbox-ns tunnel` connector. `update` carries the latest release notice when
an update check has run, else null. `sbox-ns doctor` also polls this route
to check the listener.

## Revision init handshake

At game startup the client sends a one-time handshake with its running
revision: `POST /v3/manage/{projectId}/revision-init`. The server compares
it against the synced game package and reports whether the client is
outdated. The route is read-only and never mutates the store.

Authentication uses the public game key (no management scope needed), sent
as the `x-api-key` header, the `?apiKey=` query parameter, or the
`x-public-key` header. The client revision travels in the JSON body as
`revisionId`, or in the `x-ns-revision-id` header (body wins when both are
present). The `x-ns-client-type` header marks the caller as the game.

Success body (HTTP 200):

```json
{
  "ok": true,
  "playerRevision": 42,
  "currentRevisionId": 42,
  "latestRevisionId": 42,
  "revisionOutdated": false,
  "message": "Revision is current."
}
```

`revisionOutdated` is true when the client revision trails the synced
package. A missing client revision is not an error; the server acknowledges
with its latest revision and a message saying so. A project with no synced
game package answers that there is nothing to compare against. Revisions the
server does not know (`playerRevision`, `currentRevisionId`,
`latestRevisionId`) are left out of the body rather than sent as `null`.

Failures use the status code plus a short body: 401 `UNAUTHORIZED` for a
missing, invalid, or disabled key or an unknown project; 403
`PROJECT_DISABLED` for a disabled project; 400 `INVALID_JSON` for a
non-JSON body. The client tolerates handshake failure, so a failed
revision-init never blocks gameplay; it only skips the outdated notice.

The game package the server compares against is synced from the editor Sync
Tool (`POST /v3/manage/{projectId}/package-sync`), exactly as with the
managed service. Sync your `Editor/Network Storage/` sources after pointing
the editor at your server (see [client-setup.md](client-setup.md)), then
publish a new build so players run against the synced revision.
