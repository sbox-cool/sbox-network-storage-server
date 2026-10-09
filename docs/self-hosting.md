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
2. Validates the resolved version, downloads the release archive, `SHA256SUMS`
   and `SHA256SUMS.p256.sig` over HTTPS, verifies the checksum manifest's
   ECDSA P-256/SHA-256 signature with a pinned current or rotation public key,
   and only then checks the archive hash. Missing/invalid signatures and hash
   mismatches abort before extraction or binary replacement. Redirects to HTTP
   are refused by both curl and wget.
3. Installs `sbox-ns`:
   - as root on Linux: `/usr/local/bin/sbox-ns`, operator config in `/etc/sbox-ns/`
     (`root:sbox-ns`, directories `0750`, files `0640`, read-only to the service),
     data and runtime state in `/var/lib/sbox-ns/` owned by the stable `sbox-ns`
     system user (created with `systemd-sysusers`, or `useradd`/`adduser` as a fallback);
   - otherwise: `~/.local/share/sbox-ns/` linked into `~/.local/bin/sbox-ns`
     (or `/opt/sbox-ns/` linked into `/usr/local/bin` as root on macOS).
4. On a Linux upgrade, runs the verified new binary's `layout migrate` before
   setup, unit installation or service startup. The new binary is installed
   first because older releases do not have the migration command; rendered
   units must use the permanent installed binary path.
5. Runs `sbox-ns setup` interactively if a terminal is attached, otherwise
   prints the command to run. Linux setup and quickstart run as root because
   they write operator configuration; runtime state is created with the service
   identity. Tunnel, DNS and telemetry enablement run through `runuser -u sbox-ns`.
   No recursive config-to-service-user ownership transfer is performed.
6. On Linux root installs with systemd, runs `sbox-ns service install` and `sbox-ns service start`.

Linux/macOS signature verification requires `openssl` on `PATH`; install it
with `sudo apt-get install openssl`, `sudo dnf install openssl` or
`brew install openssl`. Linux root installs also require `runuser` from
`util-linux`. Generated secrets, managed configuration overlays and cloudflared
live in `/var/lib/sbox-ns/state/{secrets,conf.d,bin}`, not `/etc/sbox-ns`.

Installer options are environment variables:

| Variable | Effect |
| --- | --- |
| `SBOX_NS_VERSION=0.3.0` | install a specific version instead of the latest |
| `SBOX_NS_PRERELEASE=1` | allow the newest prerelease |
| `SBOX_NS_NO_SETUP=1` | skip `sbox-ns setup` |
| `SBOX_NS_NO_SERVICE=1` | skip `sbox-ns service install` |
| `SBOX_NS_INSECURE_SKIP_SIGNATURE=1` | **DANGEROUS:** manual install only; explicitly disables release signature authentication, prints a loud warning, and still checks archive hashes |
| `GITHUB_TOKEN=...` | authenticate GitHub API calls (rate limits) |

