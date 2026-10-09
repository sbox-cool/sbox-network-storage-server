"use strict";
// Guards for the resources editor gallery, palette and check wiring
// (wwwroot/js/owner-builder-catalog.js). The script returns early without a
// definition textarea; stub the DOM it touches on load.
globalThis.document = { getElementById: () => null };
globalThis.window = { location: { pathname: "/" } };

const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const catalog = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-builder-catalog.js");

describe("projectPaths", () => {
  it("resolves the catalog root and kind", () => {
    assert.deepEqual(
      catalog.projectPaths("/dashboard/projects/proj_1/resources/endpoint"),
      { projectRoot: "/dashboard/projects/proj_1/resources", kind: "endpoint" });
  });
  it("defaults to endpoint", () => {
    assert.equal(catalog.projectPaths("/dashboard/projects/proj_1/resources").kind, "endpoint");
  });
  it("rejects other pages", () => {
    assert.equal(catalog.projectPaths("/dashboard/projects"), null);
  });
});

describe("stepGroups", () => {
  it("covers every executor step type exactly once", () => {
    const supported = ["read", "write", "delete", "lookup", "filter", "lookup_many", "random_select",
      "condition", "assert", "transform", "object", "array", "merge", "sort", "switch", "compute",
      "random", "block", "workflow", "webhook", "sleep", "response"];
    const grouped = Object.values(catalog.stepGroups).flat();
    assert.deepEqual([...grouped].sort(), [...supported].sort());
  });
});

describe("callSummary", () => {
  it("describes secret and public calls", () => {
    assert.equal(
      catalog.callSummary({ method: "POST", secret: false, status: 200, input: '{"a":1}' }),
      "POST, public key, expects 200, input {\"a\":1}");
    assert.equal(catalog.callSummary({ secret: true, status: 409 }), "POST, secret key, expects 409");
  });
});
