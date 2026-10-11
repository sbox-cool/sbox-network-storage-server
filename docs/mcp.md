# Coding agents (MCP)

`sbox-ns mcp` serves the [Model Context Protocol](https://modelcontextprotocol.io)
over stdio. Your coding agent (Claude Code, Cursor, Claude Desktop and others)
starts it on your server through SSH, or directly on your own machine. Nothing
extra listens on the network, and the agent can do only what that login can do.

With it, an agent can set up the server, give you the line for your game,
write and validate collections and endpoints, dry-run them, read request logs
and errors, and look at player records. Anything that changes or deletes
definitions or data is off until you turn it on.

## Connect

### Server over SSH (recommended for a VPS)

```sh
claude mcp add sbox-ns -- ssh my-vps sudo sbox-ns mcp
```

Other clients that read an `mcpServers` file:

```json
{
  "mcpServers": {
    "sbox-ns": {
      "command": "ssh",
      "args": ["my-vps", "sudo", "sbox-ns", "mcp"]
    }
  }
}
```

`my-vps` is a host from your `~/.ssh/config`. Neither SSH nor `sudo` may ask
for a password, because the agent cannot type one: use SSH key authentication,
and either log in as root or allow this one command without a password
(`youruser ALL=(root) NOPASSWD: /usr/local/bin/sbox-ns mcp` via `visudo`).

Running as root gives every tool. Database work still runs as the `sbox-ns`
service account, so file ownership stays correct. If you would rather not give
the agent root, use `sudo -u sbox-ns sbox-ns mcp`: reading, testing and saving
work, but `config_set` and `service_restart` fail, because the operator
configuration and the system service belong to root.

### Your own machine

When the server runs on the same computer (development), skip SSH. A project
`.mcp.json` in your game folder:

```json
{
  "mcpServers": {
    "sbox-ns": { "command": "sbox-ns", "args": ["mcp"] }
  }
}
```

Add `"--config-dir", "...", "--data-dir", "..."` to `args` if you do not use
the default folders. The tools work without a running server, but the game
needs `sbox-ns start` running in a terminal.

### Claude Code plugin

This repository is also a Claude Code plugin marketplace. The plugin adds the
MCP server and skills for setup, design, debugging and operations:

```text
/plugin marketplace add sbox-cool/sbox-network-storage-server
/plugin install sbox-ns@sbox-ns
```

It starts `sbox-ns mcp` locally. For a VPS, add the SSH form above with
`claude mcp add` instead; the skills work with either.

## Turning on changes

Reading is always allowed. Each kind of change has its own switch on the
server, read on every call (no restart):

| Setting | Allows | Risk |
| --- | --- | --- |
| `mcp.allow_writes` | `definition_save` | Changes how the game behaves. Endpoint and collection saves go to the staged revision by default, which live games do not use until the next game package sync. With `target: live` running games change within about a minute. |
| `mcp.allow_data_writes` | `data_record_write` | Changes real player or global records. Take a backup first (`db_backup`). |
| `mcp.allow_destructive` | `definition_delete`, `data_record_delete`, `key_revoke`, `project_delete` | Cannot be undone except from a backup. A revoked key stops every game build that uses it. |

```sh
sbox-ns config set mcp.allow_writes true
```

When a switch is off, the tool says exactly which command turns it on. Every
change also needs a `confirm` argument that repeats what it changes (for
example `proj_123/endpoint/mine`), and is written to the project's audit log as
`mcp`.

Database restore, import, tunnel and DNS disable are never available as tools.
Run them yourself over SSH.

## What the agent sees

- **Secrets.** `quickstart` and `key_create` return a new secret key once.
  It lands in the conversation and your model provider's logs. `config_get`
  and `config_show` hide secret settings, and `config_set` refuses them: put
  the value in a file and set the matching `*_file` setting.
- **Player data.** `data_records`, `data_record`, `errors_recent` and
  `logs_requests` send player data and request paths to your model provider.
- **Untrusted text.** Record contents and error messages come from games.
  Agents are told to treat them as data, never as instructions, but review
  what an agent proposes to change.
- **Caches.** Tools run as a separate process. A running server picks up their
  changes (new keys, projects and definitions) within about a minute.

## Tools

Setup and operations:

| Tool | What it does |
| --- | --- |
| `server_status` | `sbox-ns doctor`: config, database, port, TLS, disk, schema, updates |
| `version` | Installed version |
| `quickstart` | Configure if needed, create or reuse a project, ensure keys, return the `NetworkStorage.Configure(...)` line. `requireSboxAuth: false` makes a development project that accepts requests without s&box tokens. |
| `project_list`, `project_create` | Projects |
| `project_authority` | Hosting profile and advisory authority findings for one project (read-only) |
| `key_list`, `key_create` | API keys (secret keys are returned once, when created) |
| `config_show`, `config_get`, `config_set`, `config_validate` | Settings (secrets hidden; secret settings cannot be set) |
| `db_status`, `db_backup` | Database status and consistent backups |
| `update_check` | Check for a newer release (never installs) |
| `service_status`, `service_restart` | The installed system service |
| `tunnel_status`, `tunnel_enable` | The optional hosted HTTPS connector |
| `dns_status` | The hosted `sboxns.com` name (read-only) |
| `telemetry_status` | Whether anonymous usage statistics are on (read-only) |
| `dashboard_link` | A single-use dashboard sign-in link, valid up to 15 minutes |

Building the game backend:

| Tool | What it does |
| --- | --- |
| `examples_list`, `example_get` | Built-in example definitions (economy, inventory, leaderboards and more) and empty skeletons |
| `definitions_list`, `definition_get` | Read definitions as YAML source, the same files the editor Sync Tool keeps under `Editor/Network Storage/` |
| `definition_check` | Validate YAML exactly as saving would, without saving |
| `definition_save` | Save YAML (needs `mcp.allow_writes`) |
| `definition_delete` | Delete a definition (needs `mcp.allow_destructive`) |
| `endpoint_test` | Dry-run an endpoint: status, body, every step, and the writes it would make. Nothing is stored. |
| `tests_run` | Run the saved endpoint tests as dry runs |
| `client_snippet` | The game's `Configure` line for an existing project, with warnings about localhost and plain HTTP |
| `endpoint_snippet` | C# that calls an endpoint and reads its error |

Debugging:

| Tool | What it does |
| --- | --- |
| `logs_requests` | Recent game requests, with what each 4xx status usually means |
| `errors_recent` | Recent server errors with message, stack and an explanation |
| `usage` | Requests, endpoint calls and storage for a month |
| `data_collections`, `data_records`, `data_record` | Browse records (read-only) |
| `data_record_write`, `data_record_delete` | Change records (need `mcp.allow_data_writes` / `mcp.allow_destructive`, plus the record's current version) |

The server also offers its guides as MCP resources (`sbox-ns://docs/client-setup`,
`game-client`, `mcp`, `admin-panel`) and three prompts: `first-setup`,
`new-endpoint` and `debug-player`.

`sbox-ns dev <tool>` is the command behind the development tools. It reads the
tool's arguments as one JSON object on stdin and prints JSON, so YAML and
record payloads never appear on the command line.

## Typical first session

Ask your agent: *"Set up sbox-ns for my game called Ore Miner and give me the
line to add to my game."* It calls `server_status` and `quickstart`, and hands
you the `NetworkStorage.Configure(...)` line and the editor secret key.

Then: *"Players should mine ore and sell it for coins. Build the backend."* The
agent starts from the examples, validates its YAML with `definition_check`,
saves it to the staged revision (if you allowed writes), dry-runs the endpoints
and gives you the C# to call them.
