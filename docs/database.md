# Databases

`sbox-ns` stores everything in one of two backends, selected by
`database.provider` in `database.toml` (`sqlite` or `postgres` only).
Check the connection with `sbox-ns db test` and inspect versions with
`sbox-ns db status`. Every key below is also listed in
[configuration.md](configuration.md).

## Which one to choose

| Backend | Choose it when | Scaling note |
| --- | --- | --- |
| SQLite (default) | Solo dev, a single server, or first install. Nothing to install or configure. | One file, one writer machine. Fine for most single-server games. |
| PostgreSQL 13 or newer | You already operate Postgres, want the database on a separate machine, or want your provider's managed backups. | Tune the pool (below). One server instance per schema; see below. |

ScyllaDB is not supported by this server. There is no ScyllaDB provider
setting. ScyllaDB notes elsewhere in this repo describe the managed service
internals, not this binary.

## SQLite

```toml
[database]
provider = "sqlite"

[database.sqlite]
path = "sbox-ns.db"   # relative paths resolve inside the data folder
```

Everything lives in `<data dir>/sbox-ns.db` (WAL mode). No users, no
passwords, no server to run. Copy the file only while the server is stopped;
for live backups use `sbox-ns db backup`, which backs up consistently while
running.

Each connection opens with `journal_mode=WAL` and `synchronous=NORMAL`.
WAL keeps committed transactions atomic across an application crash; NORMAL
avoids a disk sync on every commit. An OS crash or power loss can lose recent
commits, so use reliable local storage and keep backups.

## PostgreSQL

```toml
[database]
provider = "postgres"
startup_timeout_seconds = 60   # keep retrying at startup (for example when the DB boots together with the server)

[database.postgres]
# Full connection string. When set, it overrides the fields below.
connection_string = ""
# Example: "Host=db.internal;Port=5432;Database=sbox_ns;Username=sbox_ns;Password=..."

host = "localhost"
port = 5432
database = "sbox_ns"
username = "sbox_ns"

# Prefer password_file so the secret is not stored in this file.
password = ""
password_file = ""

# Disable, Allow, Prefer, Require, VerifyCA, VerifyFull
ssl_mode = "Prefer"

# Maximum pooled connections.
max_pool_size = 50

# All tables are created inside this schema, so the server can share a
# database with other applications.
schema = "network_storage"

# Seconds to wait when opening a connection.
connect_timeout_seconds = 15
```

Connection fields: `host`, `port`, `database`, `username`, plus either
`password` or `password_file`. A full Npgsql `connection_string` may replace
the individual fields. In containers, the same keys are available as
`NS_` environment variables, for example
`NS_DATABASE__POSTGRES__HOST=db.internal` with a Docker secret at
`NS_DATABASE__POSTGRES__PASSWORD_FILE=/run/secrets/pg_password`
(see [self-hosting.md](self-hosting.md) for a Compose example).

Password handling: `sbox-ns setup` never takes a password as a command line
argument. Typed interactively, the database password is stored in
`config/secrets/postgres_password` and referenced from `password_file`;
scripted installs use `--pg-password-file FILE`. Use an absolute path for
`password_file` outside the default layout.

Schema behavior: every table is created inside `database.postgres.schema`
(default `network_storage`), so one database can host other applications
alongside it. The PostgreSQL user needs permission to create the schema, or
ownership of an existing one:

```sql
CREATE ROLE sbox_ns LOGIN PASSWORD 'change-me';
CREATE DATABASE sbox_ns OWNER sbox_ns;
```

One server instance (one data directory) per schema. Sharing a schema
between independent instances or data directories is not supported. Owner
identity and cookie keys live with the data directory, so a schema moved
without its data directory loses its owner sessions and secret key material.

## Migrations

```sh
sbox-ns db status    # provider, schema version, pending migrations
sbox-ns db migrate   # apply pending migrations
```

