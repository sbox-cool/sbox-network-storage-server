# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Optional owner authenticator enrollment with encrypted confirmed secrets,
  replay-resistant TOTP login and hashed single-use recovery codes; local
  `admin reset-2fa` recovery invalidates owner sessions.
- Owner endpoint CIDR restrictions, `sbox-ns adminpanel disable|enable`, and
  optional operator-configured Turnstile with server-side success, action and
  hostname validation, including explicitly configured sboxns.com tunnel hosts.
- Owner dashboard supports system, neutral dark, light, slate and warm themes,
  plus opt-in visual editing of collection definitions and records alongside
  JSON. Visual mode preserves unknown fields and refuses precision-losing
  numbers, duplicate keys and authoritative sourceText definitions.
- `tls.hsts` sends Strict-Transport-Security on HTTPS responses when TLS is
  enabled. Plain-HTTP installs never send it.
- Endpoint collection scans are capped at 1000 rows per request (`SCAN_TOO_LARGE`
  beyond that) instead of reading unbounded collections on every game call.
- Dedicated public demo mode (`adminpanel.demo_read_only`) with server-enforced
  read-only access and generated sample data in an isolated SQLite database;
  every mutation, owner login/setup/security/export and game execution route is
  denied server-side.
- Authoritative per-project snapshot core (`IAuthoritativeProjectStore`,
  v2 archive with per-table counts and content hashes) for exact lossless
  migration auditing. Live customer cutover remains disabled pending fenced
  drain, full ingress coverage and verified soak evidence.

- Free hosted name with your own IP: `sbox-ns dns enable|status|disable` publishes a
  signed `<name>.nN.sboxns.com` A/AAAA record pointing directly at the server, with a
  Let's Encrypt certificate, automatic public-address updates every 10 minutes, an
  ownership proof endpoint at `/.well-known/sbox-ns/dns-proof/{nonce}`, a `doctor`
  DNS check and a read-only `dns_status` MCP tool. Tunnel and DNS modes are exclusive.
- Opt-in unattended updates: `sbox-ns update --auto [--all-instances]`, run every 15
  minutes by the new `sbox-ns-update.timer` (`sbox-ns service install --auto-update`,
  or install.sh `SBOX_NS_AUTO_UPDATE=1`). Settings `updates.auto_install` (default
  off), `updates.channel` (`stable`/`canary`), `updates.window` (UTC) and
  `updates.min_release_age_hours`. Every instance sharing the binary is backed up,
  migrated, restarted and health-checked (`/health` reports the new version within
  120 s); any failure restores the binary and every database backup and records
  `failed` in `last-update.json`. Exit codes 0/1/2/3/4 are documented.
- Release channels: the release feed is requested with `?channel=`, honours held
  channels, and the new `canary-verify` workflow promotes canary releases to stable
  after a 24 h soak and a passing managed-service parity diff on the official canary.
- Several instances per host: `sbox-ns service install --instance NAME --port PORT`
  with the `sbox-ns@.service` template unit; `rollback --all-instances`; `doctor`
  shows unattended-update settings and each instance's last update.
- `auth.security_signing_key_id` keeps a migrated server's security-config key id.
- `sbox-ns import FILE --verify-only` validates an archive (including archives
  produced outside sbox-ns; contract in docs/export.md) in a throwaway database.
- install.sh `SBOX_NS_CHANNEL` installs the channel's version and sets `updates.channel`.
- Installer signed-DNS opt-in (`SBOX_NS_DNS=1` with explicit Let's Encrypt email
  and terms acceptance) starts the service before ownership proof/registration.
  Installers disclose the registry/Bunny DNS and direct-player-traffic dependency,
  the separate Cloudflare tunnel path, and the absence of reliability guarantees.
- Automatic recovery refuses database/binary restoration if a service cannot
  be stopped; Linux migrations retain service-user ownership. Canary promotion
  refuses held channels and re-checks channel state before promotion.

## [0.3.0] - 2026-10-08

### Added

- Repository `llms.txt` documentation index for coding agents, covering self-hosted
  operation, MCP, client setup and official YAML/library references.
- Owner-console resource authoring for collection schemas, endpoints, workflows,
  queries and game values, using the existing editor compiler and preserving
  source-backed YAML/JSON metadata.
- Owner record create/edit/delete with schema validation, audit logs and atomic
  protected-snapshot conflict checks, including game writes that reuse a version.
