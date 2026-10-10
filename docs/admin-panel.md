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
Until an owner exists, `/login` shows these steps instead of a password form.

How the link works:

- **Base URL.** The link uses `server.public_url` when it is set. Otherwise it
  is built from the active listener: HTTPS with the ACME domain when TLS is on,
  or the bound address. For wildcard binds like `0.0.0.0` it uses the first
  routable IPv4 address of the machine. Set `server.public_url` if that guess
  is wrong, for example behind NAT or a proxy. `sbox-ns setup` and
  `sbox-ns quickstart` use the same rule, but print `<this-host>` for wildcard
  binds instead of guessing.
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
  requests per minute per client IP and 60 per minute across all addresses,
  and at most 2 passwords are checked at a time. Requests over the limit get
  HTTP 429.

Forgot your password? Run `sbox-ns admin login-link` to sign in, or
`sbox-ns admin reset-password` to set a new password. A reset ends all
existing sessions.

## HTTP vs HTTPS

From another machine, the server refuses your password, login links and the
setup form over plain HTTP, because anyone on the network path could read them.
The refusal page lists the ways in: an SSH port forward
(`ssh -L 8080:127.0.0.1:8080 you@your-server`, then open
`http://localhost:8080/login`), `sbox-ns tunnel enable`, or HTTPS. Loopback
connections are always accepted. On a network you trust you can turn the check
off with `sbox-ns config set adminpanel.allow_insecure_http true`; the panel then
shows a warning banner, and your password and session cookie travel
unencrypted. To use HTTPS, pick one of these:

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

## Connect a game

**Create project** on **All projects** also creates a public key labeled
`Game client` unless you clear **Create a public game key**. The project
overview then opens with a **Connect your game** card:

- The exact `NetworkStorage.Configure( "<projectId>", "<public key>", "<base URL>" );`
  line, filled with the project ID, the oldest enabled public key and the base
  URL. Without an enabled public key the line shows `<public-api-key>`.
- The same values as fields for **Editor > Network Storage > Setup** in the
  s&box editor. The secret key is only shown once, when you create it under
  **API keys**.
- The base URL is `server.public_url` when set, otherwise the address you
  opened the dashboard with. The card warns when that address is `localhost`
  (players on other machines cannot use it) or plain HTTP.
- A checklist: a public key exists, a collection or endpoint is defined, the
  game reached the server (with the last request from the request log), and
  401/403 responses in the last 24 hours with what each status usually means.

Copy buttons use the browser clipboard. Browsers only allow that over HTTPS
or on `localhost`; elsewhere the button selects the text and asks you to
press Ctrl+C.

Creating a key redirects back to the overview and shows the new raw key
there once. Reloading the page does not create another key or show the raw
key again. Revoking a key and deleting saved tests, rate limit rules, webhook
profiles and pages ask for confirmation first.

**All projects** shows the create form first while there are no projects,
with import and export folded under **Import or export**. Its **Use with a
coding agent** card has copyable commands for connecting Claude Code or
another MCP client to `sbox-ns mcp` over SSH, a local `.mcp.json`, the Claude
Code plugin, and the three `mcp.allow_*` switches with what each allows
([coding agents](mcp.md)). Each project overview has a copyable first prompt
that names the project ID.

## Project settings

The overview keeps name, description, **Enabled** and **Require s&box
authentication** open. The other settings are folded and closed by default.

### Auth sessions

With **Enable auth sessions**, the game can trade its s&box token for a signed
session token (`/v3/auth-sessions/{projectId}/create`). Later calls send the
session token and skip the s&box check until it expires after **Session TTL**
seconds. Off by default.

### Encrypted requests

**Enable encrypted requests** is reported to the client library in the signed
security config, and the library then encrypts endpoint calls (AES-256-GCM
with a key derived from the public key, signed with HMAC-SHA256). This server
decrypts such calls and rejects replays, but still accepts plain calls, so it
does not make encryption mandatory. It accepts request IDs up to 120 seconds
old whatever the window setting says; the window value is passed to the client
library.

