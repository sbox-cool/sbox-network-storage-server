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

// Exercise browser wiring without adding a DOM dependency.
const fs = require("node:fs");
const vm = require("node:vm");
const source = fs.readFileSync(require.resolve("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-builder-catalog.js"), "utf8");

class Node {
  constructor(tag) {
    this.tagName = tag;
    this.children = [];
    this.attributes = {};
    this.listeners = {};
    this.value = "";
    this.disabled = false;
    this.text = "";
  }
  set textContent(value) { this.text = value; this.children = []; }
  get textContent() { return this.text + this.children.map(node => node.textContent).join(""); }
  appendChild(node) { this.children.push(node); return node; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, callback) { (this.listeners[name] ||= []).push(callback); }
  dispatchEvent(event) { (this.listeners[event.type] || []).forEach(callback => callback(event)); }
  click() { if (!this.disabled) this.dispatchEvent({ type: "click" }); }
  focus() { this.focused = true; }
  setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
}

const example = {
  id: "endpoint.example", kind: "endpoint", category: "Data", title: "Example",
  summary: "An example", source: "method: POST\n", requires: ["collection.players"], exists: false
};
const catalogResponse = { examples: [example], stepYaml: { read: "read:\n" } };
const response = (body, ok = true) => ({ ok, json: async () => body });
const settle = () => new Promise(resolve => setImmediate(resolve));

function browser(responses, definition = "") {
  const nodes = Object.fromEntries(["definition", "example-gallery", "builder-step", "builder-add-step",
    "builder-check", "check-results"].map(id => [id, new Node(id)]));
  nodes.definition.value = definition;
  const requests = [];
  const confirmations = [];
  const context = {
    document: { getElementById: id => nodes[id] || null, createElement: tag => new Node(tag), querySelector: () => null },
    window: { location: { pathname: "/dashboard/projects/proj_1/resources/endpoint" },
      OwnerDialog: { confirm: options => { confirmations.push(options.message); return Promise.resolve(context.confirmReplacement); } } },
    confirmReplacement: false,
    Event: class { constructor(type) { this.type = type; } },
    FormData, URLSearchParams,
    fetch: (url, options) => {
      requests.push({ url, options });
      const next = responses.shift();
      if (next instanceof Error) return Promise.reject(next);
      return Promise.resolve(next);
    }
  };
  vm.runInNewContext(source, context);
  function find(node, predicate) {
    if (predicate(node)) return node;
    for (const child of node.children) {
      const found = find(child, predicate);
      if (found) return found;
    }
  }
  return { nodes, requests, confirmations, context,
    button: label => find(nodes["example-gallery"], node => node.tagName === "button" && node.textContent === label),
    feedback: () => find(nodes["example-gallery"], node => node.attributes.role === "status") };
}

describe("catalog browser behavior", () => {
  it("replaces Loading with an accessible, recoverable catalog error", async () => {
    const ui = browser([response({ error: "missing" }, false), response(catalogResponse)]);
    await settle();
    assert.match(ui.feedback().textContent, /Could not load/);
    assert.equal(ui.nodes["builder-add-step"].disabled, true);
    ui.button("Retry examples").click();
    await settle();
    assert.ok(ui.button("Use example"));
    assert.equal(ui.nodes["builder-add-step"].disabled, false);
  });

  it("also recovers from network and malformed catalog failures", async () => {
    for (const failure of [new Error("offline"), response({})]) {
      const ui = browser([failure, response(catalogResponse)]);
      await settle();
      assert.match(ui.feedback().textContent, /Could not load/);
      ui.button("Retry examples").click();
      await settle();
      assert.ok(ui.button("Use example"));
    }
  });

  it("preserves edited text when replacement is canceled and announces accepted edits", async () => {
    const ui = browser([response(catalogResponse)], "method: GET\n");
    let inputEvents = 0;
    ui.nodes.definition.addEventListener("input", () => inputEvents++);
    await settle();
    ui.button("Use example").click();
    assert.equal(ui.nodes.definition.value, "method: GET\n");
    assert.equal(inputEvents, 0);
    assert.match(ui.confirmations[0], /Unsaved changes/);
    ui.context.confirmReplacement = true;
    ui.button("Use example").click();
    await settle();
    assert.equal(ui.nodes.definition.value, example.source);
    assert.equal(inputEvents, 1);
  });

  it("keeps partial companion failures outside the button and permits retry", async () => {
    const ui = browser([response(catalogResponse),
      response({ created: ["collection.one"], skipped: [], failed: [{ id: "collection.players", message: "Invalid schema" }] }),
      response({ created: ["collection.players"], skipped: ["collection.one"], failed: [] })]);
    await settle();
    ui.button("Create needed definitions").click();
    await settle();
    assert.match(ui.feedback().textContent, /Invalid schema/);
    assert.match(ui.feedback().textContent, /existing definitions are kept/);
    assert.equal(ui.button("Retry needed definitions").disabled, false);
    ui.button("Retry needed definitions").click();
    await settle();
    assert.equal(ui.button("Needed definitions ready").disabled, true);
    assert.match(ui.feedback().textContent, /Already present: collection.one/);
  });

  it("allows retry after non-OK companions responses", async () => {
    const ui = browser([response(catalogResponse), response({}, false)]);
    await settle();
    ui.button("Create needed definitions").click();
    await settle();
    assert.equal(ui.button("Retry needed definitions").disabled, false);
    assert.match(ui.feedback().textContent, /Could not create/);
  });
});

describe("definition check browser behavior", () => {
  it("never reports non-OK or malformed JSON responses as valid", async () => {
    for (const failure of [response({ ok: true, diagnostics: [] }, false), response({}),
      new Error("offline"), { ok: true, json: async () => { throw new Error("not JSON"); } }]) {
      const ui = browser([response(catalogResponse), failure]);
      await settle();
      ui.nodes["builder-check"].click();
      await settle();
      assert.match(ui.nodes["check-results"].textContent, /Could not check/);
      assert.equal(ui.nodes["builder-check"].disabled, false);
    }
  });

  it("describes successful validation without promising saving or runtime execution", async () => {
    const ui = browser([response(catalogResponse), response({ ok: true, diagnostics: [] })]);
    await settle();
    ui.nodes["builder-check"].click();
    await settle();
    assert.match(ui.nodes["check-results"].textContent, /Validation passed/);
    assert.match(ui.nodes["check-results"].textContent, /Nothing was saved or executed/);
    assert.equal(ui.nodes["check-results"].attributes.role, "status");
  });

  it("honors ok false and renders server diagnostics", async () => {
    const ui = browser([response(catalogResponse), response({ ok: false,
      diagnostics: [{ isError: true, code: "INVALID_DEFINITION", path: "/", message: "Missing method" }] })]);
    await settle();
    ui.nodes["builder-check"].click();
    await settle();
    assert.match(ui.nodes["check-results"].textContent, /Validation failed/);
    assert.match(ui.nodes["check-results"].textContent, /Missing method/);
  });

  it("does not report an earlier text snapshot as current validation", async () => {
    let finish;
    const pending = new Promise(resolve => { finish = resolve; });
    const ui = browser([response(catalogResponse), pending], "method: GET");
    await settle();
    ui.nodes["builder-check"].click();
    ui.nodes.definition.value = "method: POST";
    finish(response({ ok: true, diagnostics: [] }));
    await settle();
    assert.match(ui.nodes["check-results"].textContent, /changed during the check/);
    assert.equal(ui.nodes["builder-check"].disabled, false);
  });
});
