#!/usr/bin/env bash
set -euo pipefail
# A fresh project per invocation; --postgres must refer to a disposable database.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
postgres=''; binary=''; out=''; port=18421; no_build=false
while (($#)); do
  case "$1" in
    --postgres) postgres="$2"; shift 2 ;;
    --binary) binary="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --no-build) no_build=true; shift ;;
    *) echo "Usage: bash scripts/smoke.sh [--postgres CONNECTION] [--binary PATH] [--no-build] [--out JSON] [--port PORT]" >&2; exit 2 ;;
  esac
done
cd "$root"
if ! $no_build; then
  dotnet build SboxNetworkStorage.sln -c Release
fi
server=(dotnet "$root/src/SboxNetworkStorage.Server/bin/Release/net10.0/sbox-ns.dll")
if [[ -n "$binary" ]]; then
  if [[ "$binary" == *.dll ]]; then server=(dotnet "$binary"); else server=("$binary"); fi
fi
work="$(mktemp -d)"
pid=''
cleanup() {
  status=$?
  trap - EXIT
  if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; fi
  if ((status != 0)); then
    echo "Smoke failed; server log:" >&2
    if [[ -f "$work/server.log" ]]; then cat "$work/server.log" >&2; fi
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT
cli() { "${server[@]}" "$@" --config-dir "$work/config" --data-dir "$work/data"; }
setup=(setup --non-interactive --listen "127.0.0.1:$port")
if [[ -n "$postgres" ]]; then setup+=(--database postgres --pg-connection-string "$postgres"); fi
cli "${setup[@]}"
project_output="$(cli project create 'HTTP Parity Smoke' --key-mode public --require-sbox-auth false)"
project=''
while IFS= read -r line; do [[ "$line" != 'Project ID: '* ]] || project="${line#Project ID: }"; done <<< "$project_output"
[[ -n "$project" ]] || { echo "Cannot parse project id: $project_output" >&2; exit 1; }
parse_key() {
  local line key=''
  while IFS= read -r line; do
    if [[ "$line" =~ ^[[:space:]]+(sbox_[^[:space:]]+)[[:space:]]*$ ]]; then key="${BASH_REMATCH[1]}"; fi
  done
  [[ -n "$key" ]] || { echo 'Cannot parse CLI key output' >&2; return 1; }
  printf '%s' "$key"
}
public="$(cli key create "$project" --type public | parse_key)"
secret="$(cli key create "$project" --type secret | parse_key)"
if (exec 3<>/dev/tcp/127.0.0.1/$port) 2>/dev/null; then exec 3<&-; exec 3>&-; echo "Port $port is already in use by another server; refusing to run against a stale instance" >&2; exit 1; fi
cli start > "$work/server.log" 2>&1 &
pid=$!
healthy=false
for ((i=0; i<120; i++)); do
  if curl --fail --silent "http://127.0.0.1:$port/health" > /dev/null; then healthy=true; break; fi
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.5
done
$healthy || { echo 'Server did not become healthy' >&2; exit 1; }
args=(check --target "http://127.0.0.1:$port" --project-id "$project" --public-key "$public" --secret-key "$secret" --corpus "$root/tests/parity/corpus")
if [[ -n "$out" ]]; then args+=(--out "$out"); fi
dotnet "$root/tools/SboxNetworkStorage.Parity/bin/Release/net10.0/sbox-ns-parity.dll" "${args[@]}"
