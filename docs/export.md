# Export and import

`sbox-ns export` writes the whole server (every project, its data, and the
config folder) into one `.tar.gz`. `sbox-ns import` restores that archive into
whatever database the target server is configured for. Two commands move a
server to another machine or switch it from SQLite to PostgreSQL.

```sh
sbox-ns export                       # <data dir>/exports/sbox-ns-export-<time>.tar.gz, secrets included
sbox-ns export --out ns.tar.gz --no-secrets
sbox-ns import ns.tar.gz             # refuses a database that already has projects
sbox-ns import ns.tar.gz --config    # also restore the config folder (and secrets, if archived)
sbox-ns import ns.tar.gz --force     # merge into a non-empty database
sbox-ns import ns.tar.gz --verify-only  # validate only; writes nothing to this server
```

The owner dashboard has the same export: **Export server → Download export**
(`POST /dashboard/export`). It leaves secrets out unless you tick
**Include secrets**, which also asks for your password, and your authenticator
or recovery code if you enabled one. Every dashboard download is written to each project's
audit log (`server.export`) and logged as a warning with the owner name and
client address. Only one export runs at a time.

## Backup or export?

| | `sbox-ns db backup` / `db restore` | `sbox-ns export` / `import` |
| --- | --- | --- |
| Format | Native: SQLite file copy, or `pg_dump` custom format | Driver-neutral JSON lines in a `.tar.gz` |
| Restores into | The same database driver only | SQLite or PostgreSQL, whichever the target uses |
| Contents | Every table byte for byte, including logs and usage counters | All project data and settings (see below), plus the config folder |
| Config and secrets | Not included | Included (secrets optional) |
| Needs | `pg_dump`/`pg_restore` for PostgreSQL | Nothing extra |
| Use it for | Nightly backups, rollback before updates | Moving servers, switching drivers, a portable copy |

Keep taking `db backup` for routine backups; an export is the portable copy.

## Move a server to another machine

1. On the old server: `sbox-ns export --out ns.tar.gz` (prints a warning
   because the archive holds secrets).
2. Copy `ns.tar.gz` to the new machine over SSH (`scp`), never through a
   public location.
3. On the new machine install sbox-ns, run `sbox-ns setup`, stop the
   service (`sbox-ns service stop`), then
   `sbox-ns import ns.tar.gz --config`.
4. Check `server.toml` (listen address, `public_url`, TLS) for the new
   machine, then `sbox-ns service start`. Point your DNS at the new machine.
5. Delete `ns.tar.gz` from both machines.

With `--config` the old server's secrets are restored, so player sessions,
secret API keys and signed security configs keep working. The old server's
owner login is part of the data and replaces the login created on the new
server.

## Switch from SQLite to PostgreSQL

1. `sbox-ns export --out ns.tar.gz` while still on SQLite.
2. Stop the service. Point `database.toml` at PostgreSQL
   (`sbox-ns config set database.provider postgres` plus the
   `database.postgres.*` keys, see [database.md](database.md)); check it with
   `sbox-ns db test`.
3. `sbox-ns import ns.tar.gz`. The schema is created automatically and the
   data lands in PostgreSQL. `--config` is not needed on the same machine,
   because the config folder and secrets are already in place.
4. Start the service. The old SQLite file stays untouched in the data folder
   until you delete it.

The reverse (PostgreSQL to SQLite) works the same way.

## What `--config` restores

- `server.toml`, `updates.toml`, `alerts.toml` and `conf.d/*.toml`.
- `secrets/` and any other secret file the settings point to inside the
  config folder, written with owner-only permissions (0600).
- `database.toml` is never replaced: the import has already gone into the
  database this server is configured for. The archived copy is saved as
  `database.toml.from-export` for reference. If you keep database settings in
  `conf.d/`, review them after the import.
- Every replaced file is kept next to it as `*.before-import`.

Files are written atomically (temp file + rename). Restart the server after
an import so it reads the new config.

## What the archive contains

