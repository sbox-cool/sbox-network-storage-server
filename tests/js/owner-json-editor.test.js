"use strict";
// Guards for the opt-in visual editor (wwwroot/js/owner-json-editor.js).
// The shipped file runs its DOM wiring on load; stub the one call it makes.
globalThis.document = { querySelectorAll: () => [] };

const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const editor = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-json-editor.js");

describe("exactNumber", () => {
  it("accepts ordinary values", () => {
    for (const token of ["0", "27", "-4.25", "1e3", "0.1", String(Number.MAX_SAFE_INTEGER)])
      assert.equal(editor.exactNumber(token), true, token);
  });
  it("rejects precision-losing decimals", () => {
    assert.equal(editor.exactNumber("0.123456789012345678901"), false);
    assert.equal(editor.exactNumber("9007199254740993"), false);
    assert.equal(editor.exactNumber("-0"), false);
  });
});

describe("scanProblems", () => {
  it("accepts clean objects", () => {
    assert.equal(editor.scanProblems('{"score":27,"nested":{"enabled":true},"items":["sword",null]}'), null);
  });
  it("refuses precision loss the parser would hide", () => {
    assert.match(editor.scanProblems('{"value":0.123456789012345678901}'), /precision/);
  });
  it("refuses duplicate keys the object model would hide", () => {
    assert.match(editor.scanProblems('{"value":1,"value":2}'), /Duplicate/);
    assert.match(editor.scanProblems('{"a":{"b":1,"b":2}}'), /Duplicate/);
  });
  it("ignores numeric strings", () => {
    assert.equal(editor.scanProblems('{"id":"9007199254740993","v":"0.123456789012345678901"}'), null);
  });
});

describe("parse", () => {
  it("rejects non-objects and authoritative sourceText", () => {
    assert.throws(() => editor.parse("[1,2]"), /must be a JSON object/);
    assert.throws(() => editor.parse('{"sourceText":"kind: collection"}'), /authoritative sourceText/);
  });
  it("rejects unsafe integers that survive parsing", () => {
    assert.throws(() => editor.parse('{"n":9007199254740993}'), /precision/);
  });
  it("keeps unknown fields", () => {
    assert.deepEqual(editor.parse('{"unknown":{"tag":"kept"}}'), { unknown: { tag: "kept" } });
  });
});
