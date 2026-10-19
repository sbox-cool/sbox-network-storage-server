"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const H = require(path.join(__dirname, "..", "..", "src", "SboxNetworkStorage.Server", "wwwroot", "js", "owner-highlight.js"));

function kinds(text, language) {
  return H.tokenize(text, language).filter((t) => t.type !== null).map((t) => [t.type, t.text]);
}

function assertLossless(text, language) {
  const joined = H.tokenize(text, language).map((t) => t.text).join("");
  assert.equal(joined, text);
}

test("normalizeLanguage maps aliases and plain names", () => {
  assert.equal(H.normalizeLanguage("yml"), "yaml");
  assert.equal(H.normalizeLanguage("TS"), "javascript");
  assert.equal(H.normalizeLanguage("c#"), "csharp");
  assert.equal(H.normalizeLanguage("bash"), "shell");
  assert.equal(H.normalizeLanguage("jsonc"), "json");
  assert.equal(H.normalizeLanguage("toml"), "toml");
  assert.equal(H.normalizeLanguage("text"), null);
  assert.equal(H.normalizeLanguage(""), null);
  assert.equal(H.normalizeLanguage(undefined), null);
  assert.equal(H.normalizeLanguage("cobol"), "generic");
});

test("plain languages produce a single unhighlighted token", () => {
  assert.deepEqual(H.tokenize("hello: world", "text"), [{ type: null, text: "hello: world" }]);
});

test("yaml highlights keys, strings, numbers, booleans and comments", () => {
  const text = "name: \"demo\"\nport: 8080 # http\nenabled: true\n";
  const got = kinds(text, "yaml");
  assert.deepEqual(got.find((t) => t[1] === "name"), ["property", "name"]);
  assert.deepEqual(got.find((t) => t[1] === "\"demo\""), ["string", "\"demo\""]);
  assert.deepEqual(got.find((t) => t[1] === "8080"), ["number", "8080"]);
  assert.deepEqual(got.find((t) => t[1] === "true"), ["boolean", "true"]);
  assert.deepEqual(got.find((t) => t[1] === "# http"), ["comment", "# http"]);
  assertLossless(text, "yaml");
});

test("yaml template expressions are split and their contents tokenized as javascript", () => {
  const text = "url: \"{{ ns.name }}\"";
  const got = kinds(text, "yaml");
  assert.ok(got.some((t) => t[0] === "template" && t[1] === "{{"));
  assert.ok(got.some((t) => t[0] === "template" && t[1] === "}}"));
  assert.ok(got.some((t) => t[0] === "builtin" && t[1] === "ns"));
  assertLossless(text, "yaml");
});

test("yaml keys inside list items are properties", () => {
  const got = kinds("- name: alpha\n  size: 3\n", "yaml");
  assert.ok(got.some((t) => t[0] === "property" && t[1] === "name"));
  assert.ok(got.some((t) => t[0] === "property" && t[1] === "size"));
});

test("yaml unquoted scalar values are strings; keys, numbers, booleans, comments and indicators keep their kinds", () => {
  const text = "kind: collection\nmethod: POST # verb\nurl: https://x.test/a:b\nvalue: 10\nok: true\nitems:\n  - alpha beta\n  - id: grant\nbody: |\n  text\nkey: \"{{steamId}}\"\n";
  const got = kinds(text, "yaml");
  assert.deepEqual(got.find((t) => t[1] === "collection"), ["string", "collection"]);
  assert.deepEqual(got.find((t) => t[1] === "POST"), ["string", "POST"]);
  assert.deepEqual(got.find((t) => t[1] === "# verb"), ["comment", "# verb"]);
  assert.deepEqual(got.find((t) => t[1] === "https://x.test/a:b"), ["string", "https://x.test/a:b"]);
  assert.deepEqual(got.find((t) => t[1] === "10"), ["number", "10"]);
  assert.deepEqual(got.find((t) => t[1] === "true"), ["boolean", "true"]);
  assert.deepEqual(got.find((t) => t[1] === "alpha beta"), ["string", "alpha beta"]);
  assert.deepEqual(got.find((t) => t[1] === "id"), ["property", "id"]);
  assert.deepEqual(got.find((t) => t[1] === "grant"), ["string", "grant"]);
  assert.ok(!got.some((t) => t[1].includes("|")), "block indicator stays plain");
  assert.ok(got.some((t) => t[0] === "template" && t[1] === "{{"));
  assertLossless(text, "yaml");
});

test("json property keys are distinct from string values", () => {
  const text = "{\"key\": \"value\", \"n\": -1.5e3, \"ok\": null}";
  const got = kinds(text, "json");
  assert.deepEqual(got.find((t) => t[1] === "\"key\""), ["property", "\"key\""]);
  assert.deepEqual(got.find((t) => t[1] === "\"value\""), ["string", "\"value\""]);
  assert.deepEqual(got.find((t) => t[1] === "-1.5e3"), ["number", "-1.5e3"]);
  assert.deepEqual(got.find((t) => t[1] === "null"), ["boolean", "null"]);
  assertLossless(text, "json");
});

test("javascript highlights keywords, builtins, functions, types and comments", () => {
  const text = "const x = ns.run(42); // note\nclass Foo {}";
  const got = kinds(text, "javascript");
  assert.deepEqual(got.find((t) => t[1] === "const"), ["keyword", "const"]);
  assert.deepEqual(got.find((t) => t[1] === "ns"), ["builtin", "ns"]);
  assert.deepEqual(got.find((t) => t[1] === "run"), ["function", "run"]);
  assert.deepEqual(got.find((t) => t[1] === "42"), ["number", "42"]);
  assert.deepEqual(got.find((t) => t[1] === "// note"), ["comment", "// note"]);
  assert.deepEqual(got.find((t) => t[1] === "Foo"), ["type", "Foo"]);
  assertLossless(text, "javascript");
});

