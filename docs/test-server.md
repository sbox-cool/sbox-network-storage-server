# Live test server

A disposable Vultr VPS runs the current `main` over the real network so changes can be tested
beyond CI loopback. It is throwaway infrastructure: nothing on it is precious, never put real game
traffic or real secrets on it, and it serves plain HTTP on `:8080` with SQLite.

The workflow [`.github/workflows/test-server.yml`](../.github/workflows/test-server.yml) runs on
every push to `main` and on demand (`gh workflow run test-server.yml -f reset=true` wipes config and
data first). It publishes a self-contained `linux-x64` build as `0.0.0-live.<run number>`, copies it
to the VPS over SSH, and runs [`scripts/ci/deploy-test-server.sh`](../scripts/ci/deploy-test-server.sh)
there. That script mirrors the Linux layout of `install/install.sh` (`/etc/sbox-ns`,
`/var/lib/sbox-ns`, unprivileged `sbox-ns` user, systemd unit), creates a fresh fixture project and
keys, and the workflow replays `tests/parity/corpus` against `http://<host>:8080` with
`sbox-ns-parity check`.

Repository secrets: `TEST_SERVER_HOST` (public IPv4), `TEST_SERVER_SSH_KEY` (private deploy key for
root), `TEST_SERVER_KNOWN_HOSTS` (pinned `ssh-keyscan -t ed25519` line). Point a game at it with
`NetworkStorage.Configure(projectId, publicKey, "http://<host>:8080")`; read the fixture keys from
`/root/live-fixture.env` on the box.

This is an `ci-only` result in the sense of [AGENTS.md](../AGENTS.md): HTTP replays are never a
substitute for a real s&box editor run.

## Rebuilding the VPS

The instance is Debian 13 on Vultr with a reserved IPv4 (Vultr instances without one are
IPv6-only, which GitHub-hosted runners cannot reach and `github.com` cannot be reached from).
A reserved IPv4 on Vultr needs two guest-side fixes, both applied on the current box:

- DHCP gives no IPv4 default route; `/etc/dhcpcd.exit-hook` adds
  `default via <gateway of 169.254.169.254> src <reserved ip>`. Only the reserved address is NATed
  to the internet, so the route must use it as source.
- Add a 1 GB swap file (the instance has 512 MB RAM), `ufw allow 22/tcp` and `8080/tcp`, and set
  `PasswordAuthentication no` in both `/etc/ssh/sshd_config` and
  `/etc/ssh/sshd_config.d/50-cloud-init.conf` (cloud-init's drop-in wins otherwise).
