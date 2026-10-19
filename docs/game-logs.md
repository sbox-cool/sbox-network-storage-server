# Game logs

Game logs are structured events your game sends to the server, stored per project and searchable by the project owner or a tool holding a log-reading key. They are off by default for every project. While a project's game logs are off, `/api/game-logs/{projectId}` answers exactly like any unknown route (`404 NOT_FOUND`).

## Turn them on

Open the project in the owner dashboard and expand **Game logs**:

- **Game logs enabled**: master switch for the project.
- **Streams**: each stream has an enabled flag, retention in days (1-3650) and sampling (0-1, the fraction of entries kept).
- **Allowed client events**: comma- or newline-separated event names that public-key callers may send. Names ending in `.*` match a prefix.
- **Rate limit**: entries per minute for the whole project (default 12,000).

## Streams and retention defaults

| Stream | Default | Retention | Who writes it |
| --- | --- | --- | --- |
| `default` | on | 30 days | Game events. |
| `audit` | on | 365 days | Admin and audit events. |
| `chat` | off | 14 days | Chat messages. Opt-in: nothing is captured until the owner enables it. |

Entries older than the stream's retention are purged once a day. Search never returns entries older than retention, even before the purge runs. Disabling a stream stops new writes; its stored rows are kept until they age out (purging on disable is not implemented yet). Rows of a deleted project are removed by the same daily purge, under the default retention.

## Sending entries

```
POST /api/game-logs/{projectId}
x-api-key: <public or secret key>
x-auth-session: <player session token>      # public-key callers, for player identity
Content-Type: application/json              # or application/x-ndjson
```

A key in the query string (`?apiKey=`) is refused with `400 API_KEY_IN_QUERY` before any key lookup. Send the key in the `x-api-key` header.

The body is a JSON array of entries, or one JSON object per line as NDJSON.

```json
[
  { "event": "match.end", "level": "info", "message": "Round over", "attrs": { "winner": "red", "rounds": 12 } },
  { "event": "client.join", "stream": "default", "ts": 1767225600000 }
]
```

### Entry fields