test("typescript shares the javascript grammar", () => {
  assert.deepEqual(kinds("let a: string = 'x';", "typescript"), kinds("let a: string = 'x';", "javascript"));
});

test("a slash after an identifier is division, not a regex", () => {
  const got = kinds("const r = a / b / c;", "javascript");
  assert.equal(got.filter((t) => t[0] === "regex").length, 0);
  assertLossless("const r = a / b / c;", "javascript");
});

test("a slash after an operator starts a regex literal", () => {
  const got = kinds("const r = /ab+c/gi;", "javascript");
  assert.deepEqual(got.find((t) => t[0] === "regex"), ["regex", "/ab+c/gi"]);
});

test("a comment opener is never read as a regex", () => {
  const got = kinds("x = 1; // note with /slash/ inside", "javascript");
  assert.equal(got.filter((t) => t[0] === "regex").length, 0);
});

test("unterminated block comments and template strings stay lossless", () => {
  assertLossless("/* open comment", "javascript");
  assertLossless("`open template ${x}", "javascript");
  assert.deepEqual(kinds("/* open", "javascript"), [["comment", "/* open"]]);
});

test("csharp highlights verbatim strings, keywords, types and preprocessor", () => {
  const text = "#nullable enable\npublic static string Name = @\"c:\\temp\";";
  const got = kinds(text, "csharp");
  assert.deepEqual(got.find((t) => t[1] === "#nullable"), ["keyword", "#nullable"]);
  assert.deepEqual(got.find((t) => t[1] === "public"), ["keyword", "public"]);
  assert.deepEqual(got.find((t) => t[1] === "string"), ["keyword", "string"]);
  assert.deepEqual(got.find((t) => t[0] === "string"), ["string", "@\"c:\\temp\""]);
  assertLossless(text, "csharp");
});

test("shell highlights commands, flags, variables and strings", () => {
  const text = "sbox-ns --port 8080 $HOME \"quoted\" # local";
  const got = kinds(text, "shell");
  assert.deepEqual(got.find((t) => t[1] === "sbox-ns"), ["builtin", "sbox-ns"]);
  assert.ok(got.some((t) => t[0] === "property" && t[1].trim() === "--port"));
  assert.deepEqual(got.find((t) => t[0] === "variable"), ["variable", "$HOME"]);
  assert.deepEqual(got.find((t) => t[0] === "comment"), ["comment", "# local"]);
  assertLossless(text, "shell");
});

test("toml highlights tables, keys, booleans and numbers", () => {
  const text = "[server]\nport = 8080\nenabled = true\n";
  const got = kinds(text, "toml");
  assert.deepEqual(got.find((t) => t[0] === "type"), ["type", "[server]"]);
  assert.deepEqual(got.find((t) => t[0] === "property"), ["property", "port"]);
  assert.deepEqual(got.find((t) => t[0] === "number"), ["number", "8080"]);
  assert.deepEqual(got.find((t) => t[0] === "boolean"), ["boolean", "true"]);
  assertLossless(text, "toml");
});

test("generic fallback colours strings, flags and calls", () => {
  const got = kinds("run --force \"name\" x()", "generic");
  assert.ok(got.some((t) => t[0] === "property" && t[1].trim() === "--force"));
  assert.ok(got.some((t) => t[0] === "string" && t[1] === "\"name\""));
  assert.ok(got.some((t) => t[0] === "function" && t[1] === "x"));
});

test("unknown language names fall back to generic highlighting", () => {
  assert.deepEqual(kinds("true", "cobol"), [["boolean", "true"]]);
});

test("tokenize is lossless for mixed input in every grammar", () => {
  const samples = {
    yaml: "a: {{ req.body.x }}\n- 'q'\n# c\n",
    json: "[1, \"two\", {\"three\": false}]",
    javascript: "f(`a${b}`, /x\\/y/, 1e3, 0x1f) => {}",
    csharp: "var s = $\"v={x}\"; #if DEBUG\n#endif",
    shell: "export A=\"b\" && curl -s \"$URL\" | jq '.x'",
    toml: "[[arr]]\nk = '''x'''",
    generic: "ns.run(\"a\") --flag 'b' 7",
  };
  for (const [language, text] of Object.entries(samples)) assertLossless(text, language);
});

test("looksLikeCode accepts code-like inline text and rejects prose", () => {
  assert.equal(H.looksLikeCode("sbox-ns server start"), true);
  assert.equal(H.looksLikeCode("Owner.Save()"), true);
  assert.equal(H.looksLikeCode("System::Ns"), true);
  assert.equal(H.looksLikeCode("config/app.yaml"), true);
  assert.equal(H.looksLikeCode("--port"), true);
  assert.equal(H.looksLikeCode("Yes."), false);
  assert.equal(H.looksLikeCode("   "), false);
  assert.equal(H.looksLikeCode("plain words here"), false);
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

test("render writes text nodes and spans only, never markup from content", () => {
  const doc = fakeDocument();
  const target = doc.createElement("code");
  H.render(target, "<img src=x onerror=alert(1)> \"x\"", "javascript");
  assert.equal(target.textContent, "<img src=x onerror=alert(1)> \"x\"");
  for (const child of target.children) {
    assert.ok(child.nodeType === 3 || child.className.startsWith("syntax-"));
  }
});
