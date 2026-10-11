"use strict";
// Guards for the project filter matching (wwwroot/js/owner-manage.js).
// The shipped file runs its DOM wiring on load; stub the calls it makes.
globalThis.document = {
  querySelector: () => null,
  querySelectorAll: () => [],
  getElementById: () => null,
};
globalThis.window = { location: { pathname: "/" } };

const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const manage = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-manage.js");

describe("matchesProject", () => {
  it("matches name or id, case-insensitive", () => {
    assert.equal(manage.matchesProject("My Game", "proj_abc", "game"), true);
    assert.equal(manage.matchesProject("My Game", "proj_abc", "PROJ_ABC"), true);
    assert.equal(manage.matchesProject("My Game", "proj_abc", "abc"), true);
  });

  it("rejects non-matches and matches everything on an empty query", () => {
    assert.equal(manage.matchesProject("My Game", "proj_abc", "other"), false);
    assert.equal(manage.matchesProject("My Game", "proj_abc", ""), true);
    assert.equal(manage.matchesProject("My Game", "proj_abc", "   "), true);
  });
});