- Per-project portable exports and imports between self-hosted SQLite/PostgreSQL
  instances, retaining project IDs without copying instance configuration/secrets.
- Runtime analytics, audit/request logs, error summaries and usage views.
- Real owner-dashboard screenshots in the README.

### Changed

- Reorganized the README around a Linux VPS quickstart with explicit hosted HTTPS
  opt-in, project/key provisioning, public-key game configuration and editor YAML
  sync. Clarified provider-paid hosting costs, privacy defaults, Cloudflare routing,
  preview caveats and the distinction between synced definitions and runtime data.
- Updated client setup examples for HTTPS/public keys and YAML Source, and corrected
  the outdated claim that the self-hosted server has no dashboard.
- Redesigned the owner console with shared navigation, project cards, responsive
  forms and explicit runtime/authoring/operations sections.
- Documented destination secret-key regeneration and player reauthentication
  after an independent-instance project migration.

## [0.2.0] - 2026-10-08

### Added

- Opt-in anonymous usage statistics, off by default: `sbox-ns telemetry status|enable|disable|preview`,
  `telemetry.enabled` / `telemetry.endpoint` settings, a one-time `[y/N]` question in interactive
  `setup`, `SBOX_NS_TELEMETRY=1` in the installers, a `doctor` line and a read-only `telemetry_status`
  MCP tool. When enabled the server sends a random statistics-only ID, version, OS, architecture,
  container/database/tunnel flags, uptime hours and project, player and 30-day active player counts
  about 10 minutes after start and then daily. No IPs, emails, project IDs, keys or game data are sent.

## [0.1.0] - 2026-10-08

### Added

- Self-hostable Network Storage server extracted from the sbox.cool production code,
  compatible with the existing s&box client library by setting its base URL.
- `sbox-ns` single binary acting as server and CLI (`start`, `setup`, `config`,
  `db`, `project`, `key`, `service`, `logs`, `update`, `rollback`, `version`,
  `doctor`).
- `GET /health` and `GET /v3/server-info` endpoints.
- SQLite (default) and PostgreSQL storage, with PostgreSQL tables in a configurable schema.
- TOML configuration folder with `conf.d` overrides and `NS_` environment variables.
- Optional HTTPS with your own PEM certificate or automatic Let's Encrypt (ACME).
- Install scripts for Linux, macOS and Windows, systemd unit, Docker image.
- Release update notices (no automatic updates).
- Persisted single-owner account with ASP.NET Identity password hashing, cookie
  sessions, CSRF-protected local setup/login/logout and invalidation on password reset.
- `admin create|reset-password` commands and owner creation during `setup`,
  with hidden password input, password-file and environment support.
- Standalone Razor project/key/settings dashboard and embedded external stylesheet;
  update banners emphasize security releases. Full managed dashboard and managed
  project export/import parity remain explicitly unestablished.
- Versioned HTTP replay corpus and differential parity harness, CLI-driven SQLite
  and PostgreSQL smoke gates, and release blocking until an approved stable
  response recording reports zero unlisted differences.
- Idempotent `quickstart` provisioning, JSON output and `SBOX_NS_PROJECT` /
  `SBOX_NS_PUBLIC_URL` installer options for non-interactive VPS setup.
- Allowlisted stdio MCP management tools for local agents or SSH access.
- Portable config and driver-neutral database export/import, including a
  CSRF-protected dashboard download and explicit secret inclusion controls.
- Short-lived, single-use owner login links and a collection/record data browser.
- Optional self-certifying `sboxns.com` HTTPS names, signed registry ownership,
  checksum-pinned cloudflared downloads and supervised connector status.
- `tunnel enable|status|disable`, MCP tunnel status/enable, and
  `SBOX_NS_TUNNEL=1` installer opt-in; previous listener and TLS settings are restored on disable.
- Optional `register --email` double-opt-in security/update notices,
  single-use expiring confirmation and email/CLI unsubscribe.

### Fixed

- Use `SameSite=Lax` for owner sessions so links from alerts retain sign-in;
  antiforgery cookies remain Strict and unsafe dashboard requests require tokens.
- Reject incomplete manifest-declared archive data before restoring it, and
  refuse config destinations that traverse filesystem symlinks.
- Return JSON-RPC errors for malformed MCP field types without terminating
  the stdio session or dropping subsequent requests.
