# Security policy

## Supported versions

Until 1.0, only the latest release receives security fixes. After 1.0 this
section will list supported release lines.

## Reporting a vulnerability

Please do **not** open a public issue for security problems.

Report privately through either channel:

1. **GitHub Security Advisories:** use
   [Report a vulnerability](https://github.com/sbox-cool/sbox-network-storage-server/security/advisories/new)
   on this repository.
2. **Email:** security@sbox.cool

Include the affected version (`sbox-ns version`), your platform and database,
steps to reproduce, and the impact you expect. Proof of concept code is welcome.

## What to expect

- Acknowledgement within 3 working days.
- An initial assessment and planned fix timeline within 10 working days.
- Credit in the release notes and advisory, unless you prefer to stay anonymous.

Fixed releases set `"security": true` in their `release.json`, so every running
server shows a security update notice (unless update checks are disabled).

## Scope

In scope: this repository, its release artifacts, the install scripts and the
container image `ghcr.io/sbox-cool/sbox-network-storage-server`.

Issues in the s&box client library belong to
[sbox-cool/sbox-network-storage](https://github.com/sbox-cool/sbox-network-storage).
Issues in the managed service at sbox.cool can also be sent to security@sbox.cool.

Please do not test against servers you do not operate.

## Verifying releases

Every release publishes `SHA256SUMS`, signed with Sigstore keyless signing
(`SHA256SUMS.sig`, `SHA256SUMS.pem`). The install scripts verify checksums
automatically. See [docs/self-hosting.md](docs/self-hosting.md#manual-install)
for manual verification with `cosign verify-blob`.
