# Releasing

## Version numbers

Releases are numbered `0.x.z`.

- **Patch (`z`) is the default.** Every release bumps `z`: `0.5.0`, `0.5.1`, `0.5.2`.
- **Minor (`x`) changes only for an operator-breaking change**: a moved config or data
  folder, a removed or renamed setting, or anything an operator must act on during the update.
  The release notes for such a release say what to do, and `.github/release-meta.json` sets
  `migrationRequired` and, where needed, `minUpgradableFrom`.

Pre-releases use a suffix: `0.5.1-rc.1`. They are published as GitHub pre-releases and the
container image is not tagged `latest`.

## Where the version comes from

The git tag. Pushing `v0.5.1` makes the release workflow build `0.5.1` into the binary
(`-p:Version`), the archives, the image tags and `release.json`. The workflow rejects a tag
that is not `vMAJOR.MINOR.PATCH[-pre]`. The `<Version>0.1.0-dev</Version>` in the server
project is only the value for local builds.

## Cutting a release

1. Move the `CHANGELOG.md` **Unreleased** entries under a new `## [0.x.z] - YYYY-MM-DD` heading.
2. Set `.github/release-meta.json` when the release is a security update (`security`), needs a
   migration (`migrationRequired`) or cannot be installed over very old versions (`minUpgradableFrom`).
3. If the release adds a migration, check its `additive`/`breaking` tag in both drivers
   (`docs/database.md`, "Rollback-safe migrations"). Run `scripts/rollback-notes.sh` on the
   release commit and add its output to the release notes: the line
   "Rolls back without data loss to: 0.x.y, …" lists the earlier releases that
   `sbox-ns rollback --keep-data` can return to without restoring a backup. The same tags
   drive the runtime checks: `rollback --keep-data` and pinned downgrades proceed only when
   the target binary's supported schema covers the database's `min_binary_schema` floor,
   confirmed both by the installed binary and by the target's own `sbox-ns compatibility` probe.
4. Run the **Install matrix** workflow on the commit (Actions > Install matrix > Run workflow)
   and wait for it to pass. It replaces the manual VM checklist; see below.
