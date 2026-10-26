# sbox-cli end-to-end run

Drives the real [Network Storage library](https://github.com/sbox-cool/sbox-network-storage) inside
s&box through **sbox-cli** (`<s&box>/bin/managed/sbox-cli`, s&box 26.10.13 or newer), against
`sbox-ns`, and writes the evidence an editor run needs: engine build, library version/source/revision,
server version/kind/database/commit, OS, per-check results and a draft `tested` row for
[`compat/library-matrix.json`](../../compat/library-matrix.json).

```sh
# Windows, s&box from Steam, editor + dedicated server, library main, server from this checkout
dotnet run tools/sbox-cli-e2e/run.cs

# the published sbox.game package (Library Manager install inside the editor)
dotnet run tools/sbox-cli-e2e/run.cs -- --library package

# an exact published revision, by its GitHub release tag
dotnet run tools/sbox-cli-e2e/run.cs -- --library r423774

# any OS with a built engine: dedicated server only (no editor, so the row is ci-only)
dotnet run tools/sbox-cli-e2e/run.cs -- --engine ~/Projects/sbox-public/game --targets dedicated
```

Needs the .NET 10 SDK and git. The editor target also needs a running, logged-in Steam client: the
editor refuses to start without one ("We need Steam!"), which is why GitHub-hosted runners cannot
run it.

## CI

The **Compat** workflow ([`.github/workflows/compat-matrix.yml`](../../.github/workflows/compat-matrix.yml),
driven by [`scripts/ci/compat-engine.ps1`](../../scripts/ci/compat-engine.ps1)) runs the dedicated
target on `windows-latest` for every library in
[`compat/library-targets.json`](../../compat/library-targets.json) (the current sbox.game revision,
library `main`, and older published revisions games still embed): on pull requests that touch the
server, on main, nightly, on demand, and as a release gate. A library that fails against the change
is re-run against the latest release (`--server-binary`): passing there makes it a **regression**
that fails the check; failing on both is reported as an engine or library problem. Each run builds
the newest [Facepunch/sbox-public](https://github.com/Facepunch/sbox-public) engine from source
(`Setup.bat`, no pin), so new engine releases are picked up without changes here. Source builds
report engine version `0000000`; the evidence records the sbox-public commit instead. The rows it
writes are `ci-only`.

## What it does

1. Builds this checkout and starts `sbox-ns` on `127.0.0.1:8080` with a throwaway config and data
   folder, then `quickstart` creates a project (s&box auth off unless `--require-sbox-auth`) with a
   public and a secret key. `--server-binary <path>` does the same with a prebuilt `sbox-ns` (for
   example an unpacked release, to compare a change against what owners run today).
   `--server-url` with `--project/--public-key/--secret-key` tests an existing server instead
   (PostgreSQL, or a hosted backend).
2. Copies [`fixture/`](fixture) (game project `compat_probe` with endpoints, collections and game
   values as Sync Tool sources) and installs the library into `Libraries/sboxcool.network-storage`
   from `main`, `git:<ref>`, `r<revision>`, a local checkout (tracked files only, like a clean
   `git clone`) or, with `--library package`, the published package through
   `Editor.LibrarySystem.Install`, the same whole-folder installer the Library Manager button uses.
   The package leg first installs revision 165702 when available, then updates it to the latest
   revision, restarts the editor (sbox-cool/sbox-network-storage#106: an in-session update leaves the Network Storage menu
   and toolset unregistered until restart, so the editor never goes idle), and checks compilation.
   It does not use the unrelated asset-only `install_package` tool.
   With `--attach`, the runner never closes the user's editor: after updating, it stops with
   instructions to restart manually and rerun with `--library latest --attach`.
3. **Editor** (Windows launches `sbox-dev -project`; on other systems open the fixture yourself and
   pass `--attach`): waits for the compile, then with the library's `network_storage` toolset
   (`ns_editor_status`, `ns_sync_push` with post-push verification, `ns_auto_test`) or, for older
   libraries, `run_csharp` with the Sync Tool API. Then `play_start` and real library calls:
   endpoint success and input body, the `PLAYER_EXISTS` error reaching `TryGetLastEndpointError`, an
   `inc` op with `{{values.*}}`, game values, and the secret-key gate.
4. **Dedicated server**: `sbox-server +game compat_probe.sbproj -mcp -allowlocalhttp` with the secret
   key, attached with `sbox-cli --connect <pid>`: endpoint calls, game values and a
   `requiresSecretKey` endpoint passing with the secret key. Without the editor target the runner
   pushes the fixture sources itself, as the Sync Tool would.
   With `endpoint-js` and a library supporting separate script files, the fixture adds JS and TS
   envelopes plus sibling scripts. Editor runs use the library's source loader; dedicated-only runs
   inline these controlled fixture files before HTTP push. Both execute the pushed scripts through
   the real library and verify that the management pull preserves each runtime and script body.
   This headless contract check complements, rather than replaces, the native Sync Tool pull/conflict UI check.
   When both the library and server offer v2, required checks select `v2-required` and
   exercise an encrypted echo/response, a no-input call, refusal after changing a previously
   valid signing-key pin, recovery with the correct pin, request-key rotation with zero
   grace followed by the library's signed-config refresh/retry, and v2→v1→v2 switching
   without reconfiguring the cached caller. Older library/server pairs explicitly report
   that v2 was not exercised, while retaining the legacy switching checks.
   The signing-key probe runs when the library follows signing-key rotations (`ResetSigningKeyTrust`)
   and the server CLI can rotate the signing key; otherwise it reports not exercised. It trusts signing
   key A on first use, follows an announced A → B rotation with a one-hour grace, refuses an unannounced
   B → C rotation with zero grace (`ENCRYPTION_SIGNING_KEY_CHANGED` or `ENCRYPTION_SIGNING_KEY_ROTATION_INVALID`),
   and restores the caller with an explicit pin on the current key. Each rotation forces a fresh
   signed-config fetch, so a still-cached config cannot make the announcement check pass trivially.
   Then the **rate-limit probe**: the runner sets `server.limits.game_burst` and
   `server.limits.game_per_second` to 1 with `sbox-ns config set`, restarts this server, and makes
   two game-values GETs, five concurrent GETs and one POST `ping` from the game. The second GET must
   succeed after its 429 retry, the burst must mix successes with `RATE_LIMITED`, and the POST must
   come back `RATE_LIMITED` unretried. Required for every library source containing the bounded retry implementation, including local PR candidates; older refs report it.
   The runner discovers these knobs through `config show`. Supported older binaries without them
   keep all ordinary runtime/encryption checks, but record the low-limit probe as not exercised;
   latest/main must still pass the bounded-retry checks when they expose the knobs.
5. Reads what the server recorded (`sbox-ns project clients`, `dev logs_requests`) and checks the
   library version header arrived.

Every sbox-cli call has a hard timeout. Exit code 0 means every required check passed.

## Output

`artifacts/sbox-cli-e2e/<timestamp>/` (or `--out`):

| File | Contents |
|---|---|
| `summary.md` | identity, the checklist (sections from [editor-test-plan.md](../../docs/editor-test-plan.md)), observations, the draft row |
| `evidence.json` | the same plus every tool call and its result (keys redacted) |
| `tested-row.json` | draft `tested` row; add `--evidence` links (the PR or issue holding `summary.md`) before committing it |
| `sbox-ns.log`, `sbox-server.log`, `editor-console.txt`, `dedicated-console.txt`, `server-clients.json`, `server-requests.json`, `sbox-cli.log` | raw logs |

The row's `result` is `pass` or `fail` only when the editor ran; a dedicated-only run is `ci-only`.
A `pass` covers the sections listed in its `notes`, not the whole
[editor test plan](../../docs/editor-test-plan.md): the Sync Tool window UI, Library Manager update
prompts, security modes other than the secret-key gate, and revision handling still need the manual
sections.
