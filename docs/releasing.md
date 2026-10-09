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
3. Tag the commit on `main` and push the tag: `git tag v0.x.z && git push origin v0.x.z`.
4. The release workflow runs the tests, the HTTP smoke gate and the stable-reference parity
   diff, then builds, signs and publishes. A failing gate stops the release.
5. Check the published assets: `SHA256SUMS`, `SHA256SUMS.p256.sig`, the SBOMs and the
   provenance attestations. `docs/self-hosting.md` describes how operators verify them.
