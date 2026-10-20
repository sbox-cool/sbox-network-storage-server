// Types for sbox network storage endpoint scripts (runtime: javascript or typescript).
// The project typings endpoint (GET /dashboard/projects/<id>/resources/endpoint-typings)
// appends the project's collections, game values, workflows and global endpoints to this file.
// A test keeps the Ns interfaces in step with the [ScriptApi] members of EndpointScriptHost.

/** The request object: the first argument of `export default function handle(req, ns)`. */
interface EndpointRequest {
  /** Endpoint input: the query string for GET, the JSON body for POST. */
  input: Record<string, unknown>;
  /** The server-verified player, or null when anonymous or called with a secret key. */
  player: { steamId: string } | null;
  /** HTTP method. */
  method: "GET" | "POST";
  /** Endpoint slug. */
  endpoint: string;
  /** The game client's revision id, or null when the client sent none. */
  revision: string | null;
}

/** A stored record: a JSON object. */
type RecordData = { [field: string]: unknown };

/** Collection ids of the project. The project typings fill in CollectionIdMap. */
interface CollectionIdMap {}
type CollectionId = [keyof CollectionIdMap] extends [never] ? string : keyof CollectionIdMap & string;

/** Game value groups and tables with their keys. The project typings fill in GameValueGroups. */
interface GameValueGroups {}
type GameValuePath = [keyof GameValueGroups] extends [never] ? string
  : { [G in keyof GameValueGroups & string]: G | `${G}.${keyof GameValueGroups[G] & string}` }[keyof GameValueGroups & string];

/** Workflows by camelCase alias: `workflows.<alias>(input)`. The project typings fill in WorkflowAliases. */
interface WorkflowAliases {}

/** Exports of global endpoints by endpoint id: `globals.<endpointId>.<export>`. The project typings fill in GlobalModules. */
interface GlobalModules {}

interface NsRecords {
  /** Reads a record. Returns null when it does not exist. */
  get(collection: CollectionId, key: string): RecordData | null;
  /** Writes a record. */
  put(collection: CollectionId, key: string, data: RecordData): void;
  /** Deletes a record. */
  delete(collection: CollectionId, key: string): void;
  /** Lists the records of a collection. */
  list(collection: CollectionId): RecordData[];
}

interface NsValues {
  /** Reads a game value by dotted path: "group" or "group.key". Returns null when absent. */
  get(path: GameValuePath): unknown;
}

interface NsWorkflows {
  /** Runs a workflow of this project and returns its result. Its writes apply only if it succeeds. */
  run(workflowId: string, input: Record<string, unknown>): unknown;
}

/** Options for `ns.fetch`. Every field is optional. */
interface FetchOptions {
  /** HTTP method. Defaults to GET. */
  method?: "GET" | "HEAD" | "POST" | "PUT" | "PATCH" | "DELETE";
  /** Request headers. Forbidden names are rejected: host, content-length, transfer-encoding, connection, upgrade, expect, te, trailer, keep-alive and any proxy-*. */
  headers?: Record<string, string>;
  /** Request body. An object or array is sent as JSON with content-type application/json, unless the headers set content-type. Not allowed with GET or HEAD. */
  body?: string | Record<string, unknown> | unknown[];
  /** Timeout in milliseconds, clamped to 100-10000. Defaults to 5000. */
  timeoutMs?: number;
}

/** The response of `ns.fetch`. */
interface FetchResponse {
  status: number;
  /** True when status is 200-299. */
  ok: boolean;
  /** Header names are lowercase. A repeated header is joined with ", ". */
  headers: Record<string, string>;
  /** The body, decoded as UTF-8. */
  body: string;
  /** The parsed JSON body, or null when the body is not JSON. */
  json: unknown;
}

/** The error `ns.fetch` throws. `code` is a FETCH_* code. */
interface FetchError extends Error {
  code: string;
}

interface Ns {
  records: NsRecords;
  values: NsValues;
  workflows: NsWorkflows;
  /** Server time in milliseconds since the Unix epoch. */
  now(): number;
  /** Writes a value to the server log. */
  log(value: unknown): void;
  /** Ends the request with an error. The script cannot catch it. */
  fail(code: string, message: string, status?: number): never;
  /**
   * Outbound HTTP to a host on the project's allow-list. The first run of the handler gets null while the result
   * loads, and the handler re-runs with the response. A refused or failed call throws a FetchError.
   */
  fetch(url: string, options?: FetchOptions): FetchResponse | null;
}

/** The `ns` object. Also the second argument of `handle(req, ns)`. */
declare const ns: Ns;
/** The request object. Also the first argument of `handle(req, ns)`. */
declare const req: EndpointRequest;
/** Workflows by camelCase alias. */
declare const workflows: WorkflowAliases;
/** Exports of global endpoints by endpoint id. */
declare const globals: GlobalModules;

/** The default export of an endpoint script. */
type EndpointHandler = (req: EndpointRequest, ns: Ns) => unknown;