```
manifest.json                          format version, sbox-ns version, schema version,
                                       provider, createdAt, project ids and row counts
data/workspace-objects.jsonl           project lists, packages, key indexes, owner login
data/memberships.jsonl                 project ownership
data/projects/<id>/<resource>.jsonl    one JSON object per line, per resource
config/...                             the config folder (see above)
```

Per project: the project record, collections, per-player records, ledger
entries, global records, endpoints, workflows, queries, API keys, pages, game
values, rate limits, checkpoint cursor, player profiles, each player's current
session, player analytics events, audit logs, and the cumulative storage
footprint.

Not included, because they are operational history the server rebuilds:
request logs, storage error logs, query run history, monthly/daily usage
counters (only the total storage footprint is carried over), record
idempotency keys, analytics issue buckets and ended player sessions. Records
are found through their collection definitions, so records left behind in a
collection whose definition was deleted are not exported. Analytics events
are found through player profiles.

The export reads the database through the same storage interface for both
drivers, while the server keeps running. Writes that happen during an export
may or may not be in it; stop the server first if you need an exact cut-over.
Version numbers, hashes and payloads are kept exactly. Write timestamps the
database stamps itself (`updated_at` of collections, records, endpoints,
workflows, queries, keys, game values and rate limits; `created_at` of ledger
entries and global records) take the time of the import. Pages, audit logs,
memberships, player profiles, sessions and events keep their timestamps.

## Import rules

- `manifest.json` must be the first entry. An archive written by a newer
  sbox-ns with a newer export format version is refused with a message to
  upgrade sbox-ns first. Older formats stay readable.
- The target must be empty: no projects for the owner, no project data, and
  none of the archived project ids. An owner login alone does not count, so a
  freshly set-up server accepts an import. `--force` merges instead: rows
  with the same keys are overwritten by the archive, all other rows are kept.
- Every row is an upsert, so running the same import again gives the same
  result. If an import stops halfway (for example a full disk), fix the cause
  and run it again with `--force`.
- Archive entries must be regular files with plain relative paths; links,
  absolute paths and `..` are rejected.
- All manifest-declared data entries and their row counts are validated before
  database writes. Missing entries and truncated data are rejected.
- Config restoration rejects symlinked or reparse-point destinations and
  ancestors, including the config root, rather than following them outside it.

### Portable project imports

The dashboard's single-project import is separate from whole-server CLI replay.
It accepts only a project archive without instance configuration or secrets,
and never overwrites an existing project ID. SQLite and PostgreSQL claim that ID
at the database boundary in the same transaction as every imported project row,
owner membership, workspace object and storage-footprint counter. Concurrent
importers sharing a database have only one winner; a losing import changes
nothing. Malformed rows, cancellation and storage failures roll back the entire
project, so after fixing the cause the corrected archive can be retried normally.

API key rows must have the shape sbox-ns writes, or the import is refused: a
public key is stored in full and starts with `sbox_ns_`; a secret key is stored
only masked (`sbox_sk_xxxx...xxxx`) with a 64-character hex `key_hash`, so an
archive never holds a usable secret key. Public keys in an archive still work
on the new server. If the archive came from someone else, revoke its keys and
create new ones.

## Verify an archive without importing

`sbox-ns import <FILE> --verify-only` checks an archive and changes nothing on
the server: it needs no configuration, no stopped service and no empty
database. It runs every import rule below, checks the archive contract, then
replays all rows into a throwaway SQLite database in a temporary folder (deleted
afterwards), so a row the real import would reject fails here too. It prints the
projects and row counts and exits 0 when the archive is importable, 1 with the
first problem otherwise.

## Archive contract (archives produced outside sbox-ns)

Any tool can produce an archive `sbox-ns import` accepts, for example to move a
project from the hosted sboxcool.com service onto a self-hosted server. Check
the result with `sbox-ns import <FILE> --verify-only` before using it.

