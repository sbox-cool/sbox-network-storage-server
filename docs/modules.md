# Modules

Installable server modules: starter kits (economy, progression, inventory, leaderboards),
integrity monitoring (server-side anticheat) and player insights (analytics). Modules are
**declarative bundles compiled into the binary**: a manifest plus authored definitions. They
never download code at runtime and never run outside code.

## Lifecycle

1. **Catalog.** The dashboard Modules page lists every embedded module with its version,
   description, requested permissions, the definitions it adds, and its install state. Works
   offline; `modules.json` absent means nothing is installed.
2. **Preview.** Installing first shows every definition and permission. Name conflicts with
   existing definitions are listed and the install refuses until they are resolved.
3. **Install.** Definitions are written into the live definition tables with a `module`
   ownership field under the module's `id_` prefix (for example `economy_earn`). The install
   is audited and shows a toast. Kit endpoints are public-key callable, so they work in
   `player-hosted` projects.
4. **Disable.** Hooks stop, panels hide, and the module's endpoints answer `ENDPOINT_DISABLED`.
   Data and settings are kept; re-enabling restores everything without data loss.
5. **Uninstall.** Requires typing the module id. Keep data converts definitions to ordinary
   project definitions (ownership stripped, names and records kept); delete data also removes
   the module's definitions, its records (ledger entries included) and its game-values items.
   Definitions outside the module's namespace are never touched.
6. **Upgrade.** A catalog upgrade without new permissions applies on next install. A version
   that adds permissions is held: the installed version keeps running until the owner approves
   the new permissions on the Modules page. Applying an upgrade, automatic or approved, first
   checks for conflicts with definitions the module does not own, including retired names from
   the installed version that the owner has replaced. A conflict refuses the upgrade and leaves
   the installed definitions and `modules.json` unchanged. Deleting old definitions and writing
   their replacements share one transaction, so a failed write rolls back the whole replacement.
   Settings carry over: an explicit value submitted
   with the upgrade wins, then the installed value, then the catalog default. Settings the new
   version no longer declares are dropped. Values are not type-checked on save; consumers fall
   back to their default when a value does not parse. Explicit values submitted with a held
   upgrade are not saved until the owner approves; change them in the settings form first.

## Sync preservation

A package sync or editor push never deletes, overwrites or renames module-owned definitions:
batch pushes report `kept_module_owned` per item, single PATCH keeps them, full replacements
skip them, deletes answer `403 MODULE_MANAGED`, and full game-values replaces merge owned
items back. Staged promotion skips owned rows too. Snapshots, project export and import carry
module-owned definitions verbatim, so old revisions keep working with their kits.

## Permissions

Fixed list: `read-player-data`, `write-player-data`, `read-analytics`, `flag-players`,
`block-requests`, `send-alerts`. Module background actions only run when granted; a revoked
permission degrades an automatic action to flag-only and logs it.

## Hooks and panels

Fixed hooks (all dispatched off the request path through a bounded channel; a full channel
drops events and counts them instead of delaying requests): `endpoint.executed`,
`ledger.delta`, `request.rejected`, `revision.reported`.

Fixed panels (Razor partials under `Views/Owner/Modules/`): `review-queue` (integrity),
`player-overview`, `player-profile`, `action-log` (insights).

## Manifest reference

Embedded resources `SboxNetworkStorage.Infrastructure.Modules.<id>.module.yml` and
`<id>.definitions.yml`:

```yaml
id: economy            # ^[a-z][a-z0-9-]{0,63}$, matches the file name
version: 1.0.0         # semantic version
minServerVersion: 0.6.0
name: Economy starter kit
description: ...
permissions: []        # subset of the fixed list
hooks: []              # subset of the fixed list
panels: []             # subset of the fixed list
settings:             # typed settings with defaults
  - key: starting_balance
    type: integer
    default: "0"
    description: ...
```

