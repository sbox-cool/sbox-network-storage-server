"use strict";
// Reads the endpoint script declarations (ns.d.ts plus the project typings) and returns the
// members a script can use, with their JSDoc. The docs panel and insert list read only this,
// so ns.d.ts stays the single source of truth. Runs in the browser and under node:test.
(function (global) {
  var MAX_DEPTH = 4;

  function commentLine(raw) {
    return raw.replace(/^\/\*\*?/, "").replace(/\*\/\s*$/, "").replace(/^\*+\s?/, "").trim();
  }

  function parse(text) {
    var interfaces = Object.create(null);
    var consts = [];
    var docBuffer = [];
    var inDoc = false;
    var current = null;
    var lines = String(text).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var trimmed = lines[i].trim();
      if (inDoc) {
        if (trimmed.indexOf("*/") >= 0) inDoc = false;
        var inner = commentLine(trimmed);
        if (inner) docBuffer.push(inner);
        continue;
      }
      if (trimmed.indexOf("/**") === 0) {
        var inner2 = commentLine(trimmed);
        if (inner2) docBuffer.push(inner2);
        inDoc = trimmed.indexOf("*/") < 0;
        continue;
      }
      if (current !== null) {
        if (trimmed.charAt(0) === "}") { current = null; docBuffer = []; continue; }
        var member = /^(['"]?)([A-Za-z_$][\w$-]*)\1(\??)\s*(\(|:)/.exec(trimmed);
        if (member) {
          var doc = docBuffer.join(" ");
          docBuffer = [];
          var body = trimmed.replace(/;\s*$/, "");
          if (member[4] === "(") {
            var returns = /\)\s*:\s*(.+)$/.exec(body);
            interfaces[current].push({
              name: member[2], kind: "method", optional: member[3] === "?", doc: doc,
              signature: body, type: returns ? returns[1].trim() : "void",
            });
          } else {
            interfaces[current].push({
              name: member[2], kind: "property", optional: member[3] === "?", doc: doc,
              signature: body, type: body.slice(body.indexOf(":") + 1).trim(),
            });
          }
          continue;
        }
        docBuffer = [];
        continue;
      }
      var iface = /^(?:export\s+)?interface\s+([A-Za-z_$][\w$]*)\s*\{/.exec(trimmed);
      if (iface) {
        interfaces[iface[1]] = interfaces[iface[1]] || [];
        if (trimmed.slice(-1) === "{") current = iface[1];
        docBuffer = [];
        continue;
      }
      var constant = /^declare\s+const\s+([A-Za-z_$][\w$]*)\s*:\s*([^;]+);?/.exec(trimmed);
      if (constant) {
        consts.push({ name: constant[1], type: constant[2].trim(), doc: docBuffer.join(" ") });
      }
      docBuffer = [];
    }
    return { interfaces: interfaces, consts: consts };
  }

  // The interface a property type refers to, if any: `NsRecords`, `NsRecords | null`.
  function referencedInterface(parsed, type) {
    var name = String(type || "").replace(/\s*\|\s*null\s*$/, "").trim();
    return /^[A-Za-z_$][\w$]*$/.test(name) && Object.prototype.hasOwnProperty.call(parsed.interfaces, name) ? name : null;
  }

  var GROUPS = { req: "Request", ns: "Script API", workflows: "Workflows", globals: "Global endpoints" };
  var NAME_GROUPS = [
    { iface: "CollectionIdMap", group: "Collections" },
    { iface: "GameValueGroups", group: "Game value groups" },
    { iface: "WorkflowAliases", group: "Workflows" },
    { iface: "GlobalModules", group: "Global endpoints" },
  ];

  // Flat list of every member a script can reach, each with its docs and the text to insert.
  function entries(parsed) {
    var out = [];
    function walk(prefix, interfaceName, depth, group, seen) {
      (parsed.interfaces[interfaceName] || []).forEach(function (member) {
        var path = prefix + "." + member.name;
        out.push({
          path: path, name: member.name, kind: member.kind, group: group,
          signature: member.signature, doc: member.doc, optional: member.optional,
          insert: member.kind === "method" ? path + "(" : path,
        });
        var target = member.kind === "property" ? referencedInterface(parsed, member.type) : null;
        if (target && depth < MAX_DEPTH && !seen[target]) {
          var nextSeen = Object.assign({}, seen);
          nextSeen[target] = true;
          walk(path, target, depth + 1, group, nextSeen);
        }
      });
    }
    parsed.consts.forEach(function (constant) {
      var group = GROUPS[constant.name] || "Script API";
      out.push({
        path: constant.name, name: constant.name, kind: "value", group: group,
        signature: constant.name + ": " + constant.type, doc: constant.doc, optional: false,
        insert: constant.name,
      });
      var target = referencedInterface(parsed, constant.type);
      if (target) {
        var seen = {};
        seen[target] = true;
        walk(constant.name, target, 1, group, seen);
      }
    });
    NAME_GROUPS.forEach(function (source) {
      (parsed.interfaces[source.iface] || []).forEach(function (member) {
        out.push({
          path: member.name, name: member.name, kind: "name", group: source.group,
          signature: source.iface + "." + member.name, doc: member.doc, optional: false,
          insert: JSON.stringify(member.name),
        });
      });
    });
    return out;
  }

  // The global endpoint modules the project typings declare, each with its export names:
  // `declare module "globals/<id>" { export ... }`.
  function globalModules(text) {
    var out = [];
    var block = /declare module "globals\/([^"]+)" \{([\s\S]*?)^\}/gm;
    var exportLine = /^\s*export\s+(?:(?:function|const|let|var|class)\s+)?([A-Za-z_$][\w$]*)/gm;
    var match;
    while ((match = block.exec(String(text || ""))) !== null) {
      var names = [];
      var found;
      exportLine.lastIndex = 0;
      while ((found = exportLine.exec(match[2])) !== null) names.push(found[1]);
      out.push({ id: match[1], exports: names });
    }
    return out;
  }

  // Case-insensitive match on the path, signature and docs. An empty query keeps every entry.
  function filter(items, query) {
    var needle = String(query || "").trim().toLowerCase();
    if (!needle) return items.slice();
    return items.filter(function (item) {
      return (item.path + " " + item.signature + " " + item.doc).toLowerCase().indexOf(needle) >= 0;
    });
  }

  var api = { parse: parse, entries: entries, filter: filter, globalModules: globalModules };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else global.OwnerScriptDocs = api;
})(typeof window !== "undefined" ? window : globalThis);
