# Library/server compatibility

Server releases and game-library releases are independent. Updating the backend must not require republishing an otherwise working game. Library version, package revision, game revision, API version (`v3`) and database schema version are different identifiers; never compare them as one version.

Tracking: [server epic #57](https://github.com/sbox-cool/sbox-network-storage-server/issues/57) and [library tracker #6](https://github.com/sbox-cool/sbox-network-storage/issues/6). Backend/client contracts link [S1 #58](https://github.com/sbox-cool/sbox-network-storage-server/issues/58) ↔ [L1 #7](https://github.com/sbox-cool/sbox-network-storage/issues/7), [S3 #60](https://github.com/sbox-cool/sbox-network-storage-server/issues/60) ↔ [L2 #8](https://github.com/sbox-cool/sbox-network-storage/issues/8), and [S6 #63](https://github.com/sbox-cool/sbox-network-storage-server/issues/63) ↔ [L3 #9](https://github.com/sbox-cool/sbox-network-storage/issues/9). GitHub dependency links, not prose alone, define the implementation order.

## Non-breaking release rules

- Patch releases preserve existing request syntax, response meanings, error codes, authentication, save semantics and defaults. New fields/capabilities are additive. Follow the existing operator-version rules in [releasing.md](releasing.md); a server version bump does not authorize breaking the game API.
- A rename introduces the replacement while continuing to accept the old form. Keep actual translation logic at the API boundary; do not scatter version checks through storage/endpoint execution. Record why the old form exists and its linked deprecation issue. Do not create empty compatibility abstractions ahead of a real rename.
- Deprecation is a warning, not a rejection. Support a replaced library version (or API form) for **at least six calendar months after its successor ships**, with **at least 90 days of published removal notice**. The later date wins. Announced sunsets cannot be shortened. An editor-constructor incident is not evidence that published games are incompatible.
- A removal needs a replacement already released in the library, matching issues in both repos, a migration guide, real old/new library test evidence, and an impact review including unidentified clients. Unknown clients are not proof that everyone upgraded. Prefer retaining `v3` and adding a new API generation over altering existing `v3` syntax. Any eventual removal is an explicitly migration-gated release, never a surprise patch or routine unattended update.
- Security/data-loss emergencies can use a narrow, advisory-linked block. Do not turn ordinary obsolescence into a security block. Player game-revision enforcement is separate from library compatibility; players cannot replace the library inside a published game.

### Every compatibility-affecting PR

Link the parent server/client issues, counterpart PR, dependency and priority (`compat`, `priority: P0/P1/P2`). State whether the change is additive, deprecated or removal; which existing clients remain accepted; the replacement and sunset; and the request/response and database implications. A library counterpart must be merged/released before a backend removal depends on it. Include SQLite/PostgreSQL corpus results and update the tested matrix evidence. Run the unchanged legacy path, not only a newly versioned client. Reviewers must reject a shortened notice, a patch-only syntax change, or an untested removal.

## Automated gates

These run without anyone remembering to. All of them are `ci-only` evidence (hosted runners cannot
start the s&box editor); they hold changes back, they do not certify a pairing.

| Where | Check | Blocks |
| --- | --- | --- |
| Server PR and `main` | **Compat gate** ([`compat-matrix.yml`](../.github/workflows/compat-matrix.yml)): the parity corpus on the change, diffed against the approved stable reference **and against the latest release**; library version headers; the real library inside s&box (newest Facepunch/sbox-public, `-mcp` dedicated server) for every library in [`compat/library-targets.json`](../compat/library-targets.json) | Any unlisted wire difference. Any library that passes on the latest release and fails on the change (**regression**). A library failing on both is reported as an engine/library problem, not blamed on the change. |
| Server release | The same gate inside `release.yml`, before anything is built or signed | Publishing. Passing libraries go into the signed `release.json` `testedLibraries`. |
| Server nightly | The same gate on `main` with the newest engine | Nothing; opens or updates a `compat` issue on a regression, a contract failure, or a library that now fails on the released server too (an s&box update broke it). |
| Library PR | Its own **Compat gate**: public API diff, the PR's library inside s&box against the latest server release and server `main`, and an editor-evidence hold for `Code/`/`Editor/` changes | See the library repo's `docs/`. |

An intentional wire change is not a failure to work around: add the difference to
[`tests/parity/intentional-differences.json`](../tests/parity/intentional-differences.json) with a
reason in the same PR, so review sees exactly what older games will receive. When a new library
revision is published on sbox.game, add it to `compat/library-targets.json` so every later server
change is run against it too; keep the revisions players still run there.

## Certification: certify once, re-test on fundamental change

Both sides are LTS: games embed the library for months and self-hosted servers
update independently. A certified pairing is the unit of trust. Certify it once
with real evidence, then hold that certification until a fundamental change
forces re-testing. Do not re-certify the same pairing on every release, and do
not extend a certification to a pairing that was not exercised.

A certification is one `tested` row: exact server version and source commit,
library version and package revision (or null when genuinely unassigned),
library source, s&box engine build, operating system, backend kind, database
providers actually exercised, a `pass` result, and evidence URLs (run
logs/artifacts, both-repo PRs/issues). `ci-only` (HTTP parity without the real
s&box editor/game run) never certifies a pairing. A certified request pattern
(for example legacy revision-init shape, save/load round trip, endpoint
success/error path) covers that pattern only; new patterns need new rows.

Fundamental changes invalidate affected certifications and MUST be re-tested
before release. Treat these as fundamental on either side:
request/response shapes, error codes/envelopes, authentication/authorization
behavior, save/endpoint/query/game-value semantics, defaults, rate-limit or
revision behavior, added/removed/renamed endpoints or library public methods,
identity parsing, capability advertisement, version-policy evaluation, migration
assumptions, or supported-provider changes. Internal refactors with no wire,
semantic or storage-contract change do not invalidate certification, but still
require the unchanged parity corpus and SQLite/PostgreSQL suite.

Library public methods and endpoint support are LTS surface. Do not rename,
remove, narrow or change the meaning of a library method, endpoint, field,
error code or auth requirement without a shipped replacement, both-repo
`compat` issues/PRs, a migration guide, the six-month/90-day support window,
real old-client and new-client evidence, and an impact review covering
unidentified traffic. Reviewers MUST reject silent removals, narrowed support,
renames without translation, shortened notice, patch-only syntax changes, and
any fundamental change without new certification rows. When in doubt, keep the
old form and add side-by-side.

## When an s&box editor test is required

An editor or game run is the most expensive evidence there is, and it only proves something when the
change can alter what the library sends or how it interprets a reply. So the rule is about the **wire and
the library**, not about how big or risky a server change feels.

**Classify every PR's client-visible surface** (the PR template asks for it):

| Class | Meaning | Editor test |
| --- | --- | --- |
| **None** | Request and response bytes, headers, status codes, error codes and defaults the library sees are identical to the previous release for every request it can send. Owner console, CLI, installers, updater, DNS/tunnel, monitoring and alerting, demo, storage internals, additive migrations whose reads and writes return the same results, performance, docs. | **Not required.** Required evidence is that the claim is true (below). |
| **Additive, off by default** | New fields, headers, endpoints, capabilities or behaviors that appear **only** when the owner turns something on. The default-off path is byte-identical. | **Not required for the default-off path.** The new client-visible behavior needs an editor test once, before it is documented as supported to game developers. Until then label the issue/PR `pending-sbox-editor-test` and describe the feature as server-side only. |
| **Changed** | Any difference for a request the library can already send: shapes, field meanings, error codes or envelopes, auth/session/encryption handling, save/endpoint/query/value semantics, rate-limit or revision behavior, defaults, status-header or capability values the library reads. | **Required** (a `pass` row in [`compat/library-matrix.json`](../compat/library-matrix.json)) before release, plus the old-client replay in [Every compatibility-affecting PR](#every-compatibility-affecting-pr). Label `pending-sbox-editor-test` until then. |
| **Library code** | Any change in the library repo's `Code/` or `Editor/` (runtime, Sync Tool, Setup). | **Required** for the paths changed. Pure logic is covered by library unit tests, but only an editor run covers the engine APIs (`Http`, `Package`, windows). |

**"Identical" must be shown, not asserted.** For a **None** or default-off claim attach:

1. the parity diff against `tests/parity/stable-reference.json` with no unrecorded differences
   (`scripts/smoke.sh --out ...`, then `sbox-ns-parity diff ...`), and a note if
   `tests/parity/intentional-differences.json` changed;
2. SQLite and PostgreSQL both green for the touched storage path, and `scripts/smoke.sh` clean;
3. for an additive migration, the `additive` tag in both drivers and a legacy-request replay against
   the migrated database (docs/database.md, "Rollback-safe migrations");
4. a regression test for the default-off path when new behavior is gated by a project or server setting
   (for example: a legacy request with no `x-ns-revision-id` performs no extra lookup and returns the
   same body).

If any of these cannot be produced, the change is not **None**; classify it honestly and require the test.
Unknown clients and older libraries are always in scope: "the current library behaves the same" is not
enough when an older published game could see a difference.

**Do not substitute a server-only run for the editor when the class says editor.** HTTP tests, the
parity corpus and fake-handler tests are `ci-only`. A `ci-only` row can release a **None** or
**Additive, off by default** change (`--allow-ci-only`); it can never certify a **Changed** or **Library
code** change.

**Blocked on an editor run?** Add the label `pending-sbox-editor-test` to the issue and the PR at the time
you know. Do not close the issue as done and do not describe the behavior as certified; say exactly which
rows are missing. Remove the label only when the `tested` row exists.

## Client identity and response contract

The wire headers are described in [game-client.md](game-client.md#library-compatibility):
`x-ns-library-version`, `x-ns-library-revision`, `x-ns-client-type`,
`x-ns-revision-id`, `x-ns-engine-version`. Missing/invalid versions are the
`legacy` bucket and are advertised as `unknown`; this is accepted by default.
Existing revision-init, heartbeat and analytics JSON already parsed by their
handlers can supply the library version (`networkStorageVersion`,
`libraryVersion`, `library_version`) and the published game revision
(`revisionId` on a published bundle); a version header takes precedence. The
body fallback enriches telemetry, not authentication or a pre-handler
enforcement decision. No additional body buffering/parsing is needed. Existing
response bodies are unchanged except the additive `compat` object in
`/v3/server-info`.

The project's Errors tab captures the same request identity (library/context,
package revision and game revision). Errors raised outside an HTTP request have
legacy/unknown context and no revision; no player IDs or API keys are added.

Responses include `x-ns-server-version` and `x-ns-library-status`: `unknown`, `supported`, `outdated`, `deprecated`, `unsupported` or `blocked`. A known latest version and deprecation sunset are advertised in `x-ns-library-latest` and `x-ns-library-sunset` when relevant. The explicit project minimum can override the status of a refused request to `unsupported`.

`GET /v3/server-info` adds `compat.library` with nullable `minimum`, `recommended`, `latest`, supported `legacy`, `deprecated`, `blocked`, `knownBroken` and `tested`, plus `compat.matrixUrl`. Null means no released/tested version has been established; it does not mean an invented `1.0.0`. Recommendations and sunsets do not expire working games automatically.

The shipped matrix is immutable and offline: no unsigned remote source can impose a library minimum or a remote kill switch. The release feed may carry a `libraryCompat` overlay, but the overlay only ever tightens the *reported* status on servers that have not been updated (for example to flag a library with a security problem). It never refuses a request: enforcement reads the embedded matrix only, and requests without a parseable library version are never refused by any rule. Header identities and player counts are self-reported, not a security boundary; a hostile caller can spoof versions. Keep all real authentication/authorization checks.

## Test evidence, not guessed compatibility

[`compat/library-matrix.json`](../compat/library-matrix.json) is embedded in the server and validated against [`library-matrix.schema.json`](../compat/library-matrix.schema.json). It currently records supported legacy clients and the separate old-editor incident, not fictional library releases or editor passes.

Each published `libraries` entry records `version`, `status`, numeric `revision`
(when assigned by s&box), `released` and `minServer`. The legacy entry instead
uses the `revisions` range. Deprecation and blocked-version ranges use
space-separated SemVer comparisons (`=`, `<`, `<=`, `>`, `>=`, or an exact
version); all comparisons must match. Every token is validated by the release
gate and when loading the embedded policy, even when an earlier comparison
would not match the current client. Version values also respect the server
parser's 64-character limit and non-negative 32-bit major/minor/patch
components.

A `tested` row records the exact server version/source commit, library
version/package revision and source, s&box engine build, operating system,
backend kind, database providers, `pass`/`fail`/`ci-only`, and evidence URLs.
Follow [editor-test-plan.md](editor-test-plan.md). Evidence links should
identify the run, logs/artifacts and related PRs/issues, with secrets removed.
Automated HTTP parity is `ci-only`, not a real editor pass.

```sh
python3 scripts/compat-release.py
python3 scripts/compat-release.py --matrix /path/to/release-evidence.json \
  --server 0.x.y --commit TESTED_SOURCE_COMMIT \
  --baseline /path/to/previous-release-matrix.json \
  --policy-source compat/library-matrix.json
```

Release notes require a matching-commit `pass` for the recommended library. A maintainer can explicitly use `--allow-ci-only` only when a matching `ci-only` row exists; the output prominently says no editor pass is certified. `dotnet test` validates the committed matrix and exercises these gates; Python 3 is required. The PowerShell companion [`scripts/compat-tested-with.ps1`](../scripts/compat-tested-with.ps1) renders the same rows as the "Tested with" release-notes table; it is a notes renderer only, not the gate. The authoritative release gate is `scripts/compat-release.py` (see [releasing.md](releasing.md#release-evidence)): it also enforces the support window above, requiring a superseded deprecated library entry to sunset no earlier than six months after its successor's recorded `released` date and, when an `announced` date is recorded, no earlier than 90 days after it.

A source commit cannot contain its own hash in a newly committed evidence row. Keep the release evidence as a **detached build/release artifact** after testing the final source commit; record it in the repo subsequently. Never change the tested commit to the later bookkeeping commit. Use `--matrix` for that artifact, and attach the generated table and matrix to the release. A binary's `tested` advertisement only includes records actually embedded for its version; `matrixUrl` also points to the maintained evidence history, not an authenticated policy override. The maintainer owns release-workflow attachment and the real s&box editor run.

Validate a detached artifact with `--policy-source`: it may change `tested`
evidence only, not the matrix policy that was exercised by the tests. Embed that
validated artifact without changing the source commit:

```sh
dotnet publish src/SboxNetworkStorage.Server -c Release \
  -p:Version=0.x.y \
  -p:LibraryCompatibilityMatrix=/absolute/path/to/release-evidence.json
```

Without this build property, the committed matrix is embedded. The build
property supplies a build input, not a runtime overlay or an unsigned policy
refresh. Keep the artifact with the signed release outputs.

## Operator impact and opt-in enforcement

Open **Project → Clients** (`/dashboard/projects/{id}/clients`) or run:

```sh
sbox-ns project clients PROJECT_ID --days 30
sbox-ns project clients PROJECT_ID --days 30 --json
```

The read-only MCP tool is `project_clients`. Reports group library/package revision, client context and game revision, showing request count, approximate distinct players, last seen, status/sunset and affected share. The dashboard previews a proposed minimum without changing it. Saving a non-empty/new minimum requires owner authentication, an antiforgery token and explicit acknowledgement that older published games that report a library version will be blocked; the change is audited. Clear the minimum to restore default acceptance. A backend update never sets it. Clients that report no library version are never blocked by any rule. Updates through this dashboard invalidate the policy cache immediately; external project-setting changes can take up to 30 seconds.

Project JSON stores `libraryMinimum` and optional `libraryAlertPercent`; both are absent by default. A refusal is HTTP 426 using the library's existing structured error envelope, and only ever for two reasons: the project's explicit minimum, or a `blocked` range in the embedded matrix (never the feed overlay):

```json
{"ok":false,"error":{"code":"LIBRARY_UNSUPPORTED","message":"Update the library and republish the game."}}
```

Advisory blocks use `LIBRARY_BLOCKED` with the advisory in the message. Missing/invalid keys and unauthorized management keys retain their original 401/403 paths, and management diagnostics and recovery (Pull, Test Live, revision-init) stay reachable. The s&box runtime can hide 4xx bodies; use response headers, server logs and the library-side update UI, not an assumption that every old game can display this message.

Traffic is buffered in memory, flushed once per minute and at graceful shutdown, and retained locally for 90 UTC days. The collector accepts at most 2,000 identity groups per UTC day/process and 2,000 pending groups; flushing does not reset the daily budget. Overflow is best-effort telemetry loss, never a failed game request. Restarting resets the in-process cardinality/drop counters; the CLI counter belongs to its own process, while the live dashboard shows the running server's counter. Database queries do not silently truncate the report.

Distinct-player sketches merge across requests, flushes and days with approximately 3.25% relative error. No raw player IDs or hashes are persisted in this compatibility table. Unidentified players are not counted. Percentages are **by requests**, not players; this traffic cannot prove every installed game upgraded. Setting the project's alert threshold enables rate-limited Discord/SMTP alerts through the existing alert sink when at least 100 requests in a flushed batch meet the affected percentage. Notifications remain opt-in; nothing is sent to a hosted analytics service.

## Safe binary rollback is a separate guarantee

Schema rollback (preserved-data downgrades, schema floors, `rollback --keep-data`) is documented in [database.md](database.md). It certifies schema/config compatibility, not game behavior. For release notes, capture each prospective target's successful `compatibility --json` result against the release's schema/config and pass it as `--rollback-report /path/to/report.json` to `compat-release.py`; without reports, the generated notes explicitly certify no preserved-data target.

## On the owner's server: preflight, watch, roll back

CI cannot cover every library build in every published game, so each server also guards its own
games (details in [self-hosting.md](self-hosting.md#updates)):

- **Before an update**, `sbox-ns update` compares the release's signed `release.json`
  `testedLibraries` with the library versions this server's games sent in the last 30 days. A
  library with at least 1% of requests that the release lists as `fail` stops the update
  (`--ignore-compat` overrides); untested builds are a warning. Unattended updates hold such a
  release.
- **After an update**, every request is counted per library version and server version
  (`client_outcomes_hourly`, additive migration v7; telemetry only, nothing on the wire changes).
  For 48 hours the server compares each library's error rate (5xx plus 4xx other than 401, 403, 409
  and 429) with the previous release; a jump of at least 10 points and 2x, with at least 50
  requests on both sides, is a regression. It sends one critical alert with the rollback commands,
  shows it in `sbox-ns doctor` and the owner console's Server page, and `sbox-ns update verify`
  prints the table (exit 5 on a regression).
- **Rolling back** is one command: `sbox-ns rollback --keep-data` when every migration since is
  additive, otherwise `sbox-ns rollback` (restores the pre-update backup). Unattended updates roll
  back by themselves on a regression when that keeps all data (`updates.auto_rollback`, default on).

These are self-reported library versions: they detect breakage, they never authenticate anything
or refuse a request.
