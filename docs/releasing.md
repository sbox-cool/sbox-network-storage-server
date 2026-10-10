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
3. Run the **Install matrix** workflow on the commit (Actions > Install matrix > Run workflow)
   and wait for it to pass. It replaces the manual VM checklist; see below.
4. Tag the commit on `main` and push the tag: `git tag v0.x.z && git push origin v0.x.z`.
5. The release workflow runs the tests, the HTTP smoke gate and the stable-reference parity
   diff, then builds, signs and publishes. A failing gate stops the release.
6. Check the published assets: `SHA256SUMS`, `SHA256SUMS.p256.sig`, the SBOMs and the
   provenance attestations. `docs/self-hosting.md` describes how operators verify them.

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
| Ubuntu 22.04, Ubuntu 24.04, Ubuntu 24.04 arm64 | Fresh install, and upgrade from the previous release (`0.4.0`) with layout migration; then `update`, `rollback`, port 80 through the capability drop-in, a named instance, and no seccomp, `ProtectProc` or AppArmor denials in the journal |
| Debian 12 | The same scenarios in a privileged container running systemd. GitHub has no Debian runner; the kernel is the host's, so the journal check is weaker there |
| Windows | `install.ps1` under Windows PowerShell 5.1 and PowerShell 7, then the Windows service |

Not covered: the hosted tunnel and signed DNS modes, which need the sboxcool registry. Update
`PREVIOUS` in the workflow after each release so the upgrade path starts from the newest one.