### Player key mode

Sets the record key endpoint steps use for the calling player (`playerKey`).
**Player** uses the Steam ID. **Player save** uses `{steamId}_{saveId}` when
the call input has a `saveId`, for games with save slots, and the Steam ID
otherwise.

### Legacy player projections

A built-in repair and leaderboard projection for endpoint saves into the
`players`, `skills`, `kills` and `leaderboard_global` collections. Projects
that existed before the setting was introduced keep it on; new projects start
with it off. Leave it off unless your game was built on those collections.

### Revision policy

These settings are stored with the project and returned to the client library
and the editor Sync Tool. This server never blocks a request because of them.
It only answers revision-init with whether the client's game revision is
outdated ([revision handshake](game-client.md#revision-init-handshake)). Any
blocking, banner or popup happens in the client library.

## Data browser

On a project page, choose **Browse data**, or open
`/dashboard/projects/{projectId}/data`.

- **Collections:** every synced collection with its type (per-player or
  global) and live record count. Deleted (tombstoned) player records are not
  counted. Without collections, the page links to the Collections editor and
  to the [Sync Tool setup](client-setup.md#3-configure-the-editor).
- **Find player:** enter a Steam ID to list that player's records in every
  per-player collection: the record whose key is the Steam ID and save slots
  whose key starts with `{steamId}_` (up to 50 per collection; open the
  collection to see more). `?player=` takes the same value.
- **Records:** sorted by key, 50 per page by default (`?size=` accepts 1–200).
  Each row shows version, last change, size and a short preview. **Key starts
  with** (`?q=`, up to 256 characters) lists keys that begin with the text;
  keys are case-sensitive. An empty collection offers **Create record** and a
  link to endpoint tests.
- **Record detail:** pretty-printed JSON in an editable payload form. Records
  of a player (key is a Steam ID, or starts with `{steamId}_`) link to
  **Test an endpoint as this player**, which opens Endpoint tests with that
  Steam ID filled in (`/tests?steamId=`), and to all records of that player.
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
SQLite and PostgreSQL. Counts and pages are queried from the database, so
large collections are not loaded into memory; only **Download** reads the
whole collection.

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
definitions and game values. Author definitions in YAML; the dashboard stores
your source text and compiles it with the same compiler as editor sync.
Raw JSON is still accepted for older edits but is deprecated. Saving preserves
source metadata and updates only the selected resource, not other definitions.

Saved definitions are available to the runtime immediately. Editor sync can
overwrite dashboard edits, so reconcile changes with your checked-in YAML/JSON
source. Endpoint expressions use double braces, such as `{{steamId}}` and
`{{player.level}}`.

The definition editor keeps **Check definition** and **Save live definition**
alongside the source. Expand the endpoint or collection builder for routing,
storage and snippet controls. Expand **Examples** below the editor to browse
complete definitions; **Use example** replaces the editor text and asks for
confirmation when it is not empty. Review the replacement before saving.

A new collection starts as `accessMode: endpoint`: game clients reach it only
through endpoints and queries, and direct document calls answer
`403 ENDPOINT_ONLY`. The collection builder sets **Game client access**
(`accessMode: endpoint` or `public`) and **Deletes from game clients**
(`allowRecordDelete`) together with the storage kind. See
[collection access from game clients](client-setup.md#collection-access-from-game-clients).

A save that fails validation keeps your text and lists each diagnostic as
`Error CODE at path: message`, the same wording **Check definition** uses.

**Check definition** validates the current text without saving or executing it.
It reports server diagnostics, not a guarantee of runtime success. If the text
changes during a check, check again. A failed request never counts as successful
validation. Catalog failures offer **Retry examples** while direct editing stays
available. **Create needed definitions** reports created, existing and failed
dependencies separately; retrying keeps definitions that already exist.

Analytics, audit/request logs, errors and usage tabs read stored runtime data.
Empty panels mean no matching data has been recorded, not synthetic activity.
The console does not replace every screen or workflow in the managed dashboard:
it covers project settings and keys, records, collection, endpoint, workflow and
query definitions, game values, endpoint tests, version history, rate limit
rules, webhooks and pages.

### Use from the game

When an endpoint or collection is open in the editor, a **Use from the game**
card shows the C# for it, with a **Copy** button:

- Endpoints: `await NetworkStorage.CallEndpoint( "<slug>", new { ... } );`
  with the input filled from the endpoint's `input` schema (declared defaults,
  otherwise a value of the declared type), followed by the
  `TryGetLastEndpointError` check. The `endpoint_snippet` MCP tool returns the
  same code. Links open **Endpoint tests** with the endpoint selected and its
  **Version history**.
- Collections with `accessMode: public`: `GetDocument` and `SaveDocument` for
  the player's own document (per-player) or a named record (global), with the
  saved object built from the schema. Other collections say to call an
  endpoint instead, because direct calls answer `403 ENDPOINT_ONLY`. A link
  opens the collection in the data browser.
- Workflows link to their version history.

### Pages

**Pages** publishes markdown or key/value pages that games read without a
key. An open page shows its absolute public URL, built like the Connect card's
base URL (`server.public_url`, otherwise the dashboard address), and a
**Fetch it from the game** example. The response is JSON with `title`,
`updatedAt` and `markdown` (markdown pages) or `data` (key/value pages).

### Request log and errors

**Logs** lists recent data-plane requests (`/v3`, `/v1`, `/api/storage`,
`/api/network-storage`, `/pages`, `/api/pages`) with time, method, path,
status and duration:

- Authenticated requests are listed under their project.
- Rejected requests that never authenticated (status 400 or more, such as a
  wrong API key or failed s&box authentication) are listed under the project
  ID in the URL, but only when that project exists.
- Successful unauthenticated requests (for example `/v3/server-info`) and
  OPTIONS preflights are not listed.

**Errors** lists errors the server reported for the project: unhandled
exceptions on a project route, endpoint failures and conflicts, storage
failures and unconfirmed client saves. Each row shows the error code, message
and source, plus up to 4,000 characters of the stack trace when there is one.
Messages and stack traces can contain player data.

Both tabs explain common statuses and codes (`UNAUTHORIZED`,
`SBOX_AUTH_FAILED`, `ENDPOINT_ONLY`, `RECORD_DELETE_DISABLED`, `FORBIDDEN`
and others) in a **What it means** column; see
[error codes](client-setup.md#error-codes).

To keep the database small, each project records at most 20 successful
requests, 20 rejected or failed requests and 20 errors per minute. Rows are
written within a few seconds and deleted after 7 days. For more detail, read
the server log with `sbox-ns logs -f`.

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

Record payloads open in JSON mode. Select **Visual**
to edit strings, numbers, booleans, objects and arrays without writing JSON.
You can add or remove nested fields, then switch back to JSON or save directly.
Unknown fields are kept.

Visual mode refuses duplicate keys and numbers that JavaScript would round.
Both modes use the same server-side schema validation, permission checks
and conflict detection.

## Optional admin security

Open **Admin security** to add a time-based authenticator. On a phone, open
**Add to authenticator app**: it is an `otpauth://totp/` link (issuer
`sbox-ns`, your owner name, SHA-1, six digits, 30 seconds) that authenticator
apps open directly. Elsewhere, add the displayed secret manually. Then confirm
your current password and a six-digit code. Enrollment expires after ten
minutes. The server does not activate 2FA until confirmation succeeds.

Save the ten recovery codes offline. Each works once. Enrollment and local
reset invalidate existing owner sessions. Password reset does not remove 2FA.
With local server access, recover using `sbox-ns admin reset-2fa`.
The authenticator is encrypted with a key kept in the data folder, which an
export does not include. After a restore on another machine, sign-in says the
server cannot read your authenticator: use a recovery code, or run
`sbox-ns admin reset-2fa` and sign in with your password.
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
`adminpanel.turnstile.enabled`, `adminpanel.turnstile.sitekey`, `adminpanel.turnstile.secret` and `adminpanel.turnstile.hostname` as described
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
