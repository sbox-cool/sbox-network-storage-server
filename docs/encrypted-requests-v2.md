# Encrypted requests v2

This is the wire specification for the server side of encryption v2 (issue
[#153](https://github.com/sbox-cool/sbox-network-storage-server/issues/153)).
v1 stays as documented in [admin-panel.md](admin-panel.md#encrypted-requests)
and [game-client.md](game-client.md#encrypted-requests).

Status: server side only. The library that sends v2, and fingerprint pinning in
the library, are a later library release. No s&box or engine run has been made for v2.

## Known limits

v2 gives confidentiality of request and response payloads from anyone without the
server's private key. It does not hide the bearer or auth tokens that the client
sends in plain headers or in the outer `security` block. An active man in the middle
(MITM) who sees such a token can still act with it. HTTPS remains required for that.

v2 also does not authenticate the server on first contact by itself. The client
wraps its AES key to the server key it was given. If an attacker substitutes a key
on the first request, the attacker can read that request. Fingerprint pinning
(see [Key pinning](#key-pinning)) closes this gap in the library.

## Why v2

In v1 the AES key is `SHA-256("sboxcool.network-storage.encrypted-request.v1" + "\0" + publicKey + "\0" + context)`.
The public key ships in every game build, so anyone with a build can derive the key,
read requests, and forge envelopes. v1 responses are not encrypted at all.

v2 wraps a fresh random AES-256 key per request with the server's RSA-2048 public key.
Only the server can unwrap it. Responses are encrypted under the same per-request key.

## Modes

The project setting is one of four values. Legacy `true` maps to `v1`; a missing value maps to `off`.

| Mode | Plain call | v1 envelope | v2 envelope | Published `encryptedRequests` | `encryption` block in signed config |
| --- | --- | --- | --- | --- | --- |
| `off` | accepted | `SECURITY_ENCRYPTION_DISABLED` 409 | `SECURITY_ENCRYPTION_DISABLED` 409 | `disabled` | not published |
| `v1` | `SECURITY_ENCRYPTION_REQUIRED` 409 | accepted | `SECURITY_MODE_UNSUPPORTED` 400 | `required` | not published |
| `v2-preferred` | `SECURITY_ENCRYPTION_REQUIRED` 409 | accepted | accepted | `required` | published, `required: false` |
| `v2-required` | `SECURITY_ENCRYPTION_REQUIRED` 409 | `SECURITY_ENCRYPTION_REQUIRED` 409 | accepted | `required` | published, `required: true` |

An envelope whose `envelope.version` is present and is neither `"1"` nor `"2"` is
refused with `SECURITY_MODE_UNSUPPORTED` 400 in every mode. A missing `version`
means `"1"`.

Published games with a v1-only library keep working on `v1` and `v2-preferred`.
They cannot call a `v2-required` project: they receive `SECURITY_ENCRYPTION_REQUIRED`.

## Server key

- One RSA-2048 key per server. It is separate from the config-signing key
  (`auth.security_signing_key_file`), which signs the security config itself.
- File: `auth.request_encryption_keys_file`, default `secrets/request_encryption_keys.json`.
  Relative paths resolve against the runtime folder, like the other `auth.*_file` settings.
- Generated on first start if the file is missing. `sbox-ns security status` and
  `sbox-ns security rotate-key` also create it when it is missing. An existing file is
  never overwritten.
- Written with owner-only permissions on Unix, like the other secret files.
- The file holds private keys. Back it up with the other secrets and keep it out of
  version control and game builds.
- If the file is lost, the next ensure creates a new key. v2 requests wrapped to the old
  key then get `SECURITY_CONFIG_STALE` until clients refresh their config.

File format (UTF-8 JSON):

```json
{
  "schemaVersion": 1,
  "currentKeyId": "<keyId>",
  "keys": [
    {
      "keyId": "<keyId>",
      "createdAt": "<ISO-8601>",
      "retiredAt": null,
      "graceUntil": null,
      "privateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n"
    }
  ]
}
```

A retired entry has `retiredAt` and `graceUntil` set. `keyId` is the first 32 characters
of `fingerprint`. `fingerprint` is the lowercase hex SHA-256 of the key's
SubjectPublicKeyInfo DER (64 characters).

## Signed config `encryption` block

Only projects in `v2-preferred` or `v2-required` get this block. It sits in the signed
payload, before `signature`:

```json
"encryption": {
  "algorithm": "rsa-oaep-256+aes-256-gcm",
  "keyId": "<32 hex characters>",
  "publicKeyJwk": { "kty": "RSA", "n": "<base64url>", "e": "<base64url>" },
  "fingerprint": "<64 hex characters>",
  "required": false
}
```

`publicKeyJwk` uses base64url without padding, as the existing config builder does.
`e` is usually `AQAB` (65537). `required` is `true` only for `v2-required`.

`modes.encryptedRequests` keeps its meaning: `required` for any mode other than `off`,
otherwise `disabled`. For `off` and `v1`, the signed bytes are unchanged from v1.

## Refusals

A refusal is a JSON body with the existing security fields. The `security` object has
`code`, `retrySafe`, `configVersion`, `configSource` (`"project"`), `requestMode`, and
`expected`. `expected` holds `authSessions` and `encryptedRequests` as before, plus
`encryptionMode`, the resolved mode string. `encryptedRequests` stays `required` or
`disabled`; its meaning is unchanged.

On endpoint routes, callers with a parseable `x-ns-library-version` receive security-class
errors with HTTP 200 and the intended status in the body's `status` field. Plain HTTP
callers keep the actual HTTP status. Library callers must read `error.code` and `status`,
not only the transport status. Security-class families are `SECURITY_*`,
`AUTH_SESSION_*`, `ENCRYPTED_REQUEST_*` and `REQUEST_SIGNATURE_*`.

| Code | `status` | When |
| --- | --- | --- |
| `SECURITY_ENCRYPTION_REQUIRED` | 409 | A plain call in a mode other than `off`, or a v1 envelope in `v2-required`. |
| `SECURITY_ENCRYPTION_DISABLED` | 409 | Any encrypted call in mode `off`. |
| `SECURITY_MODE_UNSUPPORTED` | 400 | A v2 envelope in mode `v1`, or an unknown envelope version. |
| `SECURITY_CONFIG_STALE` | 409 | A v2 `keyId` is missing or no longer usable (see Rotation). Also the existing stale `configVersion` case. |
| `REQUEST_SIGNATURE_INVALID` | 401 | A v2 key unwrap or AES-GCM check failed. |
| `ENCRYPTED_REQUEST_INVALID_ID` | 400 | The replay ID is malformed. |
| `ENCRYPTED_REQUEST_STALE` | 400 | The replay ID is outside the project window. The error object carries `serverTime` (unix seconds, the server clock at refusal) so a client can correct its clock and retry with a fresh ID. |
| `ENCRYPTED_REQUEST_REPLAY_DETECTED` | 409 | The replay ID was already accepted for this identity. |
| `AUTH_SESSION_STEAMID_MISMATCH` | 403 | The envelope's `steamId` names a different player than the auth session. Checked for v1 and v2. |

Refusals before decryption are plain JSON. This includes every mode-matrix refusal and
`SECURITY_CONFIG_STALE` for an unknown `keyId`. Clients must accept a plain error body
for any request.

## v2 request

Outer body, unchanged from v1:

```json
{ "security": { ... }, "encrypted": true, "envelope": { ... } }
```

`envelope` fields:

| Field | Value |
| --- | --- |
| `version` | `"2"` |
| `algorithm` | `"rsa-oaep-256+aes-256-gcm"` |
| `projectId` | Project ID, as in v1. |
| `endpointSlug` | Required for v2, equal to the resolved route slug. Always included in the AAD. v1 remains unchanged. |
| `keyId` | `encryption.keyId` from the signed config the client holds. |
| `nonce` | At least 8 characters, as in v1. |
| `sessionId` or `steamId` | Identity binding, as in v1. `sessionId` is used first when both are present. |
| `wrappedKey` | RSA-OAEP with SHA-256 for the hash and MGF1, empty label, of the 32-byte AES key. base64url, no padding. 256 bytes for RSA-2048. |
| `iv` | 12 random bytes, base64url. |
| `tag` | 16-byte GCM tag, base64url. |
| `encryptedPayload` | AES-256-GCM ciphertext of the UTF-8 JSON payload, base64url. The tag is separate. |

There is no `publicKeyFingerprint` and no `signature`. Integrity comes from GCM under the
per-request key, which only the client and the server hold.

The payload and `wrappedKey` are ciphertext. `projectId`, `endpointSlug`, `keyId`,
`nonce` and the identity field are plaintext; the AAD binds them.

### AAD

The GCM AAD is the UTF-8 bytes of the canonical JSON of this string map: keys sorted,
compact, as v1 `StableJson` produces:

- `version`
- `algorithm`
- `projectId`
- `endpointSlug` (required and equal to the route slug)
- `identityType`: `session` or `steam`
- `identity`: the `sessionId` or `steamId` value
- `nonce`
- `keyId`

### Client steps

1. Generate a fresh random 32-byte AES key and a 12-byte IV.
2. Build the payload JSON: endpoint input plus `encryptedRequestId`, as in v1, and
   `_endpointSlug` when used.
3. Build the AAD bytes as above.
4. Encrypt the payload with AES-256-GCM, the IV, and the AAD. Keep the ciphertext and the
   16-byte tag separate.
5. Wrap the AES key with RSA-OAEP (SHA-256, MGF1-SHA-256, empty label) under
   `encryption.publicKeyJwk`.
6. Build `envelope` and the outer body.

### Server steps

1. Shape and binding checks, as v1 `ValidateShape`, without the HMAC and fingerprint
   checks. Failures use the v1 codes.
2. Key lookup: `RequestEncryptionKeyStore.Current().FindPrivate(keyId, now)`. Missing or
   not found gives `SECURITY_CONFIG_STALE` 409, plain.
3. Unwrap to 32 bytes. Failure gives `REQUEST_SIGNATURE_INVALID` 401, plain.
4. AES-256-GCM decrypt with the AAD. Failure gives `REQUEST_SIGNATURE_INVALID` 401, plain.
5. Payload parse, `_endpointSlug` check, and `WithoutReservedKeys`, as v1.
6. Replay consumption, as v1: same window (30-600 seconds, default 120) and same error codes.

On success the server keeps `(aesKey, nonce, keyId)` in the request context for the
response. The AES key exists only for that request.

## v2 response

After a v2 request decrypts, every JSON body the server writes for that request is
encrypted:

```json
{ "encrypted": true, "v": 2, "iv": "<base64url, 12 bytes>", "tag": "<base64url, 16 bytes>", "payload": "<base64url>" }
```

- Plaintext: the UTF-8 JSON of the final body, including `ok`, `status`, `error`,
  `_revisionStatus`, and the same fields the plain body would carry.
- Cipher: AES-256-GCM with the request's AES key and a fresh random 12-byte IV per response.
- AAD: UTF-8 of `sboxcool.network-storage.encrypted-response.v2`, a NUL byte, `nonce`
  (as sent in the request), a NUL byte, and `keyId` (as sent in the request).
- HTTP status and headers are unchanged, including `X-Request-Id`.

A client decrypts when the body has `encrypted: true` and `v: 2`. Otherwise it reads the
body as plain JSON. A valid tag shows that the holder of the request's AES key produced
the response. That key was wrapped to the server key the client pinned or was given, so
the tag authenticates the server only as far as that key does.

## Rotation

Owner console: the encrypted requests settings of the project include a **Rotate** form
with a grace value in seconds. Default 86400 (one day). Valid range 0 to 2592000 (30
days). Rotation needs the owner's current password. If two-factor is enabled, it also
needs the authenticator code or a recovery code. This is the same re-entry the owner
export uses. A wrong password or code, or an authenticator the server cannot read,
leaves the key unchanged and shows the error on the project page (HTTP 400). The form
uses the same CSRF protection as the other owner forms. Each rotation writes an
`encryption.key.rotate` audit entry.

On submit, the server:

1. Generates a new RSA-2048 key and makes it current.
2. Sets the previous current key's `retiredAt` to now and `graceUntil` to now plus the grace value.
3. Removes retired keys whose `graceUntil` has passed. Removal happens on rotation, not on read.
4. Writes the file atomically (temp file, then move).
5. Reloads the server's cached keyring.

Effects:

- The new key is published in the signed config for `v2-preferred` and `v2-required` projects.
- A v2 request with the retired key's `keyId` is accepted until `graceUntil`. At `graceUntil`
  and after, it gets `SECURITY_CONFIG_STALE` 409, and the client refreshes its config.
- Grace 0 ends the old key immediately: `graceUntil` equals the rotation time, and the key
  is refused from then on.
- v1 requests do not use the keyring, so rotation does not affect them.
- Rotation changes `keyId` but not `configVersion`. Clients pick up the new key on their
  next signed-config fetch, within `ttlSeconds`. `configVersion` is not bumped on purpose:
  bumping it would make every project's clients stale at once on upgrade.
- The key is server-wide. One rotation applies to every project on the server that uses
  a v2 mode.
- An expired retired key stays in the file, refused, until the next rotation removes it.

The serving process reads the keyring file again within 5 seconds of a change, so a
rotation takes effect without a restart. Rotation through the console reloads the cache
at once. If the file goes missing while the server runs, the server keeps the last good
snapshot and logs one warning when the file disappears. With no snapshot yet, requests
fail closed.

Rotation takes an exclusive lock on `<keys file>.lock`, created next to the keys file
(owner-only on Unix), and reads the file under that lock. Concurrent rotations from the
console and the CLI therefore each add one key. A rotation waits up to 10 seconds for the
lock and then fails; retry it. The lock file is transient and is not part of the backup
set. If the keys file is missing, rotation fails before any lock file is created.

Operators on the server host can rotate with the CLI. It runs the same operation. It does
not ask for the owner's password: it needs shell access to the server host, which already
allows `sbox-ns admin reset-2fa`. The owner console rotates both keys after the owner's password, and the authenticator or recovery code when enabled.

- `sbox-ns security rotate-key [--grace-seconds N]`: makes a new key current. `N` defaults
  to 86400 and must be 0 to 2592000. The output names the new key and says whether the
  previous key still accepts requests until a time, or was removed at once (grace 0).
  If no key file exists yet, the command creates the first key and rotates nothing.
- `sbox-ns security rotate-signing-key [--grace-seconds N]`: replaces the signing key that
  games pin; see Key pinning below. `N` defaults to 86400 and must be 0 to 2592000. If no
  signing key file exists yet, the command refuses and rotates nothing; start the server once
  to create the key.
- `sbox-ns security status`: prints the signing key (key id, fingerprint, announced rotations
  with their end times), then the current request key, its fingerprint and creation time, and
  each retired request key with whether it is still accepted.

`RequestEncryptionKeyring.Rotate` and `SecuritySigningKeyStore.Rotate` are the underlying operations.

## Key pinning

Games pin the signing key, not the request key. The signing key is the server's identity:
it signs the security config, and a pin fixes which key the library must match.

- Pin value: the signing key fingerprint, the lowercase hex SHA-256 of the signing key's
  SubjectPublicKeyInfo DER (64 characters). It has the same format as the request-key
  fingerprint. The library computes it from `signing.publicKeyJwk` (`n` and `e`); those bytes
  equal the ones behind `signing.publicKeyPem`. No separate field is published, so the
  `signing` block keeps its reference shape.
- Normalising a pasted pin: strip spaces, colons and line breaks, then lowercase it.
- Where to read it: `sbox-ns security status` prints it under "Security signing key", and the
  owner console project page shows it in the Signing key card. Both show the same value.
- Owner flow: copy the fingerprint from one of those places, check it against the console,
  then pin it with `NetworkStorage.Configure(..., securityKeyFingerprint)` or the Setup window
  field.
- A pinned mismatch is a hard refusal, not a fallback.
- Without a pin, the library trusts the signing key it first sees per base URL, stores it,
  and warns loudly on change.
- The request-key fingerprint (`encryption.fingerprint`) is informational for pinning. When
  the request key rotates, the signed config carries the new `keyId`, which is accepted
  because the signature verifies against the pinned signing key.
- Signing-key rotation: `sbox-ns security rotate-signing-key [--grace-seconds N]` makes a new
  signing key current. The old key signs a statement that names the new key's fingerprint and
  public key. The server publishes the statement under `signing.rotations` in the signed config.
  A library that already trusts the old key verifies the statement with it and follows the chain
  to the new key without a repin, until the statement's `notAfter` (the end of the grace). Grace 0
  publishes no statement, so trusting clients refuse the new key at once.
- The server keeps the statements in `auth.security_signing_rotations_file`, oldest first, and
  publishes only those still in grace, at most 8. A rotation that would need a ninth unexpired
  statement is refused until one expires.
- A pin is an exact match, so a game pinned to the old fingerprint refuses the new key at once.
  Repin it to the fingerprint from `sbox-ns security status`. Statements never move a pin.
- An unpinned game that has not followed the chain before the grace ends refuses the new key with
  `ENCRYPTION_SIGNING_KEY_CHANGED`. That refusal blocks every endpoint call from the game, including
  projects in `off` mode, and nothing on the server clears it. The owner's remedy is a game build that
  pins `securityKeyFingerprint`, because a pin skips the stored trust. A player who needs the trust
  cleared again needs a game that calls `NetworkStorage.ResetSigningKeyTrust()` after the server
  identity is verified.
- Reinstalling or restoring the server without its `secrets/` directory creates a new signing key.
  Unpinned games then refuse it with the same lockout, and no announced statement bridges it. Restore
  `secrets/` with the rest of the state to keep the key.
- After a suspected compromise, do not rotate: the old key signs the statement, so whoever holds it
  can forge one. Stop the server, move `auth.security_signing_key_file` and
  `auth.security_signing_rotations_file` aside, start the server to create a new key, read the
  fingerprint with `sbox-ns security status`, announce it to game owners, and have them repin.

The v2 library is published as r427072 (`compat/library-matrix.json`). Following announced
signing-key rotations needs a library release that includes sbox-network-storage PR #87. Until
then, clients on older libraries refuse a rotated key, as they did before. Nothing here was run
against an s&box build.

## Golden vectors

- v1: `tests/SboxNetworkStorage.Server.Tests/Fixtures/network-storage-fixtures/encrypted-request-vectors.json`,
  generated by the reference Node implementation.
- v2: `tests/SboxNetworkStorage.Server.Tests/Fixtures/network-storage-fixtures/encrypted-request-v2-vectors.json`,
  generated by an independent `node:crypto` script (not the server), and shared with the library.

## Related

- [admin-panel.md](admin-panel.md#encrypted-requests): owner console for modes and rotation.
- [game-client.md](game-client.md#encrypted-requests): what the library sends.
- [configuration.md](configuration.md): `auth.request_encryption_keys_file`.