**Container.** A gzip-compressed POSIX tar (`.tar.gz`). Only regular files with
plain relative paths (no links, no absolute paths, no `.` or `..` segments, no
backslashes, no entry twice). `manifest.json` must be the first entry. Every
other entry is `data/...` (below) or `config/...` (a config folder; leave it
out of migration archives).

**`manifest.json`** (camelCase JSON):

| Field | Type | Meaning |
| --- | --- | --- |
| `format` | string | Always `"sbox-ns-export"` |
| `formatVersion` | integer | `1`; newer versions are refused by older servers |
| `sboxNsVersion` | string | Producer and its version, informational (e.g. `"sboxcool-hosted 2026.10"`) |
| `schemaVersion` | integer | Producer schema version, informational (`1`) |
| `provider` | string | Source store, informational (e.g. `"scylladb"`) |
| `createdAt` | string | ISO 8601 time of the export |
| `includesConfig` | bool | `false` for migration archives |
| `includesSecrets` | bool | `false` for migration archives (secrets are installed separately) |
| `workspaceObjects` | integer | Rows in `data/workspace-objects.jsonl` |
| `memberships` | integer | Rows in `data/memberships.jsonl` |
| `projects` | array | `[{ "id": "<projectId>", "counts": { "<resource>": <rows>, ... } }]` |

Every data file declared with a count above 0 must be present with exactly that
many rows; resources with 0 rows may be omitted from `counts` and from the
archive. Project ids match `^[a-zA-Z0-9_-]{1,128}$` and are kept as-is.

**Data files.** UTF-8 JSON lines, one object per line, snake_case columns. A
line may be at most 32 MiB.
Columns marked *required* must be present (JSON `null` reads as empty / 0 /
false); `*_json` columns marked *JSON* take any JSON value and may be omitted
(`null`); integers are 64-bit; `*_unix_ms` are Unix milliseconds.

| File | Columns |
| --- | --- |
| `data/workspace-objects.jsonl` | `path`, `content` (string, usually JSON text) |
| `data/memberships.jsonl` | `user_id` = `"1"`, `project_id`, `role` = `"owner"`, `created_at_unix_ms` |
| `data/projects/<id>/project.jsonl` | exactly one row: `payload` (JSON, the project document) |
| `.../collections.jsonl` | `collection_id`, `name`, `visibility`, `version`; `definition_json` (JSON) |
| `.../records.jsonl` | `collection_id`, `record_key`, `deleted`, `version`; `payload_json` (JSON) |
| `.../ledger-entries.jsonl` | `collection_id`, `record_key`, `sequence`; `entry_json` (JSON) |
| `.../global-records.jsonl` | `collection_id`, `record_id`, `version`; `payload_json` (JSON) |
| `.../endpoints.jsonl` | `endpoint_id`, `slug`, `method`, `enabled`, `version_hash` (nullable), `version`; `definition_json` (JSON) |
| `.../workflows.jsonl` | `workflow_id`, `name`, `version_hash` (nullable), `version`; `definition_json` (JSON) |
| `.../queries.jsonl` | `query_id`, `name`, `requires_secret_key`, `version`; `definition_json` (JSON) |
| `.../api-keys.jsonl` | `api_key`, `user_id` = `"1"`, `key_type` (`public`/`secret`), `key_hash`, `key_identifier`, `label`, `enabled`, `version`; `permissions_json` (JSON). Public: `api_key` starts with `sbox_ns_`. Secret: `api_key` is the masked `sbox_sk_xxxx...xxxx` form and `key_hash` is the 64-character hex SHA-256 of the full key |
| `.../pages.jsonl` | `page_slug`, `title`, `content_json` (string), `created_at_unix_ms`, `updated_at_unix_ms` |
| `.../game-values.jsonl` | `version_hash` (nullable), `version`; `payload_json` (JSON) |
| `.../rate-limits.jsonl` | `version`; `rules_json` (JSON) |
| `.../checkpoint-cursor.jsonl` | `latest_sequence`, `manifest_path` (nullable), `version` |
| `.../player-profiles.jsonl` | `steam_id`, `player_name`, `is_online`, `online_since_unix_ms` (nullable), `last_seen_unix_ms`, `last_heartbeat_unix_ms` (nullable), `current_session_id` (nullable), `current_session_last_seconds` (nullable), `total_seconds`, `session_count`, `last_event_type` (nullable), `last_endpoint_slug` (nullable), `updated_at_unix_ms`; `managed_counters_json` (JSON, default `{}`) |
| `.../player-sessions.jsonl` | `steam_id`, `session_id`, `started_at_unix_ms`, `last_heartbeat_at_unix_ms`, `ended_at_unix_ms` (all three nullable); `last_metrics_json`, `summary_json` (JSON) |
| `.../player-events.jsonl` | `steam_id`, `created_at_unix_ms`, `event_id`, `event_type`, `category`, `label`, `endpoint_slug`, `collection_id`; `payload_json` (JSON) |
| `.../audit-logs.jsonl` | `created_at_unix_ms`, `log_id`, `user_id`, `action`, `actor_json`, `target_json`, `summary_json`, `diff_json` (all strings) |
| `.../usage.jsonl` | one row: `storage_bytes` (cumulative storage footprint) |

