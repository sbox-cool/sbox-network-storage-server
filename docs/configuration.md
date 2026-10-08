# Configuration reference

sbox Network Storage Server reads its settings from a folder of commented TOML
files. `sbox-ns setup` writes these files for you; this page documents every key
so you can edit them by hand. Changes apply after a restart
(`sbox-ns service restart`, or stop and rerun `sbox-ns start` in the foreground).

## Where the files live

| Install type | Config folder | Data folder |
| --- | --- | --- |
| Linux service install (installer run as root) | `/etc/sbox-ns/` | `/var/lib/sbox-ns/` |
| Everything else (user installs, macOS, Windows, portable) | `<install dir>/config/` | `<install dir>/data/` |
| Docker image | `/config` (volume, `NS_CONFIG_DIR`) | `/data` (volume, `NS_DATA_DIR`) |

`<install dir>` is the folder that contains the `sbox-ns` executable. The
installers use:

- Linux as root: `/usr/local/bin/sbox-ns`, with `/etc/sbox-ns/` and `/var/lib/sbox-ns/`
- Linux and macOS without root: `~/.local/share/sbox-ns/` (linked into `~/.local/bin`)
- macOS as root: `/opt/sbox-ns/` (linked into `/usr/local/bin`)
- Windows as administrator: `%ProgramFiles%\sbox-ns\`
- Windows as a normal user: `%LOCALAPPDATA%\Programs\sbox-ns\`

The folders can always be chosen explicitly with the `--config-dir` and
`--data-dir` flags or the `NS_CONFIG_DIR` and `NS_DATA_DIR` environment
variables. Run `sbox-ns config path` to print the folders the server will use.

## Files and precedence

```text
<config dir>/
  server.toml      listener, TLS, logging, auth
  database.toml    SQLite or PostgreSQL
  updates.toml     update notices
  conf.d/*.toml    optional overrides, loaded in alphabetical order
  secrets/         generated secrets (auth session secret, storage encryption key, security signing key)
```

Each key belongs to one main file (`server.*`, `tls.*`, `logging.*` and `auth.*`
in `server.toml`; `database.*` in `database.toml`; `updates.*` in
`updates.toml`); validation reports a key placed in the wrong main file.
Files in `conf.d/` may set any key. Values are layered, later sources override
earlier ones:

1. Built-in defaults
2. `server.toml`, `database.toml`, `updates.toml`
3. `conf.d/*.toml` (alphabetical, so `conf.d/90-local.toml` beats `conf.d/10-base.toml`)
4. Environment variables prefixed with `NS_`
5. Command line flags

### Environment variables

Any key can be set with an environment variable: `NS_` followed by the dotted
key path in upper case, with each `.` replaced by `__` (two underscores).

| Key | Environment variable |
| --- | --- |
| `server.listen` | `NS_SERVER__LISTEN=0.0.0.0:9000` |
| `tls.mode` | `NS_TLS__MODE=certificate` |
| `database.provider` | `NS_DATABASE__PROVIDER=postgres` |
| `database.postgres.host` | `NS_DATABASE__POSTGRES__HOST=db.internal` |
| `database.postgres.password_file` | `NS_DATABASE__POSTGRES__PASSWORD_FILE=/run/secrets/pg` |
| `updates.check` | `NS_UPDATES__CHECK=false` |

`NS_CONFIG_DIR` and `NS_DATA_DIR` select the config and data folders themselves.

### Command line flags

| Flag | Overrides |
| --- | --- |
| `--config-dir <path>` | config folder |
| `--data-dir <path>` | data folder |
| `--listen <address:port>` | `server.listen` (for `start` and `setup`) |

### Editing from the command line

```sh
sbox-ns config path                     # config and data folders in use
sbox-ns config show                     # merged effective config, secrets redacted
sbox-ns config get server.listen
sbox-ns config set server.listen 0.0.0.0:9000
sbox-ns config validate                 # parse and check every file
sbox-ns config edit                     # open the config in $EDITOR
```

## server.toml

```toml
# sbox Network Storage Server: server settings.
# Edit, then restart the server to apply.

[server]
# Address and port for plain HTTP. "0.0.0.0:8080" accepts connections on every
# IPv4 interface; "127.0.0.1:8080" only from this machine (for example behind
# a reverse proxy on the same host).
listen = "0.0.0.0:8080"

# The URL players and the s&box editor use to reach this server, for example
# "https://ns.example.com" or "http://203.0.113.10:8080". Shown in setup and
# doctor output. Empty means it is derived from `listen`.
public_url = ""

# Where the database file, backups and ACME certificates are stored.
# Empty uses the default: /var/lib/sbox-ns for Linux service installs,
# otherwise <install dir>/data. Docker sets /data through NS_DATA_DIR.
data_dir = ""

[tls]
# "off"          plain HTTP only (default)
# "certificate"  serve HTTPS with the PEM files below
# "acme"         obtain and renew a Let's Encrypt certificate automatically.
#                Requires ports 80 and 443 reachable from the internet for acme_domain.
#                Certificates are cached in <data dir>/acme.
mode = "off"

# Address and port for HTTPS when mode is "certificate" or "acme".
https_listen = "0.0.0.0:443"

# mode = "certificate": PEM certificate (full chain) and PEM private key.
certificate_path = ""
key_path = ""

# mode = "acme": the public domain name, a contact email for Let's Encrypt,
# and explicit acceptance of the Let's Encrypt subscriber agreement.
acme_domain = ""
acme_email = ""
acme_accept_terms = false

[logging]
# Trace, Debug, Information, Warning, Error
level = "Information"

[auth]
# Secret files, relative to the config folder. Missing files are generated with
# permissions 600 on first start (or by `sbox-ns setup`) and are never rotated
# automatically. Keep them private and include them in backups.
#
# Signs player auth sessions. Losing it signs every player out.
session_secret_file = "secrets/auth_session_secret"
# 64 hex characters that derive secret API key identifiers. Losing it
# invalidates every existing secret key (sbox_sk_...).
storage_encryption_key_file = "secrets/storage_encryption_key"
# RSA private key (PEM) that signs the security config game clients download.
security_signing_key_file = "secrets/security_signing_key.pem"
```

### Hosted tunnel and notice settings

These keys also belong to `server.toml`. Use `tunnel enable|disable` and
`dns enable|disable` rather than manually changing tunnel or DNS state: the CLI
atomically manages `conf.d/zzzz-tunnel.toml` (loopback listener and public URL)
and `conf.d/zzzzz-dns.toml` (public URL and Let's Encrypt settings). Disabling
DNS removes its overrides from `zzzzz-dns.toml`, so the values in effect before
enable apply again. Tunnel and DNS modes cannot be enabled at the same time.

| Key | Default | Meaning |
| --- | --- | --- |
| `tunnel.enabled` | `false` | Start the supervised connector after server restart |
| `tunnel.registry` | `https://sboxcool.com/api/network-storage/tunnels` | Signed registry API; HTTPS required except loopback testing |
| `tunnel.name` | empty | Derived identity name, managed by enable |
| `tunnel.hostname` | empty | Assigned public hostname, managed by enable |
| `tunnel.local_port` | `8080` | HTTP loopback origin port, managed by enable |
| `tunnel.previous_listen` | empty | Original listener restored by disable |
| `tunnel.previous_public_url` | empty | Original public URL restored by disable |
| `tunnel.previous_tls_mode` | `off` | Original TLS mode restored by disable |
| `dns.enabled` | `false` | Hosted `<name>.nN.sboxns.com` DNS name pointing at this server's own IP; use `dns enable` / `dns disable` |
| `dns.registry` | `https://sboxcool.com/api/network-storage/dns` | Signed DNS registry API; HTTPS required except loopback testing |
| `dns.hostname` | empty | Assigned hostname, managed by `dns enable` |
| `dns.ipv4` | empty | Published IPv4 address, managed by `dns enable` and the address updater |
| `dns.ipv6` | empty | Published IPv6 address, managed by `dns enable` and the address updater |
| `dns.auto_address` | `true` | Check the public address every 10 minutes and update the name when it changes; `dns enable --ipv4/--ipv6` sets it to `false` |
| `dns.previous_public_url` | empty | Public URL in effect before `dns enable` (restored by disable) |
| `dns.previous_tls_mode` | `off` | TLS mode in effect before `dns enable` (restored by disable) |
| `dns.previous_acme_domain` | empty | ACME domain in effect before `dns enable` (restored by disable) |
| `notices.registry` | `https://sboxcool.com/api/network-storage/notices` | Optional notice API; no request without explicit opt-in |
| `notices.email` | empty | Last registered email, not authoritative consent; use `register` to subscribe/remove |
| `telemetry.enabled` | `false` | Send anonymous usage statistics once a day; use `telemetry enable` / `telemetry disable` |
| `telemetry.endpoint` | `https://sboxcool.com/api/network-storage/telemetry` | Usage statistics API; HTTPS required except loopback testing; no request unless enabled |

Identity and connector credentials are separate private files under
`secrets/`, not TOML values. Notice removal uses the private
`<data dir>/install-id` file; usage statistics use the separate private
`<data dir>/telemetry-id` file. See the
[hosted HTTPS and notices guide](self-hosting.md#hosted-https-without-a-domain)
and [anonymous usage statistics](self-hosting.md#anonymous-usage-statistics-opt-in).


## database.toml

```toml
# sbox Network Storage Server: database settings.
# Edit, then restart the server to apply. Check the connection with: sbox-ns db test

[database]
# "sqlite" (default, zero configuration) or "postgres".
provider = "sqlite"

# Seconds to keep retrying the database connection at startup before giving up.
# Useful when the database starts at the same time as the server.
startup_timeout_seconds = 60

[database.sqlite]
# Database file. Relative paths are inside the data folder.
path = "sbox-ns.db"

[database.postgres]
# Full Npgsql connection string. When set, it overrides the fields below.
# Example: "Host=db.internal;Port=5432;Database=sbox_ns;Username=sbox_ns;Password=..."
connection_string = ""

host = "localhost"
port = 5432
database = "sbox_ns"
username = "sbox_ns"

# Prefer password_file over password so the secret is not stored in this file.
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

The PostgreSQL user needs permission to create the schema, or ownership of an
existing one. A minimal setup:

```sql
CREATE ROLE sbox_ns LOGIN PASSWORD 'change-me';
CREATE DATABASE sbox_ns OWNER sbox_ns;
```

## updates.toml

```toml
# sbox Network Storage Server: update notices.
# The server never updates itself. It only tells you when a release is available.
# Install updates with: sbox-ns update   (undo with: sbox-ns rollback)

[updates]
# Check for new releases in the background and show a notice in the logs and
# in `sbox-ns doctor`. Set to false to disable all outbound update checks.
check = true

# Release feed. If unreachable, the GitHub Releases API for github_repo is used.
feed_url = "https://sboxcool.com/api/network-storage/releases/latest"
github_repo = "sbox-cool/sbox-network-storage-server"

# Hours between background checks.
interval_hours = 24

# Also notify about prereleases (versions with a "-" suffix, for example 0.4.0-rc.1).
include_prereleases = false
```

## conf.d

Drop extra `.toml` files into `conf.d/` to override settings without editing
the main files, for example from configuration management:

```toml
# conf.d/50-production.toml
[server]
listen = "127.0.0.1:8080"
public_url = "https://ns.example.com"

[logging]
level = "Warning"
```
