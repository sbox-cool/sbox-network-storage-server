# Configuration reference

sbox Network Storage Server reads its settings from a folder of commented TOML
files. `sbox-ns setup` writes these files for you; this page documents every key
so you can edit them by hand. Changes apply after a restart
(`sbox-ns service restart`, or stop and rerun `sbox-ns start` in the foreground).

## Owner administration security

The owner panel is enabled by default. `sbox-ns adminpanel disable` writes
`adminpanel.enabled = false`; restart the server to deny all owner controller
endpoints, including setup, login links, login and the dashboard. Game APIs and
health remain available. Restore with `sbox-ns adminpanel enable` or
`sbox-ns config set adminpanel.enabled true`, then restart. Environment and
`conf.d` overrides still take precedence over the file changed by the CLI.

`adminpanel.demo_read_only` defaults to false. It is only for a dedicated public
demonstration using an isolated SQLite database: the server seeds fixture data
and supplies an ephemeral guest dashboard, rejecting owner auth/security/export,
game operations and writes. Never enable it on a real operator database.

`adminpanel.allowed_ips` is an optional comma-separated list of addresses or
CIDRs, for example `"127.0.0.1,::1,192.0.2.0/24"`. An empty string permits
all addresses. Restrictions apply before authentication and CSRF to every
owner controller route. The policy uses only trusted `Connection.RemoteIpAddress`
and never reads raw visitor headers. The server's existing forwarded-header
middleware trusts only loopback proxies, with a one-hop limit. Through a local
connector this may be the forwarded visitor address; without trusted forwarding
it is the connector's address. `CF-Connecting-IP` is not used by this policy.
Do not expose a loopback-origin listener through an untrusted local proxy.

`adminpanel.allow_insecure_http` defaults to false. While it is false, the
server refuses owner passwords, login links and the setup form when they arrive
over plain HTTP from another machine, and shows a page that explains the safe
ways in: an SSH port forward, `sbox-ns tunnel enable`, or TLS. Loopback
requests and HTTPS (including a TLS proxy on loopback that sends
`X-Forwarded-Proto: https`) are not affected. Set it to true only on a network
you trust: your password, login links and session cookie then travel
unencrypted.

Owners may enroll an authenticator at `/dashboard/security` after signing in.
Enter the displayed secret manually in an authenticator app using SHA-1,
six digits and a 30-second period, then confirm a current code and password.
Enrollment expires after ten minutes and is not enabled until confirmation.
The encrypted secret, last accepted time step and hashed recovery codes are
stored with the owner account. Protect and back up the data directory, including
`owner-cookie-keys`: losing its protection keys prevents authenticator verification.
Only a new time step can be used; a 30-second clock-skew window is supported.
Ten random recovery codes are shown once and each can be used once in the login
code field. Enrollment invalidates all existing sessions. Local
`sbox-ns admin reset-2fa` removes the authenticator and invalidates sessions;
password reset preserves an enabled authenticator. The existing locally issued
single-use `admin login-link` remains a shell-authorized recovery route.

### Optional Cloudflare Turnstile

Turnstile is off by default and never gates game APIs. Create and configure your
own widget; the server does not provision widgets or Cloudflare resources.
Set these keys in `server.toml` and restart:

```toml
[adminpanel.turnstile]
enabled = true
sitekey = "YOUR_PUBLIC_SITEKEY"
hostname = "YOUR_EXACT_PUBLIC_HOSTNAME"
```

Supply the secret through `NS_ADMINPANEL__TURNSTILE__SECRET` in the service's
protected environment or set `adminpanel.turnstile.secret` in operator-only
configuration. Do not put it in a game client, source control or browser code.
Register the exact hostname in the widget's allowed domains. For an enabled
`sboxns.com` tunnel use the actual `tunnel.hostname`, also reflected in
`server.public_url`, as `hostname`; schemes, paths and ports are not allowed.
No direct-origin hostname is inferred from untrusted request headers.

