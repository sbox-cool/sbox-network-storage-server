"use strict";
// Parser and filter behavior for the endpoint script docs panel.
const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const docs = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-script-docs.js");


describe("project typings", () => {
  const project = `
interface CollectionIdMap {
  /** Player profiles. */
  "player-profiles": { id: string };
  inventory: { id: string };
}
interface WorkflowAliases {
  /** Runs the reward workflow. */
  grantReward(input: { amount: number }): unknown;
}
`;

  it("adds collection ids and workflow aliases with their docs", () => {
    const items = docs.entries(docs.parse(project));
    const profiles = items.find((item) => item.path === "player-profiles");
    assert.equal(profiles.group, "Collections");
    assert.equal(profiles.insert, "\"player-profiles\"");
    assert.equal(profiles.doc, "Player profiles.");
    assert.equal(items.find((item) => item.path === "grantReward").doc, "Runs the reward workflow.");
  });
});

describe("parse", () => {
  it("reads multi-line JSDoc and skips doc lines that precede nothing", () => {
    const parsed = docs.parse([
      "interface Box {",
      "  /**",
      "   * First line.",
      "   * Second line.",
      "   */",
      "  open(): void;",
      "}",
    ].join("\n"));
    assert.equal(parsed.interfaces.Box[0].doc, "First line. Second line.");
    assert.equal(parsed.interfaces.Box[0].kind, "method");
  });

  it("keeps copyable example formatting separate from descriptions, errors and limits", () => {
    const parsed = docs.parse([
      "interface Service {",
      "  /** Reads a value.",
      "   * @example if (ready) {",
      "   *   read();",
      "   * }",
      "   *",
      "   * finish();",
      "   * @throws READ_FAILED on unavailable storage.",
      "   * @limits Shares the caller's budget.",
      "   */",
      "  read(): unknown;",
      "}"
    ].join("\n"));
    const entry = parsed.interfaces.Service[0];
    assert.equal(entry.doc, "Reads a value.");
    assert.equal(entry.example, "if (ready) {\n  read();\n}\n\nfinish();");
    assert.equal(entry.errors, "READ_FAILED on unavailable storage.");
    assert.equal(entry.limits, "Shares the caller's budget.");
  });

  it("lists fields reached through a nullable interface without expanding cycles", () => {
    const parsed = docs.parse([
      "interface Request {",
      "  player: Player | null;",
      "}",
      "interface Player {",
      "  steamId: string;",
      "  request: Request;",
      "}",
      "declare const req: Request;"
    ].join("\n"));
    assert.deepEqual(docs.entries(parsed).map(entry => entry.path), [
      "req", "req.player", "req.player.steamId", "req.player.request"
    ]);
  });

  it("returns nothing for empty input", () => {
    assert.deepEqual(docs.entries(docs.parse("")), []);
  });
});

describe("filter", () => {
  const all = [
    { path: "ns.records.get", signature: "get(key: RecordKey)", doc: "Reads saved data.", errors: "READ_FAILED" },
    { path: "ns.now", signature: "now(): number", doc: "Server time.", limits: "Execution budget" }
  ];
  it("keeps everything for an empty query and matches path, signature and docs otherwise", () => {
    assert.equal(docs.filter(all, "  ").length, all.length);
    const found = docs.filter(all, "RECORD").map((item) => item.path);
    assert.ok(found.includes("ns.records.get"));
    assert.ok(!found.includes("ns.now"));
    assert.ok(docs.filter(all, "server time").some((item) => item.path === "ns.now"));
    assert.deepEqual(docs.filter(all, "read_failed"), [all[0]]);
    assert.deepEqual(docs.filter(all, "execution budget"), [all[1]]);
  });
});

describe("globalModules", () => {
  it("lists each declared global module with its export names, including modules with none", () => {
    const typings = [
      "declare module \"globals/grant\" {",
      "  export function handle(req: unknown): unknown;",
      "  export const limits: unknown;",
      "}",
      "",
      "declare module \"globals/empty\" {",
      "}",
      "",
    ].join("\n");
    assert.deepEqual(docs.globalModules(typings), [
      { id: "grant", exports: ["handle", "limits"] },
      { id: "empty", exports: [] },
    ]);
    assert.deepEqual(docs.globalModules(null), []);
  });
});
