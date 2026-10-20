"use strict";
// Guards for the YAML script block shared by the script editor and the source (owner-script-block.js).
const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const block = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-script-block.js");

const endpoint = [
  "id: grant",
  "runtime: javascript",
  "method: POST",
  "script: |",
  "  export default function handle(req, ns) {",
  "    return { ok: true };",
  "  }",
  "",
  "# trailing comment",
  "enabled: true",
  "",
].join("\n");

describe("find", () => {
  it("reads the body of a top-level block scalar and drops trailing blank lines", () => {
    const found = block.find(endpoint);
    assert.equal(found.header, 3);
    assert.equal(found.first, 4);
    assert.equal(found.last, 6);
    assert.equal(found.body, "export default function handle(req, ns) {\n  return { ok: true };\n}");
    assert.equal(block.scriptOf(endpoint), found.body);
  });

  it("accepts the header indicators and comments the block syntax allows", () => {
    for (const header of ["script: |", "script: |-", "script: |+", "script: |2", "script: | # keep", "script:|"]) {
      const text = `runtime: typescript\n${header}\n  const x = 1;\nnext: 2\n`;
      assert.equal(block.scriptOf(text), "const x = 1;", header);
    }
  });

  it("returns null when there is no block scalar", () => {
    assert.equal(block.find("script: \"quoted\"\n"), null);
    assert.equal(block.find("id: x\n"), null);
  });

  it("treats an empty block as a body with no lines", () => {
    const found = block.find("script: |\nnext: 1\n");
    assert.equal(found.first, 1);
    assert.equal(found.last, 0);
    assert.equal(found.body, "");
  });

  it("keeps the leading spaces past the indent digit of a block header", () => {
    assert.equal(block.scriptOf("script: |2\n   const x = 1;\n  const y = 2;\nnext: 2\n"), " const x = 1;\nconst y = 2;");
  });
});

describe("runtimeOf", () => {
  it("reads a top-level javascript or typescript runtime only", () => {
    assert.equal(block.runtimeOf("runtime: javascript\n"), "javascript");
    assert.equal(block.runtimeOf("runtime: \"typescript\" # ts\n"), "typescript");
    assert.equal(block.runtimeOf("  runtime: typescript\n"), null);
    assert.equal(block.runtimeOf("runtime: JavaScript\n"), null);
    assert.equal(block.runtimeOf("steps: []\n"), null);
  });
});

describe("replace", () => {
  it("rewrites only the body and keeps the rest of the document", () => {
    const next = block.replace(endpoint, "export default function handle() {\n  return 1;\n}\n");
    assert.equal(next, endpoint.replace(
      "  export default function handle(req, ns) {\n    return { ok: true };\n  }",
      "  export default function handle() {\n    return 1;\n  }"));
  });

  it("returns the same text when the body already matches", () => {
    const body = block.scriptOf(endpoint);
    assert.equal(block.replace(endpoint, body), endpoint);
  });

  it("keeps CRLF line endings", () => {
    const crlf = endpoint.replace(/\n/g, "\r\n");
    const next = block.replace(crlf, "const a = 1;");
    assert.equal(next.includes("\n") && !next.replace(/\r\n/g, "").includes("\n"), true);
    assert.equal(block.scriptOf(next), "const a = 1;");
  });

  it("inserts a body under an empty header", () => {
    const next = block.replace("script: |\nnext: 1\n", "const a = 1;\nconst b = 2;");
    assert.equal(next, "script: |\n  const a = 1;\n  const b = 2;\nnext: 1\n");
  });

  it("writes blank script lines as empty lines", () => {
    const next = block.replace("script: |\n  a();\nnext: 1\n", "a();\n\nb();");
    assert.equal(next, "script: |\n  a();\n\n  b();\nnext: 1\n");
  });

  it("adds the indent digit when the first script line starts with a space, and reads it back", () => {
    const next = block.replace("script: |\nnext: 1\n", " const a = 1;\nconst b = 2;");
    assert.equal(next, "script: |2\n   const a = 1;\n  const b = 2;\nnext: 1\n");
    assert.equal(block.scriptOf(next), " const a = 1;\nconst b = 2;");
  });

  it("leaves text without a block untouched", () => {
    assert.equal(block.replace("id: x\n", "const a = 1;"), "id: x\n");
  });
});
