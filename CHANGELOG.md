# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.10] - 2026-11-05

### Fixed

- `sbox-ns export` keeps every stored record, ledger entry and global record.
  It used to walk only collections with a definition, so heartbeat
  `player-stats`, data in collections whose definition was removed, and ledger
  entries without a record row were left out of exports and server moves.

## [0.5.9] - 2026-11-05

### Fixed

- Large collection, endpoint, workflow and query definitions are no longer
  refused by the 64 KiB record payload limit. Definitions now have their own
  limit (`database.max_definition_bytes`, default 4 MiB), so projects moved
  from hosted sboxcool.com with large definitions import and sync again.

### Added

- `database.max_payload_bytes` (default 64 KiB) and
  `database.max_definition_bytes` (default 4 MiB) configure the record and
  definition size limits.

## [0.5.8] - 2026-11-03

### Added

- `sbox-ns security rotate-signing-key` announces a signing-key rotation
  signed by the previous key. Explicitly pinned clients still require a repin;
  the published library r427072 does not follow announced rotations.
- The `integrate-analytics` MCP prompt helps plan and integrate game analytics.

### Fixed

- Record writes accept `x-ns-write-mode: replace` or `ops`, allowing documents
  with a top-level `ops` field to be stored without interpreting it as an
  operation. Requests without the header retain the existing heuristic; older
  servers ignore the header.
- `ENCRYPTED_REQUEST_STALE` refusals include the server time for clock correction.
- YAML sync preflight, owner editing and auto-test preserve case-distinct nested
  keys such as `Foo` and `foo` instead of failing or losing a value.
- Updated Microsoft runtime packages, the S3 SDK and the test-only JSON schema
  validator.

### Changed

- Clarified endpoint-only encryption coverage and corrected documentation about
  non-2xx response bodies in s&box.

## [0.5.2] - 2026-10-12

### Fixed

- `sbox-ns service start` and `service restart` return only once the server
  answers `/health` (up to 60 seconds). The installer's `SBOX_NS_DNS=1` mode
  runs `dns enable` right after starting the service; the registry's ownership
  proof could arrive before the server listened and the install failed.

## [0.5.1] - 2026-10-12

### Upgrade notes

- This release adds an additive database migration (schema v6: three tables
  for integrity flags, player blocks and an action log). Take a
  `sbox-ns db backup` before updating. Existing game requests, responses and
  save behavior are unchanged; every new behavior below is off until an owner
  turns it on.

### Fixed

- `sbox-ns dns enable` and the installer's `SBOX_NS_DNS=1` mode no longer fail
  the ownership proof on dual-stack hosts. A name is published only for the
  address families the server listens on, and the other family is looked up
  over a connection forced to it, so an IPv4-only listener registers IPv4 even
  when the host prefers IPv6, and `[::]` listeners register both addresses.

### Added

- Owner **Server** page with live host stats (CPU, memory, disk, network,
  request rate, latency), alert rules, and console-managed email, Discord and
  webhook notification channels with a shared throttle. Optional provider
  adapters for Cloudflare Email and an sboxns.com client contract; see
  `docs/monitoring.md` and `docs/alerting.md`.
- Game version policy: retained revision snapshots, pinning of older
  revisions inside their support window, `_revisionStatus` on responses and
  an optional, off-by-default server-side enforcement switch per project.
  See `docs/version-policy.md`.
- Optional module kits (economy, progression, inventory, leaderboards),
  integrity monitoring and player insights, installed per project. See
  `docs/modules.md`.
- The read-only demo shows generated sample data on more pages, including
  monitoring.

### Changed

- Use the Demo-green, interlocking `ns` icon for the owner console, browser
  favicon, reusable app assets and repository branding. Keep product labels
  compact in the UI; describe the self-hosted s&box game backend in supporting
  documentation.
- Default dark and light themes use Demo green accents (`#10b981` on dark,
  `#15803d` on light) on neutral backgrounds.
  Alternate themes and semantic success/error colours remain available.
- Serve the embedded favicon with other owner assets so it also works in the
  read-only demo. Owner pages allow same-origin images without permitting
  third-party image sources.

## [0.5.0] - 2026-10-12

