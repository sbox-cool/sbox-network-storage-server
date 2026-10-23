# <img src="src/SboxNetworkStorage.Server/wwwroot/img/sbox-network-storage-icon-512.png" alt="Network Storage icon: lime companion cube" width="48" height="48" /> s&box Network Storage

**Open-source, self-hosted game backend for s&box.** Player-hosted, P2P or dedicated: your
server checks saves, purchases, rewards and combat results against game values your game reads too.

One `sbox-ns` binary, SQLite or PostgreSQL, no signup and no mandatory telemetry. Games use the
existing [Network Storage library](https://github.com/sbox-cool/sbox-network-storage) unchanged.

[Website](https://sboxns.com) · [Docs](docs/README.md) · [Live demo](https://demo.sboxns.com) ·
[Releases](https://github.com/sbox-cool/sbox-network-storage-server/releases) ·
[Changelog](CHANGELOG.md)

<a href="docs/images/console-project-overview.png"><img src="docs/images/console-project-overview.png" width="49%" alt="Project overview in the owner console"></a>
<a href="docs/images/console-endpoint-pipeline.png"><img src="docs/images/console-endpoint-pipeline.png" width="49%" alt="Endpoint pipeline in the owner console"></a>

## Quickstart

**1. Install** on a Linux VPS (systemd). This creates **My Game** with API keys, gives it a free
HTTPS name through a Cloudflare tunnel (no open ports) and starts the service:

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh \
  | sudo env SBOX_NS_PROJECT="My Game" SBOX_NS_TUNNEL=1 sh
```

Save the printed project ID, keys and `Configure(...)` line; a new secret key is shown once.
Signed DNS on your own IP, your own domain, macOS, Windows, Docker and manual installs:
[self-hosting guide](docs/self-hosting.md).

**2. Point your game at it.** Install
[Network Storage](https://sbox.game/sboxcool/network-storage) from **Editor > Library Manager**:

```csharp
NetworkStorage.Configure( projectId, publicKey, "https://<name>.sboxns.com" );
```

Only the **public** key ships in a game. See [client setup](docs/client-setup.md).

**3. Write the rules.** Author collections, endpoints and workflows in the owner console at
`/dashboard` (`sbox-ns admin login-link` prints a sign-in link) or as YAML under
`Editor/Network Storage/` and push them with **Editor > Network Storage > Sync Tool**.

## What you get

| | |
| --- | --- |
| **Server-side rules** | YAML or [JavaScript/TypeScript endpoints](docs/endpoints-javascript.md) recompute claims from game values and the server clock before anything is written. |
| **[Game values](docs/game-values.md)** | Shared numbers and tables, read by the game in one call and by endpoints on the server; snapshotted per published build. |
| **Endpoint-only data** | New collections answer `403 ENDPOINT_ONLY` to direct client writes. Per-player records keyed by Steam ID, global collections, queries and leaderboards. |
| **[Owner console](docs/admin-panel.md)** | Records, endpoint editor with dry runs, version history, analytics, logs, [monitoring](docs/monitoring.md) and [alerts](docs/alerting.md). |
| **Security** | Optional auth sessions and [encrypted requests](docs/encrypted-requests-v2.md), rate limits, request and audit logs. |
| **Portable** | [Export a project](docs/export.md) and import it on another SQLite or PostgreSQL server; same project ID. |
| **Operations** | Opt-in automatic updates with backup and rollback, `sbox-ns doctor`, [MCP for coding agents](docs/mcp.md). |

Checks bound what a client can claim; they cannot prove a hit landed. Cap rewards and review
the rules that matter. [Hosting models](docs/hosting-models.md) explains which key each caller
holds.

## Status

**Pre-1.0 preview.** Configuration and storage formats may change between releases; keep backups
(`sbox-ns db backup`). Server and library release independently, and older or unidentified game
libraries stay accepted by default: [compatibility rules](docs/compatibility.md).

## Build from source

```sh
dotnet build SboxNetworkStorage.sln
dotnet test
dotnet run --project src/SboxNetworkStorage.Server -- start
```

Needs the .NET SDK pinned in `global.json`. Read [CONTRIBUTING.md](CONTRIBUTING.md) before a PR
and [SECURITY.md](SECURITY.md) to report a vulnerability privately. Coding agents: start with
[AGENTS.md](AGENTS.md) and [llms.txt](llms.txt).

## License

[AGPL-3.0-only](LICENSE). If you run a modified version for others over a network, offer them its
source. s&box is a trademark of Facepunch Studios; this project is not affiliated with or endorsed
by Facepunch Studios.
