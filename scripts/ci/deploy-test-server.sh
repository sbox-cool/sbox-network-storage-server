#!/usr/bin/env bash
# Runs ON the disposable Vultr test VPS, as root, over SSH from .github/workflows/test-server.yml.
#
#   deploy-test-server.sh [--reset] <sbox-ns-linux-x64.tar.gz> <public-url>   install a build (upgrade in place)
#   deploy-test-server.sh --no-install <public-url>                           keep what live-baseline-install.sh installed
#
# Installs the tarball's binary at /usr/local/bin/sbox-ns using the same Linux service layout as
# install/install.sh (config /etc/sbox-ns, data /var/lib/sbox-ns, unprivileged `sbox-ns` user),
# (re)starts the systemd service, and writes /root/live-fixture.env with a fresh fixture project
# and its keys for the parity corpus. The first run also does the SQLite setup; later runs only
# swap the binary, so projects and records survive and the schema migrates forward like a real
# update. `--reset` wipes config and data first (clean reinstall).
#
# If live-baseline-install.sh left a baseline snapshot, the run is an upgrade test: the version must
# change (unless --no-install) and every baseline project must still be listed afterwards.
# The box is a throwaway: never point production traffic at it.
set -euo pipefail

reset=0
install_binary=1
while [ "${1:-}" = "--reset" ] || [ "${1:-}" = "--no-install" ]; do
    case "$1" in
        --reset) reset=1 ;;
        --no-install) install_binary=0 ;;
    esac
    shift
done
usage="usage: deploy-test-server.sh [--reset] <tarball> <public-url> | --no-install <public-url>"
if [ "$install_binary" = 1 ]; then
    tarball="${1:?$usage}"
    public_url="${2:?$usage}"
else
    tarball=""
    public_url="${1:?$usage}"
fi
[ "$reset" = 0 ] || [ "$install_binary" = 1 ] || { echo "--reset needs a tarball to reinstall" >&2; exit 2; }

port=8080
user=sbox-ns
bin=/usr/local/bin/sbox-ns
config_dir=/etc/sbox-ns
data_dir=/var/lib/sbox-ns
dirs=(--config-dir "$config_dir" --data-dir "$data_dir")
baseline_projects=/root/baseline-projects.txt
baseline_version=/root/baseline-version

stage="$(mktemp -d)"
trap 'rm -rf "$stage" ${tarball:+"$tarball"}' EXIT

if ! id "$user" >/dev/null 2>&1; then
    printf '%s\n' 'u sbox-ns - "sbox Network Storage Server" /var/lib/sbox-ns /usr/sbin/nologin' \
        > /usr/lib/sysusers.d/sbox-ns.conf
    systemd-sysusers /usr/lib/sysusers.d/sbox-ns.conf
fi

# Runtime commands run as the service user; the single-file binary unpacks into the data dir.
ns() { runuser -u "$user" -- env "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$data_dir/.net" "$bin" "$@"; }

if [ "$install_binary" = 1 ]; then
    tar -xzf "$tarball" -C "$stage"
    test -x "$stage/sbox-ns"
    systemctl stop sbox-ns 2>/dev/null || true
    if [ "$reset" = 1 ]; then
        rm -rf "$config_dir" "$data_dir"
        rm -f "$baseline_projects" "$baseline_version"
    fi
    install -m 0755 "$stage/sbox-ns" "$bin"

    umask 0027
    install -d -o root -g "$user" -m 0750 "$config_dir"
    install -d -o "$user" -g "$user" -m 0750 "$data_dir" \
        "$data_dir/state" "$data_dir/state/secrets" "$data_dir/state/conf.d" "$data_dir/state/bin"

    if [ ! -f "$config_dir/server.toml" ]; then
        "$bin" setup --non-interactive --database sqlite --listen "[::]:$port" --public-url "$public_url" "${dirs[@]}"
        find "$config_dir" -type d -exec chown "root:$user" {} + -exec chmod 0750 {} +
        find "$config_dir" -type f -exec chown "root:$user" {} + -exec chmod 0640 {} +
    fi

    "$bin" service install "${dirs[@]}"
    "$bin" service start
else
    test -x "$bin"
    systemctl is-active --quiet sbox-ns
fi

for _ in $(seq 1 30); do
    if curl -fsS -m 3 "http://127.0.0.1:$port/health" >/dev/null 2>&1; then break; fi
    sleep 2
done
if ! curl -fsS -m 3 "http://127.0.0.1:$port/health" >/dev/null; then
    echo "server did not become healthy on :$port" >&2
    journalctl -u sbox-ns --no-pager -n 40 >&2 || true
    exit 1
fi

now_version="$(curl -fsS -m 5 "http://127.0.0.1:$port/health" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p')"

# Upgrade test: what the baseline install created must have survived the new build.
if [ -f "$baseline_projects" ]; then
    before="$(cat "$baseline_version" 2>/dev/null || echo unknown)"
    echo "upgrade: $before -> $now_version"
    if [ "$install_binary" = 1 ] && [ "$before" = "$now_version" ]; then
        echo "the version did not change; nothing was upgraded" >&2
        exit 1
    fi
    listed="$(ns project list "${dirs[@]}")"
    kept=0
    while read -r id; do
        [ -n "$id" ] || continue
        if ! grep -q "^$id " <<<"$listed"; then
            echo "baseline project $id is missing after the upgrade" >&2
            exit 1
        fi
        kept=$((kept + 1))
    done < "$baseline_projects"
    [ "$kept" -gt 0 ] || { echo "baseline snapshot lists no projects" >&2; exit 1; }
    echo "upgrade kept $kept baseline project(s)"
    rm -f "$baseline_projects" "$baseline_version"
fi

# Fresh fixture project per deploy, same shape as scripts/smoke.sh.
project_output="$(ns project create "Live test $(date -u +%Y%m%dT%H%M%SZ)" --key-mode public --require-sbox-auth false "${dirs[@]}")"
project="$(sed -n 's/^Project ID: //p' <<<"$project_output" | tr -d '\r' | head -n1)"
[ -n "$project" ] || { echo "cannot parse project id: $project_output" >&2; exit 1; }
parse_key() { sed -nE 's/^[[:space:]]+(sbox_[^[:space:]]+)[[:space:]]*$/\1/p' | tr -d '\r' | tail -n1; }
public="$(ns key create "$project" --type public "${dirs[@]}" | parse_key)"
secret="$(ns key create "$project" --type secret "${dirs[@]}" | parse_key)"
[ -n "$public" ] && [ -n "$secret" ] || { echo "cannot parse fixture keys" >&2; exit 1; }
install -m 0600 /dev/null /root/live-fixture.env
printf 'PROJECT_ID=%s\nPUBLIC_KEY=%s\nSECRET_KEY=%s\n' "$project" "$public" "$secret" > /root/live-fixture.env

curl -fsS -m 5 "http://127.0.0.1:$port/v3/server-info"; echo
