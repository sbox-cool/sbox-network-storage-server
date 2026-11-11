# Backups

sbox-ns can back up its database on a schedule. Every install keeps local
backups. Installs enrolled in **sboxns Backups** also upload each backup to
offsite storage. Offsite upload is opt-in and off by default.

## Local backups (on by default)

- The server makes a backup every `backup.schedule.interval_hours` (default 24).
  The first run is about 10 minutes after start when no run is recorded. After
  that, the next run is due one interval after the last attempt, so restarts do
  not reset the schedule.
- Files go to `<data dir>/backups/` and use the same format as `sbox-ns db backup`:
  `sbox-ns-auto-YYYYMMDD-HHMMSS.db` (SQLite) or `.dump` (PostgreSQL).
- `backup.schedule.keep_local` (default 7) keeps the newest automatic backups.
  Only `sbox-ns-auto-*` files are removed. Files from `db backup` are never deleted.
- `sbox-ns backup now` runs the same procedure at once. It works while the server
  is stopped, and it follows the same keep_local rule.
- Only one backup runs at a time. A second run reports that another backup is running.
- The last run is recorded in `backup-state.json` in the state folder (the data
  folder for legacy layouts). It holds the time, outcome, any error, and the file
  name. It holds no secrets.

## sboxns Backups (opt-in offsite copies)

sboxns Backups is available only to hosted sbox.cool installs at this time. Other
installs see "sboxns Backups is coming soon" and keep local backups. You can
copy `<data dir>/backups/` anywhere with any tool you like.

When enabled, each backup is:

1. Compressed with gzip into a temporary file.
2. Checksummed (SHA-256) and sized.
3. Uploaded to sboxns Backups, then confirmed by the service, which checks the size.

The request is signed with this install's existing sboxns identity (the same key
used for sboxns tunnels and DNS names). Run `sbox-ns backup identity --json` to see
its name and public key. The identity is created if it does not exist yet.

Offsite copies contain everything in the database. Treat them like the database itself.
The service keeps copies within the plan's quota and count, and removes the oldest
completed copies first. It never removes the newest completed copy.

If the upload fails, the local backup is kept. The scheduler logs a warning, and
`backup status` shows the error. `backup now` exits non-zero, so scripts and
timers can notice.

```sh
sbox-ns backup sboxns enable       # checks availability; restart the server afterward
sbox-ns backup sboxns disable      # no upload from the next backup on; restart afterward
sbox-ns backup list --offsite      # completed offsite copies, newest first
sbox-ns backup download <ID>       # saves sbox-ns-backup-<ID>.<db|dump>.gz and checks it
sbox-ns backup delete <ID>         # removes one offsite copy
```

To restore an offsite copy, stop the server, then:

```sh
sbox-ns backup download <ID>
gunzip -k sbox-ns-backup-<ID>.db.gz
sbox-ns db restore sbox-ns-backup-<ID>.db
```

## Commands

| Command | Purpose |
| --- | --- |
| `backup status [--json]` | Schedule, last run, local backup count, and sboxns Backups state. |
| `backup now` | Make a local backup now. Exits non-zero if the offsite upload fails. |
| `backup list [--json]` | Local backups in `<data dir>/backups`. |
| `backup list --offsite [--json]` | Offsite copies. |
| `backup download <ID> [--output FILE]` | Download and verify one offsite copy. Refuses to overwrite a file. |
| `backup delete <ID>` | Delete one offsite copy. |
| `backup identity [--json]` | This install's sboxns name and public key. |
| `backup sboxns enable\|disable` | Turn offsite upload on or off. Restart the server afterward. |

`--json` output is stable for scripts. `backup status --json` has `local` and
`sboxns` objects. `backup identity --json` has `name` and `publicKey` (base64 SPKI DER).

## Settings

| Key | Default | Meaning |
| --- | --- | --- |
| `backup.schedule.enabled` | `true` | Run scheduled local backups. |
| `backup.schedule.interval_hours` | `24` | Hours between scheduled backups (1 to 168). |
| `backup.schedule.keep_local` | `7` | Automatic local backups to keep (1 to 365). |
| `backup.sboxns.enabled` | `false` | Upload each backup to sboxns Backups. Use `backup sboxns enable/disable`. |
| `backup.sboxns.endpoint` | `https://sboxcool.com/api/network-storage/backups` | sboxns Backups API; HTTPS required except loopback testing. |

Settings live in `server.toml` and are changed with `sbox-ns config set` or the
`backup sboxns` commands. Restart the server after changing them.

## Monitoring

The existing `backup.age.hours` metric reads the newest file in `<data dir>/backups`,
so it shows the age of the newest local backup whether or not it was uploaded.
