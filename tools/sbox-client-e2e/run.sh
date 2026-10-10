#!/usr/bin/env bash
# End-to-end check that the real s&box Network Storage client library works against this server.
#  1. Builds this repo and starts a fresh sbox-ns on 127.0.0.1:$PORT.
#  2. Bootstraps a project (auth disabled) from the parity corpus: game values, collections, endpoints, queries.
#  3. Runs the client library inside a headless engine (MSTest + TestAppSystem): ClientAlignmentTests.
#  4. Runs a real s&box project on the dedicated server (sbox-server) with a secret key.
# Needs a built s&box engine (github.com/Facepunch/sbox-public, ./Setup.sh) and .NET 10.
# Usage: tools/sbox-client-e2e/run.sh [--engine <sbox-public>/game] [--library <sbox-network-storage>] [--no-build] [--port 8080]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)" # physical path: /tmp vs /private/tmp breaks copy-local
root="$(cd "$here/../.." && pwd -P)"
engine="${SBOX_GAME:-$HOME/Projects/sbox-public/game}"
library="${NS_LIBRARY:-}"
port=8080; build=true
while (($#)); do
  case "$1" in
    --engine) engine="$2"; shift 2 ;;
    --library) library="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --no-build) build=false; shift ;;
    *) sed -n '2,9p' "$0" >&2; exit 2 ;;
  esac
done
# Game code may reach loopback only on 80/443/8080/8443 (Sandbox.Http); the dedicated server also gets -allowlocalhttp.
case "$port" in 80|443|8080|8443) ;; *) echo "port must be 80, 443, 8080 or 8443 (s&box loopback allowlist)" >&2; exit 2 ;; esac
[[ -f "$engine/bin/managed/Sandbox.Engine.dll" ]] || { echo "no built s&box engine at $engine (pass --engine)" >&2; exit 2; }

work="$(mktemp -d)"; pid=''; dpid=''
cleanup() {
  [[ -n "$dpid" ]] && { kill "$dpid" 2>/dev/null || true; wait "$dpid" 2>/dev/null || true; }
  [[ -n "$pid" ]] && { kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; }
  rm -rf "$work"
}
trap cleanup EXIT

if [[ -z "$library" ]]; then
  library="$work/sbox-network-storage"
  git clone -q --depth 1 https://github.com/sbox-cool/sbox-network-storage.git "$library"
fi
echo "client library: $library ($(git -C "$library" log -1 --format='%h %s' 2>/dev/null || echo 'not a git checkout'))"

if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then echo "port $port is in use" >&2; exit 1; fi
$build && dotnet build "$root/SboxNetworkStorage.sln" -c Release -v q -nologo
ns=(dotnet "$root/src/SboxNetworkStorage.Server/bin/Release/net10.0/sbox-ns.dll")
cli() { "${ns[@]}" "$@" --config-dir "$work/config" --data-dir "$work/data"; }
key() { sed -nE 's/^[[:space:]]+(sbox_[^[:space:]]+)[[:space:]]*$/\1/p'; }

cli setup --non-interactive --listen "127.0.0.1:$port" > /dev/null
project="$(cli project create 'Client E2E' --key-mode public --require-sbox-auth false | sed -n 's/^Project ID: //p')"
public="$(cli key create "$project" --type public | key)"
secret="$(cli key create "$project" --type secret | key)"
("${ns[@]}" start --config-dir "$work/config" --data-dir "$work/data" > "$work/server.log" 2>&1) & pid=$!
for _ in $(seq 120); do curl -fs "http://127.0.0.1:$port/health" > /dev/null && break; sleep 0.5; done
curl -fs "http://127.0.0.1:$port/health" > /dev/null || { cat "$work/server.log" >&2; exit 1; }

# Project resources come from the parity corpus. package-sync is left out on purpose: the
# revision-init test covers projects that never synced a game package.
mkdir -p "$work/corpus"
python3 - "$root/tests/parity/corpus/01-management-push.json" "$work/corpus/01-bootstrap.json" <<'PY'
import json, sys
corpus = json.load(open(sys.argv[1]))
keep = {"manage-game-values", "manage-push-all", "manage-queries"}
corpus["scenarios"] = [s for s in corpus["scenarios"] if s["name"] in keep]
json.dump(corpus, open(sys.argv[2], "w"))
PY
dotnet "$root/tools/SboxNetworkStorage.Parity/bin/Release/net10.0/sbox-ns-parity.dll" check \
  --target "http://127.0.0.1:$port" --project-id "$project" --public-key "$public" --secret-key "$secret" \
  --corpus "$work/corpus" > "$work/bootstrap.log" || { cat "$work/bootstrap.log" >&2; echo "project bootstrap failed" >&2; exit 1; }

status=0
echo "== client library in a headless engine (ClientAlignmentTests)"
FACEPUNCH_ENGINE="$engine" NS_E2E_PROJECT="$project" NS_E2E_KEY="$public" NS_E2E_BASEURL="http://127.0.0.1:$port" \
  dotnet test "$here/runner/NsE2E.csproj" -nologo --logger "console;verbosity=normal" \
  -p:SboxGame="$engine" -p:NsLibrary="$library" || status=1

echo "== real project on the dedicated server (secret key)"
cp -R "$here/game" "$work/game"
mkdir -p "$work/game/Libraries"
cp -R "$library" "$work/game/Libraries/network-storage"
rm -rf "$work/game/Libraries/network-storage/.git"
log="$work/sbox-server.log"
(cd "$engine" && exec ./sbox-server +game "$work/game/nse2e.sbproj" \
  +nse2e_project "$project" +nse2e_key "$public" +nse2e_url "http://127.0.0.1:$port" \
  +network_storage_secret_key "$secret" -allowlocalhttp > "$log" 2>&1) & dpid=$!
result=''
for _ in $(seq 240); do
  result="$(grep -o '\[NSE2E\] RESULT [A-Z]*' "$log" || true)"
  [[ -n "$result" ]] && break
  kill -0 "$dpid" 2>/dev/null || break
  sleep 0.5
done
grep -E 'NSE2E|NetworkStorage\]' "$log" | cut -c1-300 || true
if [[ "$result" != '[NSE2E] RESULT PASS' ]]; then echo "dedicated-server probe failed: ${result:-no result}" >&2; status=1; fi

if ((status == 0)); then echo "CLIENT E2E PASS"; else echo "CLIENT E2E FAIL; server log:" >&2; tail -n 50 "$work/server.log" >&2; fi
exit "$status"