`db status` prints the schema version, the version the binary supports, and
whether a migration is pending (or the database is newer than the binary, in
which case upgrade `sbox-ns`). `db migrate` applies pending migrations; the
server also migrates on start. Take a backup before upgrading to a release
that needs a migration; releases list the minimum version they upgrade from,
so old installs may need to step through an intermediate release
(see [self-hosting.md](self-hosting.md#updates)).

Schema version 3 adds project/slug endpoint and project/key-identifier API-key
indexes, plus surrogate primary keys for request logs and storage errors.
Events sharing a project and millisecond are now kept separately. Existing log
rows are preserved by the migration. On PostgreSQL, the v3 `ADD PRIMARY KEY`
on the log tables takes an `ACCESS EXCLUSIVE` lock on each table for the whole
migration: expect request/error-log writes to pause on large tables. Take a
backup and migrate in a maintenance window. Version 2 remains the earlier
legacy-player-projection opt-in migration.

Before applying or testing migrations against an existing database, run
`sbox-ns db backup`. A plain rollback restores that pre-migration backup with the
matching older binary; when every migration since is additive,
`sbox-ns rollback --keep-data` returns to the older binary without restoring
anything (see below).

### Rollback-safe migrations

Migrations still only move forward, but each one is tagged in code
(`SchemaChange` in `SqliteMigrations.cs` and `PostgresMigrations.cs`, which must agree):

- **`additive`**: the previous binary's statements keep working on the result: a new
  nullable column, a new table, a new index, data that older code ignores or tolerates.
- **`breaking`**: anything else: a dropped or renamed column, a changed type or
  constraint, a table rebuilt so that old statements no longer fit, rewritten data that
  older code would misread. When in doubt, tag it `breaking`. Migration 1 (the baseline) is `breaking`.

`schema_version` records the tag (`compat`) and `min_binary_schema` for every applied
migration: the highest `breaking` version applied so far (the *compatibility floor*). Tables
created before this feature get the two columns, and their rows are filled from the tags in
the binary, the first time `db migrate` (or the server on start) runs; until then the floor is
derived on read, so `db status` and `doctor` work either way.

| Database vs. this binary | Result |
| --- | --- |
| Schema equal | Runs |
| Schema older | Pending migrations: applied on start or by `db migrate` |
| Schema newer, binary's supported schema ≥ `min_binary_schema` | Runs on it as is: nothing is applied or rewritten, `doctor` passes (it names the floor) |
| Schema newer, binary's supported schema < `min_binary_schema` | Refused with the existing `SchemaVersionTooNewException` message ("…supports schema version N at most… restore a backup taken before the upgrade to roll back"); `doctor` fails |
| Newer rows with no recorded floor | Counted as `breaking` (safe default) |

`sbox-ns db status` prints the floor (`Compat floor:`), and `db status --json` prints
`{"provider","schemaVersion","minBinarySchema","supportedSchemaVersion"}`;
`sbox-ns schema-info [--json]` prints the highest schema the binary supports, and
`sbox-ns compatibility [--json]` runs this binary's non-mutating preflight (the floor-rule
verdict plus the layout generation it understands). `rollback --keep-data` and
`update --allow-downgrade` run the *target* binary's copy of that preflight against every
instance's database, so either side refusing stops everything before the service is stopped
(see [self-hosting.md](self-hosting.md#rolling-back)).

Adding a migration: pick the tag with the rule above and put it in both drivers.
`scripts/rollback-notes.sh` turns the tags into the release-notes line
"Rolls back without data loss to: …" ([releasing.md](releasing.md)).

**Limitation.** Binaries released before this feature check for an exact schema match and
have no `schema-info` command, so they are never offered a data-preserving rollback: going
back to one of them still needs the restore of the pre-update backup (and loses data written
since). The same applies to the first update that crosses this feature: only releases that
already contain the floor rule can be rolled back to with `--keep-data`.

### Transactions

Endpoint record writes and deletes commit in one store transaction on both
backends. A failure rolls back the complete endpoint flush instead of issuing
compensating writes. Analytics batches also commit transactionally; their
retention and bounded buffer are documented in
[configuration.md](configuration.md#analytics-retention-and-buffering).

## Multiple server instances

The record write queue (`endpoints.write_queue.*`, see [configuration.md](configuration.md#record-write-queue)) lives in memory in one server process. It orders writes to the same record only among requests that reach that process.

- **Covered:** concurrent writes to one record inside one process, through the record API, YAML endpoints and script endpoints. The script concurrency gate (`endpoints.script_gate.wait_ms`) is per process as well, so each instance admits its own scripted requests.
- **Not covered:** two server processes writing to the same database. They do not share the queue, so it cannot order their writes.

What still protects data across processes:

- **Version checks.** YAML collections with `requireSaveVersion` check the record version before they commit. A writer that lost the race gets `409 CONFLICT` and must reload before retrying. The owner and dev paths that take `expectedVersion` behave the same way.
- **Store transactions.** Endpoint record writes and deletes commit in one transaction, so a rejected write never leaves a half-applied endpoint (see [Transactions](#transactions)).

What is not protected across processes:

- **Unversioned record API writes.** `POST /api/storage/{project}/{collection}/{key}` has no expected-version parameter. Two processes that read-modify-write the same record can interleave, and the last commit wins.

Until the record API accepts an expected version, run one server process per database schema when clients write the same records concurrently, or write those records through a `requireSaveVersion` YAML endpoint.

## Project secrets table

Schema version 11 adds `{prefix}project_secrets` (`project_id`, `secret_name`, `value_enc`, `updated_at_unix_ms`; primary key `(project_id, secret_name)`). The migration is tagged additive (see [Rollback-safe migrations](#rollback-safe-migrations)): existing tables and statements are unchanged. `value_enc` holds version-prefixed, base64 AES-GCM ciphertext; the table never holds plaintext. A foreign key to `projects` with `ON DELETE CASCADE` prevents orphan writes and preserves cleanup when an older binary deletes a project. The owner-deletion operation removes the authoritative project and membership in one transaction. PostgreSQL secret writes take a transaction advisory lock, so the 64-secret cap holds across server instances.

## Backups and restores

```sh
sbox-ns db backup                        # timestamped backup inside the data folder
sbox-ns db backup --output /backups/ns.bak
sbox-ns db restore /backups/ns.bak       # stop the server first
```

Backups are same-provider restores, not cross-provider migration. To move
between SQLite and PostgreSQL (either way) or to another machine, use
`sbox-ns export` and `sbox-ns import`; see [export.md](export.md).

Media uploads are not in the database backup. `sbox-ns db backup` and
`sbox-ns export` cover the database only: media object rows are there, but the
image bytes (local media folder or S3 bucket) are not. Back those up separately;
see [media.md](media.md#backups-and-export).

SQLite: the backup is a consistent file copy, safe while the server runs.
Restore replaces the database file and keeps the replaced file as
`.before-restore`; WAL sidecars are cleared.

PostgreSQL: the backup uses `pg_dump` custom format scoped to the
configured schema, and restore uses `pg_restore` with `--clean --if-exists
--no-owner --schema=<schema>`. Both tools must be installed and on `PATH`;
the server passes host, port, username, database, password (`PGPASSWORD`)
and SSL mode from your configuration. Stop the server before restoring.

Always back up the config folder with the database, including
`<config dir>/secrets/`. Without the auth session secret, player sessions
become invalid after a restore; without the storage encryption key, every
secret API key stops working.

## Resource guidance

SQLite: no tuning. Keep the data folder on local disk with room for the
database plus timestamped backups under `<data dir>/backups/`.

PostgreSQL: defaults are `max_pool_size = 50`, `connect_timeout_seconds =
15`, `startup_timeout_seconds = 60`. Raise `max_pool_size` only with the
database sized to accept the connections; raise `startup_timeout_seconds`
when the database boots at the same time as the server (Compose, systemd
ordering). Connection problems surface in `sbox-ns db test`,
`sbox-ns doctor`, and `GET /health` (503 with `database-unavailable` when
the database is down).
