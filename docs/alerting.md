# Operator alerting

sbox-ns notifies operators through **channels**: email over SMTP, Discord
webhooks, and generic HTTP webhooks (with presets for Slack-compatible
webhooks, Microsoft Teams, ntfy and Gotify). Alerts come from two sources:

- **Captured errors**, built in and always on (below).
- **Alert rules** you add in the owner console under **Alerts**, for example
  "CPU use above 90 % for 2 minutes" or "free disk below 5 GB". Rules,
  metrics and the Server page are described in [monitoring.md](monitoring.md).

Every send is best-effort: a failed send is logged, shown in the alert
history, and never fails the request or rule check that triggered it. No
channel is configured by default, and no outside service is required.

## Channels

Channels come from two places and are used the same way:

| Where | Kinds | Edited with |
| --- | --- | --- |
| `alerts.toml`, `conf.d/`, `NS_` environment variables | one Discord webhook, one SMTP target | `sbox-ns config set`, restart to apply |
| Owner console, **Alerts → Channels** | any number of email, Discord and webhook channels | the console, live |

Both appear in the console's channel list; `alerts.toml` channels are marked
and read-only there. Each console channel has:

- **Minimum severity**: `info`, `warning`, `error` or `critical`. The channel
  receives alerts at or above it. `alerts.toml` channels receive everything.
- **Scope**: all alerts (server-wide), or only alerts about one project.
  Project-scoped channels receive that project's captured errors only; rules
  watch the whole server, so their alerts go to server-wide channels.
- **Send test** sends a real notification and shows the result next to the
  channel. Tests ignore the severity and scope filters.

### Secrets

Webhook URLs (which embed tokens), SMTP passwords and webhook signing
secrets entered in the console are written owner-only (`0600`, or an
owner-only ACL on Windows) to the server's secrets folder
(`<data>/state/secrets/monitoring/` in the default layout) and never into
the settings file. The console never shows them again: leave a secret field
blank when editing to keep the saved value. A server export includes them
only when you export with secrets.

`alerts.discord.webhook_url` and `alerts.smtp.password` are flagged secret:
`sbox-ns config show` and `sbox-ns config get` print `********` unless
`--show-secrets` is passed, the MCP `config_set` tool refuses them, and the
values never appear in logs or alert payloads. Prefer the `*_file` keys so
secrets live outside the TOML files (same pattern as
`database.postgres.password_file`). The first line of the file is used.

## What triggers a captured-error alert

Every error that reaches the shared error pipeline fans out to the log plus
every channel that accepts the `error` severity:

- Unhandled request exceptions captured for the project's **Errors** tab (5xx,
  plus 503 database outages).
- Native endpoint-execution shadow failures (`EndpointShadowReporter`):
  executor exceptions, 5xx results, and 409 data-integrity conflicts
  (`SAVE_REGRESSION_BLOCKED` / `STALE_SAVE`). Repeated identical conflicts
  are throttled so a looping autosave cannot flood the channel.
- Compatibility-proxy captures (`ProxyErrorReporter`) and handler-caught
  exceptions reported via `HandlerErrorReporter`.
- Data-plane storage errors (`INetworkStorageErrorAlertSink`), e.g.
  unconfirmed saves reported by the game through
  `POST /api/network-storage/{projectId}/save-failure`. That route needs an
  enabled project and real collection and record ids, and cuts the client's
  `reason` to 200 characters.
