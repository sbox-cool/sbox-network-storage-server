# Game values

Game values are the shared numbers your game and your server both read: tuning
constants, named groups such as `combat`, and typed tables such as `weapons`.
You author them once. The editor Sync Tool or the owner console stores them on
your server, the game reads them with one call, endpoints read the same numbers
when they check a claim, and every published build keeps the values it shipped
with.

This page covers authoring, reading them from the game, using them in
endpoints, how they follow published revisions, and a worked example that
checks a combat result on the server. For the owner console screens, see
[admin panel guide](admin-panel.md#authoring-and-runtime-diagnostics).

## What a game value looks like

A project has two kinds of shared data:

- **Groups** are named sets of key and value pairs. `combat` can hold
  `zombie_hp_per_level: 20`. Endpoints read a group value as
  `{{values.combat.zombie_hp_per_level}}`.
- **Tables** are lists of rows with typed columns. Each column is `string`,
  `number` or `boolean`. A `weapons` table can hold one row per weapon. Endpoints
  find a row with a `lookup` or `lookup_many` step that uses `source: values`.

Game code gets both in one response. The game cannot write game values.

## Authoring

There are three ways to author game values. The owner console and the
management API are the verified paths on release 0.5.4. The current source
also compiles Sync Tool YAML `game_values` collections; release 0.5.4 does
not include that fix. See
[#107](https://github.com/sbox-cool/sbox-network-storage-server/issues/107)
for release tracking and the editor-test hold.

### In the game project with the Sync Tool

The editor Sync Tool reads YAML source from the game project and publishes
package revisions (**Push Staged** or **Push Live**, below). In the current
server source, YAML `game_values` collections are compiled when read, without
rewriting the stored source or pinned snapshots. On release 0.5.4 the source
remains uncompiled and its values are not returned by `GET /v3/values` or
`{{values.*}}`.
On release 0.5.4, use the owner console or
`PUT /v3/manage/{projectId}/game-values` (secret key, `items` format below).

### In the owner console

Open the project and then **Game values**. The page has **Guided editor**,
**Raw source** and **Examples** tabs, plus **Check definition** and
**Save live definition**. Check definition validates the text without saving.
The console stores a list of `items`, where each item is a group or a table:

```yaml
items:
  - id: combat
    type: group
    name: Combat rules
    entries:
      zombie_hp_per_level: 20
      max_zombie_level: 50
  - id: weapons
    type: table
    name: Weapons
    columns:
      - key: weapon_id
        type: string
      - key: damage
        type: number
    rows:
      - weapon_id: pistol
        damage: 15
```

Each `id` can be used in endpoint expressions. Console saves are stored parsed,
so comments and formatting from the source text are not kept.

Within one project, a collection definition wins over a console item with the
same id. The server only fills in items that no collection defines
(`GameValuesHandler.cs:352-383`).

### Over MCP

Coding agents can read and check game values with the `sbox-ns` MCP tools.
Saving needs the operator to set `mcp.allow_writes`. See the
[MCP guide](mcp.md).

## Reading them in the game

The library loads every group and table in one request:

```csharp
var gv = (await NetworkStorage.GetGameValues()).Value;
var combat = gv.GetProperty( "groups" ).GetProperty( "combat" ).GetProperty( "values" );
int zombieHp = combat.Int( "zombie_hp_per_level" ) * zombieLevel; // show it before the fight
var weapons = gv.GetProperty( "tables" ).GetProperty( "weapons" )
    .ReadList( "rows", r => (Id: r.Str( "weapon_id" ), Damage: r.Int( "damage" ), Model: r.Str( "model" )) );
```

`GetGameValues()` calls `GET /v3/values/{projectId}` and returns `null` when the
request fails (`NetworkStorageGameValues.cs:14`). The response has this shape:

```json
{
  "version": 3,
  "updatedAt": "2026-10-01T12:00:00Z",
  "groups": {
    "combat": { "name": "Combat rules", "values": { "zombie_hp_per_level": 20 } }
  },
  "tables": {
    "weapons": {
      "columns": [ { "key": "weapon_id", "type": "string" }, { "key": "damage", "type": "number" } ],
      "rows": [ { "weapon_id": "pistol", "damage": 15 } ]
    }
  }
}
```

The library is API-only. It does not keep an offline copy. Load the values
when the game starts and keep the result in memory. Lookups after that are
local, so the game makes no request per item. The game still calls the server
for that load.

Good uses are client-side numbers that must match the server (zombie hit
points, expected damage), item catalogs loaded once, shop prices, drop tables,
daily reward amounts, and tuning that changes without a game update.

## Using them in endpoints

Endpoint expressions read a group value as `{{values.<group>.<key>}}`. A
`lookup` step reads one row from a table, and `lookup_many` reads a list of
rows. Both take `source: values`:

```yaml
  - id: weapon
    type: lookup
    source: values
    table: weapons
    where:
      field: weapon_id
      op: "=="
      value: "{{input.weapon_id}}"
    required: true
    onMissing:
      status: 404
      errorCode: UNKNOWN_WEAPON
```

`onMissing` sets the error the client sees when no row matches. The
[worked example](#worked-example-validate-combat-without-a-dedicated-server)
uses both forms.

## Versions and publishing

Game values belong to the revision they ship with.

- **Publish writes a snapshot.** When the Sync Tool publishes a package
  revision (`POST /v3/manage/{projectId}/package-sync`), the server writes an
  immutable snapshot of that revision's endpoints, workflows, collections,
  queries and game values. See [version policy](version-policy.md).
- **Old builds keep their values.** A published build sends
  `x-ns-revision-id`. While its revision is kept and inside its support window,
  the server resolves game values and collection definitions from that
  revision's snapshot. A request with no header, an unknown revision or an
  expired one runs against live values
  ([pinned execution](version-policy.md#pinned-execution)).
- **Retention.** The server keeps the live values plus the most recent earlier
  revisions. The default is 3 and the range is 0 to 20. A revision inside its
  support window is kept even past that count.
- **Push Staged or Push Live.** **Push Staged** sends the publish target `next`.
  Its changes are promoted when the next package revision is published.
  **Push Live** changes the live target; retained revision snapshots continue
  to resolve independently. See
  [client setup](client-setup.md#staged-pushes-and-editor-play-sessions).

Keep schemas backward-compatible across the support window. Player records are
shared live data, not revision snapshots.

Revision enforcement is optional and off by default. It can block outdated
builds, but only when the owner turns it on
([revision policy](version-policy.md#rules)).

Player records are not versioned. Old and new builds read and write the same
live player data, so keep collection schemas backward-compatible across
revisions.

## Worked example: validate combat without a dedicated server

This example has a game client report the claims for a fight. The server checks
each claim against the game values and its own clock before it awards anything.
The client cannot award itself xp. It can only ask for it, and the server decides.

Game values used:

- Group `combat`: `zombie_hp_per_level: 20`, `xp_per_zombie_level: 15`,
  `max_zombie_level: 50`, `max_clock_skew_ms: 2000`.
- Table `weapons`: rusty pipe 8 damage every 600 ms, pistol 15 every 300 ms,
  shotgun 45 every 900 ms, crossbow 60 every 1200 ms.

The `players` collection needs `combat` and `xp` fields. Two endpoints do the work.

For the concurrency check, configure the `players` collection with
`requireSaveVersion: true`. Protected endpoints validate the records they read
again inside the write transaction; if another request changed that state,
the stale call returns `409 CONFLICT` and persists no reward. This setting is
opt-in. Without it, endpoint writes keep their existing behavior and are not
safe against simultaneous requests that read and then overwrite the same record.

### start-combat

`endpoints/start-combat.endpoint.yml`:

```yaml
sourceVersion: 1
kind: endpoint
id: start-combat
name: Start combat
description: Stamps the server time a fight starts and returns the zombie's hit points from game values.
method: POST
enabled: true
input:
  type: object
  properties:
    weapon_id:
      type: string
    zombie_level:
      type: number
  required:
    - weapon_id
    - zombie_level
steps:
  - id: weapon
    type: lookup
    source: values
    table: weapons
    where:
      field: weapon_id
      op: "=="
      value: "{{input.weapon_id}}"
    required: true
    onMissing:
      status: 404
      errorCode: UNKNOWN_WEAPON
      message: "Unknown weapon {{input.weapon_id}}."
  - id: level_ok
    type: condition
    check:
      field: input.zombie_level
      op: "<="
      value: "{{values.combat.max_zombie_level}}"
    onFail:
      status: 400
      error: LEVEL_TOO_HIGH
  - id: zombie
    type: compute
    values:
      hp: "{{values.combat.zombie_hp_per_level}} * {{input.zombie_level}}"
  - id: begin
    type: write
    collection: players
    key: "{{steamId}}"
    ops:
      - op: set
        path: combat
        value:
          weapon_id: "{{input.weapon_id}}"
          zombie_level: "{{input.zombie_level}}"
          zombie_hp: "{{zombie.hp}}"
          started_ms: "{{_unixMs}}"
response:
  status: 200
  body:
    zombie_hp: "{{zombie.hp}}"
    damage_per_hit: "{{weapon.damage}}"
    server_time_ms: "{{_unixMs}}"
```

### finish-combat

`endpoints/finish-combat.endpoint.yml`:

```yaml
sourceVersion: 1
kind: endpoint
id: finish-combat
name: Finish combat
description: Checks a reported kill against the weapon stats in game values and the server clock before awarding xp.
method: POST
enabled: true
input:
  type: object
  properties:
    hits:
      type: number
    client_time_ms:
      type: number
  required:
    - hits
    - client_time_ms
steps:
  - id: player
    type: read
    collection: players
    key: "{{steamId}}"
  - id: in_combat
    type: condition
    check:
      field: player.combat.started_ms
      op: exists
    onFail:
      status: 409
      error: NO_COMBAT
      message: Start combat first.
  - id: weapon
    type: lookup
    source: values
    table: weapons
    where:
      field: weapon_id
      op: "=="
      value: "{{player.combat.weapon_id}}"
    required: true
    onMissing:
      status: 404
      errorCode: UNKNOWN_WEAPON
  - id: fight
    type: compute
    values:
      damage: "{{input.hits}} * {{weapon.damage}}"
      elapsed_ms: "{{_unixMs}} - {{player.combat.started_ms}}"
      max_hits: "floor({{fight.elapsed_ms}} / {{weapon.fire_interval_ms}}) + 1"
      clock_skew_ms: "abs({{input.client_time_ms}} - {{_unixMs}})"
      xp: "{{player.combat.zombie_level}} * {{values.combat.xp_per_zombie_level}}"
  - id: zombie_dead
    type: condition
    check:
      field: fight.damage
      op: ">="
      value: "{{player.combat.zombie_hp}}"
    onFail:
      status: 400
      error: ZOMBIE_ALIVE
      message: Not enough damage to kill this zombie.
  - id: hits_possible
    type: condition
    check:
      field: input.hits
      op: "<="
      value: "{{fight.max_hits}}"
    onFail:
      status: 400
      error: TOO_MANY_HITS
      message: More hits than this weapon can fire in the time since combat started.
  - id: clock_ok
    type: condition
    check:
      field: fight.clock_skew_ms
      op: "<="
      value: "{{values.combat.max_clock_skew_ms}}"
    onFail:
      status: 400
      error: CLOCK_SKEW
      message: Client clock is too far from the server clock.
  - id: reward
    type: write
    collection: players
    key: "{{steamId}}"
    ops:
      - op: inc
        path: xp
        value: "{{fight.xp}}"
        source: combat
        reason: "Killed a level {{player.combat.zombie_level}} zombie"
      - op: set
        path: combat
        value:
          ended_ms: "{{_unixMs}}"
response:
  status: 200
  body:
    success: true
    xp: "{{fight.xp}}"
    elapsed_ms: "{{fight.elapsed_ms}}"
```

### Results

These responses came from running the two endpoints over HTTP on sbox-ns 0.5.4.
The test server had authentication turned off. A real game sends the s&box
identity, which the server verifies.

| Request | Response | Why |
| --- | --- | --- |
| `finish-combat` with no `start-combat` | `409 NO_COMBAT` | No fight is open. |
| `start-combat` with an unknown weapon | `404 UNKNOWN_WEAPON` | No row matches `weapon_id`. |
| `start-combat` with `zombie_level: 99` | `400 LEVEL_TOO_HIGH` | Above `max_zombie_level` (50). |
| `start-combat` with pistol at level 3 | `200 {"zombie_hp": 60, "damage_per_hit": 15}` | 20 × 3 hit points. |
| `finish-combat` with 4 hits right after start | `400 TOO_MANY_HITS` | One pistol shot every 300 ms allows 1 hit at 0 ms. |
| `finish-combat` with 2 hits right after start | `400 ZOMBIE_ALIVE` | 30 damage does not cover 60 hit points. |
| `finish-combat` with the client clock 60 s behind | `400 CLOCK_SKEW` | Skew is over `max_clock_skew_ms` (2000). |
| `finish-combat` with 4 hits after 1 s | `200 {"success": true, "xp": 45, "elapsed_ms": 1020}` | 60 damage kills a 60 hit point zombie; 3 × 15 xp. |
| Replay of the successful `finish-combat` | `409 NO_COMBAT` | The fight was closed by the first finish. |

Each rejected request leaves the player's record unchanged, because every
`write` step comes after the checks.

Concurrent completion is a separate case: without `requireSaveVersion: true`,
two requests can both pass the initial `in_combat` check. With it enabled,
only one request can commit against a given player-record version; the other
receives `409 CONFLICT`. Reload the player state before deciding whether to
submit another action.

### What the checks do not prove

The checks limit what a client can claim. A fight cannot award more damage than
the weapon's stats allow, faster than its fire rate allows after the server
start time, or with a clock far from the server's. They do not prove that a hit
landed. A client that controls the game can still send four hits that were
never fired, as long as they fit the rules above. Cap rewards, and treat the
server as the place where rules are enforced, not where gameplay is proven.

## Related guides

- [Version policy](version-policy.md): revisions, retention, pinned execution
  and revision enforcement.
- [Admin panel](admin-panel.md): the owner console, dry-run endpoint tests and
  the **Game values** page.
- [Game client](game-client.md): the requests the library sends.
- [Client setup](client-setup.md): Base URL, the Sync Tool and staged pushes.
- [MCP](mcp.md): coding agents and `mcp.allow_writes`.
