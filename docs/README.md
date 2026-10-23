# Documentation

The same guides are published at [sboxns.com/docs](https://sboxns.com/docs/), versioned per release.

## Start

| Guide | What it covers |
| --- | --- |
| [Self-hosting](self-hosting.md) | Install on Linux, macOS, Windows or Docker; HTTPS, tunnel and signed DNS; services, backups, updates. |
| [Connect a game](client-setup.md) | Base URL, project and keys, editor setup, error codes, which server URLs the s&box editor allows. |
| [Hosting models](hosting-models.md) | Player-hosted, dedicated and hybrid; which key each caller holds; self-hosted vs managed. |
| [Game client handshakes](game-client.md) | What the library sends at startup: routes, revision init, heartbeats, version headers. |

## Build your backend

| Guide | What it covers |
| --- | --- |
| [Game values](game-values.md) | Shared numbers and tables, revisions and snapshots, a worked anti-exploit example. |
| [JavaScript and TypeScript endpoints](endpoints-javascript.md) | Script endpoints, the `ns` API, `ns.fetch`, globals and limits. |
| [Modules](modules.md) | Installable server modules. |
| [Media uploads](media.md) | Player and project media, quotas and retention. |
| [Game logs](game-logs.md) | Structured game events, search and retention. |
| [Encrypted requests v2](encrypted-requests-v2.md) | The optional request encryption protocol. |

## Operate

| Guide | What it covers |
| --- | --- |
| [Owner console](admin-panel.md) | Sign-in, projects, keys, records, authoring, diagnostics, moving a project, screenshots. |
| [CLI reference](cli.md) | Every `sbox-ns` command, grouped. |
| [Configuration](configuration.md) | Every TOML key, environment variables, precedence and secrets. |
| [Databases](database.md) | SQLite and PostgreSQL, schemas, migrations, backups. |
| [Export and import](export.md) | Portable archives and switching servers or databases. |
| [Monitoring](monitoring.md) and [alerting](alerting.md) | Live server stats, alert rules and notification channels. |
| [Coding agents (MCP)](mcp.md) | `sbox-ns mcp`, permissions and the Claude Code plugin. |

## Compatibility and project process

| Guide | What it covers |
| --- | --- |
| [Compatibility](compatibility.md) | How server updates keep published games working; the rules for API changes. |
| [Version policy](version-policy.md) | Library version reporting and opt-in minimums. |
| [s&box test plan](editor-test-plan.md) | How library and server pairings are tested and recorded. |
| [Releasing](releasing.md) | Release process and evidence. |
| [Live test server](test-server.md) | The disposable CI test server. |
| [Owner console visual system](ui-guidelines.md) | UI tokens and patterns for console work. |

## Elsewhere

- [Client library source](https://github.com/sbox-cool/sbox-network-storage) and its
  [package page](https://sbox.game/sboxcool/network-storage).
- [Network Storage v3 wiki](https://sboxcool.com/wiki/network-storage-v3): YAML source
  authoring, Sync Tool and C# examples. It also documents the managed service; its billing, tiers
  and default hostname do not apply to a self-hosted server.
