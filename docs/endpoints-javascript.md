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
| `ns.fail(code, message, status?)` | Ends the request with a typed error. |

Writes made by a script apply when the endpoint returns normally. A request
that ends with `ns.fail` does not keep its writes.

## Scope, imports and globals

`scope` says who can reach the script:

- `scope: public` (the default). The endpoint is a public HTTP route like any
  other endpoint. It can also be imported by other scripts.
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
| Time per request | 1 s, counted from the start of script work (includes store reads between rounds) | `SCRIPT_LIMIT_EXCEEDED` |
| Statements per request | 1,000,000, across all engine entries and rounds | `SCRIPT_LIMIT_EXCEEDED` |
| Memory per engine | 64 MiB (each round has its own engine) | `SCRIPT_LIMIT_EXCEEDED` |
| Call depth per engine | 200 | `SCRIPT_LIMIT_EXCEEDED` |
| Re-run rounds per request | 8 | `SCRIPT_LIMIT_EXCEEDED` |
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
| `ENDPOINT_NOT_FOUND` | Run, 404 | Unknown slug, or a `scope: global` endpoint called publicly. |
| `SCRIPT_SYNTAX` | Save, sync | The source cannot be parsed. Reports the line and column. |
| `SCRIPT_AND_STEPS` | Save | An endpoint has both `script` and `steps`. |
| `MISSING_SCRIPT` | Save | A scripted endpoint has no `script`. |
| `SCRIPT_FILE_WITHOUT_SCRIPT` | Save | `scriptFile` is set but `script` is not. |
| `SCRIPT_TOO_LARGE` | Save, sync | The source is larger than the 64 KiB limit. Reported at `/script`. |
| `SCRIPT_MODULE_UNSUPPORTED` | Save, sync | Module syntax outside the supported forms. |
| `GLOBAL_NOT_FOUND` | Save, sync | An import names no enabled global endpoint. |
| `GLOBAL_EXPORT_NOT_FOUND` | Save, sync | A global endpoint does not export the imported name. |
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

Each template ships in both runtimes. Templates are data files served as
static assets. Their list is:

| Template | Method | Shows |
| --- | --- | --- |
| `grant-coins` | POST | A secret-key-only grant that writes a record. |
| `submit-score` | POST | Input validation, a best-score update and a typed error. |
| `daily-reward` | POST | A 24-hour cooldown from `ns.now()` and a game value with a default. |
| `profile` | GET | A read-only response for the signed-in player. |
| `inventory` | GET | A `scope: global` module with a class and two functions. Not public. |
| `buy-item` | POST | `import` from `globals/inventory`, `globals.inventory`, and typed errors. |

Files are named `<template>.javascript.endpoint.yml` and
`<template>.typescript.endpoint.yml`. Served at
`/owner-assets/endpoint-api/templates/<file>`. The shared typings are at
`/owner-assets/endpoint-api/ns.d.ts`. A project's typings, with its own
collections, game values and globals, are at
`GET /dashboard/projects/{id}/resources/endpoint-typings`.

## File layout

Authored endpoints keep the definition and the script side by side:

```text
Editor/Network Storage/
  endpoints/daily-reward.endpoint.yml
  endpoints/daily-reward.endpoint.js
```

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
