# Self-hosting guide

This guide covers installing, configuring, running, backing up and updating
sbox Network Storage Server. For every configuration key see
[configuration.md](configuration.md). To connect your game see
[client-setup.md](client-setup.md).

> Status: early preview. Releases before 1.0 may change configuration and
> storage formats. Keep backups. The local-owner dashboard manages projects,
> API keys and core settings; it does not yet have full managed-dashboard or
> managed project export/import parity. Resources can be pushed from the s&box editor Sync Tool.

## Requirements

- Linux (x64 or arm64), macOS (Apple Silicon or Intel) or Windows (x64)
- No .NET runtime needed: release binaries are self-contained
- SQLite needs nothing extra. PostgreSQL 13 or newer if you choose it.
- A port reachable by your players (8080 by default)

## Local owner and management dashboard

Run `sbox-ns setup` to choose the database and optionally create the owner with
hidden password input. Leave the owner username blank to use browser setup.
For scripted setup, supply `--admin-username NAME --admin-password-file FILE`
or `NS_ADMIN_USERNAME` and `NS_ADMIN_PASSWORD`; passwords are never accepted
as command-line arguments. Password files are read relative to the current
working directory, with a final newline removed. Passwords must be 12–1024
characters. Setup retains an existing account rather than overwriting it.

For a remote server, the simplest first login is `sbox-ns admin login-link`.
Run it on the server and open the printed single-use link; with no owner yet,
the link opens owner creation. See [admin panel](admin-panel.md) for how the
link works, HTTPS, and the data browser.

On startup without an owner, the server log contains a local `/setup?token=...`
URL, using the HTTP listener with TLS off or the HTTPS listener when
certificate/ACME TLS is enabled. This random one-time capability expires after
two hours, rotates on restart, and works only over a loopback connection.
For a remote plain-HTTP host, tunnel the listener:
`ssh -L 8080:127.0.0.1:8080 user@server`, then open the logged URL locally.
For TLS, tunnel the HTTPS port and route a hostname matching its certificate
to loopback before opening the URL with that hostname.
Docker's bridged port mapping is not a loopback connection inside the container;
create its owner with `docker exec -it sbox-ns /app/sbox-ns admin create`
instead. Creating the owner consumes the setup capability; `/setup` subsequently
returns 404.

The account is persisted at workspace object `server/identity/owner.json` in
the selected SQLite/PostgreSQL provider. It contains the username, a salted
ASP.NET Identity PBKDF2 hash (210,000 iterations), creation time and a security
stamp—never the plaintext password. No website identity database is involved.
Use one server instance and the same data directory for its operator CLI.
Owner mutations are serialized locally; sharing a PostgreSQL schema between
independent server instances/data directories is not supported.

Runtime entry points:

- `/login`: owner login, rate limited per client IP.
- `/login/link`: single-use login links from `sbox-ns admin login-link`
  (GET confirms, POST consumes), with the same rate limit.
- `/dashboard`: list/create projects and view available update notices.
- `/dashboard/projects/{projectId}`: project/security/player-key settings,
  create/enable/disable/revoke keys, synced collection/endpoint inventory, and
  confirmed permanent project deletion.
- `/dashboard/projects/{projectId}/data`: read-only data browser with JSON
  download per collection and confirmed, audited single-record deletion.
- `POST /logout`: ends the current cookie session.

All management mutations, login, setup and logout require ASP.NET antiforgery
tokens. Owner cookies are HttpOnly, SameSite Strict, and `__Host-` prefixed (Secure, no Domain) on HTTPS; sessions
last eight hours without sliding renewal. Management pages are not cached and
load an embedded external stylesheet, with no inline scripts or CSS. Use HTTPS
for remote management; plain HTTP sends credentials in cleartext and should
only be used locally or through a trusted tunnel. Cookie protection keys live
in `data/owner-cookie-keys`; protect and back up that directory with the data
directory. Keys are protected by OS directory permissions, not encrypted at
rest on platforms without a configured OS key protector.

Owner recovery is local operator access, not email:

```sh
sbox-ns admin create --username owner           # hidden, confirmed password input
sbox-ns admin reset-password                   # invalidates existing owner sessions
sbox-ns admin reset-password --password-file /run/secrets/new-owner-password
```

The dashboard reuses the extracted project/key/settings and audit services.
The managed record editors, query/workflow authoring, game-value editor,
analytics/log/error/usage views and project export/import are **not ported**.
Full dashboard parity and managed project export/import parity are **not
established**. The existing editor and `/v3` management APIs are the resource
entry points; `db backup/restore` is a whole-store recovery tool, not a substitute
for managed project export/import.
Moving the whole server to another machine or between SQLite and PostgreSQL
is `sbox-ns export` / `sbox-ns import` (also **Export server** on the
dashboard); see [export.md](export.md).


## Install

### Linux and macOS

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | sh
```

The installer:

1. Detects your OS and CPU (x64 or arm64).
2. Downloads the release archive and `SHA256SUMS` from GitHub Releases and
   aborts if the checksum does not match.
3. Installs `sbox-ns`:
   - as root on Linux: `/usr/local/bin/sbox-ns`, config in `/etc/sbox-ns/`,
     data in `/var/lib/sbox-ns/`, owned by a new `sbox-ns` system user;
   - otherwise: `~/.local/share/sbox-ns/` linked into `~/.local/bin/sbox-ns`
     (or `/opt/sbox-ns/` linked into `/usr/local/bin` as root on macOS).
4. Runs `sbox-ns setup` interactively if a terminal is attached, otherwise
   prints the command to run.
5. On Linux root installs with systemd, runs `sbox-ns service install` and `sbox-ns service start`.

Installer options are environment variables:

| Variable | Effect |
| --- | --- |
| `SBOX_NS_VERSION=0.3.0` | install a specific version instead of the latest |
| `SBOX_NS_PRERELEASE=1` | allow the newest prerelease |
| `SBOX_NS_NO_SETUP=1` | skip `sbox-ns setup` |
| `SBOX_NS_NO_SERVICE=1` | skip `sbox-ns service install` |
| `GITHUB_TOKEN=...` | authenticate GitHub API calls (rate limits) |

For a system service on Linux, run the installer as root:

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | sudo sh
```

### Windows

In PowerShell (as administrator to install for all users into Program Files):

```powershell
irm https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.ps1 | iex
```

The script verifies the download with `Get-FileHash`, installs to
`%ProgramFiles%\sbox-ns\` (administrator) or `%LOCALAPPDATA%\Programs\sbox-ns\`,
adds it to `PATH` and runs `sbox-ns setup`.

### Docker

```sh
docker run -d --name sbox-ns \
  -p 8080:8080 \
  -v sbox-ns-config:/config \
  -v sbox-ns-data:/data \
  --restart unless-stopped \
  ghcr.io/sbox-cool/sbox-network-storage-server:latest

docker exec -it sbox-ns /app/sbox-ns setup
docker restart sbox-ns
docker exec -it sbox-ns /app/sbox-ns project create "My Game"
```

The image runs as a non-root user, reads config from `/config` and stores data
in `/data`. Any setting can also be passed as an `NS_` environment variable,
for example `-e NS_DATABASE__PROVIDER=postgres`.

Docker Compose with PostgreSQL:

```yaml
services:
  sbox-ns:
    image: ghcr.io/sbox-cool/sbox-network-storage-server:latest
    ports: ["8080:8080"]
    volumes:
      - config:/config
      - data:/data
    environment:
      NS_DATABASE__PROVIDER: postgres
      NS_DATABASE__POSTGRES__HOST: postgres
      NS_DATABASE__POSTGRES__USERNAME: sbox_ns
      NS_DATABASE__POSTGRES__PASSWORD_FILE: /run/secrets/pg_password
    secrets: [pg_password]
    depends_on: [postgres]
    restart: unless-stopped
  postgres:
    image: postgres:16
    environment:
      POSTGRES_USER: sbox_ns
      POSTGRES_DB: sbox_ns
      POSTGRES_PASSWORD_FILE: /run/secrets/pg_password
    secrets: [pg_password]
    volumes:
      - pgdata:/var/lib/postgresql/data
    restart: unless-stopped
