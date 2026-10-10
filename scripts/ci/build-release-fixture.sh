#!/usr/bin/env bash
# Builds signed candidate releases for the install matrix (CI only; it rewrites the pinned keys in this checkout).
#
#   scripts/ci/build-release-fixture.sh --rid linux-x64 --out DIR [--previous 0.4.0] [VERSION...]
#
# 1. Generates a throwaway P-256 release key and pins it into the installers and the binary
#    (scripts/pin-release-keys.sh), exactly as a real key rotation would.
# 2. Publishes each VERSION like release.yml and signs SHA256SUMS, writing DIR/v<VERSION>/.
# 3. Copies the real assets of --previous from GitHub (signed with the real keys) to DIR/v<previous>/.
# 4. Writes DIR/latest (the newest VERSION) and a throwaway CA plus a TLS certificate for
#    github.com, api.github.com and sboxcool.com in DIR/tls/.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
rid=''; out=''; previous=''; versions=()
while (($#)); do
  case "$1" in
    --rid) rid="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --previous) previous="$2"; shift 2 ;;
    *) versions+=("$1"); shift ;;
  esac
done
[[ -n "$rid" && -n "$out" && ${#versions[@]} -gt 0 ]] || { sed -n '2,12p' "$0" >&2; exit 2; }
mkdir -p "$out/tls"
out="$(cd "$out" && pwd)"
keys="$out/keys"; mkdir -p "$keys"

openssl ecparam -name prime256v1 -genkey -noout -out "$keys/release.pem"
# Windows runners ship python, not python3, which scripts/pin-release-keys.sh calls.
if ! command -v python3 > /dev/null && command -v python > /dev/null; then
  mkdir -p "$out/bin"; printf '#!/bin/sh\nexec python "$@"\n' > "$out/bin/python3"; chmod +x "$out/bin/python3"
  export PATH="$out/bin:$PATH"
fi
openssl pkey -in "$keys/release.pem" -pubout -out "$keys/release.pub"
cp "$keys/release.pub" "$root/src/SboxNetworkStorage.Server/Updates/release-signing-keys.pub"
sh "$root/scripts/pin-release-keys.sh"

for version in "${versions[@]}"; do
  dir="$out/v$version"; mkdir -p "$dir"
  publish="$out/publish-$version"
  dotnet publish "$root/src/SboxNetworkStorage.Server/SboxNetworkStorage.Server.csproj" -c Release -r "$rid" \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:Version="$version" -o "$publish" -nologo -v q
  cp "$root/LICENSE" "$root/README.md" "$root/CHANGELOG.md" "$publish/"
  if [[ "$rid" == win-* ]]; then
    pwsh -NoProfile -Command "Compress-Archive -Path '$publish/*' -DestinationPath '$dir/sbox-ns-$version-$rid.zip'"
  else
    tar -C "$publish" -czf "$dir/sbox-ns-$version-$rid.tar.gz" .
  fi
  cp "$root/install/install.sh" "$root/install/install.ps1" "$dir/"
  (cd "$dir" && sha256sum sbox-ns-* > SHA256SUMS)
  openssl dgst -sha256 -sign "$keys/release.pem" -out "$dir/SHA256SUMS.p256.sig" "$dir/SHA256SUMS"
  printf '{"version":"%s","minUpgradableFrom":null,"migrationRequired":false,"security":false,"changelogUrl":null,"publishedAt":"2020-01-01T00:00:00Z","prerelease":false}\n' \
    "$version" > "$dir/release.json"
  rm -rf "$publish"
done
printf '%s\n' "${versions[-1]}" > "$out/latest"

if [[ -n "$previous" ]]; then
  # Real, previously published assets for this platform plus the installers and manifests.
  gh release download "v$previous" --repo sbox-cool/sbox-network-storage-server --dir "$out/v$previous" \
    --pattern "*-$rid.*" --pattern 'install.*' --pattern 'SHA256SUMS*' --pattern 'release.json'
fi

tls="$out/tls"
openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj '/CN=sbox-ns install matrix CA' \
  -keyout "$tls/ca.key" -out "$tls/ca.crt" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj '/CN=github.com' -keyout "$tls/server.key" -out "$tls/server.csr" 2>/dev/null
printf 'subjectAltName=DNS:github.com,DNS:api.github.com,DNS:sboxcool.com\nextendedKeyUsage=serverAuth\n' > "$tls/ext.cnf"
openssl x509 -req -in "$tls/server.csr" -CA "$tls/ca.crt" -CAkey "$tls/ca.key" -CAcreateserial -days 2 \
  -extfile "$tls/ext.cnf" -out "$tls/server.crt" 2>/dev/null
echo "fixture ready in $out: ${versions[*]}${previous:+ (previous $previous)}"
