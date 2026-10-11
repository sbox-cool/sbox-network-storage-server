# Hosting models

s&box Network Storage is the central server your game is missing. Your
game servers, whether a player's listen server or your own dedicated box,
are thin callers: currency, XP, levels, items, high scores and every other
important rule run here, behind the HTTPS endpoint you control.

## Player-hosted

A player runs the listen server from their own game. That server only ever
holds the **public key** (`sbox_ns_...`), so it can read shared data and
call public endpoints, but every important check runs on this backend.
Game collections stay endpoint-only, so a compromised host cannot rewrite
player data directly. This is the cheapest way to start, and it is safe by
default.

## Dedicated

Your own box runs the game server with a **secret key** (`sbox_sk_...`).
It can call dedicated-only endpoints (`requiresSecretKey`) and use the
management API. Players still only hold the public key in the published
game. Use this for tournaments, seasons, or anything worth protecting.

## Hybrid

Players host day-to-day sessions while your dedicated box handles special
modes. One project serves both at once: public endpoints for everyone,
secret-key endpoints for your box.

## Key rules

- The game client and any player-run host get only the project ID and the
  public key. The `NetworkStorage.Configure` line in game code uses those
  two values plus your base URL.
- The secret key belongs only in editor tooling (`Editor > Network Storage
  > Setup`, the Sync Tool) and trusted servers. It never ships in a
  published game.
- The dashboard authority check lists any game data a client could still
  change directly, so you can close the gaps before players find them. Set
  the project's hosting profile first; see [admin-panel.md](admin-panel.md#authority-check)
  for the findings and the CLI/MCP equivalents.

For pointing the game and editor at your server, see
[client-setup.md](client-setup.md). For what the client says to your
server, see [game-client.md](game-client.md).
