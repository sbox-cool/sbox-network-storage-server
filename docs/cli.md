# CLI reference

One `sbox-ns` binary is the server and its operator CLI. `sbox-ns --help` prints the full list
with every flag; this page groups the commands and links the guides. Every command accepts
`--config-dir` and `--data-dir` to pick the folders ([configuration](configuration.md)).

## Get started

| Command | Purpose |
| --- | --- |
| `quickstart <name> [--public-url URL] [--json]` | Configure if needed, create or reuse the project and keys, print the game's `Configure(...)` line. Safe to re-run. Treat `--json` output as secret. |
| `setup [--non-interactive ...]` | Create or update the config folder (`--database`, `--listen`, `--public-url`, `--pg-*`, `--allow-http`). |
| `start [--listen ADDR]` | Run the server in the foreground. |
| `doctor [--project ID] [--json]` | Check config, database, port, TLS, disk and updates, or one project's authority. |
| `version` | Print the version. |

## Projects, keys and owner

| Command | Purpose |
| --- | --- |
| `project create <name> [--hosting player-hosted\|dedicated\|hybrid\|unset]` | Create a project. |
| `project list` / `project delete <projectId>` | List or delete projects. |
| `project clients <projectId> [--days 30] [--json]` | Library versions seen and approximate players ([version policy](version-policy.md)). |
| `project authority <projectId> [--json]` | Advisory check for game data a client could still change. |
| `key create <projectId> --type public\|secret [--label L]` | Create an API key. Only public keys ship in games. |
| `key list <projectId>` / `key revoke <projectId> <key>` | List or revoke keys. |
| `admin login-link [--minutes N]` | Single-use owner sign-in link (creates the owner if none exists). [Admin panel](admin-panel.md). |
| `admin create` / `admin reset-password` / `admin reset-2fa` | Manage the local owner. |
| `adminpanel disable\|enable` | Turn the owner console off or on; game APIs stay available. |

`project delete <projectId>` atomically removes the project's database rows and
revokes its keys, including keys cached by another running server. It then removes
media blobs and project/key workspace files. If blob or workspace storage is
unavailable, fix that storage and repeat the same command with the original ID;
a persisted cleanup journal lets deletion finish even after a restart or after
the project disappears from `project list`. Repeating a completed deletion is safe.
Finish cleanup before importing an archive with that project ID.

## HTTPS and network

| Command | Purpose |
| --- | --- |
| `tunnel enable\|status\|disable` | Free HTTPS name through a Cloudflare tunnel, no open ports ([guide](self-hosting.md#hosted-https-without-a-domain)). |
| `dns enable\|status\|disable` | Free `sboxns.com` name pointing at your own public IP ([guide](self-hosting.md#free-hosted-name-with-your-own-ip-sboxnscom)). |
| `security status` | Signing key and request encryption keys ([encrypted requests](encrypted-requests-v2.md)). |
| `security rotate-key [--grace-seconds N]` / `security rotate-signing-key [--grace-seconds N]` | Rotate the request encryption key, or the signing key games pin ([encrypted requests](encrypted-requests-v2.md#key-pinning)). |

## Data

| Command | Purpose |
| --- | --- |
| `db test\|status\|migrate` | Connectivity, schema version, migrations ([databases](database.md)). |
| `db backup [--output FILE]` / `db restore <FILE>` | Back up and restore (stop the server before restoring). |
| `export [--out FILE] [--no-secrets]` | Whole server (data and config) to one `.tar.gz` ([export](export.md)). |
| `import <FILE> [--config] [--force] [--verify-only]` | Restore an export, or validate it without writing. |
| `compatibility [--json]` / `schema-info [--json]` | Non-mutating schema checks used by rollback and downgrade. |

## Configuration

| Command | Purpose |
| --- | --- |
| `config path\|show\|validate` | Locate, print (with sources) and validate settings. |
| `config get <key>` / `config set <key> <value>` | Read or change one setting; comments are kept. |
| `config edit [file]` | Open a config file in `$EDITOR` and validate it. |

## Service and updates

| Command | Purpose |
| --- | --- |
| `service install\|uninstall\|start\|stop\|restart\|status` | systemd (Linux), launchd (macOS) or Windows service. `--instance NAME --port PORT` adds an instance; `--auto-update` installs the update timer. |
| `logs [-f]` | Show or follow service logs. |
| `update [--check] [--version X.Y.Z [--allow-downgrade]]` | Check for or install a release ([updates](self-hosting.md#updates)). |
| `update --auto [--all-instances]` | Unattended update inside `updates.window`, health-checked, rolled back on failure. |
| `update verify` / `update observed-libraries` | Post-update regression verdict; library builds served in the last 30 days. |
| `rollback [--keep-data] [--all-instances]` | Return to the previous release. |
| `layout migrate [--revert]` | Linux, root: move runtime state out of the config folder. |

## Optional services

| Command | Purpose |
| --- | --- |
| `mcp` | Model Context Protocol server over stdio for coding agents ([MCP](mcp.md)). |
| `register --email ADDRESS` / `register --remove` | Confirmed security and update email notices. |
| `telemetry status\|enable\|disable\|preview` | Anonymous usage statistics, off by default. |
