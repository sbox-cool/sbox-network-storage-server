"use strict";
// Guards for the endpoint script docs panel (wwwroot/js/owner-script-docs.js).
// The shipped ns.d.ts is the source; these tests fail when the Ns surface and the parser drift apart.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { describe, it } = require("node:test");
const docs = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-script-docs.js");

const shipped = fs.readFileSync(
  path.join(__dirname, "../../src/SboxNetworkStorage.Server/wwwroot/js/endpoint-api/ns.d.ts"), "utf8");
const all = docs.entries(docs.parse(shipped));
const byPath = new Map(all.map((item) => [item.path, item]));

describe("ns.d.ts members", () => {
  it("lists every member of the script API with its JSDoc", () => {
    const get = byPath.get("ns.records.get");
    assert.equal(get.kind, "method");
    assert.equal(get.insert, "ns.records.get(");
    assert.equal(get.doc, "Reads a record. Returns null when it does not exist.");
    assert.equal(byPath.get("req.input").doc, "Endpoint input: the query string for GET, the JSON body for POST.");
    assert.equal(byPath.get("req.input").insert, "req.input");
    assert.equal(byPath.get("ns.values.get").doc.startsWith("Reads a game value by dotted path"), true);
    assert.match(byPath.get("ns.fail").doc, /Ends the request with an error/);
  });

  it("keeps in step with the Ns interfaces (drift guard)", () => {
    const expected = [
      "ns.records.get", "ns.records.put", "ns.records.delete", "ns.records.list",
      "ns.values.get", "ns.workflows.run", "ns.now", "ns.log", "ns.fail",
      "req.input", "req.player", "req.method", "req.endpoint", "req.revision",
    ];
    for (const member of expected) assert.ok(byPath.has(member), member);
    const topLevel = all.filter((item) => /^ns\.[^.]+$/.test(item.path)).map((item) => item.name).sort();
    assert.deepEqual(topLevel, ["fail", "log", "now", "records", "values", "workflows"]);
  });

  it("groups each entry under its root", () => {
    assert.equal(byPath.get("ns.now").group, "Script API");
    assert.equal(byPath.get("req.input").group, "Request");
    assert.equal(byPath.get("workflows").group, "Workflows");
  });
});

describe("project typings", () => {
  const project = shipped + `
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

  it("returns nothing for empty input", () => {
    assert.deepEqual(docs.entries(docs.parse("")), []);
  });
});

describe("filter", () => {
  it("keeps everything for an empty query and matches path, signature and docs otherwise", () => {
    assert.equal(docs.filter(all, "  ").length, all.length);
    const found = docs.filter(all, "RECORD").map((item) => item.path);
    assert.ok(found.includes("ns.records.get"));
    assert.ok(!found.includes("ns.now"));
    assert.ok(docs.filter(all, "server time").some((item) => item.path === "ns.now"));
  });
});
