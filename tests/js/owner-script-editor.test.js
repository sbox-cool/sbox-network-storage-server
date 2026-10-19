"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const E = require(path.join(__dirname, "..", "..", "src", "SboxNetworkStorage.Server", "wwwroot", "js", "owner-script-editor.js"));

const monacoRoot = path.join(__dirname, "..", "..", "src", "SboxNetworkStorage.Server", "wwwroot", "vendor", "monaco");

function applyEdit(current, edit) {
  return current.slice(0, edit.start) + edit.text + current.slice(edit.end);
}

test("workerFile maps Monaco labels to the vendored worker files", () => {
  assert.equal(E.workerFile("json"), E.WORKERS.json);
  assert.equal(E.workerFile("typescript"), E.WORKERS.typescript);
  assert.equal(E.workerFile("javascript"), E.WORKERS.typescript);
  assert.equal(E.workerFile("editor"), E.WORKERS.editor);
  assert.equal(E.workerFile("css"), E.WORKERS.editor);
});

test("every worker file the editor can request exists in the vendored Monaco tree", () => {
  for (const label of ["editor", "json", "typescript", "javascript", "anything-else"]) {
    const file = path.join(monacoRoot, E.workerFile(label));
    assert.ok(fs.existsSync(file), `missing ${E.workerFile(label)}`);
  }
});

test("the Monaco loader and main bundle are vendored where the editor loads them", () => {
  assert.ok(fs.existsSync(path.join(monacoRoot, "vs", "loader.js")));
  assert.ok(fs.existsSync(path.join(monacoRoot, "vs", "editor", "editor.main.js")));
});

test("minimalEdit keeps the common prefix and suffix and reproduces the target", () => {
  assert.deepEqual(E.minimalEdit("abc", "abc"), { start: 3, end: 3, text: "" });
  assert.deepEqual(E.minimalEdit("abcdef", "abXYef"), { start: 2, end: 4, text: "XY" });
  assert.deepEqual(E.minimalEdit("", "hello"), { start: 0, end: 0, text: "hello" });
  assert.deepEqual(E.minimalEdit("hello", ""), { start: 0, end: 5, text: "" });
  assert.equal(applyEdit("aaa", E.minimalEdit("aaa", "aaaa")), "aaaa");
  assert.equal(applyEdit("aaaa", E.minimalEdit("aaaa", "aa")), "aa");
});

test("minimalEdit round-trips arbitrary small edits", () => {
  let seed = 7;
  const next = () => {
    seed = (seed * 1103515245 + 12345) & 0x7fffffff;
    return seed;
  };
  const alphabet = "ab\n ";
  const sample = (length) => {
    let out = "";
    for (let i = 0; i < length; i++) out += alphabet[next() % alphabet.length];
    return out;
  };
  for (let i = 0; i < 300; i++) {
    const current = sample(next() % 12);
    const target = sample(next() % 12);
    assert.equal(applyEdit(current, E.minimalEdit(current, target)), target, `${JSON.stringify(current)} -> ${JSON.stringify(target)}`);
  }
});

test("parseCheckResult reads severity, code, path and a line and column", () => {
  assert.deepEqual(
    E.parseCheckResult("Error SCRIPT_SYNTAX at /script: Line 3, column 5: Unexpected token"),
    { severity: "error", code: "SCRIPT_SYNTAX", path: "/script", message: "Line 3, column 5: Unexpected token", line: 3, column: 5 });
});

test("parseCheckResult keeps path-only results without a position", () => {
  assert.deepEqual(
    E.parseCheckResult("Warning ENDPOINT_RUNTIME at /runtime: Use a supported runtime"),
    { severity: "warning", code: "ENDPOINT_RUNTIME", path: "/runtime", message: "Use a supported runtime", line: 0, column: 0 });
});

test("parseCheckResult rejects text that is not a check result", () => {
  assert.equal(E.parseCheckResult("hello"), null);
  assert.equal(E.parseCheckResult(""), null);
  assert.equal(E.parseCheckResult(null), null);
  assert.equal(E.parseCheckResult(undefined), null);
});

test("isPlainCode is true only outside comments, strings and regular expressions", () => {
  const tokens = [
    { offset: 0, type: "identifier.js", language: "javascript" },
    { offset: 5, type: "string.js", language: "javascript" }
  ];
  assert.equal(E.isPlainCode(tokens, 2), true);
  assert.equal(E.isPlainCode(tokens, 5), false);
  assert.equal(E.isPlainCode(tokens, 9), false);
  assert.equal(E.isPlainCode([{ offset: 0, type: "comment.js", language: "javascript" }], 0), false);
  assert.equal(E.isPlainCode([{ offset: 0, type: "regexp.js", language: "javascript" }], 1), false);
  assert.equal(E.isPlainCode([{ offset: 0, type: "string.template.js", language: "javascript" }], 1), false);
  assert.equal(E.isPlainCode([], 0), true);
});
