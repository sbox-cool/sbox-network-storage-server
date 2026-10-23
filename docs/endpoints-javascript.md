# JavaScript and TypeScript endpoints

Endpoints can be written in **YAML** (steps) or in **JavaScript / TypeScript**
(code). Collections stay YAML. More languages are planned; YAML endpoints are
unchanged and stay supported.

- **Code endpoints.** Set `runtime: javascript` or `runtime: typescript` and put
  the code in `script`. The server runs it against the same records, game values,
  access rules and clock that YAML endpoints use.
- **A full global environment.** Any endpoint can be `scope: global`: it gets no
  HTTP route and shares its exported functions, classes and constants with every
  other script, via `import { x } from "globals/<id>"` or `globals.<id>.x`
  without an import. Existing YAML workflows are callable as
  `workflows.<name>(input)`.
- **Sandboxed.** Scripts run inside the server in an isolated interpreter with
  no file system, network, process or .NET access, and with per-request limits
  on time, statements, memory, recursion and storage operations (see
  [Limits](#limits)). One project's scripts cannot see another project's data.

Available on `main` now and in the next server release (0.5.5).

```yaml
method: POST
runtime: javascript
scope: public
script: |
  export default function handle(req, ns) {
    if (req.player === null) ns.fail("AUTH_REQUIRED", "Sign in first.", 401);
    const player = ns.records.get("players", req.player.steamId) ?? { coins: 0 };
    return { ok: true, coins: player.coins };
  }
```

Starting templates for both runtimes are listed at the end of this page.

## Contract

The script exports a default function `handle(req, ns)`. Its return value is
the response body as JSON. Return plain data (objects, arrays, strings,
numbers, booleans, null).

A script ends the request with an error through `ns.fail`. A failed request
answers with its status and this body:

```json
{ "ok": false, "error": { "code": "AUTH_REQUIRED", "message": "Sign in first." } }
```

The script cannot catch `ns.fail`. Its status must be in the 400-499 range;
the default is 400.

## `req`

The first argument. Its type is `EndpointRequest` in
[`ns.d.ts`](/owner-assets/endpoint-api/ns.d.ts).

| Field | Type | Meaning |
| --- | --- | --- |
| `input` | `Record<string, unknown>` | The query string for GET, the JSON body for POST. |
| `player` | `{ steamId: string } \| null` | The server-verified player. `null` when the caller is anonymous or called with a secret key. |
| `method` | `"GET" \| "POST"` | The HTTP method. |
| `endpoint` | `string` | The endpoint slug. |
| `revision` | `string \| null` | The game client's revision id, or `null` when the client sent none. |

Check `req.player` before you use it. Anonymous callers have no player, and a
secret-key caller has no player either, even though the server trusts that
caller.

## `ns`

The second argument. Its type is `Ns` in `ns.d.ts`.

| Member | Meaning |
| --- | --- |
| `ns.records.get(collection, key)` | Reads a record. Returns `null` when it does not exist. |
| `ns.records.put(collection, key, data)` | Writes a record. `data` is a JSON object. |
| `ns.records.delete(collection, key)` | Deletes a record. |
| `ns.records.list(collection)` | Lists the records of a collection. |
| `ns.values.get(path)` | Reads a game value by `"group"` or `"group.key"`. Returns `null` when absent. |
| `ns.workflows.run(id, input)` | Runs a workflow of this project and returns its result. See [Legacy workflows](#legacy-workflows). |
| `ns.now()` | Server time in milliseconds since the Unix epoch. |
| `ns.log(value)` | Keeps a line for this request, capped by `MaxLogBytes`. Endpoint tests and the owner's error detail show the lines when the call fails. Successful live calls do not keep them, and lines are not written to the server log. |
| `ns.fetch(url, options?)` | Calls an outside HTTP or HTTPS host. The result is `null` on the first run of the handler; see [Outbound HTTP](#outbound-http-nsfetch). |
| `ns.fail(code, message, status?)` | Ends the request with a typed error. |

Writes made by a script apply when the endpoint returns normally. A request
that ends with `ns.fail` does not keep its writes.

Script record keys are literal: `item` and `item_default` name different records, just as in
the document API and owner console. No `_default` suffix is stripped by `get`, `put` or
`delete`. Legacy YAML read/write steps keep their historical `_default` key normalization.

## Outbound HTTP: `ns.fetch`

`ns.fetch(url, options?)` calls an outside HTTP or HTTPS host. The owner lists the allowed hosts for each project under **Script fetch (ns.fetch)** ([admin-panel.md](admin-panel.md#script-fetch-nsfetch)). A project with no listed host refuses every call. The operator setting `endpoints.fetch_private_networks` is in [configuration.md](configuration.md#scripted-endpoints). The types `FetchOptions`, `FetchResponse` and `FetchError` are in [`ns.d.ts`](/owner-assets/endpoint-api/ns.d.ts).

```js
export default function handle(req, ns) {
  let res;
  try {
    res = ns.fetch("https://api.example.com/v1/rank", {
      method: "POST",
      body: { steamId: req.player === null ? null : req.player.steamId },
      timeoutMs: 3000,
    });
  } catch (error) {
    if (error.code === "FETCH_BLOCKED") return { rank: null };
    throw error; // rethrown: the request answers 502 with the error's code
  }
  if (res === null) return null; // first run: the handler runs again with the response
  return { rank: res.ok && res.json !== null ? res.json.rank : null };
}
```

### Calls run in rounds

A call returns `null` until its request has finished. On the first run the handler gets `null` from every new call. The server then sends the queued requests and runs the handler again. The re-run gets each stored response and sends no second request. So:

- Code after a call must handle `null`. The first run may throw on it; that run's result is discarded.
- Writes and the response come from the final run only.
- Each round that waits on `ns.fetch` counts toward the re-run limit that store reads share (see [Limits](#limits)). The network wait is not script time.
- A call whose arguments come from an earlier call that was still `null` is sent again once those arguments change, and each send counts toward the five-call limit. Return early while an earlier result is `null` (`if (a === null) return null;`) to avoid sending the incomplete request.

The server may also rerun a writing handler after taking record locks, using fresh
record values. Once a fetch ordinal has executed in an earlier pass, its URL, method,
headers, body and timeout must stay identical on that locked rerun. Changed arguments
raise `FETCH_REPLAY_CONFLICT` instead of sending a second request. Separate calls
within one run have separate ordinals and still execute independently.

An external side effect can already have completed even when the endpoint later
fails and its record writes are discarded. Fetches and database writes are not one
transaction. Do not blindly retry a failed mutation: use a stable external
idempotency key and reconcile the external result before retrying.

### Request and response

| Part | Rule |
| --- | --- |
| `url` | Absolute `http` or `https` URL with no user name or password, at most 2,048 characters. |
| `method` | `GET`, `HEAD`, `POST`, `PUT`, `PATCH` or `DELETE`. Default `GET`. The type is uppercase; lowercase also works at run time. |
| `headers` | An object of strings. At most 32 headers, each value at most 4 KiB, no CR, LF or NUL. These names are refused: `host`, `content-length`, `transfer-encoding`, `connection`, `upgrade`, `expect`, `te`, `trailer`, `keep-alive`, and any `proxy-*`. |
| `body` | A string, an object or an array, at most 64 KiB. An object or array is sent as JSON with `content-type: application/json`, unless `headers` sets `content-type`. `GET` and `HEAD` take no body. |
| `timeoutMs` | A number, rounded and clamped to 100 to 10,000. Default 5,000. |

An option name other than `method`, `headers`, `body` or `timeoutMs` is refused as `FETCH_INVALID_REQUEST`.

The response is a `FetchResponse`:

- `status`: the HTTP status.
- `ok`: true for 200 to 299.
- `headers`: names in lowercase. A repeated header is joined with `, `.
- `body`: the text, decoded as UTF-8. Empty for `HEAD`.
- `json`: the parsed body, or `null` when it is not JSON.

The response body is at most 256 KiB. The server follows redirects for 301, 302, 303, 307 and 308, up to 5 hops. A 303 turns any method except GET and HEAD into GET, and a 301 or 302 does the same for POST. A converted request sends no body and drops the `content-*` headers. A cross-origin hop drops `authorization`, `cookie` and `proxy-authorization`. Every hop repeats the allow-list and address checks.

### Allow-list and address rules

Checks run in this order:

1. **An IP literal** (for example `https://10.0.0.5/`). The address rules run first, so a blocked address is refused even when it is listed. An allowed address still needs an exact entry in the list, unless the project allows every public host.
2. **A host name.** The allow-list runs first. Then the server resolves the name and checks every address it gets back. One blocked address refuses the call.

Allow-list entries:

- `api.example.com` matches that host. `*.example.com` matches any subdomain at any depth, but not `example.com` itself.
- Entries are host names only, lowercased, with a trailing dot removed. The port of the URL is not part of the match.
- A list holds at most 100 entries of at most 253 characters each.
- An IP-literal entry matches that exact address and still goes through the address rules.
- With **Allow every public host** on, the list is not read. The address rules still apply.

Address rules apply to every resolved address on every hop:

| Class | Ranges | Blocked | Reason |
| --- | --- | --- | --- |
| Metadata | `169.254.169.254`, `169.254.170.2`, `100.100.100.200`, `fd00:ec2::254` | Always | `metadata_address` |
| Reserved and special-use | `0.0.0.0/8`, `192.0.0.0/24`, `192.0.2.0/24`, `198.18.0.0/15`, `198.51.100.0/24`, `203.0.113.0/24`, `224.0.0.0/4`, `240.0.0.0/4`, `::/96`, `100::/64`, `2001:db8::/32`, `ff00::/8` | Always | `reserved_address` |
| Private, loopback and link-local | `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `::1/128`, `fc00::/7`, `fe80::/10`, `fec0::/10` | Unless `endpoints.fetch_private_networks` is true | `private_address` |

An IPv4 address inside an IPv4-mapped IPv6 address, a NAT64 address (`64:ff9b::/96`) or a 6to4 address (`2002::/16`) is checked as IPv4. Metadata is checked before private, so `169.254.169.254` reports `metadata_address`.

### Limits, errors and the 502 rule

Per execution, across all rounds:

- At most 5 calls are queued. A call counts when it is queued, including one the transport refuses later (a name that resolves to a blocked address, or a redirect to one). A call refused by the allow-list or by an IP-literal address rule is refused at once and does not count. Each call counts, even when it repeats an earlier URL.
- `timeoutMs` defaults to 5,000 ms, clamped to 100 to 10,000 ms.

A failed call throws a JavaScript `Error`. Its `message` starts with `ns.fetch:`, and its `code` is one of these:

| Code | When |
| --- | --- |
| `FETCH_INVALID_REQUEST` | The URL, an option, a header or the body breaks a rule above. Nothing is sent and the call does not count. |
| `FETCH_BLOCKED` | The allow-list or an address rule refuses the host. The message gives the reason. |
| `FETCH_LIMIT_EXCEEDED` | A call beyond the fifth sent or queued request. |
| `FETCH_REPLAY_CONFLICT` | A fetch ordinal executed by an earlier pass has different arguments on the locked rerun. No second request is sent; an earlier external effect may already have completed. |
| `FETCH_TIMEOUT` | No complete response within `timeoutMs`. |
| `FETCH_RESPONSE_TOO_LARGE` | The response is larger than 256 KiB, by `Content-Length` or as it streams. |
| `FETCH_FAILED` | A network error, or more than 5 redirects. |

The script can catch these errors. If the uncaught error is an `ns.fetch` error, the request answers `502` with that code:

```json
{ "ok": false, "error": { "code": "FETCH_BLOCKED", "message": "An outbound request failed." } }
```

A caught error that the script does not rethrow does not change the response. Any other uncaught error answers `500 SCRIPT_ERROR`.

### Diagnostics

- **Try-it trace.** Each call adds a `fetch#N` step with `host`, `method`, `status`, `error` and `reason`.
- **Live requests.** A refused call is reported to the owner's Errors page as `EndpointFetch.blocked` (status 502), with the host and reason as tags. Reports are throttled per project, endpoint, host and reason. A report is made even when the script caught the error and the response succeeded. Try-it runs are not reported. The owner's **Script fetch (ns.fetch)** card lists the refusals ([admin-panel.md](admin-panel.md#script-fetch-nsfetch)).

## Project secrets: `ns.secrets.get`

`ns.secrets.get(name)` returns the value of a named secret that the owner stored for this project, or `null` when no secret has that name. The owner sets, replaces and deletes secrets in the project page's **Secrets** section. Names are case-sensitive: 1 to 64 letters, digits, `_`, `.` or `-`, starting with a letter or digit. A name outside those rules throws an error. The type is `NsSecrets` in [`ns.d.ts`](/owner-assets/endpoint-api/ns.d.ts).

- **Read-only.** A script cannot write secrets. A value is read when a run needs it and is kept for that run only.
- **Write-only in the panel.** After saving, the panel shows the name and the last update time, never the value. Saving an existing name replaces its value.
- **Limits.** 64 secrets per project. A value is 1 to 4096 UTF-8 bytes.
- **Encrypted at rest.** Values are sealed with AES-256-GCM under a key derived from the storage encryption key (`auth.storage_encryption_key_file`, see [configuration.md](configuration.md#project-secrets)). Without that key a secret cannot be saved, and a run that reads a stored secret fails with `SECRET_UNAVAILABLE` (500) instead of receiving `null`.
- **Accidental-output protection.** Unchanged values read during a run are replaced with `[secret:NAME]` in responses, structured logs (before JSON escaping), property names, failure details, traces and blocked-fetch diagnostics. Replacements are matched against the original text once, longest value first; a replacement token is never scanned again. Colliding redacted property names fail closed with `SECRET_OUTPUT_REJECTED`. Very short secrets can mask unrelated text.
- **No secret-bearing record writes.** A write whose collection, key, property name or string data contains a secret read by the run is rejected with `SECRET_WRITE_FORBIDDEN`, before persistence or player projections. Hiding only a write preview would leave a credential available to later reads.
- **Trusted scripts, not information-flow isolation.** Scripts intentionally receive strings and may send them to an allowed external API. They can also encode, split or otherwise transform those strings; exact-value redaction cannot reliably recognise transformed data. Do not return or persist data derived from a secret. This is not a sandbox against a malicious project owner.
- **Outbound requests.** A secret may be sent to an allowed host in an `ns.fetch` header or body; that is the intended use. Redaction covers what the script logs or returns, not the outbound request.
- **Audit.** Owner set, replace and delete actions are audited with the secret's name only. Script reads are not audited individually.
- **Moving servers.** Portable project archives exclude project secrets; re-enter them on the destination. Whole-server export format v2 carries their ciphertext only when secrets are included, and needs the same storage encryption key to decrypt after import. `--no-secrets` excludes both the values and that key. Format v1 archives remain importable. Deleting a project removes its secret rows atomically and prevents a racing save from leaving an orphan.


## Scope, imports and globals

`scope` says who can reach the script:

- `scope: public` (the default). The endpoint is a public HTTP route like any
  other endpoint. Other scripts cannot import it.
- `scope: global`. The endpoint is never a public route. A public call
  answers `404 ENDPOINT_NOT_FOUND`, the same as an unknown slug. Its exports
  are shared with other scripts.

A global endpoint exports values with `export`:

```js
// endpoints/inventory.endpoint.js (scope: global)
export class Inventory {
  constructor(items) { this.items = items; }
  has(itemId) { return this.items.includes(itemId); }
}
export function check_inventory_has_item(player, itemId) {
  const record = ns.records.get("players", player.steamId);
  return Boolean(record) && (record.items ?? []).includes(itemId);
}
```

Other scripts use an export in one of two ways.

```js
// 1. Import it by endpoint id.
import { Inventory } from "globals/inventory";

// 2. Read it through the globals object.
const has = globals.inventory.check_inventory_has_item(req.player, "sword");
```

The `globals/<id>` import path and `globals.<id>` both use the global
endpoint's slug. The server checks imports when the endpoint is saved: an
import cycle reports `GLOBAL_CYCLE`, a missing global `GLOBAL_NOT_FOUND`, a
missing export `GLOBAL_EXPORT_NOT_FOUND`, and module syntax outside the forms
above `SCRIPT_MODULE_UNSUPPORTED`.

Saving a global endpoint also checks the scripted endpoints that import it,
and refuses the save if it would break one of them:

- it disables the global (`enabled: false`): `GLOBAL_NOT_FOUND` on `enabled`;
- it changes the global to `scope: public`: `GLOBAL_NOT_FOUND` on `scope`;
- it removes an export that an importer names: `GLOBAL_EXPORT_NOT_FOUND` on `script`.

The error names the importer. Fix the importer, or keep the export, then save
the global again. A global whose own script does not check fails its own save,
so nothing is compared with its importers. Namespace imports
(`import * as bank from "globals/bank"`) are checked for a disabled or
re-scoped global, but not for removed exports. Reads through `globals.<id>`
are not checked at save time.

## Legacy workflows

Workflows remain the step-based definitions that YAML endpoints use. Scripts
can run them in two ways:

- `ns.workflows.run("<workflow-id>", input)`
- `workflows.<alias>(input)`, where the alias is the workflow id in camelCase

A workflow's writes apply only if the workflow succeeds, as they do for YAML
endpoints. Nested workflow calls stop at the workflow depth limit (see [Limits](#limits)).

An endpoint has either `script` or `steps`, never both. Setting both answers
`SCRIPT_AND_STEPS` at save time.

Save-time checks run when the owner dashboard saves an endpoint, and on the Sync Tool and endpoint writes: `PUT .../sync`, `PUT` and `POST .../endpoints`, and the single-resource `PATCH .../endpoints`. A write that carries a script the checks refuse is refused as a whole and stores nothing (see [Sync refusals](#sync-refusals)). A script that passes is stored as received. The package sync (`POST .../package-sync`) does not run these checks.

## TypeScript

Set `runtime: typescript`. The server removes the type annotations with the
vendored [sucrase](https://github.com/alangpierce/sucrase) stripper (MIT, see
[Third-party notice](#third-party-notice)) and then runs the result as
JavaScript. Nothing else is transformed.

- Types are removed, not checked. A type error does not stop the endpoint.
- Code the stripper cannot read is rejected as `SCRIPT_SYNTAX`, with a line and column, when the owner dashboard saves it.
- The `ns.d.ts` declarations give editors the types. The project typings
  endpoint adds the project's collections, game values, workflow aliases and
  global exports.

## Limits

Every scripted request is bounded. These limits are fixed in code. The only script setting is `endpoints.scripts`; no limit is read from `server.toml`.

The time and statement limits are one budget for the whole request. Module evaluation, the handler call, response
serialisation and every re-run round all draw from it, and a re-run does not reset it. Memory is per engine: each
round runs in its own engine, and rounds run one after another, so the peak is one engine's limit.

Promises are bounded by the same time limit: a promise that does not settle before the request's deadline fails with `SCRIPT_LIMIT_EXCEEDED`, so an async handler cannot hang past it. If the caller disconnects, the run is cancelled and is not reported as `SCRIPT_ERROR`.

| Limit | Default | When exceeded |
| --- | --- | --- |
| Script execution time per request | 1 s of user-code evaluation across all rounds. Engine setup, TypeScript stripping and asynchronous host reads/fetches between rounds are excluded; they still observe request cancellation and the load/round limits. | `SCRIPT_LIMIT_EXCEEDED` |
| Statements per request | 1,000,000, across all engine entries and rounds | `SCRIPT_LIMIT_EXCEEDED` |
| Memory per engine | 64 MiB (each round has its own engine) | `SCRIPT_LIMIT_EXCEEDED` |
| Call depth per engine | 200 | `SCRIPT_LIMIT_EXCEEDED` |
| Re-run rounds per request | 8. Store reads and `ns.fetch` waits share this count. | `SCRIPT_LIMIT_EXCEEDED` |
| Outbound `ns.fetch` calls per request | 5 (see [Outbound HTTP](#outbound-http-nsfetch)) | `FETCH_LIMIT_EXCEEDED`, 502 when uncaught |
| Workflow nesting and steps per request | depth 5, 10,000 steps | `SCRIPT_LIMIT_EXCEEDED` |
| Record reads per request (record gets and lists, workflow definitions, global modules) | 64, across all rounds | `SCRIPT_LIMIT_EXCEEDED` |
| Rows per collection scan | 1,000 | `SCAN_TOO_LARGE` (400), checked before any row is read |
| Record writes and staged data per run | 1,000 writes and 1 MiB of record data | `SCRIPT_LIMIT_EXCEEDED`; the run's writes do not apply |
| Concurrent scripted requests | one per processor on the server; two per project | A request waits up to `endpoints.script_gate.wait_ms` (default 2 s) for a free slot. Past that it is refused with `SCRIPT_BUSY` (429), `Retry-After`, and `retrySafe: true`, and it never runs. |
| Script source | 64 KiB | Rejected when the owner dashboard saves it, and refused by a Sync Tool or endpoint push |
| Response body | 256 KiB | `SCRIPT_LIMIT_EXCEEDED` |
| Log output per run | 8 KiB | Lines past the cap are dropped and a `[log truncated]` line is kept. |

Caches: the server keeps the 256 most recently used compiled JavaScript scripts and the 256 most recently used stripped TypeScript outputs in memory, and evicts the least recently used first. Each process starts with empty caches, so the first call after a restart parses or strips again.

Kill switch: the server setting `endpoints.scripts` (default `true`) turns
scripted endpoints off. While it is off, every scripted endpoint answers `503
SCRIPTS_DISABLED`, and YAML endpoints are unaffected. Restart the server after
changing it.

## Typed error codes

| Code | Reported | Meaning |
| --- | --- | --- |
| `SCRIPT_ERROR` | Run, 500 | The script threw an error that it did not handle. |
| `SCRIPT_LIMIT_EXCEEDED` | Run, 500 | A run limit was hit. See [Limits](#limits). |
| `SCRIPTS_DISABLED` | Run, 503 | `endpoints.scripts` is off. |
| `SCRIPT_BUSY` | Run, 429 | The server or the project stayed at its maximum number of scripted requests past the wait. Nothing ran, so resend the same request after `Retry-After` (`retrySafe: true`). |
| `FETCH_*` | Run, 502 | An `ns.fetch` call failed and the script did not catch it. The codes are in [Outbound HTTP](#outbound-http-nsfetch). |
| `SECRET_UNAVAILABLE` | Run, 500 | The script read a project secret that is stored but cannot be decrypted, or no `STORAGE_ENCRYPTION_KEY` is configured. Nothing is returned. See [Project secrets](#project-secrets-nssecretsget). |
| `ENDPOINT_NOT_FOUND` | Run, 404 | Unknown slug, or a `scope: global` endpoint called publicly. |
| `SCRIPT_SYNTAX` | Save, sync | The source cannot be parsed. Reports the line and column. |
| `SCRIPT_AND_STEPS` | Save | An endpoint has both `script` and `steps`. |
| `MISSING_SCRIPT` | Save | A scripted endpoint has no `script`. |
| `SCRIPT_FILE_WITHOUT_SCRIPT` | Save | `scriptFile` is set but `script` is not. |
| `SCRIPT_TOO_LARGE` | Save, sync | The source is larger than the 64 KiB limit. Reported at `/script`. |
| `SCRIPT_MODULE_UNSUPPORTED` | Save, sync | Module syntax outside the supported forms. |
| `GLOBAL_NOT_FOUND` | Save, sync | An import names no enabled global endpoint. On a global save, also a scripted endpoint that imports a global the save disables or makes `scope: public`. |
| `GLOBAL_EXPORT_NOT_FOUND` | Save, sync | A global endpoint does not export the imported name. On a global save, also a scripted endpoint that imports a name the save removes. |
| `GLOBAL_CYCLE` | Save, sync | Global imports form a cycle. |
| `SOURCE_COMPILE_FAILED` | Save | The endpoint source did not compile. |

Sync preflight runs the same script checks as the sync write, with the batch's own globals included. Each code marked "Save, sync" above is an error in preflight, and any of them fails it with `PREFLIGHT_VALIDATION_FAILED` (400). Each finding carries `file`, and `line` and `column` where the parser reports them. Other findings stay warnings.

`ns.fail` takes any code and a status from 400 to 499. The codes above are
the server's own. Template codes, such as `AUTH_REQUIRED` or
`INSUFFICIENT_COINS`, belong to the template and are listed with it.

## Sync refusals

A write that carries a scripted endpoint the checks refuse is refused as a whole. The checks are the ones marked "Save, sync" in [Typed error codes](#typed-error-codes). They run on `PUT .../sync`, `PUT` and `POST .../endpoints`, and `PATCH .../endpoints`. Nothing is written, not even the other items of the batch. An import of a global pushed in the same batch resolves, and an import of a stored global resolves against its stored definition.

A refused write returns 400 `VALIDATION_FAILED`:

- `ok` is `false`, and `source` is `"candidate"`.
- `error` is `VALIDATION_FAILED`, and `message` says that nothing was written.
- `diagnostics` lists every refused finding, with `code`, `severity` (`error`), `message`, `resourceKind`, `resourceId`, `sourcePath` (the JSON pointer, `/script`), `file`, and `line` and `column` where the parser reports them. `file` is the item's `sourcePath` from the push, when it has one.
- `endpoints` is `{ ok, total, failed, results }`. Each failed endpoint in `results` has its `resourceId`, its own `error` and `message`, and its `diagnostics`.

A sync preflight with the same finding returns 400 `PREFLIGHT_VALIDATION_FAILED`. Each finding is in the `diagnostics` of its item under `endpoints`, with the same fields. Preflight writes nothing.

## Templates

Starter endpoints ship in JavaScript and TypeScript. The list of them is `wwwroot/js/endpoint-api/templates/index.json`, embedded in the server build. The New dialog and the examples gallery both read that list. Each entry has an `id`, `title`, `summary`, `scope` (`route` or `global`) and `runtimes`. An entry that imports another template's module names it in `needs`. Each runtime is one file, `<id>.<runtime>.endpoint.yml`, served at `/owner-assets/endpoint-api/templates/<file>`.

| Template | Method | Shows |
| --- | --- | --- |
| `grant-coins` | POST | A secret-key-only grant that writes a record. |
| `submit-score` | POST | Input validation, a best-score update and a typed error. |
| `daily-reward` | POST | A 24-hour cooldown from `ns.now()` and a game value with a default. |
| `profile` | GET | A read-only response for the signed-in player. |
| `inventory` | GET | A `scope: global` module with a class and two functions. Not public. |
| `buy-item` | POST | `import` from `globals/inventory`, `globals.inventory`, and typed errors. |

The New dialog offers Blank (YAML steps) and the standalone routes `grant-coins`, `submit-score`, `daily-reward` and `profile`, in both runtimes. Two templates stay out of the dialog and appear only in the gallery:

- `buy-item` imports `globals/inventory`, so it fails validation unless the project already has that module. Its gallery card names the module it needs.
- `inventory` is a `scope: global` module. It is not an HTTP route.

`GET /dashboard/projects/{id}/resources/endpoint/new?draft=<id>&template=<id>.<runtime>` opens a draft from a template, for example `template=grant-coins.typescript`. Leave `template` out for Blank. An unknown or unoffered choice, or a template on another kind, returns 400. An index that failed to load returns 503 with the reason.

The shared typings are at
`/owner-assets/endpoint-api/ns.d.ts`. A project's typings, with its own
collections, game values and globals, are at
`GET /dashboard/projects/{id}/resources/endpoint-typings`.

## Editing in the dashboard

The endpoint editor is Monaco, the editor behind VS Code. It is served from the server, so it works without a CDN. The YAML definition and the script body are separate editors. The script pane appears when the YAML has a top-level `script: |` block with runtime `javascript` or `typescript`.

- Completions and hover for `req`, `ns` and `globals` come from `ns.d.ts`. The project's typings are used first; the shared file is the fallback.
- The Script API panel lists each `ns` and `req` member with its docs. Filter the list, select an entry and insert its call at the cursor.
- Check results that report a line and column for `/script` appear as markers in the script. Results without a position stay in the results list.
- Ctrl+S (Cmd+S on macOS) saves. Tab indents inside the editor; press Ctrl+M to move focus out.
- Trying an endpoint from the editor is not available yet.
- If Monaco cannot load, the plain text box stays in use with the same content.

## File layout

Authored endpoints keep the definition and the script side by side:

```text
Editor/Network Storage/
  endpoints/daily-reward.endpoint.yml
  endpoints/daily-reward.endpoint.js
```

`scope` travels in the definition too. A file with `scope: global` saves a
global endpoint, and a file without `scope` saves a public one.

`scriptFile` names the script file from the definition:

```yaml
method: POST
runtime: javascript
scope: public
scriptFile: daily-reward.endpoint.js
```

`scriptFile` is an authoring reference only. The server stores the script
text under `script`. A definition that sets `scriptFile` without `script` is
rejected with `SCRIPT_FILE_WITHOUT_SCRIPT`. The Sync Tool does not yet read
`scriptFile` or upload script files. Support is tracked in
[sbox-cool/sbox-network-storage#45](https://github.com/sbox-cool/sbox-network-storage/issues/45).
Until it lands, put the script text in `script`.

## Third-party notice

The TypeScript runtime bundles sucrase 3.35.1 (MIT) with its dependencies:
lines-and-columns 1.2.4 (MIT), ts-interface-checker 0.1.13 (Apache-2.0) and the
@jridgewell source-map packages (MIT). The bundle, the licence texts and a build
script are in `src/SboxNetworkStorage.Application/NetworkStorage/Endpoints/Scripting/ThirdParty/sucrase/`
(see its `README.md`). The code runs inside the isolated script engine. It is
not compiled into the server.
