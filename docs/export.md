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
```

The owner dashboard has the same export: **Export server → Download export**
(`POST /dashboard/export`). It leaves secrets out unless you tick
**Include secrets**. Every dashboard download is written to each project's
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
