# Media uploads

Media uploads let a game store small images (PNG, JPEG or WebP) on the server
through single-use upload grants. Media is **off by default** for every project.
While it is off, the media routes answer exactly what an unrecognized Network
Storage route answers today, so enabling the feature in a build changes nothing
for projects that do not turn it on.

## Enabling

1. Turn on the server-side backend in `server.toml` (see
   [configuration.md](configuration.md#media-uploads)). The default is local disk.
2. In the owner panel, open the project's **Settings → Media uploads** section,
   tick **Enable media uploads for this project**, and configure at least one
   purpose: a name (`a-z`, `0-9`, `-`, up to 32 characters), **Enable this
   purpose**, and the limits described below.

The project settings are stored under the `media` key of the project. The
owner form rewrites only the purpose named in the form; other purposes are kept.

## Flow

```text
game server (secret key)              sbox-ns                      game client
   POST /v3/media/{pid}/grants  ->  validates policy, mints grant
   <- 201 { grantToken, uploadUrl, mediaId, expiresAtUnixMs }
   hand grantToken to the client  ------------------------------->  PUT raw image bytes
                                 <-  PUT /v3/media/{pid}/upload/{grantToken}
                                 ->  201 { mediaId, sha256, width, height, ... }
   GET /v3/media/{pid}/objects/{mediaId}   (read; see visibility)
```

A grant is a signed, short-lived token. It can be used once; the second upload
with the same token gets `409 GRANT_USED`.

### Routes

Each route is also served under `/api/media/...` through the API root aliases.
`uploadUrl` in the grant response uses the `/api/` form.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/v3/media/{projectId}/grants` | `x-api-key` (secret or public key) | Mint a grant |
| PUT | `/v3/media/{projectId}/upload/{grantToken}` | none; the grant is the credential | Upload the raw body |
| GET, HEAD | `/v3/media/{projectId}/objects/{mediaId}` | `x-api-key` for private objects | Read the bytes |

### Minting a grant

Body (JSON):

```json
{
  "purpose": "avatar",
  "contentType": "image/png",
  "sizeBytes": 48213,
  "sha256": "<64 lowercase hex characters>",
  "forSteamId": "76561198000000001"
}
```

- `purpose`, `contentType`, `sizeBytes` (at least 1) and `sha256` are required.
- `sha256` is the digest of the exact bytes the client will upload.
- `forSteamId` is required with the **secret key** (the server names the player
  the grant is for). With a **public key**, the player's identity is taken from the
  verified session or s&box token and `forSteamId` is ignored.

Response `201`:

```json
{
  "ok": true,
  "grantToken": "sbox_mg_…",
  "uploadUrl": "/api/media/{projectId}/upload/sbox_mg_…",
  "method": "PUT",
  "mediaId": "…32 hex…",
  "contentType": "image/png",
  "maxBytes": 48213,
  "expiresAtUnixMs": 1760000000000,
  "_requestId": "…"
}
```

`maxBytes` is the declared `sizeBytes` the grant is bound to. The upload must not
exceed it.

### Uploading

`PUT` the raw image bytes with a `Content-Type` header that matches the grant's
`contentType`. There is no multipart form and no API key. The server checks, in
order:

1. the grant's signature, project and expiry (`401`, `410`);
2. the declared `Content-Length` and the bytes actually read (`413`);
3. the declared `Content-Type` against the grant and the purpose's allowed types (`415`);
4. the sniffed format from the first bytes, which must match the declared type (`415`);
5. the image dimensions against `maxWidth` / `maxHeight` (`422`);
6. the SHA-256 of the bytes against the grant (`422`);
7. the project quota, if one is set (`413`);
8. only then, the grant is consumed (`409` if already used), the bytes are stored, and the object row is written.

A rejected upload (steps 2–7) does **not** consume the grant, so the client can
retry with corrected bytes until the grant expires. A successful upload consumes
it.

Response `201`:

```json
{
  "ok": true,
  "mediaId": "…",
  "sha256": "…",
  "contentType": "image/png",
  "sizeBytes": 48213,
  "width": 256,
  "height": 256,
  "visibility": "private",
  "expiresAtUnixMs": 1767000000000,
  "_requestId": "…"
}
```

### Reading

`GET` returns the bytes with `Content-Type`, `ETag` (the SHA-256),
`X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox`. Public
objects send `Cache-Control: public, max-age=3600`; private objects send
`private, no-store`.

- `public` visibility: anyone with the URL can read.
- `private` visibility: only a secret-key caller can read. Other callers get
  `404 MEDIA_NOT_FOUND`, the same as an unknown id.

## Policy

Each project has a `media` object. Each **purpose** is an entry in
`media.purposes`, keyed by its name.

| Key | Default | Meaning |
| --- | --- | --- |
| `enabled` (root) | `false` | Media on for the project |
| `purposes.<name>.enabled` | `false` | Purpose on |
| `allowClientSelfGrant` | `false` | Game clients (public key) may request grants for this purpose |
| `visibility` | `private` | `public` or `private` |
| `contentTypes` | all of png, jpeg, webp | Subset of `image/png`, `image/jpeg`, `image/webp` |
| `maxBytes` | `media.max_object_bytes` | Per-object cap; clamped to the server cap |
| `maxWidth`, `maxHeight` | 4096 | Pixel limits (1–16384) |
| `perPlayerPerHour` | 30 | Grants a player may be issued per purpose per hour |
| `projectQuotaBytes` | 0 (unlimited) | Total stored bytes for the purpose |
| `retentionDays` | 30 | Object lifetime, 1–3650 days |

### Who may mint a grant

- **Secret key** (server side): allowed for any enabled purpose.
- **Public key** (game client): refused with `403 MEDIA_GRANT_FORBIDDEN` unless
  the purpose sets `allowClientSelfGrant`. The default refusal happens before
  any identity lookup. A client grant also needs a verified player identity: a
  valid session token or s&box token. Without one the request gets `401`.

### Rate limiting

Each minted grant counts toward `perPlayerPerHour` for the player it is issued
to, per purpose, over a rolling hour. Exceeding it returns `429 MEDIA_RATE_LIMITED`.
Grants count whether or not they are later used.

### Quota

`projectQuotaBytes` is checked before the upload is stored. The check and the
insert are not atomic, so concurrent uploads near the limit can overshoot by up
to one object per concurrent request.

## Limits and sizes

- Grants expire **120 seconds** after issue.
- The upload route applies `media.max_object_bytes` as its request body limit.
  A body larger than the server cap is refused by the generic body limit with
  `413` before the media checks run. This is also true for a project where media
  is disabled: the body limit can answer before the not-found response. This is
  deliberate; the global limits are not changed for media.
- Objects are stored as the exact bytes uploaded. Nothing is re-encoded.

## Errors

Errors use the standard envelope:

```json
{
  "ok": false,
  "status": 409,
  "error": { "code": "GRANT_USED", "message": "…", "requestId": "…", "docsUrl": "https://sboxcool.com/wiki/network-storage-v3/media" },
  "message": "…",
  "_requestId": "…"
}
```

| Status | Code | When |
| --- | --- | --- |
| 400 | `MEDIA_INVALID_REQUEST` | Missing or malformed fields; `forSteamId` missing for a secret-key grant |
| 401 | `GRANT_INVALID` | Token not signed by this server, or wrong project |
| 401 | `UNAUTHORIZED` | Missing or invalid API key on a grant request |
| 403 | `MEDIA_PURPOSE_DISABLED` | Purpose not enabled |
| 403 | `MEDIA_GRANT_FORBIDDEN` | Public-key self-grant without `allowClientSelfGrant` |
| 403 | `PROJECT_DISABLED` | Project disabled |
| 404 | `NOT_FOUND` | Media disabled, or unknown project (the standard unrecognized-route answer) |
| 404 | `MEDIA_NOT_FOUND` | Unknown or unreadable object |
| 409 | `GRANT_USED` | Grant already consumed |
| 410 | `GRANT_EXPIRED` | Grant past its 120-second lifetime |
| 413 | `MEDIA_TOO_LARGE` | Body larger than the grant's `sizeBytes` |
| 413 | `MEDIA_QUOTA_EXCEEDED` | Purpose quota would be exceeded |
| 415 | `MEDIA_TYPE_MISMATCH` | Declared or sniffed type not allowed or not matching |
| 422 | `MEDIA_INVALID_IMAGE` | Bytes are not a decodable image of the declared type |
| 422 | `MEDIA_DIMENSIONS_EXCEEDED` | Width or height over the purpose limit |
| 422 | `MEDIA_HASH_MISMATCH` | SHA-256 of the bytes differs from the grant |
| 429 | `MEDIA_RATE_LIMITED` | Per-player hourly grant limit reached |

## Storage backends

Metadata (object rows, grants) lives in the relational store (SQLite or
PostgreSQL). Bytes live in a blob backend chosen by `media.backend`:

- **`local`** (default): files at `{media.local_dir}/{projectId}/{sha256}`. Empty
  `media.local_dir` uses `media/` under the data folder. Writes go to a temporary
  file and are moved into place.
- **`s3`**: objects at `media/{projectId}/{sha256}` in `media.s3.bucket`, using
  `media.s3.endpoint` (empty for AWS), `media.s3.region`, and optional path-style
  addressing for most self-hosted S3-compatible stores.

Objects are content-addressed by SHA-256 within each project. Two uploads of the
same bytes share one blob file; each upload still gets its own object row.

## Retention and deletion

**Not implemented yet.** `retentionDays` is recorded on each object as
`expiresAtUnixMs`. After that time the object is unreadable (`GET` returns
`404 MEDIA_NOT_FOUND`), but neither the object row nor the blob is deleted. No
background job purges expired media. Until one exists, stored bytes grow for as
long as the blob backend has room.

## Backups and export

**Deferred.** `sbox-ns db backup` and `sbox-ns export` do not include media
objects or blobs. Media metadata sits in the database, so a database backup
keeps the object rows, but the bytes in the local media folder or the S3 bucket
are not in the backup. Back up the media folder or the bucket separately.
Export and import of media are not implemented.

## Compatibility

Media is additive and off by default. No existing route or response changes for
projects that do not enable it. Only the server's capability list gains `media`.
