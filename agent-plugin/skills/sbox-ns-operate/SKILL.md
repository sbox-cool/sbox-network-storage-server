---
name: sbox-ns-operate
description: Operate a self-hosted s&box Network Storage server - backups, updates, settings, HTTPS, the dashboard and what coding agents may change. Use when the user asks about running, securing, backing up or updating sbox-ns.
---

# Operate sbox-ns

## Backups and updates

- `db_backup` before any risky change. Restoring is not a tool: the user runs
  `sbox-ns db restore` over SSH.
- `update_check` reports a newer release. Updating is not a tool: the user
  runs `sbox-ns update`, or turns on unattended updates.

## Settings

- `config_show` lists every setting and where it comes from (secrets hidden).
  `config_get` reads one; `config_set` changes one, then `service_restart`.
- Secret settings (database password, webhook URLs, SMTP password) cannot be
  set through the agent. The tool names the `*_file` setting to use instead.

## HTTPS and the dashboard

- `tunnel_status` / `tunnel_enable`: a hosted HTTPS address without owning a
  domain. Restart afterwards.
- The dashboard refuses sign-in over plain HTTP from another computer. Options:
  SSH port forward (`ssh -L 8080:127.0.0.1:8080 user@host`, then open
  `http://localhost:8080/login`), the tunnel, or TLS.
- `dashboard_link` gives a one-time sign-in link.

## What agents may change

Reading and dry runs are always allowed. Changes need the operator to turn on:

| Setting | Allows |
| --- | --- |
| `mcp.allow_writes` | `definition_save` |
| `mcp.allow_data_writes` | `data_record_write` |
| `mcp.allow_destructive` | `definition_delete`, `data_record_delete`, `key_revoke`, `project_delete` |

Turn on only what the task needs, and say what each risks: definitions change
game behaviour, data writes change real player data, destructive tools cannot
be undone without a backup. Every change is audited as `mcp` in the project log.

## Keys

`key_list` shows keys (secret keys masked). `key_create` makes a new one; a
new secret key appears once in the conversation. Revoking a key that shipped
in a game build breaks that build.