For a system service on Linux, run the installer as root:

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | sudo sh
```

The insecure signature override is never appropriate for unattended updates:
the installer refuses it with `SBOX_NS_AUTO_UPDATE=1`, and `sbox-ns update --auto`
also refuses it. Without that explicit override, missing pinned keys fail
closed; the installer never downloads a key from the same release it verifies.
Maintainers supply the actual current and next P-256 public keys in
[`Updates/release-signing-keys.pub`](../src/SboxNetworkStorage.Server/Updates/release-signing-keys.pub)
and run `sh scripts/pin-release-keys.sh` (Python 3 and OpenSSL required) before
shipping installers. The script validates and synchronizes those keys; it
does not generate replacement keys. Until the owner supplies the public keys,
the empty trust set deliberately refuses installation.

### Windows

In PowerShell (as administrator to install for all users into Program Files):

```powershell
irm https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.ps1 | iex
```

The script authenticates `SHA256SUMS.p256.sig` before checking the archive with
`Get-FileHash`, installs to `%ProgramFiles%\sbox-ns\` (administrator) or
`%LOCALAPPDATA%\Programs\sbox-ns\`, adds it to `PATH` and runs `sbox-ns setup`.
Windows PowerShell 5.1 needs no extra module: verification uses `ECDsaCng`
with pinned `EccPublicBlob` keys, converting the DER signature to IEEE P1363.
HTTPS-to-HTTP redirects, unsafe version strings, missing signatures, invalid
signatures and hash mismatches are refused. The same explicitly warned
manual-only insecure override is available; it is not the default.

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

The image uses the .NET 10 chiseled runtime, runs as non-root UID 1654, reads
config from `/config` and stores data in `/data`. It has no shell; use
`docker exec <container> /app/sbox-ns ...` for operator commands. Any setting
can also be passed as an `NS_` environment variable, for example
`-e NS_DATABASE__PROVIDER=postgres`.

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

Download `sbox-ns-<version>-<platform>.tar.gz` (or `.zip` on Windows),
`SHA256SUMS` and `SHA256SUMS.p256.sig` from the
[releases page](https://github.com/sbox-cool/sbox-network-storage-server/releases).
Authenticate the manifest first, using a current or rotation public key
obtained from a previously trusted checkout or installer—not from the archive
being verified. Save one trusted PEM key block as `release-key.pub`; OpenSSL's
`-verify` reads one key, so try the other pinned key separately during rotation.
Proceed only after one key successfully verifies:

```sh
openssl dgst -sha256 -verify release-key.pub -signature SHA256SUMS.p256.sig SHA256SUMS &&
sha256sum --check --ignore-missing SHA256SUMS &&
tar -xzf sbox-ns-<version>-linux-x64.tar.gz &&
./sbox-ns setup &&
./sbox-ns start
```

Every release ships `SHA256SUMS.p256.sig`, an ECDSA P-256 signature of
`SHA256SUMS` made with a key whose public half is pinned in the binary and in
the install scripts
([`release-signing-keys.pub`](../src/SboxNetworkStorage.Server/Updates/release-signing-keys.pub)).
`sbox-ns update` and the install scripts refuse a release with a missing or
invalid signature. The installer never treats a checksum downloaded alongside
an archive as proof of authenticity.

`SHA256SUMS` is also signed with Sigstore keyless signing, pinned to the release
workflow on a version tag:

```sh
cosign verify-blob SHA256SUMS \
  --signature SHA256SUMS.sig \
  --certificate SHA256SUMS.pem \
  --certificate-identity-regexp '^https://github\.com/sbox-cool/sbox-network-storage-server/\.github/workflows/release\.yml@refs/tags/v.+$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

## Configure

`sbox-ns setup` asks for the database (SQLite or PostgreSQL), the listen
address and the public URL, writes `server.toml`, `database.toml` and
`updates.toml`, generates the server secrets in `<data dir>/state/secrets/`
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
Keep `<data dir>/state/secrets/identity_ecdsa_p256.pem`: replacing it changes the name.
The connector token remains in `<data dir>/state/secrets/tunnel_token`, never in
CLI arguments, public server-info or registry database rows. Back up the state
folder as secret material. On Linux, a root-run command creates the identity, token
and connector files as the state folder's owner (the service user) and writes
nothing into the config folder.

Enabling writes `state/conf.d/zzzz-tunnel.toml`, binds HTTP to `127.0.0.1` on your
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

**Dependency notice.** Signed DNS registration and public-IP updates require
the sboxcool registry. Existing names resolve through Bunny DNS; player traffic
goes directly to your server and is not tunneled. The separate tunnel mode
sends traffic through Cloudflare. These optional hosted dependencies have no
reliability guarantee: a registry outage prevents registration/address changes,
and DNS, certificate or tunnel-provider outages can affect reachability.

The installer can opt in after starting the service (never before the registry's
HTTP ownership proof). Open the public HTTP port and 443 first:

```sh
curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh \
  | sudo env SBOX_NS_PROJECT="My Game" SBOX_NS_DNS=1 \
    SBOX_NS_ACME_EMAIL="you@example.com" SBOX_NS_ACCEPT_LETSENCRYPT_TERMS=1 sh
```

On Windows the same environment variables require an elevated PowerShell
installer. Without a supported service installation, start the server yourself
and use `dns enable` below. DNS and tunnel opt-ins are mutually exclusive.

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
`state/conf.d/zzzzz-dns.toml`. `tls.https_listen` stays `0.0.0.0:443`. Nothing changes
if any step fails. The name uses the same identity key as tunnels
(`<data dir>/state/secrets/identity_ecdsa_p256.pem`); keep and back it up.

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
`/var/lib/sbox-ns` is writable: `/etc/sbox-ns` is read-only to the service once the
layout is migrated (see [Config and state folders](#config-and-state-folders)).
`sudo sbox-ns service install` adds a managed drop-in granting only
`CAP_NET_BIND_SERVICE` when an HTTP or enabled HTTPS listener uses a port below
1024. Re-running installation, or `sudo sbox-ns service restart`, updates that
drop-in after a listener change; high-port listeners run with no capabilities.

The default sandbox uses `SystemCallFilter=@system-service` and
`ProtectProc=invisible`. If a verified runtime or connector needs a relaxation,
use `sudo systemctl edit sbox-ns` (or `sbox-ns@<name>`), never edit the generated
unit. Reset the specific setting in your operator drop-in:

```ini
[Service]
SystemCallFilter=
ProtectProc=default
```

Relax only the setting that caused a journal denial, then run
`sudo systemctl daemon-reload && sudo systemctl restart sbox-ns`. Such overrides
reduce isolation and are not the shipped baseline.

### Health checks

- `GET /health` returns 200 when the server and database are up; use it for
  load balancers, uptime monitors and container health checks.
- `GET /v3/server-info` returns the server version and basic information.

## Config and state folders

The server separates what an operator edits from what it writes itself:

| Folder | Holds | Owner and mode on a Linux service install |
| --- | --- | --- |
| `/etc/sbox-ns` (`/etc/sbox-ns/<name>`) | `server.toml`, `database.toml`, `updates.toml`, `alerts.toml`, your `conf.d/` drop-ins, operator-provided secrets such as a database password file | `root:sbox-ns`, folders `0750`, files `0640`; not in the unit's `ReadWritePaths` |
| `/var/lib/sbox-ns/state` (`/var/lib/sbox-ns/<name>/state`) | generated secrets, tunnel identity and token, the managed overlays in `conf.d/`, the `cloudflared` binary, the telemetry ID, the `.layout-version` marker | `sbox-ns`, folders `0700`, files `0600` |

`sbox-ns setup`, `config set`, `adminpanel`, `register` and the other commands that
edit operator files must run as root on a Linux service install; commands that create
runtime state (`tunnel`, `dns`, `telemetry`, generated secrets) may run as root or as
the service user and always leave the files owned by the service user. Generated
secrets, the tunnel and DNS overlays and the connector binary are never written into
the config folder. `sbox-ns config show` and `doctor` print which files were loaded.

Hosts installed before the split keep working unchanged: until the marker
`/var/lib/sbox-ns/state/.layout-version` exists, runtime files are read from and
written to the config folder, and `doctor` and the server's startup log print a
notice to run `sudo sbox-ns layout migrate`. That command (Linux, root) runs, in
order:

1. refuses to start while an update holds `<binary>.update-lock`;
2. stops the instance units that are running;
3. moves the runtime files out of each config folder into its state folder (a rename
   inside a verified folder, falling back to copy and delete);
4. sets the config folders to `root:<service group>` `0750` (files `0640`) and the
   state folders to the service user, `0700` (files `0600`);
5. moves each instance's `<data dir>/updates/last-update.json` and
   `<binary>.previous` into the root-only update state (`/var/lib/sbox-ns-update`);
6. re-renders the installed systemd units without the config folder in
   `ReadWritePaths`;
7. starts the units that were running;
8. writes `.layout-version` last.

Every step is safe to re-run, and a second run with the marker present changes
nothing. If a step fails the command undoes what it did, restores ownership and modes,
prints the exact commands to finish by hand, writes no marker and exits non-zero;
until the runtime files are back in one place `doctor` reports the layout as
partial. `sudo sbox-ns layout migrate --revert` moves the files back into the config
folders (owned by the service user again, with the unit allowed to write them) and
removes the marker, for example before running an older binary.

`sbox-ns update --auto` runs the migration as its first step when the marker is
missing, before it contacts the release feed, and `sbox-ns rollback` reverts the
layout before it restores the previous binary, because older binaries expect their
secrets in the config folder.

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

Back up the database **and** the config folder, plus the state folder
`<data dir>/state/` (it holds `secrets/`). Without the auth session secret, existing player
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

Unless you opt in to [automatic updates](#automatic-updates), the server never
updates itself. When a new release is published it prints a notice in the logs
and in `sbox-ns doctor`, including whether the release fixes a security issue or
needs a database migration.

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

The update check requests
`https://sboxcool.com/api/network-storage/releases/latest?channel=<updates.channel>`
and falls back to the newest stable release on GitHub when the feed is unreachable.

### Automatic updates

Off by default. On Linux with systemd, opt in with:

```sh
sudo sbox-ns service install --auto-update     # or install.sh with SBOX_NS_AUTO_UPDATE=1
```

This sets `updates.auto_install = true` and installs `sbox-ns-update.timer`
([`install/sbox-ns-update.timer`](../install/sbox-ns-update.timer)), which
runs `sbox-ns update --auto --all-instances` as root every hour (spread
by up to 15 minutes). A run installs nothing unless all of these hold:

- every instance on the host has `updates.auto_install = true`;
- the current UTC time is inside `updates.window` (default `"03:00-05:00"`;
  the start is inclusive, the end exclusive, `"22:00-02:00"` wraps midnight,
  `"00:00-24:00"` is always open);
- the release feed answers for `updates.channel` and offers a newer version.
  Unattended runs use only the feed, never the GitHub fallback, so channel
  promotion and holds always apply. They never downgrade;
- the version has been on its channel for `updates.min_release_age_hours`
  (`-1`, the default, means 24 hours on `stable` and 0 on `canary`), measured
  from the feed's `promotedAt` (or the release's `publishedAt`);
- that version has not already failed an unattended install on this host
  (install it by hand with `sbox-ns update --version X.Y.Z` to retry).

**Channels.** `canary` receives every new release as soon as it is published.
`stable` receives a release only after it has run on the official canary
server for at least 24 hours and passed the managed-service parity corpus
there. sboxcool can hold a channel at a version (for example to stop a bad
release); the feed then keeps offering the held version.
The hourly `.github/workflows/canary-verify.yml` gate uses `OSS_CANARY_URL`,
fixture credential secrets `OSS_CANARY_PROJECT_ID`, `OSS_CANARY_PUBLIC_KEY`,
`OSS_CANARY_SECRET_KEY`, and `OSS_RELEASE_OPS_TOKEN`. It refuses automatic
promotion while either channel is held. After a successful stable promotion,
optional secret `OSS_WEBSITE_DISPATCH_TOKEN` and variable
`OSS_WEBSITE_REPOSITORY` (`owner/repo`) send `oss-release-promoted` to the
website's official deployment workflow; without them deployment is manual.


**What a run does.** It downloads the release, verifies the pinned-key
signature of `SHA256SUMS` and then the archive checksum (and the cosign
signature as an extra check when `cosign` is installed), then for every
instance sharing the binary: stops the running instances, backs up each
database (`pre-<version>` in `<data dir>/backups`), swaps the binary (keeping
the previous one as `sbox-ns.previous`), runs `db migrate` for each instance,
starts the instances that were running, and waits up to 120 seconds for each
`/health` to answer 200 with the new version. Backups, restores and migrations
run as the instance's service user; root only controls the services and swaps
the binary. Before anything else, a run on a host that has not been migrated performs
`sbox-ns layout migrate` (see [Config and state folders](#config-and-state-folders)).

**Update state.** The updater keeps its records and the previous binary in
`/var/lib/sbox-ns-update` (`root:root`, mode `0700`), never in a folder the
service account can write. Rollback restores only the installed `sbox-ns`
binary from that folder. The release repository, feed URL and signing keys are
compiled into the binary; `updates.feed_url` and `updates.github_repo` are no
longer settings. Unattended updates never honor `SBOX_NS_INSECURE_SKIP_SIGNATURE`.

**Rollback.** If any step fails, the run puts the previous binary back,
restores every database backup taken in that run, starts the instances again,
checks they report the old version, and records `failed` with the reason in
each instance's `last-update.json` in the update state folder (shown by
`sbox-ns doctor`). After a successful run, `sbox-ns rollback --all-instances`
undoes it. If a service cannot be stopped during recovery, no database or binary is
restored underneath it; the run exits 3 and preserves the backup files for
operator recovery.

| Exit code of `update --auto` | Meaning |
| --- | --- |
| 0 | Updated and healthy, or nothing to do (the reason is printed) |
| 1 | The update failed and every instance was rolled back, or it failed before anything changed (download or verification) |
| 2 | Configuration problem: an invalid config, or instances that disagree on `updates.channel`/`updates.window` |
| 3 | The update failed and the rollback did not complete: check `sbox-ns doctor` and the logs now |
| 4 | The release feed was unreachable or invalid; nothing changed |

Follow runs with `journalctl -u sbox-ns-update`. Turn unattended updates off
with `sbox-ns config set updates.auto_install false` (or
`systemctl disable --now sbox-ns-update.timer`).

**What can still go wrong.**

- Migrations are forward-only. A rollback restores the database backup taken
  just before the update, so it only works with that backup in place: keep
  enough disk space for one backup per instance.
- Data written after an update is lost when the update is rolled back: with
  `sbox-ns rollback`, everything since the update; with an automatic rollback,
  whatever players wrote to instances already restarted on the new version
  during the health checks (at most a few minutes).
- A release can pass the health check and still misbehave for your game.
  Stay on `stable`, and pick a window when few players are online: every
  instance is unavailable while it updates (usually well under a minute).
- PostgreSQL backups and restores need `pg_dump` and `pg_restore` on the host.

### Several instances on one host

Each instance has its own config folder, data folder and port, and runs as the
systemd unit `sbox-ns@<name>` ([`install/sbox-ns@.service`](../install/sbox-ns@.service)):
Use `--all-instances` for automated updates and rollbacks when another
registered instance shares the binary; selecting only one is refused.

```sh
sudo sbox-ns service install --instance alpha --port 8101 --auto-update
sudo systemctl start sbox-ns@alpha
```

This creates `/etc/sbox-ns/alpha` (keeping existing files) and
`/var/lib/sbox-ns/alpha`, sets `server.listen = "127.0.0.1:8101"` (put a
reverse proxy in front), and enables the unit. Use
`--config-dir /etc/sbox-ns/alpha --data-dir /var/lib/sbox-ns/alpha` with other
commands to address the instance. All instances share `/usr/local/bin/sbox-ns`,
so they are updated together and must use the same `updates.channel` and
`updates.window`; `--all-instances` covers `/etc/sbox-ns/server.toml` (the
default instance, unit `sbox-ns`) and every `/etc/sbox-ns/<name>/server.toml`.
Each instance needs its own database (or PostgreSQL schema).

## Uninstall

```sh
sudo sbox-ns service uninstall
sudo rm /usr/local/bin/sbox-ns
# Remove configuration and data only if you no longer need them:
sudo rm -rf /etc/sbox-ns /var/lib/sbox-ns
sudo userdel sbox-ns
```
