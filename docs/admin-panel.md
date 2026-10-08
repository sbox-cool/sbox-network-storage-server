# Admin panel

Every server includes a web admin panel for its single local owner: projects,
API keys, project settings and a read-only data browser. No website account,
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
- **Record detail:** the stored payload as pretty-printed JSON.
- **Download:** `…/data/{collectionId}/export` returns the whole collection as
  JSON. The file contains `format = "sbox-ns.collection-export"`, the
  collection type, and each record's key, version, change time and payload.

The browser reads through the storage contract, so it behaves the same on
SQLite and PostgreSQL. Record counts and pages are computed from the full
collection on each request. Very large collections therefore take longer to
list.

The only change the browser can make is deleting a single record. It needs the
antiforgery token and the exact record key typed in as confirmation:

- Per-player records get a permanent deletion tombstone, just like a delete
  through the storage API.
- Global records are removed.

Every deletion is written to the project audit log as `record.delete` with
the collection, key, collection type and version.
