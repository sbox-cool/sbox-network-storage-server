# Admin panel

Every server includes a web admin panel for its single local owner: projects,
API keys, project settings and a schema-validated record editor. No website account,
signup or SSH tunnel is needed.

- `/login`: password login
- `/login/link`: single-use login links (see below)
- `/dashboard`: projects, keys and settings
- `/dashboard/projects/{projectId}/data`: data browser

## First login: `sbox-ns admin login-link`

Run this on the server, for example over SSH or through an agent with shell access:

```sh
sbox-ns admin login-link              # valid for 15 minutes
sbox-ns admin login-link --minutes 5  # 1–60 minutes
```

It prints a link like this:

```text
https://ns.example.com/login/link?token=3f9c…
Signs in as owner 'owner'.
Single use; expires 2026-10-08 12:15:00 UTC (15 min). Opening the page does not use it up; confirming does.
```

Open the link and confirm. If no owner exists yet, the page asks you to choose
the owner username and password (at least 12 characters). Being able to run
commands on the server proves you control it, so this works from any address.
The loopback-only `/setup?token=…` URL in the server log still works as well.

How the link works:

- **Base URL.** The link uses `server.public_url` when it is set. Otherwise it
  is built from the active listener: HTTPS with the ACME domain when TLS is on,
  or the bound address. For wildcard binds like `0.0.0.0` it uses the first
  routable IPv4 address of the machine. Set `server.public_url` if that guess
  is wrong, for example behind NAT or a proxy.
- **Token.** Each token is 256 bits of randomness. Only its SHA-256 hash and
  expiry time are stored, as workspace object
  `server/identity/login-links/<sha256>.json` in the configured
  SQLite/PostgreSQL database. The raw token is printed once and never saved.
  Minting a new link deletes expired ones.
- **Opening (GET).** Opening the link only shows a confirmation page and does
  not use the token. Chat apps and link previewers can fetch the URL without
  using it up. The page is sent with `Cache-Control: no-store` and
  `Referrer-Policy: no-referrer`.
- **Confirming (POST).** Confirming needs an antiforgery token. The server
  deletes the token and signs you in in one atomic step, so a second attempt
  gets a 404. Expired, wrong or malformed tokens also get a 404. Each
  sign-in through a link is logged as a warning with the client address.
- **Rate limit.** `/login`, `/login/link` and `/setup` share a limit of 10
  requests per minute per client IP. Requests over the limit get HTTP 429.

Forgot your password? Run `sbox-ns admin login-link` to sign in, or
`sbox-ns admin reset-password` to set a new password. A reset ends all
existing sessions.

## HTTP vs HTTPS

Over plain HTTP, your password, login links and session cookie all travel
unencrypted. The panel shows a warning banner whenever it is served over plain
HTTP to a non-loopback address. To use HTTPS, pick one of these:

- **Built-in Let's Encrypt:** `sbox-ns config set tls.mode acme`, then set
  `tls.acme_domain`, `tls.acme_email` and `tls.acme_accept_terms = true`
  (see [configuration](configuration.md)).
- **Your own certificate:** set `tls.mode = "certificate"` with
  `tls.certificate_path` and `tls.key_path`.
- **TLS-terminating reverse proxy or tunnel** on the same machine, such as
  Caddy, nginx or Cloudflare Tunnel. The server trusts `X-Forwarded-Proto` and
  `X-Forwarded-For` only from loopback proxies. Set `server.public_url` to the
  HTTPS URL.

Cookie protection:

- Both cookies are always `HttpOnly`. The session cookie is `SameSite=Lax`, so a
  link to the panel from Discord, an alert email or a bookmark keeps you signed
  in; cross-site form posts never carry it, and every change is a POST that also
  needs an antiforgery token. The antiforgery cookie is `SameSite=Strict`.
- Over HTTPS they are sent as `__Host-sbox-ns-owner` and `__Host-sbox-ns-csrf`.
  That means `Secure`, `Path=/` and never a `Domain` attribute. Another
  subdomain of a shared parent domain therefore cannot plant or overwrite
  them. Over HTTPS the server ignores unprefixed copies of these cookies.
