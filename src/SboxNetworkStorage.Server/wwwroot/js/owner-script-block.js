"use strict";
// Finds the top-level `script: |` block of an endpoint definition and rewrites only its body.
// The script editor and the YAML source share text through this module, so every other byte of the
// definition stays as the author wrote it. Runs in the browser and under node:test.
(function (global) {
  var HEADER = /^script:[ \t]*(\|[-+0-9]*)[ \t]*(#.*)?$/;
  var RUNTIME = /^runtime:[ \t]*["']?(javascript|typescript)["']?[ \t]*(#.*)?$/;

  function eol(text) {
    return text.indexOf("\r\n") >= 0 ? "\r\n" : "\n";
  }

  function indentOf(line) {
    return /^[ \t]*/.exec(line)[0].length;
  }

  // Returns the block or null: { header, indicator, first, last, indent, body }.
  // `first`/`last` are the first and last non-blank body line indexes (`last` is header when empty).
  function find(text) {
    var lines = String(text).split(/\r?\n/);
    var header = -1;
    var indicator = "";
    for (var i = 0; i < lines.length; i++) {
      var match = HEADER.exec(lines[i]);
      if (match) { header = i; indicator = match[1]; break; }
    }
    if (header < 0) return null;
    var indent = -1;
    var last = header;
    for (var j = header + 1; j < lines.length; j++) {
      var line = lines[j];
      if (line.trim() === "") continue;
      var width = indentOf(line);
      if (indent < 0) {
        if (width === 0) break;
        indent = width;
      }
      if (width < indent) break;
      last = j;
    }
    var prefix = indent < 0 ? "" : " ".repeat(indent);
    var body = [];
    for (var k = header + 1; k <= last; k++) {
      var text2 = lines[k];
      body.push(text2.trim() === "" ? "" : text2.slice(prefix.length));
    }
    return {
      header: header, indicator: indicator, first: header + 1, last: last,
      indent: indent < 0 ? 0 : indent, body: body.join("\n").replace(/\s+$/, ""),
    };
  }

  // The script text the editor shows: the block body with its trailing blank lines removed.
  function scriptOf(text) {
    var block = find(text);
    return block ? block.body : null;
  }

  // The runtime when the document declares a top-level javascript or typescript runtime.
  function runtimeOf(text) {
    var lines = String(text).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var match = RUNTIME.exec(lines[i]);
      if (match) return match[1];
    }
    return null;
  }

  // Returns `text` with the block body replaced by `script`. Returns `text` unchanged when there is no
  // block to rewrite or the body already matches, so callers can compare results.
  function replace(text, script, options) {
    var source = String(text);
    var block = find(source);
    if (!block) return source;
    var newline = eol(source);
    var lines = source.split(/\r?\n/);
    var indent = block.indent || (options && options.indent) || 2;
    var prefix = " ".repeat(indent);
    var next = String(script).replace(/\s+$/, "").split(/\r?\n/).map(function (line) {
      return line.trim() === "" ? "" : prefix + line;
    });
    if (block.body === String(script).replace(/\s+$/, "")) return source;
    var before = lines.slice(0, block.header + 1);
    var after = lines.slice(block.last + 1);
    return before.concat(next, after).join(newline);
  }

  var api = { find: find, scriptOf: scriptOf, runtimeOf: runtimeOf, replace: replace };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else global.OwnerScriptBlock = api;
})(typeof window !== "undefined" ? window : globalThis);