- Optional library compatibility alerts: set the project's affected-traffic
  percentage in **Project → Clients**. A flushed batch needs at least 100
  authenticated requests and the configured share of unsupported/blocked
  library traffic (including the project's explicit minimum). The default is
  off. Version identities are self-reported. See [compatibility.md](compatibility.md).

Each alert carries the correlation ID, route, classification, project, and
timestamp so it can be matched against the project's Errors tab and the logs.

## Limits

Anyone with a game's public key can cause some alerts, so every alert source
(captured errors and rules) shares one limit. The log still records every
error.

- An alert of a kind already sent in the last 10 minutes is not sent again.
  For errors the kind is the project, operation and code plus the collection
  (storage errors) or the route (request errors); the player does not count.
  For rules it is the rule and the transition (firing or resolved). The next
  alert of that kind says how many were held back.
- At most 10 alerts are sent per minute. The next alert sent says how many
  were dropped.

Rules also have their own cooldown (see [monitoring.md](monitoring.md)).

Chat channels (Discord, Slack-compatible, Teams) show the message, route and
project as code, so text sent by a game client cannot add links or
formatting, and alerts never ping anyone.

## Discord setup

1. In the Discord channel: **Edit Channel → Integrations → Webhooks →
   New Webhook**, copy the webhook URL.
2. Add it in **Alerts → Add a channel** (type Discord), or in config:

```toml
# alerts.toml
[alerts.discord]
enabled = true
webhook_url_file = "secrets/discord_webhook_url"
username = "sbox-ns"
```

or with environment variables:

```sh
NS_ALERTS__DISCORD__ENABLED=true
NS_ALERTS__DISCORD__WEBHOOK_URL_FILE=/run/secrets/discord_webhook
```

`webhook_url` and `webhook_url_file` are mutually exclusive; setting both is
a validation error. Relative `*_file` paths resolve against the config
folder. Discord URLs must use `https://`. The message is a single embed
(title, description and fields; red for errors, yellow for warnings, blue
for info, green for resolved).

### Webhook target safety

Console-saved Discord and generic webhook URLs must not point at this server
itself: loopback, link-local (including the `169.254.169.254` cloud metadata
address), unspecified addresses and `localhost` are refused at save time.
RFC 1918 private addresses stay allowed for self-hosters whose alert
receiver lives on their own network. Alert sends never follow HTTP
redirects.

## SMTP setup

Add an email channel in the console (host, port, STARTTLS, optional user
name and password, from, comma-separated recipients), or in config:

```toml
# alerts.toml
[alerts.smtp]
enabled = true
host = "mail.example.com"
port = 587
username = "sbox-ns@example.com"
password_file = "secrets/smtp_password"
from = "sbox-ns@example.com"
to = "ops@example.com, oncall@example.com"
use_tls = true
```

`password` and `password_file` are mutually exclusive. Empty `username`
means no authentication. `to` accepts comma- or semicolon-separated
addresses. Mail is plain text with a one-line subject (for errors
`[sbox-ns] <Classification> <Method> <Path> (<CorrelationId>)`, for rules
`[sbox-ns] FIRING <rule>` or `[sbox-ns] RESOLVED <rule>`) and a field-list
body; delivery uses a 10-second timeout and STARTTLS when TLS is on.

### Resend (no code changes needed)

Resend's free tier (3,000 emails/month, 100/day) works through the SMTP channel
above with no provider integration: host `smtp.resend.com`, port `587` with TLS
on, username `resend`, password a Resend API key, and a verified sender domain
as `from`. GitHub offers no transactional email API, so it is not an option.

## Providers (optional, one-click)

Providers are optional adapters behind the same channels above. The server is
fully self-contained: plain SMTP and webhooks need no outside account, and a
provider never runs until you set it up in **Alerts → Providers**. Setup
first shows exactly what will be created on the provider; confirming checks
the credentials, creates it, and adds a provider channel, so the severity
and project filters, the test button and the shared limits apply unchanged.
Tear down deletes only the resources the setup recorded, and misses count as
already removed. The UI labels each provider **First party** (sboxns.com) or
**Third party** (everyone else). Provider tokens live owner-only in
`<data>/state/secrets/monitoring/provider-<id>.json` and are never shown
back: the preview asks for the token again on confirm.

### Cloudflare Email (third party)

Outbound alerts through Cloudflare Email Sending, with optional forwarding
through Email Routing. Create an API token with least-privilege scopes, all
on the zone:

- **Email Sending, Edit** (required: check the token, manage the sending
  subdomain, send).
- **Email Routing Rules, Edit** (only when forwarding to an operator address
  is turned on).

Setup creates the Email Sending subdomain and, when asked, the routing
destination address and rule; it only reads the subdomain's DNS records back
so you can add them at your DNS yourself. Alerts go to the forwarding
address, or to the sender address when forwarding is off. Only these
`https://api.cloudflare.com/client/v4` paths are called: `GET`
`/user/tokens/verify`, `POST /zones/{zone_id}/email/sending/subdomains`,
`GET` the subdomain's `/dns` and `/dns/status`, `POST`
`/accounts/{account_id}/email/sending/send`, plus the routing
`/addresses` and `/rules` paths when forwarding, deleted by teardown.

### sboxns.com email (first party)

Sends as your sboxns.com name and forwards replies to you, using the
existing tunnel or DNS identity. It stays hidden until the server has one
(`tunnel enable` or `dns enable`). This repo holds only the client side of
the proposed, versioned `sboxns.email/v1` contract (`GET v1/status`, `POST`
`v1/forwarding`, `POST v1/send`, `DELETE v1/forwarding/{id}`); the
service-side endpoint, quotas and pricing are out of scope and are not
claimed to exist. Until it does, every operation reports "service not
available" and everything else keeps working.

## Webhooks

Webhook channels POST to any `http://` or `https://` URL with a 10-second
timeout. Redirects are not followed. Network errors, HTTP 429 and 5xx are
retried twice (after 1 and 4 seconds); other 4xx responses are not retried.
The **Payload** setting picks the body:

| Payload | Body |
| --- | --- |
| JSON (`sbox-ns.alert/v1`) | the documented JSON below, optionally signed |
| Slack-compatible | `{"text": "..."}` (Slack, Mattermost, Rocket.Chat, Discord's `/slack` endpoint) |
| Microsoft Teams | a `MessageCard` with title and text (incoming webhook connectors) |
| ntfy | plain-text body with `Title`, `Priority` and `Tags` headers; the URL is the topic, e.g. `https://ntfy.sh/my-topic` |
| Gotify | `{"title", "message", "priority"}`; put the application token in the URL: `https://gotify.example/message?token=...` |

### JSON payload, version 1

```json
{
  "schema": "sbox-ns.alert/v1",
  "kind": "rule",
  "severity": "warning",
  "state": "firing",
  "title": "Firing: CPU high",
  "message": "CPU use > 90 % for 2 min. Current value: 97.2 %.",
  "timestamp": "2026-10-12T08:00:00.0000000+00:00",
  "projectId": null,
  "ruleId": "r1a2b3c4d5e6f",
  "correlationId": null,
  "server": { "version": "0.5.1" },
  "fields": [ { "name": "Rule", "value": "CPU high" } ]
}
```

- `kind`: `error` (captured error), `rule` or `test`.
- `severity`: `info`, `warning`, `error` or `critical`.
- `state`: `firing` or `resolved` for rules, otherwise `null`.
- `projectId` and `correlationId` are set for captured errors.
- New fields may be added within `v1`; receivers must ignore unknown fields.
  A breaking change gets a new `schema` value.

### Signature

With a signing secret, each JSON request carries:

```
X-Sbox-Ns-Timestamp: 1791770082
X-Sbox-Ns-Signature: sha256=<hex HMAC-SHA256 of "<timestamp>.<raw body>" keyed with the secret>
```

Recompute the HMAC over the timestamp, a dot and the exact body bytes,
compare in constant time, and reject old timestamps (for example older than
5 minutes) to stop replays. For example in Python:

```python
import hashlib, hmac, time
def verify(secret: bytes, timestamp: str, body: bytes, signature: str) -> bool:
    expected = "sha256=" + hmac.new(secret, timestamp.encode() + b"." + body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, signature) and abs(time.time() - int(timestamp)) < 300
```

## Checking the configuration

```sh
sbox-ns config validate                 # parse + combination checks with file:line
sbox-ns config show                     # merged effective config, secrets redacted
sbox-ns config set alerts.discord.enabled true
sbox-ns config get alerts.smtp.host
```

`config set`/`get` work for every `alerts.*` key through the generic
mechanism. To verify delivery, press **Send test** next to the channel in
**Alerts**; the result appears next to the channel and in the alert history.
