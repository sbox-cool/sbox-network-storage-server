# s&box editor and game test plan

Everything here needs the s&box editor and the real
[Network Storage library](https://github.com/sbox-cool/sbox-network-storage),
so CI cannot run it. Server-only behavior (HTTP contract, SQLite and
PostgreSQL, installers) is covered by CI and the parity corpus.

**Run only the sections a change can affect.** Whether a change needs an editor run at all is decided by
[When an s&box editor test is required](compatibility.md#when-an-sbox-editor-test-is-required): changes
with identical request and response bytes for everything the library can send (owner console, CLI,
installers, DNS, monitoring, storage internals, additive migrations) need none; changed wire behavior
and library code do. When a run is needed, run the sections that cover the changed pattern (A: install,
B: Sync Tool, C: play mode, D: security modes, E: revision handling, F: hosted name, G: independent
releases) and record only those. A `pass` row covers the exercised pattern, not the whole plan.

Each run ends with one `tested` row in [`compat/library-matrix.json`](../compat/library-matrix.json)
(see [Recording the result](#recording-the-result)). Tick the boxes in the PR that
records the run, and link that PR from the row's `evidence`.

**Two tracks.** This plan is the GUI track: the sections below need a native Windows editor run
(Steam-interactive `sbox-dev.exe`) on a non-GH-Actions runner, and only such a run clears
`pending-sbox-editor-test`. The code track — the same runtime patterns in C, D, E and G exercised
headless — is covered by the GH-Actions Compat engine leg (real library, source-built engine,
`-mcp` dedicated server); a green gate run certifies those patterns with no native wait.
Sections A, B and F always need the native run (Library Manager, Sync Tool windows, and the
owner-approved hosted name). The scripted run below covers the code-track subset
and can drive either a dedicated server headless (CI) or the real editor where Steam is available.

**Scripted run.** [`tools/sbox-cli-e2e`](../tools/sbox-cli-e2e/README.md) drives the real editor and
a dedicated server through sbox-cli (s&box 26.10.13+, Windows with Steam running for the editor) and
records the five facts below itself. It covers B (Push All with post-push verification, Test Live), C
(endpoint success and error code, `inc` with `{{values.*}}`, game values) and the dedicated secret-key
check in D, and writes `summary.md`, `evidence.json` and a draft `tested` row. Everything else in this
plan stays manual.

## Setup

1. Server: `sbox-ns setup --non-interactive --database sqlite --listen 0.0.0.0:8080`,
   then `sbox-ns project create "Editor test"` and one public and one
   secret key (see [client-setup.md](client-setup.md)). Repeat the whole plan once with
   `--database postgres`.
2. Library: replace the project's `Libraries/sboxcool.network-storage` folder with the
   current `main` of the library repo. Never copy files over an old install. A build
   older than 1.0.309133 does not start on editor 26.10.
3. Editor **Network Storage > Setup**: project ID, public key, secret key, Base URL.
4. Write down the five facts below before the first test. A run that is missing
   any of them is **invalid evidence**: it does not count, and no `pass` row may
   be recorded from it.

|Fact|Where to read it|`tested` field|
|---|---|---|
|s&box engine build, the full string (for example `26.10.02`)|The editor's About dialog, or the `.version` file next to the editor|`engine`|
|Library version|A library from git: the tag it was built from or, if untagged, `git:<sha>` (`git rev-parse --short HEAD` in the library checkout). A library installed through the editor: the version Library Manager shows, or the `LibraryVersion` constant (`NetworkStorage.LibraryVersion`). Also note the package revision from the library's `.version` (`1.0.<revision>`) when you have it|`library`, `librarySource` (`git:<sha>`, `library-manager` or `release:<tag>`), `revision`|
|Server build|`GET /v3/server-info`: `version`, `kind` (`self-hosted` or `hosted`) and `database`. Hosted runs record `kind: hosted`; for a self-hosted server also record the commit if it is not a release build|`server`, `kind`, `database`, `commit`|
|Operating system|The machine the editor ran on, e.g. `Windows 11 23H2`|`os`|
|Result|`pass` only when every box in the sections you ran is ticked on this setup; otherwise `fail`. No editor run: `ci-only`|`result`|

The server also logs the library version and engine build it receives
(`x-ns-library-version`, `x-ns-engine-version`; see
[game-client.md](game-client.md#library-compatibility)). After the run, the project's
**Logs** tab should show the same library version and engine build you wrote down. If it shows
`legacy`, the library under test does not report its version and the run does not exercise the
version contract.

## Recording the result

Add one row to `tested` in `compat/library-matrix.json` per server build, library, engine,
operating system and database combination you ran, in the PR that records the run
(the file is checked against `compat/library-matrix.schema.json` by `dotnet test`):

```json
{
  "server": "0.5.0",
  "commit": "6386646",
  "kind": "self-hosted",
  "library": "1.1.0",
  "librarySource": "library-manager",
  "revision": 310500,
  "engine": "26.10.02",
  "os": "Windows 11 23H2",
  "database": ["sqlite"],
  "result": "pass",
  "date": "2026-10-20",
  "evidence": ["sbox-cool/sbox-network-storage-server#54", "sbox-cool/sbox-network-storage#5"]
}
```

- `engine` and `os` are optional in the schema only for `ci-only` rows. A `pass` row without
  `engine`, `os`, `kind` and `librarySource` fails the schema check, and so does one without
  `database` and `evidence`.
- Do not write free text results into issues or PR descriptions instead of the row. Link the row's PR
  or issue from `evidence`, and put the log excerpts and the filled-in checklist there.
- `scripts/compat-tested-with.ps1 -Version 0.5.0` prints the "Tested with" table for release notes
  from these rows; the release steps are in [releasing.md](releasing.md#release-evidence).

## A. Library install and update

- [ ] Library Manager lists a version of 1.0.309133 or newer and updates a project that
      has 1.0.165xxx installed, with no `Constructor on type 'SyncToolWindow' not found`.
- [ ] Editor starts with 0 `Error |` lines in `logs/sbox-dev.log`.
- [ ] Setup window saves and shows **Connected**.

## B. Sync Tool against the server

- [ ] Handshake: `POST /v3/manage/{id}/package-sync` and `GET .../game-package` return
      200 and the window shows "Last synced".
- [ ] Scaffold: the Sync window's sample-project scaffold writes endpoints using the documented
      `check` / `onFail` condition shape (library PR #5), not `field` / `operator`.
- [ ] Push All: preflight 200, `PUT .../sync` 200, dashboard shows the counts.
- [ ] Preflight on a hand-written condition with no `check` shows a `MISSING_CHECK`
      warning (server PR #49) and the push still succeeds.
- [ ] Sync window lists both `*.json` and `*.yml` resources.
- [ ] Push Staged (`x-ns-publish-target: next`) lands in the staged revision and
      Promote makes it live.
- [ ] Test Live: every scaffold endpoint passes `auto-test`; a failing endpoint shows
      `result.body.error` in Test Results, not only "Endpoint returned HTTP 400.".
- [ ] Single-resource push of one endpoint, one collection and one workflow.

## C. Play mode (runtime calls with the real library)

- [ ] `NetworkStorage.Configure(projectId, publicKey, baseUrl)` plus `revision-init`
      returns `ok` and logs the revision lines.
- [ ] `SaveDocument` then `GetDocument` round trip on a per-steamid collection.
- [ ] `CallEndpoint` success, and an error path whose `code` and `message` reach
      `TryGetLastEndpointError`.
- [ ] `inc` / `set` ops through an endpoint change the stored document (v0.4.0
      stored `{ops:[...]}` instead; confirm the build under test applies them).
- [ ] Game values load (`GET /v3/values/{id}`) and `{{values.*}}` resolves in an endpoint.
- [ ] Query and leaderboard through `RunQuery`.
- [ ] Rate-limit behavior: collection `savesPerDay` returns the error the library shows.
- [ ] Heartbeat (`/storage/{id}/stats/heartbeat`) and analytics events
      (`/storage/{id}/analytics/events`) appear in the dashboard activity pages with the
      session id, library version and context fields.

## D. Security modes

- [ ] Auth sessions on: `auth-sessions/{id}/create`, later calls send `x-auth-session`,
      expiry forces a new session.
- [ ] Encrypted requests on: endpoint call succeeds; replaying the same envelope is
      rejected; tampering with `encryptedPayload` or `signature` is rejected.
- [ ] Both on together.
- [ ] Dedicated server with a secret key: secret-only endpoint works; the same call
      with only the public key is rejected.
- [ ] Authority check (dashboard overview card) matches what the project really allows:
      a `public` collection is flagged, an endpoint-only collection is not.

## E. Revision handling (needs server Phase 4)

- [ ] Published bundle sends `x-ns-revision-id`; editor and local play send none.
- [ ] After a package sync raises the live revision, an older client gets
      `revisionOutdated` from `revision-init`.
- [ ] Endpoint responses carry `_revisionStatus`; the in-game outdated panel,
      grace countdown and block-saves flag react as configured.

## F. Hosted name (needs the owner's approval)

- [ ] `sbox-ns dns enable --accept-letsencrypt-terms --email ...` on a host with
      public ports 80 and 443, then the editor connects through the HTTPS name.
- [ ] Same with the tunnel option on a host without open ports.

## G. Independent library/backend releases

- [ ] An older published game with no new identity headers still works after a
      backend update; replay the same save/load/endpoint flow before and after.
- [ ] New runtime and Sync Tool builds report library version/package revision
      separately from game revision. Observe server/version-status headers and
      **Project → Clients**; repeated calls from one player do not become
      multiple distinct players.
- [ ] `outdated`/`deprecated` shows an update recommendation without breaking
      old runtime calls. Library Manager/editor update instructions are tested
      in the [library tracker](https://github.com/sbox-cool/sbox-network-storage/issues/6).
- [ ] Preview a project minimum, verify the affected share, and confirm saving
      without acknowledgement is refused. An explicitly enabled minimum returns
      `LIBRARY_UNSUPPORTED`; clearing it restores acceptance. Advisory blocks
      return `LIBRARY_BLOCKED`. Pull, Test Live and revision-init remain usable.
- [ ] Record which database provider pairing passed (SQLite and PostgreSQL are
      separate pairings). Do not infer a result for a library/engine/provider
      that was not exercised.

Certify once per pairing: a `pass` row covers exactly the exercised server
commit, library/revision, engine and providers. Reuse that certification until
a fundamental change (request/response, error, auth, save/endpoint semantics,
defaults, endpoint or public-method add/remove/rename, identity/capability,
policy or migration assumption) forces re-testing; see
[certification](compatibility.md#certification-certify-once-re-test-on-fundamental-change).

## Library repo checks

- [ ] `UnitTests/` and `Tests/test_source_compiler.py` pass on a machine with the
      editor assemblies.
- [ ] `Code/obj` and `Editor/obj` are not tracked (`git rm -r --cached` if they are).