secrets:
  pg_password:
    file: ./pg_password.txt
volumes:
  config:
  data:
  pgdata:
```

### Manual install

Download `sbox-ns-<version>-<platform>.tar.gz` (or `.zip` on Windows) and
`SHA256SUMS` from the [releases page](https://github.com/sbox-cool/sbox-network-storage-server/releases),
verify, extract and run:

```sh
sha256sum --check --ignore-missing SHA256SUMS
tar -xzf sbox-ns-<version>-linux-x64.tar.gz
./sbox-ns setup
./sbox-ns start
```

`SHA256SUMS` is signed with Sigstore keyless signing. To verify the signature:

```sh
cosign verify-blob SHA256SUMS \
  --signature SHA256SUMS.sig \
  --certificate SHA256SUMS.pem \
  --certificate-identity-regexp '^https://github.com/sbox-cool/sbox-network-storage-server/\.github/workflows/release\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

## Configure

`sbox-ns setup` asks for the database (SQLite or PostgreSQL), the listen
address and the public URL, writes `server.toml`, `database.toml` and
`updates.toml`, generates the server secrets in `<config dir>/secrets/`
(auth session secret, storage encryption key, security signing key) and runs
database migrations. `sbox-ns start` also generates any missing secret, so a
Docker container works without running setup first. For scripted installs:

```sh
sbox-ns setup --non-interactive --database sqlite --listen 0.0.0.0:8080 --public-url http://203.0.113.10:8080
sbox-ns setup --non-interactive --database postgres --pg-host db.internal --listen 0.0.0.0:8080
```

Afterwards:

```sh
sbox-ns config path        # where config and data live
sbox-ns config show        # effective configuration, secrets redacted
sbox-ns config validate    # check the files
sbox-ns doctor             # check config, database, ports and update status
```

Edit the TOML files and restart to apply changes.

### Projects and API keys

Use the owner dashboard or create projects and keys with the CLI, then
enter them in the s&box editor (see [client-setup.md](client-setup.md)):

```sh
sbox-ns project create "My Game"                         # prints the project ID
sbox-ns project list
sbox-ns key create <projectId> --type public --label game
sbox-ns key create <projectId> --type secret --label editor
sbox-ns key list <projectId>
sbox-ns key revoke <projectId> <key>
sbox-ns project delete <projectId>
```

The public key goes into your game; the secret key is only for the editor
Sync Tool. Collections, endpoints and workflows are pushed from the editor
Sync Tool, exactly as with the managed service.

### HTTPS

Plain HTTP on port 8080 is the default. For HTTPS, choose one of:

- **Let's Encrypt (built in).** Needs a domain pointing at this server and
  ports 80 and 443 reachable from the internet:

  ```toml
  [tls]
  mode = "acme"
  acme_domain = "ns.example.com"
  acme_email = "you@example.com"
  acme_accept_terms = true
  ```

- **Your own certificate.** PEM files:

  ```toml
  [tls]
  mode = "certificate"
  certificate_path = "/etc/sbox-ns/tls/fullchain.pem"
  key_path = "/etc/sbox-ns/tls/privkey.pem"
  ```

- **Reverse proxy.** Run Caddy, nginx or Traefik in front of the server, set
  `server.listen = "127.0.0.1:8080"` and `server.public_url = "https://ns.example.com"`.
  Example Caddyfile:

  ```text
  ns.example.com {
      reverse_proxy 127.0.0.1:8080
  }
  ```

HTTPS on port 443 needs either root, the `CAP_NET_BIND_SERVICE` capability
(see below) or a reverse proxy.

