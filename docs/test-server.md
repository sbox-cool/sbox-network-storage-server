# Live test server

A disposable Vultr VPS runs the current `main` over the real network so changes can be tested
beyond CI loopback. It is throwaway infrastructure: nothing on it is precious, never put real game
traffic or real secrets on it, and it serves plain HTTP on `:8080` with SQLite.

The workflow [`.github/workflows/test-server.yml`](../.github/workflows/test-server.yml) runs on
every push to `main` and on demand: `gh workflow run test-server.yml -f mode=<mode>`. Runs are
serialised by a concurrency group, so only one touches the VPS at a time. Every mode ends by
replaying `tests/parity/corpus` against `http://<host>:8080` with `sbox-ns-parity check`, using a
fresh fixture project the deploy script creates.

| Mode | What it does |
| --- | --- |
| `deploy` (push default) | Builds a self-contained `linux-x64` binary as `0.0.0-live.<run number>` and swaps it in, keeping the data. |
| `reinstall` | Wipes `/etc/sbox-ns` and `/var/lib/sbox-ns`, then installs this build from scratch. |
| `upgrade` | Wipes, installs the latest published release with the repository's `install/install.sh`, then updates it in place to this build. The version must change and the release's projects must survive the schema migration. |
| `installer` | Wipes, installs the latest published release with `install/install.sh` and tests that. |

[`scripts/ci/deploy-test-server.sh`](../scripts/ci/deploy-test-server.sh) and
[`scripts/ci/live-baseline-install.sh`](../scripts/ci/live-baseline-install.sh) run on the VPS.
They mirror the Linux layout of `install/install.sh` (`/etc/sbox-ns`, `/var/lib/sbox-ns`,
unprivileged `sbox-ns` user, systemd unit). The installer prints a secret key, so its output
stays in `/root/install.log` on the box with a redacted tail in the job log.

After `reinstall` (or a first `deploy`) the server listens on `[::]:8080` (dual-stack), so it is also
reachable over IPv6 at `http://[2001:19f0:5400:243f:5400:6ff:fedf:6b1a]:8080`. After `upgrade` or
`installer` it keeps the installer's `0.0.0.0:8080` (IPv4 only) until the next `reinstall`. GitHub
runners and many home networks have no IPv6 route, so CI always uses the IPv4 address.

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
