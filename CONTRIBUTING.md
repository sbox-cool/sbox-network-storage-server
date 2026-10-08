# Contributing

Thanks for helping improve sbox Network Storage Server.

## Before you start

- Open an issue before large changes so the approach can be agreed first.
- The server must stay compatible with the published s&box client library
  ([sbox-cool/sbox-network-storage](https://github.com/sbox-cool/sbox-network-storage)).
  Changes to request or response shapes need a matching client discussion.
- Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Build and test

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```sh
dotnet build SboxNetworkStorage.sln
dotnet test
```

Run the server locally with a throwaway config and data folder:

```sh
dotnet run --project src/SboxNetworkStorage.Server -- setup --config-dir ./.local/config --data-dir ./.local/data
dotnet run --project src/SboxNetworkStorage.Server -- start --config-dir ./.local/config --data-dir ./.local/data
```

### PostgreSQL tests

PostgreSQL integration tests are skipped unless `NS_TEST_POSTGRES` holds a
connection string to a disposable database:

```sh
docker run -d --name ns-test-pg -p 5432:5432 \
  -e POSTGRES_USER=sbox_ns -e POSTGRES_PASSWORD=sbox_ns -e POSTGRES_DB=sbox_ns_test \
  postgres:16

export NS_TEST_POSTGRES="Host=localhost;Port=5432;Database=sbox_ns_test;Username=sbox_ns;Password=sbox_ns"
dotnet test
```

Tests create and drop their own schemas; never point `NS_TEST_POSTGRES` at a
database holding real data.

### HTTP corpus and parity gates

The versioned corpus in `tests/parity/corpus` replays real client/editor request
shapes in filename order against a **fresh, disposable project**. It writes
source-authored collections, workflows and endpoints through editor preflight/sync,
then exercises endpoint execution, document CRUD, global append/read, save slots,
analytics, auth-session errors, queries and invalid requests. Every step has an
exact HTTP status expectation; selected responses additionally assert fields and
values. This is not a recording of the managed service.

Run the HTTP smoke gate from the repository root:

```sh
bash scripts/smoke.sh --out TestResults/sqlite.json
bash scripts/smoke.sh --no-build \
  --postgres 'Host=localhost;Port=5432;Database=sbox_ns_test;Username=sbox_ns;Password=sbox_ns' \
  --out TestResults/postgres.json
dotnet run --project tools/SboxNetworkStorage.Parity -- diff \
  --stable TestResults/sqlite.json --candidate TestResults/postgres.json
```

On Windows, use `./scripts/smoke.ps1 -Out TestResults/sqlite.json` in PowerShell 7.
Both scripts build Release unless `--no-build` / `-NoBuild` is supplied, create
temporary config/data and project keys through the real CLI, start the real server,
wait for `/health`, replay the entire corpus, and terminate the server on exit.
`--binary PATH` / `-Binary PATH` accepts a published executable or DLL; the harness
must still be built. `--port PORT` / `-Port PORT` avoids local listener conflicts.
PostgreSQL smoke leaves its newly created project in the specified database:
**never point it at production data**. No Facepunch credentials are bypassed:
smoke projects disable s&box authentication, and auth-session scenarios assert
the disabled-session error contract rather than pretend to authenticate a player.

For a differential replay against two authorized servers, each needs an equivalent
fresh project with public key mode and s&box auth disabled:

```sh
dotnet run --project tools/SboxNetworkStorage.Parity -- run \
  --target "$STABLE_URL" --project-id "$STABLE_PROJECT" \
  --public-key "$STABLE_PUBLIC" --secret-key "$STABLE_SECRET" --out stable.json
dotnet run --project tools/SboxNetworkStorage.Parity -- run \
  --target "$CANDIDATE_URL" --project-id "$CANDIDATE_PROJECT" \
  --public-key "$CANDIDATE_PUBLIC" --secret-key "$CANDIDATE_SECRET" --out candidate.json
dotnet run --project tools/SboxNetworkStorage.Parity -- diff \
  --stable stable.json --candidate candidate.json \
  --allow tests/parity/intentional-differences.json
```

`run` and `check` fail on transport failures, unexpected statuses/fields/values or
5xx responses. `diff` reports the authored request, both normalized responses and
each JSON/header/status difference; missing scenarios/steps and corpus request
drift also fail. Normalization masks timestamps, generated/captured identifiers,
correlation IDs, durations and version hashes, sorts object keys, and sorts arrays
only at explicitly declared order-insensitive paths. Results contain templated
requests rather than expanded keys; review response bodies before sharing.

An approved successful reference can be converted to golden responses with
`snapshot --from stable.json --out tests/parity/expectations`; `check --expect
tests/parity/expectations` adds strict golden comparison to the corpus assertions.
No managed goldens are committed without an authorized, reviewed stable run.
`intentional-differences.json` is deliberately empty; additions must describe the
specific scenario, step, response path and reviewed reason, not suppress bugs.

CI runs SQLite HTTP smoke on Linux/macOS/Windows and PostgreSQL smoke plus strict
cross-driver comparison on Linux. Release additionally requires the secret
`STABLE_PARITY_RESULTS_URL`: an HTTPS download URL for an approved stable-server
run of this exact corpus. Without it the stable-parity job fails explicitly;
neither binaries nor images publish. SQLite/PostgreSQL agreement alone is **not**
proof of managed-service parity.

#### Upstream and fixture prerequisites

The corpus tests the client contract, not just whichever routes currently return
success. The remaining `200` assertions below are release-blocking and must not
be relaxed to `4xx`/`5xx`, skipped, or whitelisted:

| Operation | Reference behavior | Prerequisite for success |
| --- | --- | --- |
| `POST /v3/manage/{projectId}/revision-init` (current and outdated revisions) | No implementation exists in any reference: absent from the Bun manage-api controller, routes, and server, and from production .NET. The management POST fallback returns `501 STORAGE_API_DECOMMISSIONED`. | A real revision-initialization and outdated-client policy implementation plus its response contract. `package-sync` persists game-package metadata but is not a runtime initialization equivalent. |
| `buy-upgrade` endpoint flow (`buy-pickaxe`, `buy-pickaxe-again`) | All three runtimes reject null math input identically: Bun `tools/sbox/endpoint-expression.js` (`evaluateMath` throws `Unresolved variable` on null/undefined), production .NET `EndpointExpression.cs:130-131`, and this server. No write path in any runtime applies collection schema defaults, and the fixture flow never establishes `player.level` (`save-profile` writes `playerName` only; `mine-ore` writes `ores.stone`). | A fixture that establishes the economy state a real game client would send (e.g. `gold`/`level` present before purchase), or a product decision on null-tolerant math applied consistently across runtimes. Do not special-case the endpoint or the transform. |
| `POST /v3/manage/{projectId}/does-not-exist` (`manage-unknown-mutation`) | Unknown management mutations match no reference route; Bun's router has no such path. This server answers the decommissioned-management fallback (`501 STORAGE_API_DECOMMISSIONED`). | A product decision: implement the route, or return `404 NOT_FOUND` for unknown mutations and correct the `200` assertion (no reference has ever returned `200` here). |

Resolved since extraction: `PUT /v3/manage/{projectId}/tests`,
`POST /v3/manage/{projectId}/source-upgrade`, `run-tests`, `test-endpoint`,
`suggest-tests`, `PUT settings`, and `DELETE keys` are registered as Gateway
routes served by `ManagementMutationCandidateHandler` (real writes where
ported, described dry-runs otherwise, matching production .NET). Collection
ledger reads are store-backed: an existing record or a known player profile
with no tracked deltas returns `200` with an empty ledger (matching Bun, which
never 404s a present collection's ledger on missing history); a key with no
record, no entries, and no profile returns `404 NOT_FOUND` (matching
production .NET file-missing). `GET /api/storage/{projectId}/{collectionId}/list`
is asserted `404`: no reference (Bun manage/api routes, production .NET
Gateway, route catalog) serves a global-list under `/api/storage`; the `v1`/`v3`
list routes are the supported surfaces and are covered by the catalog test.

These are not missing extraction copies: mapping a dry-run candidate to a live
route would report success without performing the requested operation. Their presence means the full smoke gate
cannot pass until the prerequisites above are implemented.

Management query listing reuses the production dashboard's `queries.json`
resource read, which the metadata workspace maps to `ListQueriesAsync`; it is a
management read, not a mutation. The standalone GET handler returns the existing
`{ ok, data }` resource-list shape and the corpus requires both data and success.

### Install scripts and workflows

```sh
shellcheck -s sh install/install.sh
actionlint
```

## Project layout

| Path | Contents |
| --- | --- |
| `src/SboxNetworkStorage.Server` | ASP.NET Core host and `sbox-ns` CLI |
| `src/SboxNetworkStorage.Storage` | Storage contract and in-memory store |
| `src/SboxNetworkStorage.Domain`, `Contracts`, `Application`, `Infrastructure` | Network Storage domain logic and API handlers |
| `install/` | Install scripts and systemd unit |
| `docs/` | Self-hosting documentation |

## Pull requests

- Keep changes focused; one topic per pull request.
- Add or update tests for behaviour changes.
- Update `CHANGELOG.md` under **Unreleased**.
- Update `docs/` when you change configuration keys, CLI commands or defaults.
- Never commit secrets, connection strings with real passwords or database files.
  CI runs gitleaks on every push.

## License

By contributing you agree that your contributions are licensed under the
[GNU AGPL-3.0](LICENSE), the license of this project.
