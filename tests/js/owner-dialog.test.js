"use strict";
// Guards for the shared modal/confirm dialogs (wwwroot/js/owner-dialog.js).
// The shipped file runs its DOM wiring on load; stub the calls it makes.
globalThis.document = {
  getElementById: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  documentElement: { classList: { add: () => {} } },
  body: null,
};
globalThis.window = {};

const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const dialog = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-dialog.js");

describe("resolveReturnTarget", () => {
  it("returns the opener while it is still in the document", () => {
    const opener = { focus: () => {} };
    const doc = { contains: () => true, getElementById: () => null, body: {} };
    assert.equal(dialog.resolveReturnTarget(opener, doc), opener);
  });

  it("falls back to the main landmark when the opener is gone", () => {
    const opener = { focus: () => {} };
    const main = { focus: () => {} };
    const doc = { contains: () => false, getElementById: (id) => (id === "main-content" ? main : null), body: {} };
    assert.equal(dialog.resolveReturnTarget(opener, doc), main);
  });

  it("falls back to body when there is no usable target", () => {
    const body = {};
    const doc = { contains: () => false, getElementById: () => null, body };
    assert.equal(dialog.resolveReturnTarget(null, doc), body);
    assert.equal(dialog.resolveReturnTarget({ notFocusable: true }, doc), body);
  });

  it("returns null without a document", () => {
    assert.equal(dialog.resolveReturnTarget({ focus: () => {} }, null), null);
  });
});
