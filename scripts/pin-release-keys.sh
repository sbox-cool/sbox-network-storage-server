#!/bin/sh
# Synchronize installer trust anchors from the maintainer-supplied P-256 public keys.
# Requires Python 3 and OpenSSL. Never generates a signing key or edits the source key file.
set -eu
root="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
exec python3 - "$root" <<'PY'
import base64
import pathlib
import re
import struct
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
source = root / 'src/SboxNetworkStorage.Server/Updates/release-signing-keys.pub'
text = source.read_text(encoding='utf-8')
pattern = r'-----BEGIN PUBLIC KEY-----\s+[A-Za-z0-9+/=\s]+-----END PUBLIC KEY-----'
keys = re.findall(pattern, text)
remaining = re.sub(pattern, '', text)
if any(line.strip() and not line.lstrip().startswith('#') for line in remaining.splitlines()):
    sys.exit('Invalid public key file: expected only SPKI PEM public keys and comments.')

# Exact DER SPKI header for id-ecPublicKey / prime256v1 and a 65-byte uncompressed point.
header = bytes.fromhex('3059301306072a8648ce3d020106082a8648ce3d03010703420004')
pems = []
blobs = []
seen = set()
for pem in keys:
    check = subprocess.run(['openssl', 'pkey', '-pubin', '-pubcheck', '-noout'],
                           input=pem.encode('ascii'), capture_output=True)
    if check.returncode:
        sys.exit('Invalid release public key: ' + check.stderr.decode(errors='replace'))
    result = subprocess.run(['openssl', 'pkey', '-pubin', '-outform', 'DER'],
                            input=pem.encode('ascii'), capture_output=True)
    if result.returncode:
        sys.exit('Cannot export release public key: ' + result.stderr.decode(errors='replace'))
    der = result.stdout
    if len(der) != len(header) + 64 or not der.startswith(header):
        sys.exit('Release keys must be ECDSA P-256 SubjectPublicKeyInfo public keys.')
    if der in seen:
        sys.exit('Duplicate release public key.')
    seen.add(der)
    encoded = base64.b64encode(der).decode('ascii')
    pems.append('-----BEGIN PUBLIC KEY-----\n' + '\n'.join(
        encoded[i:i + 64] for i in range(0, len(encoded), 64)) + '\n-----END PUBLIC KEY-----')
    # BCRYPT_ECDSA_PUBLIC_P256_MAGIC (ECS1), cbKey, X, Y; all header fields little-endian.
    blobs.append(base64.b64encode(b'ECS1' + struct.pack('<I', 32) + der[len(header):]).decode('ascii'))

marker = 'PINNED RELEASE KEYS'
blocks = {
    root / 'install/install.sh': "PINNED_RELEASE_KEYS='" + '\n'.join(pems) + "'",
    root / 'install/install.ps1': '$script:PinnedReleaseKeys = @(' + (
        '\n' + '\n'.join("    '" + blob + "'" for blob in blobs) + '\n' if blobs else '') + ')',
}
updates = []
for path, body in blocks.items():
    original = path.read_text(encoding='utf-8')
    block = re.compile(r'(?m)(^# BEGIN ' + marker + r'[^\n]*\n).*?(^# END ' + marker + r'[^\n]*$)', re.S)
    updated, count = block.subn(lambda match: match[1] + body + '\n' + match[2], original)
    if count != 1:
        sys.exit(f'{path}: expected exactly one pinned key block; no files changed.')
    updates.append((path, updated))
for path, updated in updates:
    path.write_text(updated, encoding='utf-8')
print(f'Synchronized {len(keys)} supplied public key(s) into both installers.')
if not keys:
    print('WARNING: no public keys supplied; installers remain fail-closed.', file=sys.stderr)
PY