| Field | Required | Meaning |
| --- | --- | --- |
| `event` | yes | Lower-case dotted name such as `match.end` or `client.join`. Up to 128 characters. |
| `stream` | no | `default`, `chat` or `audit`. Defaults to the stream the event implies: `chat.*` → `chat`, `admin.*` and `command.*` → `audit`, anything else → `default`. The stream must be enabled. Public keys may only send the implied stream, and never `audit` (`STREAM_NOT_PERMITTED`). |
| `level` | no | `debug`, `info`, `warn` or `error`. Defaults to `info`. |
| `ts` | no | Unix milliseconds. Defaults to the server time. Secret-key values must be at most 60 seconds in the future and within the stream's retention. Public-key values are clamped to within 5 minutes of server receive time; a clamped entry is counted in `truncated`. |
| `actor` | secret key only | SteamID of the player the event is about. See [Identity](#identity-and-the-actor-field). |
| `target` | no | SteamID of a second player involved, for example the victim. |
| `message` | no | Free text, up to 2,048 characters. Longer messages are truncated and the response says so. |
| `attrs` | no | Flat object of scalar values (strings, numbers, booleans) used for filtering. Keys are 1-64 letters, digits or underscores. |
| `revisionId`, `clientType`, `libraryVersion`, `traceId` | no | Optional correlation fields. Client fields are up to 64 characters; `traceId` up to 128. |

### Identity and the actor field

- **Public key** (the key the game client holds): the server takes the actor from the verified player session only. An entry that sends `actor` is rejected with `ACTOR_NOT_PERMITTED`, and a request that sends `x-on-behalf-of` gets `403 ACTOR_NOT_PERMITTED`. A client cannot write logs as another player.
- **Secret key** (dedicated servers and trusted tools): the entry's `actor` is honoured when it is a valid SteamID. It is rejected with `INVALID_ACTOR` otherwise. If an entry has no `actor`, the `x-on-behalf-of` header is used when present.
- **Secret key** ingest requires `logs:w` (the `logs` scope set to `w` or `rw`). A key whose permissions omit `logs` gets `403 FORBIDDEN`. Default secret keys have no `logs` grant, so the owner adds one in the key editor. Only a key with no permission map at all (full access) passes without it.
- Client IP addresses are never stored.
- Public-key callers may send `client.*` and `chat.*` events without restriction. Other events must match the project's allowed-events list.
- Public-key callers cannot name a stream and cannot write `audit`. `client.*` entries go to `default`, and `chat.*` entries go to `chat` (which must be enabled). An entry that would land in `audit`, such as `admin.*`, is refused with `STREAM_NOT_PERMITTED` even if its event is in the allowed-events list. The stream check runs before the event check.
- Each stored row records its `source`: `client` for public-key entries, `server` for secret-key entries. Search returns it.

### Response

```json
{ "accepted": 1, "rejected": [{ "index": 0, "code": "EVENT_NOT_ALLOWED" }], "truncated": 0, "sampled": 0 }
```

Status `202 Accepted`. Entries are validated one by one: a bad entry is listed in `rejected` with its index and code, and the rest of the batch is still stored. `sampled` counts entries dropped by the stream's sampling rate.

### Limits

| Limit | Value | Error |
| --- | --- | --- |
| Entries per batch | 500 | `413 BATCH_TOO_LARGE` |
| Bytes per batch | 256 KB | `413 BATCH_TOO_LARGE` |
| Bytes per entry | 8 KB | `ENTRY_TOO_LARGE` (entry) |
| Project rate limit | `gameLogsRateLimitPerMinute` (default 12,000 entries/min) | `429 LOG_RATE_LIMITED` with `Retry-After` |
| Per-actor rate limit (public keys) | 1,200 entries/min per project and player | `429 LOG_RATE_LIMITED` |
| Timestamp window | Secret keys: at most 60 seconds ahead and within retention. Public keys: clamped to 5 minutes either side of receive time. | `TIMESTAMP_OUT_OF_WINDOW` (entry, secret keys only) |
| Queue full | 10,000 entries waiting in memory | `QUEUE_FULL` (entry, counted as dropped) |
| Queued entries per project | 2,500 | `QUEUE_FULL` (entry, counted as dropped) |

Budgets are charged in this order. Identity is resolved first, so a request that fails identity costs nothing. Then the entries are validated, and only entries that pass (sampled ones included) are counted. A public-key request charges the actor bucket next, then the project bucket, so an over-limit player is refused without draining the project's budget. A request refused by the project limit has already spent its actor tokens.

Other per-entry codes: `INVALID_JSON`, `INVALID_ENTRY`, `INVALID_EVENT`, `INVALID_STREAM`, `STREAM_DISABLED`, `STREAM_NOT_PERMITTED`, `INVALID_LEVEL`, `INVALID_TIMESTAMP`, `INVALID_STEAMID`, `INVALID_ACTOR`, `ACTOR_NOT_PERMITTED`, `INVALID_TARGET`, `INVALID_MESSAGE`, `INVALID_ATTRS`, `INVALID_ATTR_KEY`, `ATTR_KEY_TOO_LONG`, `ATTR_TOO_DEEP`, `ATTR_NUMBER_OUT_OF_RANGE`, `DUPLICATE_ATTR_KEY`, `TOO_MANY_ATTRS`, `INVALID_FIELD`, `INVALID_REVISION`, `EVENT_NOT_ALLOWED`.

Errors for a whole request use `{ "ok": false, "error": "<CODE>", "detail": "..." }`.

Request-level codes also include `API_KEY_IN_QUERY` (400) and `FORBIDDEN` (403).

## Searching

```
GET /api/game-logs/{projectId}?player=7656...&event=match.end&attr.team=red&q=elimination
x-api-key: <secret key with logs:r>
```

Search needs a **secret key** whose `logs` scope includes read (`r` or `rw`). Default secret keys do not have it; the owner adds it in the key editor. Public keys cannot search (`403 FORBIDDEN`).

| Parameter | Meaning |
| --- | --- |
| `from`, `to` | Unix milliseconds or ISO-8601. `from` must be before `to`. Required when `q` or an `attr.<key>` is used, and at most 31 days apart in that case (`TIME_RANGE_REQUIRED`, `TIME_RANGE_TOO_LARGE`). |
| `level` | `debug`, `info`, `warn` or `error`. |
| `event` | Exact event name. |
| `stream` | `default`, `chat` or `audit`. Repeatable. Defaults to every enabled stream. |
| `player` | Matches entries where the SteamID is the actor or the target. |
| `attr.<key>` | Exact match on an attribute. Repeatable; all conditions must match. Values are 1-256 characters. |
| `q` | Free text. Words are combined with AND; each word matches a whole token, case-insensitively. There is no prefix, phrase or OR matching. |
| `limit` | 1-500, default 100. |
| `cursor` | Opaque value from `nextCursor` of the previous page. |

Response: `{ "entries": [ { "entryId", "tsUnixMs", "stream", "level", "event", "actor", "target", "revisionId", "clientType", "libraryVersion", "message", "attrs", "traceId", "source" } ], "nextCursor": null }`. `source` is `client` or `server`. Results are newest first. `nextCursor` is `null` on the last page.

Free-text matching uses SQLite FTS5 (unicode61) on SQLite and a token array on PostgreSQL. Ordinary words behave the same on both; tokenisation of rare Unicode scripts can differ.

Unknown parameters are rejected with `UNKNOWN_PARAMETER`.

## Delivery and what the owner sees

Accepted entries wait in a bounded in-memory queue (10,000 entries). One background writer stores them in batches of up to 500, flushed at least once a second. The owner page shows counters since the server started: accepted, written, rejected, sampled, truncated, rate limited, dropped because the queue was full, and write failures.

A batch that fails to write is dropped and counted under write failures; it is not retried. Dropped entries are never silent.

## Limits of the design

- Entries are not signed or hash-chained; the audit stream is retained for review, not as tamper-evident evidence.
- Free-text search is token based; it is not a substring or regex search.
- There is no explorer or timeline UI and no alerting on log events yet.
