# sbox Network Storage Server: self-hosted s&box game backend

**sbox Network Storage Server** is a self-hosted s&box game backend. It stores
sbox player save data and cloud saves, powers leaderboards and shared game
data, and runs server-side endpoints and workflows so game logic that must not
be trusted to the client stays on your server. Think of it as an open-source
alternative to Firebase or PlayFab built specifically for s&box games, running
on your own machine with SQLite or PostgreSQL.

It serves the existing **Network Storage** client library for s&box. Your game
needs no code changes: point the library's base URL at your server and it
works the same way it does with the managed service.

- **s&box library:** [sbox.game/sboxcool/network-storage](https://sbox.game/sboxcool/network-storage)
- **Client library source:** [github.com/sbox-cool/sbox-network-storage](https://github.com/sbox-cool/sbox-network-storage)
- **Managed hosted service:** [sbox.cool/tools/network-storage](https://sbox.cool/tools/network-storage)
- **API and library docs:** [sbox.cool/wiki/network-storage-v3](https://sbox.cool/wiki/network-storage-v3)

> **Status: early preview.** This is pre-1.0 software extracted from the
> production code behind the managed service. Parity testing against the managed
> service is in progress, and configuration or storage formats may still change
> between releases. Keep backups. A local-owner dashboard manages projects,
> API keys and core settings at `/dashboard`. Full managed-dashboard and managed
> project export/import parity are not established; use the editor Sync Tool for resources.

## Features

- **Player save data:** per-player records keyed by Steam ID, organised into collections you define.
- **Shared and global data:** global collections for world state, shared lists and game-wide records.
- **Leaderboards and queries:** named queries over your collections for leaderboards and rankings.
- **Server-side endpoints and workflows:** run validated game logic on the server
  instead of trusting the client, invoked by slug from the game.
- **Game values and stats:** remotely tunable values, player stats and session heartbeats.
- **Player auth sessions:** verifies s&box player identity before writes.
- **Rate limits and security config:** per-project limits enforced on the server.
- **Sync Tool compatible:** the editor Sync Tool pushes collections, endpoints and workflows to your server.
- **Two databases:** SQLite with zero configuration, or PostgreSQL with all tables in their own schema.
- **One binary:** `sbox-ns` is both the server and its management CLI. Self-contained builds for
  Linux, macOS and Windows on x64 and arm64, plus a Docker image.
- **Local owner:** database-backed owner identity, tokenized local first-run setup,
  cookie login with CSRF protection, and `admin create|reset-password` recovery commands.
- **No telemetry, no auto-update:** the server only shows a notice when a new release exists.

## Quick start

### Linux and macOS

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | sh
```

Run with `sudo sh` instead to install a systemd service on Linux. The installer
verifies the download checksum, installs `sbox-ns` and starts the interactive
`sbox-ns setup`.

### Windows (PowerShell)

```powershell
irm https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.ps1 | iex
```

### Docker

```sh
docker run -d --name sbox-ns -p 8080:8080 \
  -v sbox-ns-config:/config -v sbox-ns-data:/data \
  --restart unless-stopped \
  ghcr.io/sbox-cool/sbox-network-storage-server:latest
docker exec -it sbox-ns /app/sbox-ns setup
docker restart sbox-ns
```

Then create a project and keys for your game:

```sh
sbox-ns project create "My Game"
sbox-ns key create <projectId> --type public --label game
sbox-ns key create <projectId> --type secret --label editor
```

The server listens on plain HTTP port **8080** by default. HTTPS is optional:
built-in Let's Encrypt, your own certificate, or a reverse proxy.
`GET /health` reports whether the server is up.
Open the one-time local `/setup?token=...` URL printed in the server log when no
owner exists, or create one with `sbox-ns admin create`. After that, `/login`
opens the local-owner dashboard. Use an SSH tunnel for remote initial setup and
HTTPS for remote login. Details and management coverage limits are in the full guide.

Full guide: [docs/self-hosting.md](docs/self-hosting.md).

## Point your game at your server

No client changes are needed. In the s&box editor open **Editor > Network
Storage > Setup**, enter the project ID and keys from your server, and set the
**Base URL** field to your server, for example `http://your-server:8080`. Or configure it in code:

```csharp
NetworkStorage.Configure( projectId, apiKey, "http://your-server:8080" );
```

Use the base URL without a path; the client adds `/v3/...` itself. Details:
[docs/client-setup.md](docs/client-setup.md).

### Client reachability

Which kinds of URL s&box game clients can reach has not been verified yet.
Every row is **untested**; reports are welcome.

| Server URL type | Example | Status |
| --- | --- | --- |
| HTTPS domain | `https://ns.example.com` | **untested** |
| HTTP domain | `http://ns.example.com:8080` | **untested** |
| IP address and port | `http://203.0.113.10:8080` | **untested** |
| localhost | `http://localhost:8080` | **untested** |

## Configuration

Settings live in a folder of commented TOML files:

```text
server.toml      listener, TLS, logging, auth
database.toml    SQLite or PostgreSQL
updates.toml     update notices
conf.d/*.toml    optional overrides
```

The folder is `/etc/sbox-ns/` for Linux service installs (data in
`/var/lib/sbox-ns/`) and `<install dir>/config/` otherwise. Edit a file and
restart to apply.

Precedence, lowest to highest: built-in defaults, the main files, `conf.d/*.toml`,
`NS_` environment variables, command line flags (`--config-dir`, `--data-dir`,
`--listen`). Environment variables are `NS_` plus the key path in upper case
with `.` replaced by `__`, for example `NS_SERVER__LISTEN=0.0.0.0:9000` or
`NS_DATABASE__POSTGRES__PASSWORD_FILE=/run/secrets/pg`. `NS_CONFIG_DIR` and
`NS_DATA_DIR` choose the folders.

Every key with its default: [docs/configuration.md](docs/configuration.md).

## CLI reference

| Command | Purpose |
| --- | --- |
| `sbox-ns start [--listen ADDR]` | Run the server in the foreground |
| `sbox-ns setup [--non-interactive ...]` | Create the configuration interactively, or from flags (`--database`, `--pg-host`, `--listen`, `--public-url`) |
| `sbox-ns config path\|show\|get <key>\|set <key> <value>\|validate\|edit` | Locate, inspect and edit configuration |
| `sbox-ns db test\|status\|migrate` | Database connectivity, status and migrations |
| `sbox-ns db backup [--output FILE]` / `db restore <FILE>` | Back up and restore (SQLite file copy; PostgreSQL via `pg_dump`/`pg_restore`) |
| `sbox-ns project create <name>\|list\|delete <projectId>` | Manage projects |
| `sbox-ns key create <projectId> --type public\|secret [--label L]` | Create an API key |
| `sbox-ns key list <projectId>` / `key revoke <projectId> <key>` | List and revoke API keys |
| `sbox-ns service install\|uninstall\|start\|stop\|restart\|status` | Run as a systemd unit (Linux), launchd job (macOS) or Windows service |
| `sbox-ns logs [-f]` | Show or follow server logs |
| `sbox-ns update [--check] [--version vX.Y.Z]` | Check for or install a release |
| `sbox-ns rollback` | Return to the previous release |
| `sbox-ns version` | Print version information |
| `sbox-ns doctor` | Diagnose configuration, database, ports and update status |

Commands accept `--config-dir` and `--data-dir` to select the folders.

## Databases

**SQLite** is the default. Nothing to install or configure: everything is
stored in one file in the data folder.

**PostgreSQL** suits operators who already run it or want the database on a
separate machine. Configure it in `database.toml`:

```toml
[database]
provider = "postgres"

[database.postgres]
host = "localhost"
port = 5432
database = "sbox_ns"
username = "sbox_ns"
password_file = "/etc/sbox-ns/pg-password"   # or password = "..."
schema = "network_storage"                   # all tables live in this schema
ssl_mode = "Prefer"
```

A full `connection_string` may be used instead of the individual fields.
Check it with `sbox-ns db test`. A ScyllaDB driver is planned.

## Updates

The server never updates itself. It checks for new releases (by default once a
day) and shows a notice in the logs and in `sbox-ns doctor`,
flagging security fixes and required migrations.

```sh
sbox-ns update --check   # is there a new version?
sbox-ns update           # install it
sbox-ns rollback         # go back to the previous version
```

To disable update checks, set `check = false` under `[updates]` in `updates.toml`.

## Self-hosted vs managed

| | Self-hosted (this repo) | Managed ([sbox.cool](https://sbox.cool/tools/network-storage)) |
| --- | --- | --- |
| Cost | Free, you pay for your own server | See sbox.cool |
| Hosting, uptime, scaling | You | sbox.cool |
| Database | SQLite or PostgreSQL you operate | Managed |
| Backups | You (`sbox-ns db backup`) | Managed |
| Updates | Manual, with notices | Automatic |
| Management | `sbox-ns` CLI and the editor Sync Tool | sbox.cool web dashboard and the editor Sync Tool |
| Data location | Your machine | sbox.cool infrastructure |
| Client library | Same, change the base URL | Same, default base URL |
| Source code | AGPL-3.0, this repo | Closed |
| Support | GitHub issues | sbox.cool |

## Compatibility

Server releases are tested against specific versions of the s&box client
library. This table is filled in per release.

| Server version | Client library version | Notes |
| --- | --- | --- |
| _to be filled per release_ | _to be filled per release_ | |

## Building from source

```sh
dotnet build SboxNetworkStorage.sln
dotnet test
dotnet run --project src/SboxNetworkStorage.Server -- start
```

Requires the .NET 8 SDK. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Please report vulnerabilities privately, see [SECURITY.md](SECURITY.md).

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md)
first, and open an issue before large changes. Reports on which client
reachability rows work are especially useful.

## License

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only). If you run a
modified version of this server for others over a network, you must offer them
the source code of your modified version.

s&box is a trademark of Facepunch Studios. This project is not affiliated with
or endorsed by Facepunch Studios.
