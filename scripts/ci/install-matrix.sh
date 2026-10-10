#!/usr/bin/env bash
# Real install, upgrade, update and rollback on a disposable Linux machine with systemd (CI only).
#
#   sudo scripts/ci/install-matrix.sh --fixture DIR --scenario fresh|upgrade --candidate A --next B [--previous 0.4.0]
#
# DIR comes from scripts/ci/build-release-fixture.sh. This script trusts its CA, points github.com,
# api.github.com and sboxcool.com at a local fixture server, and then uses only the real installer and
# the real `sbox-ns update` / `sbox-ns rollback`. It changes system users, units and /etc/hosts:
# never run it outside a throwaway VM or container.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fixture=''; scenario=''; candidate=''; next=''; previous=''
while (($#)); do
  case "$1" in
    --fixture) fixture="$2"; shift 2 ;;
    --scenario) scenario="$2"; shift 2 ;;
    --candidate) candidate="$2"; shift 2 ;;
    --next) next="$2"; shift 2 ;;
    --previous) previous="$2"; shift 2 ;;
    *) sed -n '2,10p' "$0" >&2; exit 2 ;;
  esac
done
[[ "$(id -u)" == 0 && -d "$fixture" && -n "$candidate" && -n "$next" ]] || { sed -n '2,10p' "$0" >&2; exit 2; }
[[ "$scenario" == fresh || ( "$scenario" == upgrade && -n "$previous" ) ]] || { echo "upgrade needs --previous" >&2; exit 2; }

# Root's own single-file extraction folder; commands the binary runs as the service account use <data dir>/.net.
export DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/tmp/sbox-ns-root-extract
step() { printf '\n== %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; journalctl -u sbox-ns -u 'sbox-ns@*' -u sbox-ns-update --no-pager -n 80 >&2 || true; exit 1; }
health() {
  local url="${1:-http://127.0.0.1:8080}"
  for _ in $(seq 60); do curl -fsS "$url/health" > /dev/null 2>&1 && return 0; sleep 1; done
  fail "no health response from $url"
}
running_version() { curl -fsS "${1:-http://127.0.0.1:8080}/v3/server-info" | python3 -c 'import json,sys; print(json.load(sys.stdin)["version"])'; }
expect_version() {
  local actual; actual="$(running_version "${2:-}")"
  [[ "$actual" == "$1" ]] || fail "expected running version $1, got $actual"
  echo "running $actual"
}
expect_project() {
  sbox-ns project list --json 2>/dev/null | grep -q '"name": "Install Matrix"' \
    || sbox-ns project list 2>/dev/null | grep -q 'Install Matrix' \
    || fail "project 'Install Matrix' is missing"
}

step "Make the runner's binary folder look like a normal host"
# Hosted runner images ship /usr/local/bin writable by group or others. The updater refuses to replace a
# binary in such a folder (anyone in that group could swap it), which is correct, so give it the usual 0755.
chown root:root /usr/local/bin
chmod 0755 /usr/local/bin

step "Trust the fixture and route release hosts to it"
if command -v update-ca-certificates > /dev/null; then
  cp "$fixture/tls/ca.crt" /usr/local/share/ca-certificates/sbox-ns-install-matrix.crt
  update-ca-certificates > /dev/null
else
  fail "update-ca-certificates is required"
fi
cp /etc/hosts /etc/hosts.install-matrix
printf '127.0.0.1 github.com api.github.com sboxcool.com\n' >> /etc/hosts
python3 "$here/release_fixture.py" serve --root "$fixture" --cert "$fixture/tls/server.crt" --key "$fixture/tls/server.key" \
  > /var/log/release-fixture.log 2>&1 &
fixture_pid=$!
trap 'kill $fixture_pid 2>/dev/null || true; cp /etc/hosts.install-matrix /etc/hosts' EXIT
for _ in $(seq 30); do curl -fsS "https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v$candidate/SHA256SUMS" > /dev/null 2>&1 && break; sleep 0.5; done
curl -fsS "https://sboxcool.com/api/network-storage/releases/latest?channel=stable" > /dev/null || fail "fixture is not reachable over trusted HTTPS"

since="$(date '+%Y-%m-%d %H:%M:%S')"

if [[ "$scenario" == upgrade ]]; then
  step "Install the previous release v$previous with its own installer"
  curl -fsSL "https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v$previous/install.sh" \
    | env SBOX_NS_VERSION="$previous" SBOX_NS_PROJECT="Install Matrix" sh
  health
  expect_version "$previous"
  expect_project
fi

step "Install candidate v$candidate with the candidate installer ($scenario)"
curl -fsSL "https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v$candidate/install.sh" \
  | env SBOX_NS_VERSION="$candidate" SBOX_NS_PROJECT="Install Matrix" sh
health
expect_version "$candidate"
expect_project
[[ -f /var/lib/sbox-ns/state/.layout-version ]] || fail "layout marker missing: the data folder was not migrated to the state layout"
[[ "$(stat -c %U /var/lib/sbox-ns/state)" == sbox-ns ]] || fail "/var/lib/sbox-ns/state is not owned by sbox-ns"
[[ "$(stat -c %U:%G /etc/sbox-ns)" == root:sbox-ns ]] || fail "/etc/sbox-ns is not root:sbox-ns"
systemctl is-active --quiet sbox-ns || fail "sbox-ns.service is not active"

step "Update to v$next through the release feed"
sbox-ns update
health
expect_version "$next"
expect_project

step "Roll back to v$candidate"
sbox-ns rollback --yes
health
expect_version "$candidate"
expect_project

step "Low port: bind 80 through the capability drop-in"
sbox-ns config set server.listen 0.0.0.0:80 > /dev/null
sbox-ns service install > /dev/null
systemctl restart sbox-ns
health http://127.0.0.1:80
sbox-ns config set server.listen 0.0.0.0:8080 > /dev/null
sbox-ns service install > /dev/null
systemctl restart sbox-ns
health

step "Named instance on port 8082"
sbox-ns service install --instance matrix --port 8082
systemctl start sbox-ns@matrix
health http://127.0.0.1:8082
systemctl stop sbox-ns@matrix

step "Journal: no sandbox denials"
denials="$(journalctl --since "$since" --no-pager -o cat 2>/dev/null \
  | grep -Ei 'audit.*(type=1326|SECCOMP)|code=killed, status=31/SYS|ProtectProc|apparmor="DENIED"' || true)"
if [[ -n "$denials" ]]; then
  echo "$denials" >&2
  fail "the journal shows sandbox denials"
fi
echo "INSTALL MATRIX PASS ($scenario: ${previous:+v$previous -> }v$candidate -> v$next -> rollback)"
