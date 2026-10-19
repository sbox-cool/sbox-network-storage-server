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

## Compatibility invariant

Read `docs/compatibility.md` before changing game-facing API syntax, defaults,
save semantics, library-version policy or migrations. Backend updates must keep
older and unidentified published games working by default: no request without a
parseable library version is ever refused for its version, and the release-feed
overlay only ever tightens reported statuses, never enforcement. Add
replacements without removing old forms; support the old form for at least six
months after its successor ships and publish at least 90 days' removal notice.
No surprise game-facing breaking patch; prefer side-by-side API generations.

Link compatibility issues/PRs in both repos and record the exact tested source
commit, server/library/package revisions, engine and database in the matrix
evidence. Backend HTTP tests are not s&box evidence; the green Compat engine legs are. New migrations are
conservatively `breaking` unless old read/write assumptions demonstrably survive;
never modify released migration SQL. Exercise unchanged legacy requests and
SQLite/PostgreSQL parity before delivery. Version headers are self-reported
metadata, never authentication; runtime minimum enforcement remains explicit
owner opt-in.

Certify each server/library/engine/provider pairing once with real evidence;
a fundamental wire/semantic/storage change invalidates affected certifications
and must be re-tested before release. Library methods and endpoint support are
LTS surface: no silent rename/removal/narrowing.

## When s&box evidence is needed

Decide by the client-visible surface, not by size or risk (`docs/compatibility.md`,
"When s&box evidence is required"). Identical request/response bytes, headers, status and
error codes and defaults for everything the library can send (owner console, CLI, installers, DNS,
monitoring, storage internals, additive migrations) need **no** s&box run, but you must attach the
proof: parity diff, SQLite/PostgreSQL, smoke, and a default-off regression test. Anything that
changes what the library already sends or reads, and any library `Code/`/`Editor/` change, needs
the Compat engine legs green on the commit: the real library inside the real s&box engine, built
from `Facepunch/sbox-public`. `Editor/` changes are covered by the same legs. A failed, cancelled or
missing engine leg blocks the change. HTTP tests never stand in for an engine leg.

Server PRs, merges and server releases are tested on GitHub Actions only (the Compat contract and
the engine legs). No PR, merge or server release waits on a native editor run.

A real Windows PC running the native s&box editor (Steam build) is reserved for two things:

- **Library releases:** one native run of the library version being published, recorded as a
  `pass` row in `compat/library-matrix.json`, before it is published to sbox.game or Library
  Manager. This is where Setup/Sync Tool window behaviour gets its real check.
- **Engine parity checks:** periodically (for example when Facepunch ships a notable engine
  update, or before a library release) confirm that the source-built engine in CI and the live
  Steam engine give the same e2e results, so CI stays a trustworthy proxy. Record the result as a
  matrix row or note that names both engine identities (sbox-public commit and Steam build).

`pending-sbox-editor-test` is not used on PRs or issues. If the label is used at all, it means only
"library release awaiting its native run".

## Reporting test results

Any result you report for s&box (an engine leg run, a native run, or a real game client: a PR, an
issue, a release check) MUST name all of these, or it is invalid evidence and does not count:

- **Engine build**: the full s&box build string from the editor (About dialog or its
  `.version`), for example `26.10.02`. For a source-built engine (the Compat engine legs build
  `Facepunch/sbox-public`), also name the sbox-public commit.
- **Library version**: the git tag or `git:<sha>` for a library checked out from git, or the
  version Library Manager shows (the `LibraryVersion` constant) for one installed through the
  editor. Add the package revision (`.version`, `1.0.<revision>`) when known.
- **Server build**: `version`, `kind` (`self-hosted` or `hosted`) and `database` from
  `GET /v3/server-info`, plus the commit when it is not a release build.
- **Operating system** the editor or game ran on.

Record each run as a `tested` row in `compat/library-matrix.json` (fields `server`,
`kind`, `library`, `librarySource`, `revision`, `engine`, `os`, `database`, `result`,
`evidence`) instead of free text; see `docs/editor-test-plan.md`. A native editor run is a `pass`
row. A green Compat engine run is a `ci-only` row, the normal certification for PRs, merges and
server releases. CI-only runs are `ci-only`, never `pass`. A `pass` row without `engine`, `os`,
`kind` and `librarySource` fails the schema test. Say exactly what you ran and what you did not.
Never invent a version or an engine build you did not read from the machine.