The external widget script is loaded only when enabled. Server-side Siteverify
must report success, the exact configured hostname, and the matching action:
`owner_login`, `owner_setup`, or `owner_link`. Missing tokens, mismatches,
provider errors, malformed responses and timeouts fail closed. Existing CSRF
and rate limits remain in force. A local-only setup hostname will not pass a
production hostname check: use the public single-use login link to create the
owner through the tunnel, or create the owner locally with the CLI.
Provider-boundary tests are deterministic; they are not proof that an operator's
live widget and hostname configuration work. Validate a fresh live token and
its replay rejection on your actual deployment.

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
<config dir>/                     operator configuration (root-owned on Linux service installs)
  server.toml      listener, TLS, logging, auth
  database.toml    SQLite or PostgreSQL
  updates.toml     update notices
  conf.d/*.toml    optional overrides, loaded in alphabetical order

<data dir>/state/                 runtime state the server and CLI write (owned by the service user)
  .layout-version  marker: the config/state split is in effect
  secrets/         generated secrets (auth session secret, storage encryption key, security signing key),
                   tunnel identity and tunnel token
  conf.d/*.toml    managed overlays (tunnel, DNS, telemetry), restricted to the keys listed below
  bin/             downloaded cloudflared connector
  telemetry-id     private ID for opt-in usage statistics
```

The config folder holds only files an operator edits. On Linux service installs it
is `root:<service group>` with mode `0750` (files `0640`) and the service unit
cannot write it; everything the server or CLI creates at runtime lives in the
state folder under the data folder (`/var/lib/sbox-ns/state`, or
`/var/lib/sbox-ns/<name>/state` for a named instance). Single-user, Docker and
Windows installs use the same two folders: the state folder is always
`<data dir>/state`. Installs created before the split keep their runtime files in
the config folder until `sudo sbox-ns layout migrate` moves them; see
[Config and state folders](self-hosting.md#config-and-state-folders).

Each key belongs to one main file (`server.*`, `tls.*`, `logging.*` and `auth.*`
in `server.toml`; `database.*` in `database.toml`; `updates.*` in
`updates.toml`); validation reports a key placed in the wrong main file.
Files in the config folder's `conf.d/` may set any key. Values are layered, later
sources override earlier ones:

1. Built-in defaults
2. `server.toml`, `database.toml`, `updates.toml`
3. `<config dir>/conf.d/*.toml` (alphabetical, so `conf.d/90-local.toml` beats `conf.d/10-base.toml`)
4. The state overlay `<data dir>/state/conf.d/*.toml` (alphabetical), restricted to the keys below
5. Environment variables prefixed with `NS_`
6. Command line flags

### State overlay

The overlay holds what `tunnel`, `dns` and `telemetry` commands manage. A key set in
an overlay file replaces the operator's value for that key; every other key keeps the
operator's value, and operator files are never rewritten by these commands. An
overlay file may only set these keys:

| Key | Written by |
| --- | --- |
| `tunnel.*` | `tunnel enable` / `tunnel disable` |
| `dns.*` | `dns enable` / `dns disable` |
| `server.listen`, `server.public_url` | `tunnel`, `dns` (loopback listener, hosted public URL) |
| `tls.mode`, `tls.acme_domain`, `tls.acme_email`, `tls.acme_accept_terms` | `dns enable` (Let's Encrypt settings), `tunnel enable` (`tls.mode`) |
| `telemetry.enabled` | `telemetry enable` / `telemetry disable` |

Any other key in an overlay file is a configuration error naming the file and the
key, and the server does not start. Installs that have not run `layout migrate` keep
`conf.d/` in the config folder with no restriction, as before.

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

`config show` and `config get` print secret settings (the Turnstile secret,
the PostgreSQL connection string and password, the Discord webhook URL and the
SMTP password) as `********`. Add `--show-secrets` to print the real value.

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

# Send Strict-Transport-Security on HTTPS responses when TLS is enabled.
# Plain-HTTP installs never send it.
hsts = true

[logging]
# Trace, Debug, Information, Warning, Error
level = "Information"

[analytics]
# Days to keep player analytics (timeline events and project issues). Older rows
# are purged once a day. 0 keeps them forever.
retention_days = 90

[auth]
# Secret files, relative to the state folder (<data dir>/state; the config folder on
# installs that have not run `sbox-ns layout migrate`). Missing files are generated with
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
# Key id published with the signed security config. Empty (default) derives it
# from the public key. Set it to the previous server's key id when moving a
# project here with its signing key, so game clients keep accepting the config.
security_signing_key_id = ""
```

### Request limits

Body size limits and per-client rate limits belong to `server.toml`. Size keys
are in KiB; rate keys count requests. The limits apply per client address, taken
from the connection. `X-Forwarded-For` is honoured only from a proxy on the same
machine, and `CF-Connecting-IP` only from the local tunnel connector while
`tunnel.enabled` is true; any other client-supplied forwarding header is ignored.

| Key | Default | Meaning |
| --- | --- | --- |
| `server.limits.default` | `1024` | Largest body on routes without a limit of their own |
| `server.limits.data_plane` | `256` | Largest body on game routes (`/v1`, `/v3`, `/api/storage`) and auth-session routes |
| `server.limits.management` | `8192` | Largest body on secret-key management and sync routes (`/v3/manage`) |
| `server.limits.game_burst` | `600` | Requests a client address may send to game routes at once |
| `server.limits.game_per_second` | `200` | Sustained requests per second on game routes |
| `server.limits.management_burst` | `120` | Requests a client address may send to management routes at once |
| `server.limits.management_per_second` | `20` | Sustained requests per second on management routes |
| `server.limits.auth_session_burst` | `120` | Requests a client address may send to auth-session routes at once |
| `server.limits.auth_session_per_second` | `30` | Sustained requests per second on auth-session routes |

A body over its limit is answered with `413` and `{ "error": "PAYLOAD_TOO_LARGE" }`.
On a known route the server reads and discards an over-limit body of up to 4 MiB so
the client sees that response; a declared length above 4 MiB, or any path that is
not a route, is rejected without reading the body.
A client over its rate limit is answered with `429`, a `Retry-After` header and
`{ "error": "RATE_LIMITED" }`. The dashboard import keeps its own limit. Owner
login allows 10 attempts per minute per address, 60 per minute across all
addresses, and checks at most 2 passwords at a time.

### Analytics retention

Player analytics are written by one background writer in batches of up to 500
rows, flushed at least once a second. Up to 10,000 events wait in memory; if the
database falls behind, the oldest waiting events are dropped (and logged) instead
of slowing requests down. Reading a record produces no analytics event.

| Key | Default | Meaning |
| --- | --- | --- |
| `analytics.retention_days` | `90` | Timeline events and project issues older than this many days are deleted once a day. `0` keeps them forever. Player profiles and sessions are never purged. |
### Hosted tunnel and notice settings

These keys also belong to `server.toml`. Use `tunnel enable|disable` and
`dns enable|disable` rather than manually changing tunnel or DNS state: the CLI
atomically manages `zzzz-tunnel.toml` (loopback listener and public URL) and
`zzzzz-dns.toml` (public URL and Let's Encrypt settings) in the state overlay
`<data dir>/state/conf.d/`. Disabling
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
`<data dir>/state/secrets/`, not TOML values. Notice removal uses the private
`<data dir>/install-id` file; usage statistics use the separate private
`<data dir>/state/telemetry-id` file. `telemetry enable|disable` write
`state/conf.d/telemetry.toml`, not `server.toml`. See the
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
# sbox Network Storage Server: update notices and opt-in unattended updates.
# Manual: sbox-ns update   (undo with: sbox-ns rollback)
# Unattended: sudo sbox-ns service install --auto-update (see self-hosting.md, "Automatic updates")

[updates]
# Check for new releases in the background and show a notice in the logs and
# in `sbox-ns doctor`. Set to false to disable all outbound update checks.
check = true

# The release feed and GitHub repository are compiled into the binary and cannot be
# configured. Older updates.toml files may still contain feed_url / github_repo: they
# are ignored (and reported by `sbox-ns doctor`) and can be deleted.

# Hours between background checks.
interval_hours = 24

# Also notify about prereleases (versions with a "-" suffix, for example 0.4.0-rc.1).
include_prereleases = false

# "stable": releases promoted after soaking on the official canary server.
# "canary": every release as soon as it is published.
channel = "stable"

# Install releases unattended (`sbox-ns update --auto`, run hourly by
# sbox-ns-update.timer). With several instances on one host, all must opt in.
auto_install = false

# UTC window "HH:MM-HH:MM" in which unattended updates may start. The end is
# exclusive; "22:00-02:00" wraps midnight and "00:00-24:00" is always open.
window = "03:00-05:00"

# Hours a release must have been on its channel before an unattended install.
# -1 = channel default: 24 on stable, 0 on canary.
min_release_age_hours = -1
```

## Analytics retention and buffering

Set this in `server.toml` (or `NS_ANALYTICS__RETENTION_DAYS`), then restart:

```toml
[analytics]
retention_days = 90  # 0 disables retention
```

The daily purge removes older player timeline events, project issues and legacy
analytics rows across all projects. Player profiles and sessions are retained
as state. Record reads do not create analytics events.

Analytics are best-effort: requests enqueue without database I/O into a
10,000-event buffer. When full it drops the oldest events and counts the drops;
the writer reports them in a warning. One background writer commits batches of
up to 500 events, or the buffered partial batch every second, in one transaction.
A failed batch rolls back and is reported; it does not fail the game request.
Shutdown drains the remaining buffered events.

Collection, endpoint and game-value metadata share a per-project memory-cache
snapshot. Management writes, sync and imports invalidate its generation token
so the next request reloads it; endpoint definitions are parsed once per
generation. Request timing logs are at Debug rather than Information.
Fixed expression/request regexes have a 100 ms match timeout. Dynamic
`matches` condition patterns (and `matches(input, pattern)` templates) use a
50 ms timeout and a 256-pattern compiled LRU; invalid or timed-out patterns
produce endpoint expression errors rather than tying up a request thread.

## conf.d

Drop extra `.toml` files into the config folder's `conf.d/` to override settings
without editing the main files, for example from configuration management (the
managed `state/conf.d/` overlay is a separate, restricted folder):

```toml
# conf.d/50-production.toml
[server]
listen = "127.0.0.1:8080"
public_url = "https://ns.example.com"

[logging]
level = "Warning"
```