- Plain HTTP cannot carry `__Host-` cookies, so plain-HTTP access uses the
  unprefixed names.
- Sessions last eight hours and do not slide. Changing the password ends them.

## SSH tunnel alternative

If you would rather not expose the panel at all, bind the server to loopback
or firewall its port. Then tunnel to it:

```sh
ssh -L 8080:127.0.0.1:8080 user@server
```

Open `http://localhost:8080/login` locally. Loopback HTTP does not show the
warning banner. `sbox-ns admin login-link` still works this way: replace the
printed host with `localhost:8080`.

## Data browser

On a project page, choose **Browse stored records**, or open
`/dashboard/projects/{projectId}/data`.

- **Collections:** every synced collection with its type (per-player or
  global) and live record count. Deleted (tombstoned) player records are not
  counted.
- **Records:** sorted by key, 50 per page by default (`?size=` accepts 1–200).
  Each row shows version, last change, size and a short preview. Key search
  (`?q=`, up to 256 characters) is case-insensitive substring matching.
- **Record detail:** pretty-printed JSON in an editable payload form.
- **Create:** choose **Create record** on a collection. Player keys accept
  1–256 letters, numbers, underscores, hyphens or colons; global record IDs
  accept 1–128 letters, numbers, underscores or hyphens. Global records are
  shared entries, not player records.
- **Save:** payloads must be JSON objects within the configured store byte
  limit and match the collection schema (including required fields, types,
  enums, nested objects/arrays and supported size/range constraints). Unsupported
  schema modifiers fail closed with an explicit error rather than being ignored.
- **Download:** `…/data/{collectionId}/export` returns the whole collection as
  JSON. The file contains `format = "sbox-ns.collection-export"`, the
  collection type, and each record's key, version, change time and payload.

The browser reads through the storage contract, so it behaves the same on
SQLite and PostgreSQL. Record counts and pages are computed from the full
collection on each request. Very large collections therefore take longer to
list.

All mutations require the owner session and an antiforgery token. Edit and
delete forms carry the version and a protected snapshot of the original payload
and change time. The database compares this state and changes the record
atomically, including when game writers reuse a version number. A stale form
receives HTTP 409 and cannot overwrite or delete changed content. Create uses
an atomic missing-record check, so two create forms cannot overwrite one
another. Player tombstones can be recreated; owner saves increment the row's
version without changing the existing game API's version behavior.

Validation errors (HTTP 400) and conflicts preserve the entered JSON and key.
Use **Reload current record** to compare the latest stored value, then reconcile
your changes before submitting again.

Deletion additionally needs the exact record key typed as confirmation:

- Per-player records get a deletion tombstone, just like a delete through the
  storage API.
- Global records are removed from the global record table.

Creates, edits and deletions are written to the project audit log as
`record.create`, `record.update` and `record.delete`, with the collection, key,
collection type and expected version.

## Authoring and runtime diagnostics

The project overview links to collection/schema, endpoint, workflow and query
definitions and game values. Edit JSON directly, or keep source-backed definitions
in a wrapper with `sourceText`, `sourceFormat`, `sourcePath` and `authoringMode`.
The owner console reuses the editor compiler and management writes. Source text
remains authoritative when present; saving preserves source metadata and updates
only the selected resource, not other definitions.

Saved definitions are available to the runtime immediately. Editor sync can
overwrite dashboard edits, so reconcile changes with your checked-in YAML/JSON
source. Endpoint expressions use double braces, such as `{{steamId}}` and
`{{player.level}}`.

Analytics, audit/request logs, errors and usage tabs read stored runtime data.
Empty panels mean no matching data has been recorded, not synthetic activity.
The console does not replace every screen or workflow in the managed dashboard.

## Move one project

On the project overview, **Download project archive** exports that project's
metadata, definitions, records, runtime data and stored API-key material as a
private `.tar.gz`. It excludes instance configuration and private secrets,
including signing keys. On the destination dashboard, upload it under
**Import a portable project**.

