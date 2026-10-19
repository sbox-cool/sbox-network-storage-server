"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const H = require(path.join(__dirname, "..", "..", "src", "SboxNetworkStorage.Server", "wwwroot", "js", "owner-highlight.js"));

// Texts of every token of one type, in order.
function typed(text, language, type) {
  return H.tokenize(text, language).filter((t) => t.type === type).map((t) => t.text);
}

function kinds(text, language) {
  return H.tokenize(text, language).filter((t) => t.type !== null).map((t) => [t.type, t.text]);
}

function assertLossless(text, language) {
  assert.equal(H.tokenize(text, language).map((t) => t.text).join(""), text);
}

test("a script block is javascript when the document has a javascript or typescript runtime", () => {
  for (const runtime of ["javascript", "typescript"]) {
    const text = `name: demo\nruntime: ${runtime}\nscript: |\n  const a = ns.records.get("k");\n`;
    assert.deepEqual(typed(text, "yaml", "keyword"), ["const"], runtime);
    assert.deepEqual(typed(text, "yaml", "api"), ["ns", "records", "get"], runtime);
    assertLossless(text, "yaml");
  }
});

test("a script block stays yaml without a top-level javascript or typescript runtime", () => {
  const cases = [
    "name: demo\nscript: |\n  const a = 1;\n",
    "name: demo\nruntime: lua\nscript: |\n  const a = 1;\n",
    "name: demo\nruntime: JavaScript\nscript: |\n  const a = 1;\n",
    "config:\n  runtime: javascript\nscript: |\n  const a = 1;\n",
  ];
  for (const text of cases) {
    assert.deepEqual(typed(text, "yaml", "keyword"), [], text);
    assertLossless(text, "yaml");
  }
});

test("a quoted runtime value is read the same way", () => {
  for (const value of ["\"javascript\"", "'typescript'"]) {
    const text = `runtime: ${value}\nscript: |\n  const a = 1;\n`;
    assert.deepEqual(typed(text, "yaml", "keyword"), ["const"], value);
  }
  assert.deepEqual(typed("runtime: \"javascript'\nscript: |\n  const a = 1;\n", "yaml", "keyword"), []);
});

test("every block header form opens a script body", () => {
  for (const header of ["|", "|-", "|+", "|2", "|2-", "|-2", "| # note", "|+ # note"]) {
    const text = `runtime: javascript\nscript: ${header}\n  const a = 1;\n`;
    assert.deepEqual(typed(text, "yaml", "keyword"), ["const"], header);
  }
});

test("an explicit indentation indicator sets the content indent", () => {
  assert.deepEqual(typed("runtime: javascript\nscript: |2\n   const a = 1;\n", "yaml", "keyword"), ["const"]);
  assert.deepEqual(typed("runtime: javascript\nscript: |3\n  const a = 1;\n", "yaml", "keyword"), []);
});

test("a script body ends at the first less-indented line and keeps blank and deeper lines", () => {
  const text = "runtime: javascript\nscript: |\n  const a = 1;\n\n    const b = 2;\nname: after\n";
  assert.deepEqual(typed(text, "yaml", "keyword"), ["const", "const"]);
  assert.deepEqual(kinds(text, "yaml").find((t) => t[1] === "after"), ["string", "after"]);
  assertLossless(text, "yaml");
});

test("an empty script block opens no body", () => {
  const text = "runtime: javascript\nscript: |\nname: x\n";
  assert.deepEqual(typed(text, "yaml", "keyword"), []);
  assertLossless(text, "yaml");
  assertLossless("runtime: javascript\nscript: |", "yaml");
  assertLossless("runtime: javascript\nscript: |\n\n", "yaml");
});

test("crlf line endings give the same script body and stay lossless", () => {
  const text = "runtime: javascript\r\nscript: |\r\n  const a = 1;\r\nname: after\r\n";
  assert.deepEqual(typed(text, "yaml", "keyword"), ["const"]);
  assertLossless(text, "yaml");
});

test("host api roots and their member chains are api; a call ends the chain", () => {
  const text = "const r = req.input.id; ns.records.get(k).value; globals.endpoint.run(); workflows.run(x);";
  const api = typed(text, "javascript", "api");
  assert.deepEqual(api, ["req", "input", "id", "ns", "records", "get", "globals", "endpoint", "run", "workflows", "run"]);
  assert.ok(!api.includes("value"));
  assertLossless(text, "javascript");
});

test("names that only look like host api are not api", () => {
  const text = "const myns = foo.ns.bar; reqx.id; a.req; const s = \"ns.records.get\"; // ns.records";
  assert.deepEqual(typed(text, "javascript", "api"), []);
  assertLossless(text, "javascript");
});

test("host api members inside yaml template expressions are api", () => {
  assert.deepEqual(typed("url: \"{{ req.input.id }}\"\n", "yaml", "api"), ["req", "input", "id"]);
});

// Minimal DOM double: records created nodes and never parses HTML.
function fakeDocument() {
  function el(tag) {
    return {
      tagName: tag.toUpperCase(), className: "", children: [], dataset: {}, classList: { contains: () => false },
      get firstChild() { return this.children[0] || null; },
      get textContent() { return this.children.map((c) => c.textContent).join(""); },
      set textContent(v) { this.children = [text(v)]; },
      appendChild(c) { if (c.isFragment) { this.children.push(...c.children); } else { this.children.push(c); } return c; },
      removeChild(c) { this.children = this.children.filter((x) => x !== c); return c; },
      querySelector() { return null; },
      querySelectorAll() { return []; },
      get ownerDocument() { return doc; },
    };
  }
  function text(v) { return { nodeType: 3, textContent: v }; }
  const doc = {
    createElement: el,
    createTextNode: text,
    createDocumentFragment() { const f = el("fragment"); f.isFragment = true; return f; },
  };
  return doc;
}

test("render puts script body tokens in spans and keeps the text exact", () => {
  const doc = fakeDocument();
  const target = doc.createElement("pre");
  const text = "runtime: javascript\nscript: |\n  const a = ns.records.get(\"k\");\n";
  H.render(target, text, "yaml");
  assert.equal(target.textContent, text);
  const classes = target.children.filter((c) => c.className).map((c) => c.className);
  assert.ok(classes.includes("syntax-keyword"));
  assert.ok(classes.includes("syntax-api"));
});
