// Types for sbox network storage endpoint scripts (runtime: javascript or typescript).
// The project typings endpoint (GET /dashboard/projects/<id>/resources/endpoint-typings)
// appends the project's collections, game values, workflows and global endpoints to this file.
// A test keeps the Ns interfaces in step with the [ScriptApi] members of EndpointScriptHost.

/** The request object: the first argument of `export default function handle(req, ns)`. */
interface EndpointRequest {
  /**
   * Endpoint input: the query string for GET, the JSON body for POST.
   * @example const userKey = req.input.userKey; // User-supplied: validate before use.
   */
  input: Record<string, unknown>;
  /**
   * The server-verified player, or null when anonymous or called with a secret key.
   * @example if (!req.player) ns.fail("PLAYER_REQUIRED", "Sign in first.", 401);
   */
  player: EndpointPlayer | null;
  /** HTTP method. */
  method: "GET" | "POST";
  /** Endpoint slug. */
  endpoint: string;
  /** The game client's revision id, or null when the client sent none. */
  revision: string | null;
}

interface EndpointPlayer {
  /**
   * The authenticated player's Steam ID. Never take this identity from untrusted input.
   * @example const steamId = req.player?.steamId; // Server-verified, or undefined when no player.
   */
  steamId: string;
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
  /**
   * Reads a record. Returns null when it does not exist.
   * @example if (!req.player) ns.fail("PLAYER_REQUIRED", "Sign in first.", 401);
   * const player = ns.records.get("players", req.player.steamId);
   * @limits Counts toward the request's data-read budget.
   */
  get(collection: CollectionId, key: string): RecordData | null;
  /**
   * Stages a record write. Writes commit only when the endpoint succeeds.
   * @example if (!req.player) ns.fail("PLAYER_REQUIRED", "Sign in first.", 401);
   * ns.records.put("players", req.player.steamId, { coins: 10 });
   * @throws Schema, access or version checks can refuse the write.
   * @limits Staged writes and their serialized size share the request's write budgets.
   */
  put(collection: CollectionId, key: string, data: RecordData): void;
  /**
   * Stages a record deletion. Nothing is deleted if the endpoint fails.
   * @example if (!req.player) ns.fail("PLAYER_REQUIRED", "Sign in first.", 401);
   * ns.records.delete("players", req.player.steamId);
   * @throws Collection access rules can refuse the deletion.
   * @limits Shares the request's staged-write budget.
   */
  delete(collection: CollectionId, key: string): void;
  /**
   * Lists the records of a collection.
   * @example const players = ns.records.list("players");
   * @throws SCAN_TOO_LARGE when the collection exceeds the server's scan cap.
   * @limits The server refuses an over-cap scan rather than returning a partial collection.
   */
  list(collection: CollectionId): RecordData[];
}

interface NsValues {
  /**
   * Reads a game value by dotted path: "group" or "group.key". Returns null when absent.
   * @example const startingCoins = ns.values.get("rules.starting_coins");
   */
  get(path: GameValuePath): unknown;
}

interface NsWorkflows {
  /**
   * Runs a workflow of this project and returns its result. Its writes apply only if it succeeds.
   * @example const reward = ns.workflows.run("daily-reward", { amount: 10 });
   * @throws Workflow errors end this call; missing workflows are not silently created.
   * @limits Shares the calling endpoint's execution, workflow and storage budgets.
   */
  run(workflowId: string, input: Record<string, unknown>): unknown;
}

/** Project secrets, read-only. Unchanged values are redacted at output boundaries; do not echo or persist transformed secrets. */
interface NsSecrets {
  /**
   * The value of a project secret, or null when no secret has that name. Names are case-sensitive. A secret that
   * exists but cannot be decrypted fails the run with SECRET_UNAVAILABLE instead of returning null.
   * @example const token = ns.secrets.get("SERVICE_TOKEN");
   * @throws SECRET_UNAVAILABLE if a stored value cannot be decrypted.
   * @limits Names are case-sensitive, at most 64 characters. Never return or persist transformed credentials.
   */
  get(name: string): string | null;
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
  secrets: NsSecrets;
  /**
   * Server time in milliseconds since the Unix epoch.
   * @example const now = ns.now();
   */
  now(): number;
  /**
   * Keeps a structured log value for this invocation. Try it shows the logs; failed live calls include them in owner diagnostics.
   * @example ns.log({ endpoint: req.endpoint, item: req.input.item });
   * @limits Logs are bounded by the server's script log-byte limit. Loaded secrets are redacted.
   */
  log(value: unknown): void;
  /**
   * Ends the request with an error. The script cannot catch it.
   * @example ns.fail("INVALID_AMOUNT", "Amount must be positive.", 400);
   * @limits Status must be 400-499; the default is 400. Staged writes do not commit.
   */
  fail(code: string, message: string, status?: number): never;
  /**
   * Outbound HTTP to a host on the project's allow-list. The first run of the handler gets null while the result
   * loads, and the handler re-runs with the response. A refused or failed call throws a FetchError.
   * @example const response = ns.fetch("https://api.example.com/items", { timeoutMs: 5000 });
   * if (!response) return null;
   * return response.json;
   * @throws FETCH_BLOCKED, FETCH_INVALID_REQUEST, FETCH_LIMIT_EXCEEDED, FETCH_TIMEOUT, FETCH_RESPONSE_TOO_LARGE or FETCH_FAILED.
   * @limits At most 5 requests per execution; 64 KiB request bodies and 256 KiB responses. Timeout is clamped to 100-10000 ms.
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
