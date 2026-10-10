# Coding agents (MCP)

`sbox-ns mcp` serves the [Model Context Protocol](https://modelcontextprotocol.io)
over stdio. Your coding agent starts it through SSH, so nothing extra is
exposed on the network and the agent can only do what your SSH login can do.

## Connect

The server runs as the `sbox-ns` system user after a service install, so run
the MCP server as that user to keep database file ownership correct.

Claude Code:

```sh
claude mcp add sbox-ns -- ssh my-vps sudo -u sbox-ns sbox-ns mcp
```

Cursor, Claude Desktop and other clients that read an `mcpServers` file:

```json
{
  "mcpServers": {
    "sbox-ns": {
      "command": "ssh",
      "args": ["my-vps", "sudo", "-u", "sbox-ns", "sbox-ns", "mcp"]
    }
  }
}
```

`my-vps` is a host from your `~/.ssh/config`. Neither SSH nor `sudo` may
prompt for a password, because the agent cannot type one: use SSH key
authentication, and either log in as root or allow this one command without a
password (`youruser ALL=(sbox-ns) NOPASSWD: /usr/local/bin/sbox-ns` via `visudo`).
On a local machine without a service install, use the command `sbox-ns`
with arguments `["mcp"]` instead of SSH.

## Tools

| Tool | What it does |
| --- | --- |
| `server_status` | `sbox-ns doctor`: config, database, port, TLS, disk, schema, updates |
| `version` | Installed version |
| `quickstart` | Configure if needed, create or reuse a project, ensure keys, return the `NetworkStorage.Configure(...)` line |
| `project_list`, `project_create` | Projects |
| `key_list`, `key_create` | API keys (secret keys are returned once, when created) |
| `config_show`, `config_get`, `config_set`, `config_validate` | Settings. `config_show` and `config_get` print secret settings as `********`. `config_set` refuses secret settings; put the value in a file and set the matching `*_file` setting, or set it yourself over SSH |
| `db_status`, `db_backup` | Database status and consistent backups |
| `update_check` | Check for a newer release (never installs) |
| `service_status`, `service_restart` | The installed system service |
| `tunnel_status`, `tunnel_enable` | Inspect or enable the optional hosted HTTPS connector; restart the server afterward |
| `telemetry_status` | Show whether opt-in anonymous usage statistics are enabled (read-only; enable and disable are CLI-only) |

Destructive operations (database restore, import, project delete, key revoke,
tunnel disable) are deliberately not available as tools. Run them yourself over SSH.

## Typical first session

Ask your agent: *"Set up sbox-ns for my game called My Game and give me the
line to add to my game."* It will call `server_status`, then `quickstart`,
and hand you the `NetworkStorage.Configure(...)` line and the editor secret key.
