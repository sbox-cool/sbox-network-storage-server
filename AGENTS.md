# AGENTS.md

Paste this into a coding agent to give it context on this repo.

You are working with `sbox-ns`, the self-hosted s&box game backend in this
repo (sbox-cool/sbox-network-storage-server, .NET 8, AGPL-3.0-only).
One binary is both the server and its operator CLI. It serves the existing
Network Storage client library for s&box; games point the library at this
server with a base URL and need no client code changes:

```csharp
NetworkStorage.Configure( projectId, apiKey, "http://your-server:8080" );
```

Run it locally with SQLite (the default, zero config):

```sh
dotnet run --project src/SboxNetworkStorage.Server -- start
sbox-ns setup --non-interactive --database sqlite --listen 0.0.0.0:8080
sbox-ns project create "My Game"
sbox-ns key create <projectId> --type public --label game
```

The HTTP API contract lives in `tests/parity/corpus/*.json` (request shapes,
headers, expected bodies) with the runner in `tools/SboxNetworkStorage.Parity`
and live route tests in `tests/SboxNetworkStorage.Server.Tests` (for example
`RevisionInitHttpTests`). The game client source it must stay compatible with
is `github.com/sbox-cool/sbox-network-storage` (library page
`sbox.game/sboxcool/network-storage`, API docs `sbox.cool/wiki/network-storage-v3`).
Game dev setup is documented in `docs/client-setup.md`, the startup
handshakes in `docs/game-client.md`, databases in `docs/database.md`.

Do not touch: `CHANGELOG.md`, `install/` scripts, `.github/workflows/`,
`tests/parity/corpus/` fixtures, anything under `<config dir>/secrets/`,
and never put a secret API key in game code (public keys only). Do not share
one PostgreSQL schema between independent server instances or data
directories. Keep backups (`sbox-ns db backup`) before touching migrations.