### Upgrade notes

- On Linux, operator configuration stays in `/etc/sbox-ns` while generated
  secrets, managed overlays and connector binaries move to the data
  directory's `state/` folder. The installer and `sbox-ns update --auto` run
  the migration on upgrade; manual installs run `sudo sbox-ns layout
  migrate`. `sbox-ns rollback` reverts the layout before restoring an older
  binary, and `layout migrate --revert` moves the files back by hand.
- The server targets .NET 10, and the container image runs as non-root UID
  1654 on the chiseled runtime.
- The direct document API answers `403 ENDPOINT_ONLY` to public game keys
  unless the collection declares `accessMode: public`. Games that write
  documents directly from the client must add it.
- Release updates require a pinned ECDSA P-256 signature on the checksum
  manifest. `updates.github_repo` and `updates.feed_url` are no longer
  settings; the release sources are compiled into the binary.
- Schema migrations run on start. Take a `sbox-ns db backup` before updating
  to a release that needs a database migration.

### Security

- Updates and installers require a pinned ECDSA P-256 signature on the
  release checksum manifest. Unattended updates use compiled release sources;
  `updates.github_repo` and `updates.feed_url` are ignored.
- Linux operator configuration is root-owned and read-only to the service.
  Runtime secrets, managed overlays and connector binaries move to the data
  directory's `state/` folder through `sbox-ns layout migrate`; rollback
  reverts this layout when restoring an older binary.
- HTTP requests have route-specific body limits and per-client rate limits.
  Management and heartbeat authentication runs before reading request bodies;
  unauthenticated requests do not generate usage rows.
- The direct document API (`/v3/storage/{projectId}/{collection}/{key}`)
  enforces the collection access model for public game keys: `accessMode:
  endpoint` (also the default when unset) answers `403 ENDPOINT_ONLY`,
  deletes need `allowRecordDelete` (`403 RECORD_DELETE_DISABLED`), and
  projects that require s&box auth verify the player and only let them change
  their own documents. Public keys could previously read, overwrite and
  delete any document. Secret keys are unchanged. Collections that game
  clients write directly must declare `accessMode: public`.
- Owner sign-in over plain HTTP from another machine is refused; use an SSH
  port forward, `sbox-ns tunnel enable` or TLS, or set
  `adminpanel.allow_insecure_http`. Logout ends every session, and exporting
  with secrets asks for the password (and the authenticator code when set).
- `config get` redacts secret settings unless `--show-secrets` is passed, and
  MCP `config_set` refuses secret keys.
- API keys only resolve against rows of the matching key type, and project
  archives reject key rows the server would never write.

### Changed

- The runtime targets .NET 10 with a non-root chiseled container, signed
  release manifests, CycloneDX SBOMs and release provenance verification.
- Cached project metadata, bounded batched analytics, atomic endpoint writes
  and SQLite indexes; request and error logs retain events that share a
  timestamp. Analytics retention defaults to 90 days.
- Built-in legacy player repair and leaderboard projections are opt-in for
  new projects. Existing projects retain them after migration; the dashboard
  exposes the `legacyPlayerProjections` setting.
- Unattended update timers run hourly, with up to 15 minutes of randomized
  delay.
- Hosted-service `X-Sboxcool-*` debug response headers are no longer sent.
- Public-key query calls are served from a shared result cache that writes
  and definition changes invalidate; only secret keys can force a live run.
- Operator alerts are deduplicated and rate-limited across all sources, with
  a held-back count.
- The release-feed library overlay only tightens reported status and never
  refuses a request. Requests without a parseable library version are never
  refused by any rule.
- A database that is newer only through additive migrations now runs instead
  of being refused; a breaking gap is still refused with the existing error.
- The MCP `data_records` argument is now `keyPrefix` and matches the start of
  the key.
- Owner console themes are reworked: Deep emerald is the default dark theme,
  Mint and orchid the alternate dark theme, Paper the default light theme,
  and system follows the OS setting. Slate and Warm remain.

### Added

- Library version detection on every request with an embedded compatibility
  matrix. Responses carry `x-ns-server-version` and `x-ns-library-status`
  (plus latest and sunset headers when relevant), `/v3/server-info` gains a
  `compat` block, and the request log records library version, client type
  and engine build.
