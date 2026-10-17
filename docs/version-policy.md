# Game version policy

Publish new game builds without breaking players on older ones. Each
package sync that publishes a revision keeps an immutable snapshot of that
revision's definitions; owners set support rules; the server tells each
client what to do and can enforce it server-side (opt-in, off by default).

## Publishing flow

1. The editor Sync Tool pushes definitions, then `POST
   /api/manage/{projectId}/package-sync` with the new `currentRevisionId`.
2. When the revision actually changes, the server promotes staged
   overrides, resets `revisionPublishedAt` to now (the grace anchor), and
   writes `revisions/{revisionId}.json`: the full merged endpoints,
   workflows, collections, queries and game values plus the publish time.
   The write is once-only: later live edits never rewrite a snapshot.
3. `revisions/index.json` records the retained ids, grace starts and owner
   overrides (ended/extended grace). The Versions page
   (`/dashboard/projects/{id}/versions`) lists live, in-grace and expired
   revisions with publish times, distinct players per revision (24h, from
   heartbeat/request identity sketches) and non-additive schema warnings.

## Retention

The server keeps live plus the most recent `N` earlier revisions (`N` per
project, default 3, range 0-20, set under Project → Revision policy). A
revision still inside its support window is preserved even past the count;
it is pruned from the index only after the window lapses. Snapshot files
are definition JSON only (no player records).

## Rules

Per project (all unset by default; untouched projects behave exactly as
before — no `_revisionStatus`, no blocking, live definitions):

| Setting | Meaning |
|---|---|
| Minimum supported revision | Clients below it are treated as past grace. |
| Grace period (minutes, 0-10080) | Starts when a newer revision goes live. `0` means no grace (outdated is immediately past grace); empty means no timed window (the in-grace action applies). |
| While in grace | `notify` (default), `rejoin`, or `continue` (silent). Legacy `allow_continue` maps to `notify`, `force_upgrade` to `rejoin`; stored values are never rewritten. |
| After the grace period | `block_writes` (default) or `block_all`. Only a client-library hint unless server enforcement is on. |
| Server-side enforcement | Off by default. When on, known revisions below the minimum or past grace get HTTP 403 `REVISION_BLOCKED` (with `action`, `message`, `latestRevisionId` plus `_revisionStatus`). `block_writes` rejects only mutating requests; reads still succeed. Secret-key callers are always exempt; requests without a revision, or with an unknown one, run against live. |
| Notice for outdated players | Owner message surfaced in revision-init and `_revisionStatus`. |

The owner's client-library enforcement checkbox (`Enforce the revision
policy in the client library`, legacy `allow_continue`/`force_upgrade`
mode) is unchanged and gates what the client acts on; it never blocks
server-side by itself.

End or extend a grace period early from the Versions page (confirm dialog,
audited as `revision.grace.end` / `revision.grace.extend`).

## revision-init

`POST /api/manage/{projectId}/revision-init` (also `/v3/...`) keeps every current field and
additively returns `action` (`none`, `notify`, `rejoin`, `block_writes`,
`block_all`; omitted without an explicit policy), the owner's message for
outdated clients (replacing the generic outdated text), `graceEndsAt`
(unix seconds, when a grace window applies) and `minimumRevisionId` (when
set). The handshake stays read-only and never fails because of policy.

## Pinned execution

A data-plane request carrying `x-ns-revision-id` uses a retained revision's
snapshot only when the project has an explicit revision policy and that
revision is within its support window. Without an explicit policy, requests
use live definitions even when they carry a revision header. The staged
overlay (`x-ns-publish-target: next`) still wins when targeted. Requests without
a revision, with an unknown one, or with an expired one use live definitions
(expired ones are rejected instead when server enforcement is on).

Snapshots are not closed-world: a resource absent from the snapshot may resolve
from live definitions. For example, an endpoint added after an older revision
can still run for that revision. Pinning does not hide every newer resource.

## Shared records caveat

Records are **not** versioned: old-revision endpoints read and write the
same live player data. Keep collection schemas backward-compatible across
revisions; the Versions page warns when a newer revision removed a
collection or fields. Direct record CRUD addresses live data by collection
id (routing follows live definitions); use `minimumRevisionId` and
enforcement for a hard cutoff.

## Client library requirements

No client changes are needed. Published game bundles already send
`x-ns-revision-id` on runtime calls (`NetworkStorageHttp`), and every
endpoint response (including `ok: false`) is scanned for `_revisionStatus`
(`NetworkStoragePackageInfo.UpdateFromServerResponse`):

```json
"_revisionStatus": {
  "isOutdatedRevision": true,
  "graceRemainingMinutes": 42,
  "graceExpired": false,
  "action": "rejoin",
  "message": "New update is out, please rejoin",
  "playerRevision": 41,
  "currentRevision": 42,
  "enforcementMode": "allow_continue",
  "policy": {
    "gracePeriodMinutes": 60,
    "postGraceAction": "block_writes",
    "forceEndpointUpgrade": false,
    "notifyMessage": "New update is out, please rejoin",
    "showUpdateOptions": true,
    "showPopupOnce": true,
    "showDefaultMessage": true,
    "enforcementMode": "allow_continue"
  }
}
```

The in-game outdated UI, grace countdown and auto-disconnect trigger off
this object; without an explicit server-side policy it is absent and the
library behaves as before.
