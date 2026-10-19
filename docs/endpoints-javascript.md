# JavaScript and TypeScript endpoints

A scripted endpoint is a YAML endpoint whose logic is a function instead of
`steps`. Set `runtime: javascript` or `runtime: typescript` and put the code in
`script`. The server runs it on your server, against the same records, game
values and clock that YAML endpoints use. YAML endpoints are unchanged.

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
| `ns.log(value)` | Writes a value to the server log. Capped by `MaxLogBytes`. |
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
endpoints. Nested workflow calls stop at `MaxRecursionDepth`.

An endpoint has either `script` or `steps`, never both. Setting both answers
`SCRIPT_AND_STEPS` at save time.

## TypeScript

Set `runtime: typescript`. The server removes the type annotations with the
vendored [sucrase](https://github.com/alangpierce/sucrase) stripper (MIT, see
[Third-party notice](#third-party-notice)) and then runs the result as
JavaScript. Nothing else is transformed.

- Types are removed, not checked. A type error does not stop the endpoint.
- Code the stripper cannot read is rejected at save time as `SCRIPT_SYNTAX`,
  with a line and column.
- The `ns.d.ts` declarations give editors the types. The project typings
  endpoint adds the project's collections, game values, workflow aliases and
  global exports.

## Limits

Every run is bounded. These are the defaults.

| Limit | Default | When exceeded |
| --- | --- | --- |
| Time per run | 1 s | `SCRIPT_LIMIT_EXCEEDED` |
| Statements per run | 1,000,000 | `SCRIPT_LIMIT_EXCEEDED` |
| Memory per run | 64 MiB | `SCRIPT_LIMIT_EXCEEDED` |
| Call depth (including workflow nesting) | 200 | `SCRIPT_LIMIT_EXCEEDED` |
| Script source | 64 KiB | Rejected at save time |
| Response body | 256 KiB | `SCRIPT_LIMIT_EXCEEDED` |
| Log output per run | 8 KiB | Capped. Logging past the cap is not kept. |

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
| `ENDPOINT_NOT_FOUND` | Run, 404 | Unknown slug, or a `scope: global` endpoint called publicly. |
| `SCRIPT_SYNTAX` | Save | The source cannot be parsed. Reports the line and column. |
| `SCRIPT_AND_STEPS` | Save | An endpoint has both `script` and `steps`. |
| `MISSING_SCRIPT` | Save | A scripted endpoint has no `script`. |
| `SCRIPT_FILE_WITHOUT_SCRIPT` | Save | `scriptFile` is set but `script` is not. |
| `SCRIPT_MODULE_UNSUPPORTED` | Save | Module syntax outside the supported forms. |
| `GLOBAL_NOT_FOUND` | Save | An import names no enabled global endpoint. |
| `GLOBAL_EXPORT_NOT_FOUND` | Save | A global endpoint does not export the imported name. |
| `GLOBAL_CYCLE` | Save | Global imports form a cycle. |
| `SOURCE_COMPILE_FAILED` | Save | The endpoint source did not compile. |

`ns.fail` takes any code and a status from 400 to 499. The codes above are
the server's own. Template codes, such as `AUTH_REQUIRED` or
`INSUFFICIENT_COINS`, belong to the template and are listed with it.

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

The TypeScript runtime uses sucrase 3.35.1, MIT License, Copyright (c) 2012-2018
various contributors. The bundled stripper and its license are in
`src/SboxNetworkStorage.Application/NetworkStorage/Endpoints/Scripting/ThirdParty/sucrase/`.
The code runs inside the isolated script engine. It is not compiled into the
server.
