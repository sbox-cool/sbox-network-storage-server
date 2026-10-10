# Operator alerting

sbox-ns can notify operators when it captures errors: Discord channel
messages via an incoming webhook, and plain-text email via SMTP. Both are
**disabled by default**, best-effort (a failed send is logged and never fails
the request that triggered it), and configured entirely through `alerts.toml`,
`conf.d/` overrides, or `NS_` environment variables.

## What triggers an alert

Every error that reaches the shared error pipeline fans out to the log plus
any configured channel:

- Unhandled request exceptions captured for `/admin/errors` (5xx, plus 503
  database outages).
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

Each alert carries the correlation ID, route, classification, project, and
timestamp so it can be matched against `/admin/errors` entries and logs.

### Limits

Anyone with a game's public key can cause some alerts, so Discord and email
are limited for every alert source together. The log still records every
error.

- An alert of a kind already sent in the last 10 minutes is not sent again.
  The kind is the project, operation and code plus the collection (storage
  errors) or the route (request errors); the player does not count. The next
  alert of that kind says how many were held back.
- At most 10 alerts are sent per minute. The next alert sent says how many
  were dropped.

In Discord the message, route and project are shown as code, so text sent by
a game client cannot add links or formatting, and alerts never ping anyone.

## Discord setup

1. In the Discord channel: **Edit Channel → Integrations → Webhooks →
   New Webhook**, copy the webhook URL.
2. Store the URL in a secret file (recommended) or directly in config:

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
folder. The message is a single embed (title, description, route / status /
classification / correlation ID / project fields).

## SMTP setup

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
addresses. Mail is plain text with subject
`[sbox-ns] <Classification> <Method> <Path> (<CorrelationId>)` and a
field-list body; delivery uses a 10-second timeout and STARTTLS when
`use_tls` is true.

## Secrets handling

- `alerts.discord.webhook_url` and `alerts.smtp.password` are flagged
  secret: `sbox-ns config show` prints `"********"` unless `--show-secrets`
  is passed, and the values never appear in logs or alert payloads.
- Prefer `*_file` so secrets live outside the TOML files (same pattern as
  `database.postgres.password_file`). The first line of the file is used.

## Checking the configuration

```sh
sbox-ns config validate                 # parse + combination checks with file:line
sbox-ns config show                     # merged effective config, secrets redacted
sbox-ns config set alerts.discord.enabled true
sbox-ns config get alerts.smtp.host
```

`config set`/`get` work for every `alerts.*` key through the generic
mechanism — no special subcommands exist. There is no dry-run send command;
to verify delivery, enable a channel and trigger a real capture (for
example an invalid request against a test project), then confirm the
correlation ID matches between the Discord/email alert, the server log,
and `/admin/errors`.
