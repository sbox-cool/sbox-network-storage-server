"use strict";
// Examples gallery, full step palette and live definition check for the
// resources editor. The YAML textarea stays authoritative: gallery inserts and
// palette snippets only edit the text, and saving still runs server validation.
(function () {
  // Step palette groups mirror the executor's step families.
  var stepGroups = {
    Data: ["read", "write", "delete", "lookup", "filter", "lookup_many", "random_select"],
    Logic: ["condition", "assert"],
    Values: ["transform", "object", "array", "merge", "sort", "switch", "compute"],
    Random: ["random"],
    Flow: ["block", "workflow", "sleep", "response"],
    Integrations: ["webhook"]
  };
  function callSummary(call) {
    var parts = [(call.method || "POST"), call.secret ? "secret key" : "public key", "expects " + call.status];
    if (call.input) parts.push("input " + call.input);
    return parts.join(", ");
  }
  function projectPaths(pathname) {
    var match = pathname.match(/\/dashboard\/projects\/([^/]+)\/resources(?:\/([^/?]+))?/);
    if (!match) return null;
    return { projectRoot: "/dashboard/projects/" + match[1] + "/resources", kind: match[2] || "endpoint" };
  }
  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) module.exports = { stepGroups: stepGroups, callSummary: callSummary, projectPaths: projectPaths };

  var textarea = document.getElementById("definition");
  if (!textarea) return;
  var paths = projectPaths(window.location.pathname);
  if (!paths) return;
  var projectRoot = paths.projectRoot;
  var kind = paths.kind;

  function insert(text) {
    var start = textarea.selectionStart === null ? textarea.value.length : textarea.selectionStart;
    var end = textarea.selectionEnd === null ? start : textarea.selectionEnd;
    if (start > 0 && textarea.value[start - 1] !== "\n") text = "\n" + text;
    textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
    var caret = start + text.length;
    textarea.setSelectionRange(caret, caret);
    textarea.focus();
  }

  function token() {
    var input = document.querySelector("input[name=__RequestVerificationToken]");
    return input ? input.value : "";
  }

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function buildPalette(stepYaml) {
    var select = document.getElementById("builder-step");
    if (!select || !stepYaml) return;
    select.textContent = "";
    Object.keys(stepGroups).forEach(function (group) {
      var available = stepGroups[group].filter(function (type) { return stepYaml[type]; });
      if (available.length === 0) return;
      var optgroup = document.createElement("optgroup");
      optgroup.label = group;
      available.forEach(function (type) {
        var option = document.createElement("option");
        option.value = type;
        option.textContent = type;
        optgroup.appendChild(option);
      });
      select.appendChild(optgroup);
    });
    var add = document.getElementById("builder-add-step");
    if (add) add.addEventListener("click", function () {
      var snippet = stepYaml[select.value];
      if (snippet) insert(snippet + "\n");
    });
  }

  function buildGallery(examples) {
    var host = document.getElementById("example-gallery");
    if (!host) return;
    host.textContent = "";
    var shown = examples.filter(function (example) { return example.kind === kind; });
    if (shown.length === 0) {
      host.appendChild(el("p", "muted", "No examples for this kind yet."));
      return;
    }
    var byCategory = {};
    shown.forEach(function (example) {
      (byCategory[example.category] = byCategory[example.category] || []).push(example);
    });
    Object.keys(byCategory).sort().forEach(function (category) {
      host.appendChild(el("h3", null, category));
      byCategory[category].forEach(function (example) {
        var card = el("div", "card example");
        var heading = el("div", "section-heading");
        heading.appendChild(el("h3", null, example.title + (example.exists ? " (exists)" : "")));
        card.appendChild(heading);
        card.appendChild(el("p", "muted", example.summary));
        if (example.requires && example.requires.length > 0)
          card.appendChild(el("p", "muted", "Needs: " + example.requires.join(", ")));
        (example.calls || []).forEach(function (call) {
          card.appendChild(el("p", "muted", "Try: " + callSummary(call)));
        });
        var actions = el("div", "actions");
        var insertButton = el("button", "secondary", "Insert into editor");
        insertButton.type = "button";
        insertButton.addEventListener("click", function () {
          textarea.value = example.source.replace(/\s+$/, "") + "\n";
          textarea.focus();
        });
        actions.appendChild(insertButton);
        if (example.requires && example.requires.length > 0 && !example.exists) {
          var companions = el("button", "secondary", "Create needed definitions");
          companions.type = "button";
          companions.addEventListener("click", function () {
            companions.disabled = true;
            fetch(projectRoot + "/catalog/" + encodeURIComponent(example.id) + "/companions", {
              method: "POST",
              headers: { RequestVerificationToken: token() }
            }).then(function (response) { return response.json(); }).then(function (result) {
              var created = (result.created || []).join(", ");
              var skipped = (result.skipped || []).join(", ");
              var failed = (result.failed || []).map(function (item) { return item.id + ": " + item.message; }).join("; ");
              companions.textContent = "Created: " + (created || "none")
                + (skipped ? "; already there: " + skipped : "")
                + (failed ? "; failed: " + failed : "");
            }).catch(function () {
              companions.disabled = false;
              companions.textContent = "Create failed, try again";
            });
          });
          actions.appendChild(companions);
        }
        card.appendChild(actions);
        host.appendChild(card);
      });
    });
  }

  function buildCheck() {
    var button = document.getElementById("builder-check");
    var results = document.getElementById("check-results");
    if (!button || !results) return;
    button.addEventListener("click", function () {
      button.disabled = true;
      results.textContent = "Checking...";
      var form = new FormData();
      form.append("definition", textarea.value);
      var id = document.querySelector("input[name=id]");
      if (id) form.append("id", id.value);
      fetch(projectRoot + "/" + kind + "/check", {
        method: "POST",
        headers: { RequestVerificationToken: token() },
        body: new URLSearchParams(form)
      }).then(function (response) { return response.json(); }).then(function (result) {
        button.disabled = false;
        results.textContent = "";
        var diagnostics = result.diagnostics || [];
        if (diagnostics.length === 0) {
          results.appendChild(el("p", "notice success", "Valid: this definition saves and runs."));
          return;
        }
        var list = el("ul", null);
        diagnostics.forEach(function (item) {
          list.appendChild(el("li", null, (item.isError ? "Error " : "Warning ") + item.code + " at " + item.path + ": " + item.message));
        });
        results.appendChild(list);
      }).catch(function () {
        button.disabled = false;
        results.textContent = "Check failed; save to see server diagnostics.";
      });
    });
  }

  fetch(projectRoot + "/catalog").then(function (response) {
    if (!response.ok) throw new Error("catalog unavailable");
    return response.json();
  }).then(function (catalog) {
    buildPalette(catalog.stepYaml);
    buildGallery(catalog.examples || []);
  }).catch(function () { /* static palette and no gallery stay in place */ });
  buildCheck();
})();