**Single owner.** A self-hosted server has exactly one account, the local owner
with user id `1`. Archives from a multi-user source must be rewritten to it:

- every project has a membership row `{"user_id": "1", "project_id": "<id>", "role": "owner", ...}`;
- the project payload's `storageOwnerUserId` (if present) is `"1"`;
- every API key row has `user_id` `"1"` (keys resolve storage paths through it).
  `key_hash` and `key_identifier` are kept unchanged; secret keys keep working
  only when the server uses the source's `storage_encryption_key`;
- workspace objects use `network-storage/users/1/<projectId>/...` for project
  files (rewrite `network-storage/users/<hostedUserId>/` to
  `network-storage/users/1/`), `network-storage/users/1/projects.json` for the
  owner's project list, and `network-storage/keys/projects/<projectId>/...` for
  key indexes. The server also writes matching key files under
  `network-storage-api/keys/projects/<projectId>/...`; those are accepted too.
  JSON content that names the owner (`userId` in key and page indexes) uses `1`.
  Other paths outside `network-storage/` (except `server/identity/...` owner
  login, which migration archives leave out) and project folders of projects
  not in the manifest are rejected.

**Import behaviour that matters to producers.** Rows are upserts keyed like
the database, so re-importing the same archive with `--force` converges.
Versions, hashes and payloads are kept exactly; the write timestamps listed in
[What the archive contains](#what-the-archive-contains) take the import time.
The security config keeps the source's key id when `auth.security_signing_key_id`
is set to it (and the source's signing key is installed as
`secrets/security_signing_key.pem`).

## Security of archives

An archive holds everything a server holds: player records, hashed API keys,
the owner's password hash, and (unless `--no-secrets`) the server secrets.
With the secrets, anyone holding the archive can forge player sessions and
sign security configs.

- `sbox-ns export` always creates the file readable by its owner only (0600)
  and refuses to overwrite an existing file.
- Exports are staged in `<data dir>/tmp/` with owner-only permissions and
  removed when the archive is written. Staging left behind by a crash is
  removed by the next export after a day.
- `--no-secrets` (and the dashboard default) leaves out `secrets/`, the
  secret files settings point to, and blanks inline secret values
  (`database.postgres.password`, `database.postgres.connection_string`,
  `alerts.discord.webhook_url`, `alerts.smtp.password`). Use it for archives
  you store or share. Restoring such an archive on a server without the
  original secrets keeps all data, but secret API keys and existing player
  sessions stop working; create new secret keys with `sbox-ns key create`.
- Secret files configured outside the config folder are never included;
  `sbox-ns export` lists them so you can copy them yourself.
- Transfer archives over SSH, keep them off shared storage, and delete them
  once the move is done.