- Restore production response compression ordering so usage metering records
  the compressed bytes actually transferred to clients.
- Preserve inline comments and multiline-string boundaries when changing TOML settings.
- Stop the installed service before taking an update recovery backup; abort on
  failed service control and restore the previous binary and data on update failure.
- Refuse in-place updates through the shared `dotnet` host.
- Serialize the package-sync handler's returned response instead of discarding it.
- Store newly created public API keys under their raw client-facing key, matching
  runtime resolution and management toggle/remove lookups; secret-key hashing
  and masked management responses remain unchanged.
- Read collection ledger history from the authoritative tracked-delta projection
  instead of obsolete workspace ledger files; saved records without tracked
  changes return an empty ledger.
- Reject oversized direct document writes with HTTP 413 and `PAYLOAD_TOO_LARGE`
  before storage mutation, using the configured store's UTF-8 payload limit.
- Serve management query lists through the read handler using the existing
  authoritative metadata resource mapping instead of the mutation handler.
- Correct the HTTP corpus's batch-sync validation assertion to check the failed
  section's `VALIDATION_FAILED` error and false success flags, matching production.
- Identify unimplemented upstream revision initialization as an explicit
  smoke/release prerequisite without relaxing its success assertions or the
  failure gate (see CONTRIBUTING for the full blocked list).
- Enforce project player-authentication policy on public endpoint execution:
  required-auth projects reject forged `steamId` claims before any step runs,
  supported Facepunch/session credentials are validated and bound to the
  executor identity, and secret-key dedicated-server delegation is preserved.
  Auth-disabled projects ignore s&box tokens like the legacy runtime.
- Enforce per-resource API-key scopes on management reads, mutations, sync,
  preflight and auto-test; restricted and read-only keys can no longer read
  or overwrite unauthorized categories. Package sync requires read/write on
  all management scopes.
- Filter private-collection constants and tables from public game-values
  responses; scoped secret keys retain access. Accept parsed-object and
  string `definition_json` so public values are actually served.
- Supply the game-values context to live endpoint execution so `source:
  "values"` lookups and `values.*` expressions resolve; a values-load failure
  is a reported error, not a silent empty map.
- Map genuinely missing endpoint definitions to `404 ENDPOINT_NOT_FOUND`
  (matching the slug-read contract); present-but-unsupported definitions stay
  reported `501`.
- Grant query management (`rwx`) to default secret keys, matching the
  full-access meaning of defaults; restricted keys remain scoped.
- Register the ported management mutation routes (`PUT settings`, `PUT
  tests`, `DELETE keys`, `POST source-upgrade`/`run-tests`/`test-endpoint`/
  `suggest-tests`); unported logic answers the described dry-run contract.
- Validate collection/record identifiers at document ingress and answer `404`
  instead of `500 STORAGE_ERROR`; oversized endpoint record writes are
  rejected `413` pre-commit with zero mutation while valid multi-record
  batches still succeed.
- Return `200` with an empty ledger for known players (existing record or
  analytics profile) without tracked deltas; unknown keys stay `404`.
- Auth-disabled projects ignore unverifiable s&box tokens exactly like the
  legacy runtime; required-auth projects still reject forged identity, and
  presented auth-session tokens are always validated.
- Align oversized-payload corpus expectations with the enforced 64 KiB store
  limit (`413`), and the `/api/storage` global-list expectation with the
  reference route inventory (`404`; `v1`/`v3` lists are the supported
  surfaces).
- Implement the game-client revision handshake (`POST
  /v3/manage/{projectId}/revision-init`, per `sbox-cool/sbox-network-storage`
  `NetworkStorageRevisionInit`): public-key route comparing the client
  revision against the synced game package and reporting outdated status.
- Map workflow `returns:` blocks into step results (Bun parity); without a
  returns block the full sub-context is returned as before.
- Answer unknown management mutations with `404 NOT_FOUND` instead of 501.
- Host proxies act for the `x-on-behalf-of` player (secret keys and auth-disabled
  projects, as in the legacy runtime) instead of overwriting the host's own record.
- Key the leaderboard and tracked-field projections by the written player record,
  not the requester.
- Answer malformed sync/preflight JSON with the client-contract error shape.

[Unreleased]: https://github.com/sbox-cool/sbox-network-storage-server/compare/v0.3.0...main
[0.3.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.3.0
[0.2.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.2.0
[0.1.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.1.0
