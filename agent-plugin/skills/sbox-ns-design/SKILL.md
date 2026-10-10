---
name: sbox-ns-design
description: Design s&box Network Storage collections, endpoints, workflows, queries and game values in YAML, validate them, save them to a self-hosted sbox-ns server and dry-run them. Use when the user wants to add a game feature that stores or changes player data (currency, inventory, progression, leaderboards).
---

# Build a game backend with sbox-ns

Definitions are YAML files. The s&box editor's Sync Tool keeps them under
`Editor/Network Storage/<kind>/<id>.yml` in the game and pushes them to the
server. The MCP tools read and write the same YAML.

## Rules that keep players from cheating

- Player-owned data that matters (coins, items, XP) lives in a collection
  with `accessMode: endpoint`. Game clients cannot read or write it directly;
  only endpoints change it, and endpoints validate every input.
- Only cosmetic or harmless data uses `accessMode: public`.
- Endpoints never trust amounts from the client for rewards. Compute them on
  the server from game values, or validate them with a condition step.
- Secret keys (`sbox_sk_`) are for the editor and dedicated servers only.

## Loop

1. `definitions_list` to see what exists; `definition_get` to read a file.
2. `examples_list`, then `example_get` for the closest example. It returns the
   example and every definition it depends on, companions first. Adapt rather
   than writing from scratch: the examples show the exact step syntax.
3. Write the YAML. Run `definition_check` on each file and fix every error.
   Warnings such as COLLECTION_NOT_FOUND clear once the collection is saved.
4. Save. Two options:
   - `definition_save` with `confirm` set to `<projectId>/<kind>/<id>`. It needs
     the operator setting `mcp.allow_writes`; the tool says how to turn it on.
     Endpoints and collections go to the staged (next) revision by default,
     which live players do not use until the next game package sync. If the
     result says `stagedFallback`, there is no synced package and the save
     went live.
   - Or write the files into the game's `Editor/Network Storage/` folder and
     tell the user to push them with the Sync Tool.
   Save collections before the endpoints that use them.
5. `endpoint_test` with a success case and at least one rejected case
   (`expectStatus: 400`). Use `target: next` for staged definitions. Read
   `steps` and `pendingWrites` to confirm what would be stored; nothing is
   written.
6. `endpoint_snippet` for the C# the game uses to call it.

## Calling from the game

```csharp
var result = await NetworkStorage.CallEndpoint( "mine", new { amount = 1 } );
if ( !result.HasValue )
{
    NetworkStorage.TryGetLastEndpointError( "mine", out var code, out var message );
}
```

s&box hides 4xx bodies from game code, so a rejected call returns null with
code `HTTP_ERROR`. Use `logs_requests` to see the status.

Changes reach a running server within about a minute.
