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

The client appends the API root itself: released libraries call `/v3/...`, and the library default is planned to change to `/api/...` (counterpart #36, not shipped). The root is configurable. The base URL is only scheme, host and port with no path and no trailing slash, for example `http://your-server:8080`. `/v3/{path}` and `/api/v3/{path}` are the permanent surface for existing methods and endpoints; new features are served at `/api/{path}` only, and `/api/{path}` mirrors existing non-reserved `/v3` families. Verify the server answers from the machine running s&box:

```sh
curl -i http://your-server:8080/health
curl http://your-server:8080/api/server-info
```

## Server info handshake

`GET /api/server-info` (also `/v3/server-info`) is public and returns server identity, answered
natively without a database write:

```json
{
  "kind": "self-hosted",
  "product": "sbox-network-storage-server",
  "version": "0.0.0-dev",
  "apiVersions": ["v3"],
  "api": { "root": "/api", "aliases": ["/v3"], "since": "0.5.5" },
  "database": "sqlite",
  "capabilities": ["records", "global-records", "endpoints", "workflows", "queries", "game-values", "rate-limits", "auth-sessions", "analytics", "package-sync", "https-tunnel", "api-root"],
  "tunnel": { "enabled": false, "name": "", "hostname": "", "registry": "", "connectorState": "disabled", "connectorVersion": "", "processId": null, "updatedAt": "2026-10-10T00:00:00+00:00" },
  "compat": { "library": { "minimum": "1.1.0", "recommended": "1.1.0", "latest": "1.1.0", "legacy": "supported", "deprecated": [], "blocked": [], "knownBroken": [{ "version": "1.0.165702", "revision": 165702, "engine": ">=26.10", "reason": "Stale library breaks editor startup on s&box engine 26.10 or later.", "issue": "sbox-cool/sbox-network-storage#4" }], "tested": [] }, "matrixUrl": "https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json" },
  "update": null
}
```

`database` is the configured provider name. `tunnel` describes the
`sbox-ns tunnel` connector. `update` carries the latest release notice when
an update check has run, else null. `compat` is described under
[Library compatibility](#library-compatibility). `sbox-ns doctor` also polls
this route to check the listener.

The additive `compat` advertisement and optional `x-ns-library-*` identity/status
headers are the whole version contract. Unidentified older libraries remain
accepted by default; recommendations and deprecations alone never reject a
working game. An explicit owner minimum and advisory-linked blocks return
HTTP 426 with `{ "ok": false, "error": { "code": "LIBRARY_UNSUPPORTED",
"message": "..." } }` (or `LIBRARY_BLOCKED`); the existing library understands
this structured envelope, but the engine may hide 4xx bodies. See
[compatibility.md](compatibility.md) for the operator policy.

## Library compatibility

The server runs many library versions at once, and most are older than the
server. Nothing here is required: a library that sends none of the request
headers below keeps working exactly as before. The rules are in
[compat/library-matrix.json](../compat/library-matrix.json), which is also
embedded in the binary, so everything on this page works offline.

### Request headers (library to server)

The library sends these on runtime and Sync Tool requests. All are optional.

|Header|Value|Meaning|
|---|---|---|
|`x-ns-library-version`|SemVer, e.g. `1.1.0`|Version of the Network Storage library. Missing or invalid (not `MAJOR.MINOR.PATCH[-pre][+build]`, longer than 64 characters) is the `legacy` bucket, never an error. The old constant `1.0` counts as `legacy`. On `/api/storage`, `1.2.0` or newer selects the canonical storage handlers; see [route root and storage versions](compatibility.md#route-root-and-storage-versions).|
|`x-ns-library-revision`|integer, e.g. `309133`|s&box package revision of the library, best effort. Ignored when not a non-negative integer.|
|`x-ns-client-type`|`game`, `editor` or `dedicated`|Which kind of client is calling. Any other value is ignored.|
|`x-ns-revision-id`|integer|Game revision the client was built from (published bundles only).|
|`x-ns-engine-version`|engine build, e.g. `26.10.02`|The s&box engine build the client runs on. At most 32 characters of `[0-9A-Za-z._+-]`; any other value is ignored.|

Libraries that do not send the header can still report their version in a
request body, and the server uses it when the headers carry no valid version
(a valid header always wins):

- `networkStorageVersion` in the `revision-init` body,
- `libraryVersion` (or `library_version`) in heartbeat and analytics event bodies.

An old library's constant `"1.0"` there is `legacy` as well.

The resolved identity (library version or `legacy`, client type, engine build)
is written with each runtime request to `storage_request_log` and shown as the
**Library**, **Client** and **Engine** columns on the project's Logs tab. Rows
written before the columns existed show them empty.

### Response headers (server to library)

Every response carries these, including errors and rejected requests. They are
headers only: no response body changes, and older libraries ignore them.

|Header|When|Value|
|---|---|---|
|`x-ns-server-version`|always|The server version (`BuildInfo.Version`), e.g. `0.5.0`.|
|`x-ns-library-status`|always|`supported`, `outdated`, `deprecated`, `unsupported`, `blocked` or `unknown`.|
|`x-ns-library-latest`|status `outdated` or worse|Newest library version the server knows.|
|`x-ns-library-sunset`|status `deprecated`|Informational date (`yyyy-MM-dd`) attached to the deprecation warning; it never authorizes removal.|

|Status|Meaning|
|---|---|
|`supported`|At or above the recommended version.|
|`outdated`|Supported, but a newer version is recommended.|
|`deprecated`|Warning only: the library version still works. Any sunset date is informational.|
|`unsupported`|Below the matrix `minimum`, or a build known to be broken (for example on the engine builds named in the matrix).|
|`blocked`|A security or data-loss problem, listed in the matrix `blocked` ranges.|
|`unknown`|No valid library version was sent: the `legacy` bucket. The server serves it as before; the status is `unknown` because it cannot tell which build this is.|

When several rules match, the strictest status wins. The server only reports
status. Runtime requests are served whatever the status; the editor and the
dashboard are where developers are pushed to update.

### `compat` block of `/api/server-info`

```json
"compat": {
  "library": {
    "minimum": "1.1.0",
    "recommended": "1.1.0",
    "latest": "1.1.0",
    "legacy": "supported",
    "deprecated": [],
    "blocked": [],
    "knownBroken": [{ "version": "1.0.165702", "revision": 165702, "engine": ">=26.10", "reason": "...", "issue": "sbox-cool/sbox-network-storage#4" }],
    "tested": [{ "server": "0.5.0", "library": "1.1.0", "revision": 310500, "engine": "26.10.02", "os": "Windows 11", "database": ["sqlite"], "result": "pass", "evidence": ["..."] }]
  },
  "matrixUrl": "https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json"
}
```

`minimum` and `recommended` are library versions; `latest` is the newest the
matrix knows. `legacy` is the status the matrix gives the no-version bucket.
`deprecated` and `blocked` are version ranges (`1.2.1` exact, `<1.2.0`,
`>=1.1.0 <1.3.0`; all comparators must hold). `knownBroken` lists builds known to break on
particular engine builds. `tested` lists the editor and CI runs recorded for
releases ([release evidence](releasing.md#release-evidence)). `deprecated`, `blocked`, `knownBroken` and
`tested` are empty arrays when there is nothing to report.

A library should decide features from `capabilities`, not from version
numbers. Where the server must act on a library version anyway, it does so in
one place (`LibraryCompatShims`), and each entry there names its reason, its
issue and the server version at which it must be removed.

### Release-feed overlay

When update checks are on (`updates.check`), the server also reads an optional
`libraryCompat` object from the signed release feed, so a `blocked` range can reach
servers that have not been updated. The overlay applies only when the release info
came from the signed feed: the unsigned GitHub `release.json` fallback never carries
one, even if it contains a `libraryCompat` object. An overlay can only make a status
**stricter**: it can raise `minimum` and `recommended`, add `blocked` and
`deprecated` ranges, and deprecate or un-support the `legacy` bucket. It cannot
change an announced sunset: a feed `deprecated` range only dates versions the
embedded matrix does not already deprecate. It cannot lower `minimum`, remove a
block, set `legacy` back to `supported` or block the whole `legacy` bucket; those parts
are ignored, as is an overlay whose optional `schemaVersion` is newer than the matrix
schema this server reads (1). A feed response without `libraryCompat` keeps the current
overlay, but only for a while: after 3 consecutive checks with no overlay (or failed
checks) the overlay expires and the embedded matrix applies again. An empty `{}` removes
it immediately. `/api/server-info` reports whether one is in effect in the `compat` block
(`"overlay": {"applied": true}`). Nothing needs network access:
without the feed, the embedded matrix applies.

## Revision init handshake

At game startup the client sends a one-time handshake with its running
revision: `POST /api/manage/{projectId}/revision-init`. The server compares
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

When the owner saved an explicit version policy (see
[version-policy.md](version-policy.md)), the handshake additively returns
`action` (`none`, `notify`, `rejoin`, `block_writes` or `block_all`), the
owner's notice as `message` for outdated clients, `graceEndsAt` (unix
seconds, when a grace window applies) and `minimumRevisionId` (when set).
Without a policy these fields are omitted and the body above is unchanged.
The handshake stays read-only and never fails because of policy.

## Pinned execution and `_revisionStatus`

Published game bundles send `x-ns-revision-id` on every runtime call. When
it names a retained revision inside its support window, endpoints,
workflows, queries, game values and collection definitions run from that
revision's snapshot; anything else (no header, unknown or expired
revision) runs against live. Player records are always live.

When a version policy is configured, every endpoint response — success and
error bodies alike — carries a top-level `_revisionStatus` object with the
library's exact keys (`isOutdatedRevision`, `graceRemainingMinutes`,
`graceExpired`, `action`, `message`, `playerRevision`, `currentRevision`,
`enforcementMode`, `policy{...}`); queries and values responses carry it
too. Without a policy it is absent. With server-side enforcement switched
on, expired known revisions get HTTP 403 `REVISION_BLOCKED` (secret keys
exempt). Details: [version-policy.md](version-policy.md).
The game package the server compares against is synced from the editor Sync
Tool (`POST /api/manage/{projectId}/package-sync`), exactly as with the
managed service. Sync your `Editor/Network Storage/` sources after pointing
the editor at your server (see [client-setup.md](client-setup.md)), then
publish a new build so players run against the synced revision.

## Encrypted requests

When **Enable encrypted requests** is on, the library sends endpoint calls as an
encrypted envelope (see [admin-panel.md](admin-panel.md#encrypted-requests)).
The mode the library must use comes from the signed security config. The
server refuses a call made in the wrong mode with a security code. Those codes
(`SECURITY_*`, `AUTH_SESSION_*`, `ENCRYPTED_REQUEST_*`, `REQUEST_SIGNATURE_*`)
arrive on endpoint routes as HTTP 200 for a library caller. The code is in `error.code`, and the
status the server intended is the number in the body `status` (see
[compatibility.md](compatibility.md#endpoint-errors-for-library-callers)). The library refreshes its security config and
retries once on a `SECURITY_*` code.

## Query results

`GET /api/queries/{projectId}/{queryId}` (also `/v1/queries/...`,
`/v3/queries/...`; `/api/storage/{projectId}/queries/...` follows the [storage version rules](compatibility.md#route-root-and-storage-versions)) runs a query over whole collections,
so the server keeps each query's latest result and shares it between
callers:

- A result is reused for the query's `cache.ttlSeconds` (300 when not set),
  and always for at least 2 seconds, even with a TTL of 0.
- A record or player-profile write in the project ends that reuse: the next
  call runs the query again once the result is at least 2 seconds old. A
  player who saves a score sees it in the leaderboard within about 2 seconds.
- Changing a query, collection or game value drops the project's results at
  once.
- When many calls miss at the same time, the query runs once and they all
  get that result.

Calls with the public key always get the shared result, because every
player has that key. `?live=1`, `?fresh=1`, `?noCache=1` and `?cache=0`
force a new run only together with a secret key (as `x-api-key` or
`x-secret-key`). Without `?cache=1` the response looks like a live result
(`Cache-Control: no-store`, no cache fields); `lastran` and
`performance.at` say when the query last ran. With `?cache=1` a reused
result also carries `fromCache`, `cachedAt` and `expiresAt`.