- Client-version analytics with a dashboard Clients page, `sbox-ns project
  clients` and a read-only `project_clients` MCP tool, grouping library,
  package and game revisions with request counts and compatibility impact.
- Optional per-project library minimum: older clients are refused with HTTP
  426 `LIBRARY_UNSUPPORTED`, and matrix-blocked libraries with
  `LIBRARY_BLOCKED`. Clients that report no version are never blocked.
- Rollback-safe migrations: each migration is tagged additive or breaking,
  `sbox-ns rollback --keep-data` swaps the binary back without touching the
  database when safe, and `sbox-ns update --version X.Y.Z --allow-downgrade`
  installs a pinned older release through the same check.
- Dashboard project hub with single-link cards, create, import and export
  dialogs, a `?q=` filter and an empty state.
- Hosting profiles (`player-hosted`, `dedicated`, `hybrid`) through project
  create, settings and `project create --hosting`, plus an advisory authority
  check as an overview card, `sbox-ns project authority`, `doctor --project`
  and a read-only MCP tool. It never runs on the request path.
- Staged publishing: endpoint and collection pushes can target the next
  revision, staged entries list with `revisionTarget=next`, test runners
  resolve staged definitions first, and package sync promotes them. Sync
  pushes support explicit endpoint replacement.
- MCP development loop: `sbox-ns dev <tool>` runs definition, testing,
  debugging and gated write tools with JSON over stdin, plus embedded guides,
  prompts and a Claude Code plugin. Writes stay opt-in behind
  `mcp.allow_writes`, `mcp.allow_data_writes` and `mcp.allow_destructive`.
- Real data on the dashboard Logs and Errors tabs, with per-project budgets
  and error-code explanations, and a Connect your game card with a filled-in
  Configure line and a setup checklist. New dashboard projects get a public
  game key by default.
- Compact owner console with a guided block editor and integration examples;
  resource list pages with stats and quick preview, and dedicated resource
  pages with Overview, Edit and Code tabs. Old `?id=` and `?draft=` links
  redirect.
- Project colour with generated tile icons and a fluid left-aligned layout.
- Paged data browser with key-prefix search, Find player across per-player
  collections, game-call snippets in the resource editor, and a Use with a
  coding agent card on All projects.
- `sbox-ns doctor --project` and the authority check report records damaged
  by the 0.4.0 update-ops bug: documents holding only an `ops` field. Restore
  a backup or have the player save again; the data is not recoverable.
- Sync preflight reports steps that fail at run time as warnings with code,
  message, resource and path. Pushes still store what they are given.
- Dashboard resource definitions are YAML-first with guided endpoint and
  collection builders: the editor shows YAML, saves keep YAML source text,
  and raw JSON is deprecated but still accepted. Record payloads stay JSON.
- `tls.hsts` sends Strict-Transport-Security on HTTPS responses when TLS is
  enabled. Plain-HTTP installs never send it.
- Endpoint collection scans are capped at 1000 rows per request
  (`SCAN_TOO_LARGE` beyond that) instead of reading unbounded collections on
  every game call.
- Visual editor guards (precision loss, duplicate keys, sourceText) are
  covered by `node --test tests/js/`, wired into CI.
- The s&box client E2E harness (`tools/sbox-client-e2e/run.sh`) runs the real
  client library against a local server inside a headless s&box engine, plus
  a dedicated-server project.

### Fixed

- Player ledger reads include analytics queued by earlier writes, so a record
  or entry written a moment ago is visible to the next read.
- A body over its limit on a known route is read and discarded when its
  declared length is at most 4 MiB, so the client receives the `413` response
  instead of a connection reset. Larger declared bodies are rejected without
  being read.
- `revision-init` leaves out unknown revisions instead of sending `null`. The
  s&box client library failed its startup handshake on projects without a
  synced game package.
- `413` and `429` error bodies carry `message` next to `detail`, so the
  editor sync tool shows the reason instead of only the error code.
- Management validate reports the public key the library sends instead of
  always answering Not provided.
