#!/usr/bin/env bash
# Runs ON the disposable Vultr test VPS, as root, over SSH from .github/workflows/test-server.yml.
#
#   live-baseline-install.sh <public-url> [version]
#
# Wipes any previous install, then installs a published release with the repository's own
# install/install.sh (copied to /tmp/install.sh): signature check, service user, systemd unit,
# quickstart project. Records the installed version and project IDs so deploy-test-server.sh can
# prove an in-place upgrade kept them. The installer prints the new project's secret key, so its
# output is kept in /root/install.log and only a redacted tail is shown.
set -euo pipefail

public_url="${1:?usage: live-baseline-install.sh <public-url> [version]}"
version="${2:-}"
port=8080
user=sbox-ns
dirs=(--config-dir /etc/sbox-ns --data-dir /var/lib/sbox-ns)

systemctl stop sbox-ns 2>/dev/null || true
systemctl disable sbox-ns 2>/dev/null || true
rm -f /etc/systemd/system/sbox-ns.service
systemctl daemon-reload
rm -rf /etc/sbox-ns /var/lib/sbox-ns /usr/local/bin/sbox-ns /root/live-fixture.env /root/baseline-projects.txt /root/baseline-version

command -v openssl >/dev/null || { apt-get update -qq && apt-get install -y -qq openssl; }

redact() { sed -E 's/sbox_[A-Za-z0-9_-]+/sbox_***/g'; }
log=/root/install.log
install -m 0600 /dev/null "$log"
if ! env ${version:+SBOX_NS_VERSION="$version"} SBOX_NS_PROJECT="Baseline $(date -u +%H%M%S)" \
        SBOX_NS_PUBLIC_URL="$public_url" sh /tmp/install.sh >"$log" 2>&1; then
    tail -n 40 "$log" | redact >&2
    echo "install.sh failed" >&2
    exit 1
fi
tail -n 12 "$log" | redact

for _ in $(seq 1 30); do
    if curl -fsS -m 3 "http://127.0.0.1:$port/health" >/dev/null 2>&1; then break; fi
    sleep 2
done
health="$(curl -fsS -m 3 "http://127.0.0.1:$port/health")"
sed -n 's/.*"version":"\([^"]*\)".*/\1/p' <<<"$health" > /root/baseline-version
[ -s /root/baseline-version ] || { echo "cannot read the installed version: $health" >&2; exit 1; }

runuser -u "$user" -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/sbox-ns/.net \
    /usr/local/bin/sbox-ns project list "${dirs[@]}" | sed -n 's/^\(proj_[a-z0-9]*\) .*/\1/p' > /root/baseline-projects.txt
[ -s /root/baseline-projects.txt ] || { echo "the installer created no project" >&2; exit 1; }
echo "baseline: $(cat /root/baseline-version), $(wc -l < /root/baseline-projects.txt | tr -d ' ') project(s)"