Import retains the original project ID and assigns its owner membership to the
destination instance's owner. An existing project ID is refused rather than
overwritten. Limits are 64 MiB compressed and 256 MiB expanded. This is
self-hosted SQLite/PostgreSQL portability, not a managed-host export contract.

After moving to an independently configured instance:

- Create a new **secret key** for editor sync and dedicated servers. Secret-key
  lookup identifiers depend on the instance's encryption secret; copied hashes
  alone do not make the old secret key usable on a new instance.
- Point the game client and editor at the destination's base URL.
- Authenticate player sessions again; instance signing/session secrets are not
  part of the project archive.

Keep the archive private: it contains player data and public runtime credentials.
For whole-instance disaster recovery, use [server export/import](export.md)
with the required configuration/secrets, not a portable project archive.

## Themes and visual editing

The theme selector offers System, Dark, Light, Slate and Warm. System follows
your operating system. Your choice is stored in this browser only; it does not
change other operators' dashboards. All themes work without external assets.
The shared design rules live in [UI guidelines](ui-guidelines.md).

Record payloads and collection definitions open in JSON mode. Select **Visual**
to edit strings, numbers, booleans, objects and arrays without writing JSON.
You can add or remove nested fields, then switch back to JSON or save directly.
Unknown fields are kept, including authoring metadata.

Visual mode refuses duplicate keys, numbers that JavaScript would round, and
definitions with authoritative `sourceText`. Keep those in JSON mode to preserve
the original text. Editing compiled fields cannot replace editing authoritative
source. Both modes use the same server-side schema validation, permission checks
and conflict detection.

## Optional admin security

Open **Admin security** to add a time-based authenticator. Add the displayed
secret manually in your authenticator app, then confirm your current password
and a six-digit code. Enrollment expires after ten minutes. The server does
not activate 2FA until confirmation succeeds.

Save the ten recovery codes offline. Each works once. Enrollment and local
reset invalidate existing owner sessions. Password reset does not remove 2FA.
With local server access, recover using `sbox-ns admin reset-2fa`.
Shell-issued login links remain a local-operator recovery capability, so
protect SSH access and the configuration/data directories too.

Disable the web admin surface with:

```sh
sbox-ns adminpanel disable
sbox-ns service restart
```

This denies login, setup, login links and dashboard requests at the server
boundary. It does not disable game APIs. Configuration changes take effect
after restart; `sbox-ns adminpanel enable` restores access after another restart.
If running in the foreground, stop and start the process instead.

Allow only specific admin IPs or CIDR networks:

```sh
sbox-ns config set adminpanel.allowed_ips "192.0.2.10,2001:db8:1234::/48"
sbox-ns service restart
```

Replace those documentation addresses with yours. An empty list allows all
addresses. The policy uses the trusted connection address, not arbitrary
forwarding headers. The built-in proxy trust is loopback, one hop. Confirm the
observed address and your reverse proxy configuration before restricting access,
especially with a Cloudflare tunnel; keep local recovery access available.

Turnstile is optional. Create a widget for your exact public admin hostname
(including your `sboxns.com` tunnel name when applicable), then configure
`adminpanel.turnstile.enabled`, `site_key`, `secret` and `hostname` as described
in [configuration](configuration.md). Keep the secret in a private config file
or a protected environment variable, not shell history. The server checks
success, action and hostname with Cloudflare before login/setup/link consumption,
and denies requests when verification fails or Cloudflare is unavailable.
Turnstile does not replace 2FA, passwords, rate limits or IP restrictions.

## Public read-only demo

`adminpanel.demo_read_only=true` is for a dedicated demonstration instance,
not for making a production database public. It requires SQLite and refuses
an existing project database without its demo marker. Start with a new, private
configuration and data directory.

The instance seeds clearly labeled generated player progress, inventory,
world state, definitions and diagnostic rows into its real database. Visitors
can browse anonymously and explore local editor changes. Every HTTP mutation,
owner login/setup/security/export operation and game execution route is denied
server-side. Health and server-info remain available. No production credentials
or player data should ever be copied into this instance.