- Auto-test resolves `{{values.*}}` like test-endpoint and live calls.
- The CLI prints a clean error when the database schema is newer or older
  than the binary supports instead of an unhandled exception.
- Rate-limit rules stored in the 0.4.0 `{rules:[...]}` wrapper are read again
  instead of returning empty.
- An unreadable owner authenticator secret explains itself and points to
  `sbox-ns admin reset-2fa` instead of failing.

## [0.4.0] - 2026-10-09

### Added

- Optional owner authenticator enrollment with encrypted confirmed secrets,
  replay-resistant TOTP login and hashed single-use recovery codes; local
  `admin reset-2fa` recovery invalidates owner sessions.
- Owner endpoint CIDR restrictions, `sbox-ns adminpanel disable|enable`, and
  optional operator-configured Turnstile with server-side success, action and
  hostname validation, including explicitly configured sboxns.com tunnel hosts.
- Owner dashboard supports system, neutral dark, light, slate and warm themes,
  plus an opt-in visual editor for record payloads alongside raw editing.
  Visual mode preserves unknown fields and refuses precision-losing
  numbers and duplicate keys.
- Dedicated public demo mode (`adminpanel.demo_read_only`) with server-enforced
  read-only access and generated sample data in an isolated SQLite database;
  every mutation, owner login/setup/security/export and game execution route is
  denied server-side.
- Authoritative per-project snapshot core (`IAuthoritativeProjectStore`,
  v2 archive with per-table counts and content hashes) for exact lossless
  migration auditing. Live customer cutover remains disabled pending fenced
  drain, full ingress coverage and verified soak evidence.
- Free hosted name with your own IP: `sbox-ns dns enable|status|disable`
  publishes a signed `<name>.nN.sboxns.com` A/AAAA record pointing directly at
  the server, with a Let's Encrypt certificate, automatic public-address
  updates every 10 minutes, an ownership proof endpoint at
  `/.well-known/sbox-ns/dns-proof/{nonce}`, a `doctor` DNS check and a
  read-only `dns_status` MCP tool. Tunnel and DNS modes are exclusive.
- Opt-in unattended updates: `sbox-ns update --auto [--all-instances]`, run
  every 15 minutes by the new `sbox-ns-update.timer` (`sbox-ns service install
  --auto-update`, or install.sh `SBOX_NS_AUTO_UPDATE=1`). Settings
  `updates.auto_install` (default off), `updates.channel` (`stable`/`canary`),
  `updates.window` (UTC) and `updates.min_release_age_hours`. Every instance
  sharing the binary is backed up, migrated, restarted and health-checked
  (`/health` reports the new version within 120 s); any failure restores the
  binary and every database backup and records `failed` in `last-update.json`.
  Exit codes 0/1/2/3/4 are documented.
- Release channels: the release feed is requested with `?channel=`, honours
  held channels, and the new `canary-verify` workflow promotes canary releases
  to stable after a 24 h soak and a passing managed-service parity diff on the
  official canary.
- Several instances per host: `sbox-ns service install --instance NAME --port
  PORT` with the `sbox-ns@.service` template unit; `rollback
  --all-instances`; `doctor` shows unattended-update settings and each
  instance's last update.
- `auth.security_signing_key_id` keeps a migrated server's security-config key
  id.
- `sbox-ns import FILE --verify-only` validates an archive (including archives
  produced outside sbox-ns; contract in docs/export.md) in a throwaway database.
- install.sh `SBOX_NS_CHANNEL` installs the channel's version and sets
  `updates.channel`.
- Installer signed-DNS opt-in (`SBOX_NS_DNS=1` with explicit Let's Encrypt
  email and terms acceptance) starts the service before ownership
  proof/registration. Installers disclose the registry/Bunny DNS and
  direct-player-traffic dependency, the separate Cloudflare tunnel path, and
  the absence of reliability guarantees.
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

[Unreleased]: https://github.com/sbox-cool/sbox-network-storage-server/compare/v0.5.8...main
[0.5.8]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.5.8
[0.5.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.5.0
[0.4.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.4.0
[0.3.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.3.0
[0.2.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.2.0
[0.1.0]: https://github.com/sbox-cool/sbox-network-storage-server/releases/tag/v0.1.0