`definitions.yml` is a list of `{kind, source}` where kind is
`collection`, `endpoint`, `workflow`, `query` or `game-values` and source is a definition in
the existing authoring format. Names must already carry the `id_` prefix and endpoints and
queries must not require a secret key; the installer stamps `module: <id>` and refuses
anything else. An unknown hook, permission or panel marks only that module unavailable with
the validation error; other modules load normally.

## Integrity monitoring

Detectors on data-plane hooks with per-project settings (Modules page): value delta (a
watched ledger/numeric field rising more than `value_delta_per_minute` per player per minute),
call rate (endpoint calls per player per minute above `max_calls_per_minute`), revision
tampering (a reported revision id that was never published), repeated anti-rollback conflicts
and repeated direct-write attempts on endpoint-only collections (5 in 10 minutes by default).
Secret-key (dedicated-server) traffic is exempt.

Detection only observes: except for an explicit player block, the module never alters, delays
or rejects requests. Automatic action per detector: `flag-only` (default), `flag-and-alert`
(through the existing operator alert channels and suppression) or `flag-and-block-writes`
for `block_duration_minutes`. Flags record player, detector, severity, time, evidence and
state (`open`, `dismissed`, `confirmed`); the review queue groups them by player and every
decision is audited.

Response model (`response_mode`, default `off`): `off` takes no response-model path. `dry-run`
evaluates each eligible firing (identity not `anonymous` or blank) against the player's recent
counted firings of the same detector and stores the result as `wouldApply` evidence on the flag:
`stage` (`flag`, `rate-limit`, `shadow`, `block-writes`, `block-all`), `detector`, `hookSource`,
`windowSeconds`, `priorCount`, `approximate` and `schemaVersion`, at most 1024 bytes. Any other
`response_mode` value is treated as `off`. `response_lookback_hours` (default 24, accepted 1 to 168)
bounds the history counted. Flags in state `dismissed` or `reviewed-legitimate` are not counted;
a repeat within the detector's window of the previous flag is suppressed and not counted.

Dry-run records `wouldApply` only: zero automated blocks, rate limits, overlays or alerts come from
the response model, and request responses are unchanged. The existing `flag-and-block-writes`
action still blocks identities that are not real players until that prerequisite lands. History is
read per firing and is not coordinated across server processes, so concurrent firings may not see
each other. Evidence is marked `approximate: true` when the 16-row read ends before the lookback
boundary and before four counted firings; the stage is then a lower bound. The value-delta,
revision and rejection-reason detectors are not live.

Calibration gate for any future enforcement (not enabled by this change): 30 days of dry-run
data, reviewed-decision rate at most 1%, at least 300 reviewed legitimate decisions, at least
1000 players, and zero response-model blocks in that window. The latency figures below are not
yet measured.

Player blocks (writes or all, optional expiry and public reason) return `403 PLAYER_BLOCKED`.
Unblocking takes effect immediately.

## Player insights

Overview (daily active players, new players, sessions, median session length for 1/7/30 days;
day-1 and day-7 return rates) from sessions the server already records. Panels say
"Not enough data yet" instead of showing placeholder figures. Player search (Steam ID or
display name) and profiles (first/last seen, revision, sessions, records, ledger, flags,
block state). The action log records endpoint calls (endpoint, revision, outcome, changed
fields, time) with configurable retention (default 14 days, 1-90), batched writes that never
block or fail game requests, drop counting, and optional per-endpoint body capture. Overview
series and filtered logs export as CSV.

## Latency overhead

Detection adds no more than 2 ms at p95 (measured with the recipe below). Recipe: install the
four starter kits plus integrity with defaults in a scratch project, run the scripted normal
play session (`scripts/` smoke against earn/spend/grant-xp/grant/consume/craft/submit), assert
zero flags, and compare p95 endpoint latency with the integrity module enabled versus
uninstalled. Record both numbers here when measured:

- p95 without integrity: _to be measured_
- p95 with integrity (flag-only defaults): _to be measured_
- flags during normal play: _to be measured (must be 0)_