5. Confirm the compatibility evidence, then tag. `compat/library-matrix.json` must have a `pass`
   row for this server version (or release commit) and the matrix's `recommended` library, or the
   release must be marked `ci-only`. See [Release evidence](#release-evidence). Releases with no
   editor-GUI surface take the code track: the green Compat gate on the release commit plus the
   recorded `ci-only` row (via explicit `--allow-ci-only`) is the certification, still called out
   as ci-only in the notes.
6. Tag the commit on `main` and push the tag: `git tag v0.x.z && git push origin v0.x.z`.
7. The release workflow runs the tests, the HTTP smoke gate, the stable-reference parity diff and
   the **Compat** gate (`.github/workflows/compat-matrix.yml`): the corpus diffed against the
   stable reference and the previous release, and the real library inside s&box for every library
   in [`compat/library-targets.json`](../compat/library-targets.json) against this release's
   source. A library that worked on the previous release and fails on this one stops the release.
   Then it builds, signs and publishes. A failing gate stops the release.
8. Check the published assets: `SHA256SUMS`, `SHA256SUMS.p256.sig`, the SBOMs and the
   provenance attestations. `docs/self-hosting.md` describes how operators verify them.

## Release evidence

Every release can say which library, on which engine build, it was tested with. The source is
the `tested` list in [`compat/library-matrix.json`](../compat/library-matrix.json); editor runs
add their rows as described in [editor-test-plan.md](editor-test-plan.md#recording-the-result).

The Compat gate also writes machine-readable evidence into the signed `release.json` asset:
`testedLibraries` lists each library it ran (`library`, sbox.game `revision`, `source`, the
Facepunch/sbox-public `engine` commit, `target: dedicated`) with `result` `ci-only` (passed) or
`fail`. Libraries that also fail on the previous release (an engine or library problem) are left
out. `sbox-ns update` compares this list with the library versions the owner's games actually
send before installing (docs/self-hosting.md, "Updates"). These are hosted-runner results, never
an editor `pass`.

1. Generate the release-notes table and put it in the GitHub Release notes:

   ```sh
   pwsh scripts/compat-tested-with.ps1 -Version 0.x.z -Commit <release commit sha> > tested-with.md
   ```

   On Windows, `powershell -File scripts/compat-tested-with.ps1 -Version 0.x.z` works too. It
   lists the rows for that server version (and rows recorded against the commit) with library,
   revision, engine, OS, server kind, database, result and evidence links.
   `scripts/compat-tested-with.ps1` is a notes renderer only. Its `-Gate` switch is a
   convenience subset of the evidence-row check below (a `pass` row for the `recommended`
   library, unless `-CiOnly`); it is not the release gate.
2. Release gate: the authoritative gate is `scripts/compat-release.py`:

   ```sh
   python3 scripts/compat-release.py --matrix /path/to/release-evidence.json \
     --server 0.x.y --commit TESTED_SOURCE_COMMIT \
     --baseline /path/to/previous-release-matrix.json \
     --policy-source compat/library-matrix.json \
     --rollback-report /path/to/target-compatibility.json
   ```

   It validates the matrix shape, the version ranges, the deprecation sunsets, the
   exact-commit evidence rows, that detached evidence changes only `tested` rows, that
   announced sunsets are never shortened, and the rollback reports. The release needs a
   matching-commit `pass` for the `recommended` library (or an explicit `--allow-ci-only`
   with a matching `ci-only` row, called out in the notes). A failing gate stops the release.
3. Changing the matrix itself (`minimum`, `recommended`, `deprecated`, `blocked`) is a server
   change like any other. Support a replaced library version (or API form) for **at least six
   calendar months after its successor ships**, with **at least 90 days of published removal
   notice**. The later date wins. Announced sunsets cannot be shortened. Moving `minimum`
   additionally needs a **minor** server version bump and usage numbers showing how much
   live traffic it affects. `blocked` is the only immediate path and is for security or data-loss
   problems. The gate enforces the window: a deprecated library entry superseded by a newer
   supported entry must sunset no earlier than six months after the successor's recorded
   `released` date, and, when the entry records an `announced` date, no earlier than 90 days
   after it.
4. A library release adds its row to `libraries` (with `minServer`) in the same PR as, or right
   after, the library's own GitHub Release.

## Install matrix

`.github/workflows/install-matrix.yml` runs on pull requests that touch the installers, the
updater, the CLI, configuration or `scripts/ci/`, every night, and on demand. Each job builds
two releases from the commit (`90.0.0` and `90.0.1`), signs them with a key generated for that
run and pinned into the installers and binary by `scripts/pin-release-keys.sh`, and serves them
from a local fixture that stands in for GitHub Releases and the sboxcool.com feed
(`scripts/ci/release_fixture.py`, trusted through a throwaway CA). The real installers and the
real `sbox-ns update` then run unchanged, signature checks included.

| Job | Checks |
| --- | --- |
| Ubuntu 22.04, Ubuntu 24.04, Ubuntu 24.04 arm64 | Fresh install, and upgrades from the newest release (`0.5.4`) and from the oldest one owners may still run (`0.4.0`, which also exercises the layout migration); then `update`, `rollback`, port 80 through the capability drop-in, a named instance, and no seccomp, `ProtectProc` or AppArmor denials in the journal |
| Debian 12 | The same scenarios in a privileged container running systemd. GitHub has no Debian runner; the kernel is the host's, so the journal check is weaker there |
| Windows | `install.ps1` under Windows PowerShell 5.1 and PowerShell 7, then the Windows service |

Not covered: the hosted tunnel and signed DNS modes, which need the sboxcool registry. After each
release, bump the newest `previous` value in the workflow matrix so one upgrade path always starts
from the latest release; keep `0.4.0` until releases before the state layout are out of support.