Which URL types s&box game clients can reach is untested; see
[client-setup.md](client-setup.md#client-reachability).

### Hosted HTTPS without a domain

Hosted names are optional. They do not require a website account, payment or a
domain purchase. Enabling a name accepts the
[hosted-name acceptable-use policy](https://sboxcool.com/network-storage/hosted-names/acceptable-use).
Operators can disable names for abuse; this does not delete your local data.

```sh
sbox-ns tunnel enable
sbox-ns service restart
sbox-ns tunnel status
sbox-ns doctor
```

The CLI creates a private P-256 identity, derives a stable 12-character name,
and downloads the SHA-256-verified official cloudflared 2026.10.0 binary.
Keep `<config dir>/secrets/identity_ecdsa_p256.pem`: replacing it changes the name.
The connector token remains in `<config dir>/secrets/tunnel_token`, never in
CLI arguments, public server-info or registry database rows. Back up the config
as secret material. On Linux, root administration preserves the installed
config directory's UID/GID for private assets and connector files.

Enabling writes `conf.d/zzzz-tunnel.toml`, binds HTTP to `127.0.0.1` on your
existing HTTP port, disables local TLS and sets `server.public_url` to the
hosted HTTPS URL. Restart before using it. Keep the direct HTTP port closed in
your firewall; verify a remote request to the server IP cannot reach it.
Only local connector traffic is trusted for forwarded HTTPS headers, so owner
and antiforgery cookies retain HTTPS `__Host-` protection.

The server supervises the child with bounded restart backoff, redacts its
free-form output, and reports connection state in `doctor`, `tunnel status`
and server-info. It never auto-updates cloudflared. The official optional
cloudflared dependency includes Cloudflare crash reporting; the server's
opt-in usage statistics policy does not describe that third-party process.

```sh
sbox-ns tunnel disable
sbox-ns service restart
```

Disable removes the hosted tunnel/DNS route and restores the listener,
public URL and TLS mode captured before enable. It keeps the identity so a
later enable derives the same name. Re-enable recovers a missing local token.
Do not manually override the managed tunnel settings.

A fresh service install can opt in before start:

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh \
  | sudo env SBOX_NS_PROJECT="My Game" SBOX_NS_TUNNEL=1 sh
```

### Free hosted name with your own IP (sboxns.com)

If your server already has a public IP address, you can get a free name such
as `abcdefgh2345.n1.sboxns.com` that points straight at it. Players connect
directly to your server over HTTPS (no proxy in between) using a free Let's
Encrypt certificate. No website account, payment or domain is needed.

Requirements:

- A public IPv4 or IPv6 address on this machine (or a router that forwards to it).
- Port **443** (HTTPS) and your HTTP port (`server.listen`, default 8080) open to
  the internet. The HTTP port must not be bound to `127.0.0.1`.
- The server running while you enable the name: the registry checks that the
  address really belongs to this server by fetching a signed proof from
  `http://<your IP>:<HTTP port>/.well-known/sbox-ns/dns-proof/...`.
- Accepting the [Let's Encrypt subscriber agreement](https://letsencrypt.org/repository/)
  and giving a contact email for the certificate.

Steps:

```sh
sbox-ns start                     # or: sbox-ns service start
sbox-ns dns enable --accept-letsencrypt-terms --email you@example.com
sbox-ns service restart
sbox-ns dns status
sbox-ns doctor
```

`dns enable` detects your public address automatically. To publish a specific
address instead, pass `--ipv4 203.0.113.5` and/or `--ipv6 2001:db8::5`; this
turns off automatic address updates. With automatic updates on, the server
checks its public address every 10 minutes and updates the name when it changes.

Enabling sets `server.public_url` to `https://<name>.nN.sboxns.com`,
`tls.mode = "acme"` and `tls.acme_domain` to that name in
`conf.d/zzzzz-dns.toml`. `tls.https_listen` stays `0.0.0.0:443`. Nothing changes
if any step fails. The name uses the same identity key as tunnels
(`<config dir>/secrets/identity_ecdsa_p256.pem`); keep and back it up.

What is public: the name resolves to your IP address, so anyone who knows the
name can see the IP. Use tunnel mode if you want to keep the IP hidden.

| | Own IP (`dns enable`) | Tunnel (`tunnel enable`) |
| --- | --- | --- |
| Traffic path | Direct to your server | Through Cloudflare |
| Your IP | Public | Hidden |
| Needs public IP and open ports | Yes (443 and HTTP port) | No |
| Extra software | None | cloudflared |
| Certificate | Let's Encrypt on your server | Cloudflare |

Only one mode can be enabled at a time; disable the other first.

Limits: until sboxns.com is accepted on the Public Suffix List, the registry
allows only a limited number of new names per week across all servers, and a
few new names per hour from one address. If the limit is reached, `dns enable`
reports when to retry. Updating or re-enabling an existing name is not
affected by the weekly cap.

To remove the name:

```sh
sbox-ns dns disable
sbox-ns service restart
```

Disable deletes the DNS records and restores the public URL and TLS settings
that were in effect before enable. The identity is kept, so a later enable
gets the same name back.

### Optional security and update email notices

Interactive setup offers an email prompt; blank input and non-interactive
setup send no request and create no install ID. Registration is independent
of tunnels and always optional:

```sh
sbox-ns register --email you@example.com
sbox-ns register --remove
```

Registration sends only email, installed version and a random install ID to
the website notice service. No game data, project IDs or API keys are sent.
Open the confirmation email within 48 hours; its link is single-use. No
security/update notices are sent before confirmation. Every notice includes
an unsubscribe link; CLI removal also removes pending confirmations.

Keep `<data dir>/install-id` private: it is the removal capability. Copy it
separately when moving an installation if you want to preserve CLI removal;
portable server archives do not include this data-directory file. Editing
`notices.email` does not subscribe or unsubscribe. No marketing or billing
integration is involved.

### Anonymous usage statistics (opt-in)

Usage statistics are off by default. Interactive setup asks once
(`Share anonymous usage statistics (version, platform, project and player
counts)? [y/N]`, default No); non-interactive setup never enables them. When
enabled, the server posts one small JSON report to `telemetry.endpoint`
(default `https://sboxcool.com/api/network-storage/telemetry`) about 10
minutes after it starts and then every 24 hours. These are the only fields:

| Field | Meaning |
| --- | --- |
| `schema` | Report format version, currently `1` |
| `installId` | Random UUID stored in `<data dir>/telemetry-id` (owner-only file), used only for these statistics and separate from the notices `install-id` |
| `version` | Installed sbox-ns version |
| `os` | `linux`, `windows`, `macos` or `other` |
| `arch` | `x64`, `arm64` or `other` |
| `container` | `true` when `DOTNET_RUNNING_IN_CONTAINER` is set (Docker images) |
| `database` | `sqlite` or `postgres` |
| `tunnel` | Whether the hosted `sboxns.com` tunnel is enabled |
| `uptimeHours` | Whole hours since the server process started |
| `projects` | Number of projects |
| `players` | Distinct players across all projects (a count only) |
| `activePlayers30d` | Distinct players seen in the last 30 days (a count only) |

No IP addresses are stored, and no emails, project IDs, project names, API
keys, player IDs or game data are sent. A failed or rejected request is skipped
silently until the next interval; the server never retries in a loop and never
logs the report above Debug level.

```sh
sbox-ns telemetry status     # enabled or disabled, endpoint, telemetry ID file
sbox-ns telemetry preview    # print the exact JSON that would be sent, without sending it
sbox-ns telemetry enable     # opt in; restart the server afterward
sbox-ns telemetry disable    # opt out; restart the server afterward
```

`preview` runs as a separate CLI process, so its `uptimeHours` is `0`; the
running server reports its own uptime. Disabling keeps `telemetry-id` so a
later enable is counted as the same install; delete the file to start over
with a new random ID. A fresh install can opt in with `SBOX_NS_TELEMETRY=1`
(both `install.sh` and `install.ps1`).


## Run

### Foreground

```sh
sbox-ns start
sbox-ns start --listen 0.0.0.0:9000
```

Stops on Ctrl+C. Useful for testing and inside containers.

### As a service

```sh
sudo sbox-ns service install     # systemd on Linux, launchd on macOS, Windows service on Windows
sbox-ns service start
sbox-ns service status
sbox-ns service restart
sbox-ns service stop
sbox-ns logs                     # recent log lines; add -f to follow
sudo sbox-ns service uninstall
```

On Linux the unit file is [`install/sbox-ns.service`](../install/sbox-ns.service):
it runs as the `sbox-ns` user with systemd hardening, and only
`/var/lib/sbox-ns` and `/etc/sbox-ns` are writable. To bind ports below 1024
directly (for example `tls.mode = "acme"` on 80 and 443), run
`systemctl edit sbox-ns` and add:

```ini
[Service]
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
```

### Health checks

- `GET /health` returns 200 when the server and database are up; use it for
  load balancers, uptime monitors and container health checks.
- `GET /v3/server-info` returns the server version and basic information.

## Database

```sh
sbox-ns db test       # connect with the current settings
sbox-ns db status     # provider, schema version, pending migrations, size
sbox-ns db migrate    # apply pending migrations (automatic on start unless auto_migrate = false)
```

SQLite stores everything in one file, `<data dir>/sbox-ns.db`, which
is fine for most single-server games. Choose PostgreSQL if you already run it,
want managed backups from your provider, or want to run the database on a
separate machine. All tables are created inside the configured schema
(default `network_storage`), so the server can share a database with other
applications.

## Backups

Back up the database **and** the config folder, including
`<config dir>/secrets/`. Without the auth session secret, existing player
sessions become invalid after a restore; without the storage encryption key,
every secret API key stops working.

### Built-in

```sh
sbox-ns db backup                       # writes a timestamped backup into the data folder
sbox-ns db backup --output /backups/ns.bak
sbox-ns db restore /backups/ns.bak      # stop the server first
```

For SQLite this copies the database file. For PostgreSQL it uses `pg_dump`
and `pg_restore`, which must be installed and on `PATH`.

### SQLite by hand

Do not copy `sbox-ns.db` while the server is running; the copy can be
inconsistent. Either stop the server first, or use the SQLite CLI:

```sh
sqlite3 /var/lib/sbox-ns/sbox-ns.db ".backup '/backups/sbox-ns.db'"
```

### PostgreSQL by hand

```sh
pg_dump --format=custom --schema=network_storage --file=ns.dump "postgresql://sbox_ns@db/sbox_ns"
pg_restore --clean --if-exists --dbname="postgresql://sbox_ns@db/sbox_ns" ns.dump
```

Schedule backups with cron, a systemd timer or your provider's snapshots, and
test a restore now and then.

## Updates

The server never updates itself. When a new release is published it prints a
notice in the logs and in `sbox-ns doctor`, including whether the release fixes
a security issue or needs a database migration.

```sh
sbox-ns update --check            # show the latest version and release notes link
sbox-ns update                    # install the latest release
sbox-ns update --version v0.4.0   # install a specific release
sbox-ns rollback                  # return to the previous binary
```

Take a backup (`sbox-ns db backup`) before updating to a release that needs a
database migration. Releases list a minimum version they can be upgraded
from; older installs must step through an intermediate release.

Docker installs update by pulling a new image and recreating the container
with the same volumes:

```sh
docker pull ghcr.io/sbox-cool/sbox-network-storage-server:latest
docker rm -f sbox-ns && docker run ...   # same arguments as before
```

To turn off update checks entirely, set this in `updates.toml`:

```toml
[updates]
check = false
```

Set `include_prereleases = true` to also be notified about prereleases.

The update check requests `https://sboxcool.com/api/network-storage/releases/latest`
and falls back to the GitHub Releases API.

## Uninstall

```sh
sudo sbox-ns service uninstall
sudo rm /usr/local/bin/sbox-ns
# Remove configuration and data only if you no longer need them:
sudo rm -rf /etc/sbox-ns /var/lib/sbox-ns
sudo userdel sbox-ns
```
