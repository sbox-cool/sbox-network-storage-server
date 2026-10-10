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
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
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
        var insertButton = el("button", "secondary", "Use example");
        insertButton.type = "button";
        insertButton.addEventListener("click", function () {
          var source = example.source.replace(/\s+$/, "") + "\n";
          if (textarea.value === source) {
            textarea.focus();
            return;
          }
          var apply = function () {
            textarea.value = source;
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
            textarea.focus();
          };
          // The gallery only exists with JavaScript; the confirm dialog is the
          // same shared one destructive buttons use. Without it, keep the
          // editor text untouched rather than discarding unsaved changes.
          if (!textarea.value.trim()) { apply(); return; }
          if (window.OwnerDialog) window.OwnerDialog.confirm({
            title: "Use example",
            message: "Replace the editor contents with this example? Unsaved changes will be lost.",
            confirmLabel: "Replace",
            opener: insertButton,
          }).then(function (ok) { if (ok) apply(); });
          else insertButton.focus();
        });
        actions.appendChild(insertButton);
        if (example.requires && example.requires.length > 0 && !example.exists) {
          var companions = el("button", "secondary", "Create needed definitions");
          companions.type = "button";
          var feedback = el("p", "muted");
          feedback.setAttribute("role", "status");
          feedback.setAttribute("aria-live", "polite");
          card.appendChild(feedback);
          companions.addEventListener("click", function () {
            companions.disabled = true;
            feedback.textContent = "Creating needed definitions...";
            fetch(projectRoot + "/catalog/" + encodeURIComponent(example.id) + "/companions", {
              method: "POST",
              headers: { RequestVerificationToken: token() }
            }).then(readResponse).then(function (result) {
              if (!Array.isArray(result.created) || !Array.isArray(result.skipped) || !Array.isArray(result.failed))
                throw new Error("Invalid companions response");
              var created = result.created.join(", ");
              var skipped = result.skipped.join(", ");
              var failed = result.failed.map(function (item) { return item.id + ": " + item.message; }).join("; ");
              feedback.textContent = "Created: " + (created || "none")
                + (skipped ? ". Already present: " + skipped : "")
                + (failed ? ". Failed: " + failed + ". Retry to create the remaining definitions; existing definitions are kept." : ". Needed definitions are present. Review and save the example separately.");
              companions.disabled = result.failed.length === 0;
              companions.textContent = result.failed.length ? "Retry needed definitions" : "Needed definitions ready";
            }).catch(function () {
              companions.disabled = false;
              companions.textContent = "Retry needed definitions";
              feedback.textContent = "Could not create needed definitions. Retry; any definitions already created are kept.";
            });
          });
          actions.appendChild(companions);
        }
        card.appendChild(actions);
        host.appendChild(card);
      });
    });
  }

  function readResponse(response) {
    if (!response.ok) throw new Error("Request failed");
    return response.json();
  }

  function buildCheck() {
    var button = document.getElementById("builder-check");
    var results = document.getElementById("check-results");
    if (!button || !results) return;
    results.setAttribute("role", "status");
    results.setAttribute("aria-live", "polite");
    button.addEventListener("click", function () {
      button.disabled = true;
      results.textContent = "Checking...";
      var checkedDefinition = textarea.value;
      var form = new FormData();
      form.append("definition", checkedDefinition);
      var id = document.querySelector("input[name=id]");
      if (id) form.append("id", id.value);
      fetch(projectRoot + "/" + kind + "/check", {
        method: "POST",
        headers: { RequestVerificationToken: token() },
        body: new URLSearchParams(form)
      }).then(readResponse).then(function (result) {
        button.disabled = false;
        if (typeof result.ok !== "boolean" || !Array.isArray(result.diagnostics))
          throw new Error("Invalid check response");
        results.textContent = "";
        if (textarea.value !== checkedDefinition) {
          results.textContent = "The definition changed during the check. Check again to validate the current text.";
          return;
        }
        var diagnostics = result.diagnostics;
        results.appendChild(el("p", result.ok ? "notice success" : "notice",
          result.ok
            ? "Validation passed for this text. Nothing was saved or executed. Runtime behavior has not been tested."
            : "Validation failed. Fix the errors and check again. Nothing was saved or executed."));
        if (diagnostics.length === 0) return;
        var list = el("ul", null);
        diagnostics.forEach(function (item) {
          list.appendChild(el("li", null, (item.isError ? "Error " : "Warning ") + item.code + " at " + item.path + ": " + item.message));
        });
        results.appendChild(list);
      }).catch(function () {
        button.disabled = false;
        results.textContent = "Could not check the definition. Try Check definition again. Nothing was saved or executed.";
      });
    });
  }

  var gallery = document.getElementById("example-gallery");
  var stepSelect = document.getElementById("builder-step");
  var addStep = document.getElementById("builder-add-step");
  var catalogStatus = el("p", "muted");
  catalogStatus.setAttribute("role", "status");
  catalogStatus.setAttribute("aria-live", "polite");
  if (gallery) {
    gallery.textContent = "";
    gallery.appendChild(catalogStatus);
  }
  function loadCatalog() {
    if (stepSelect) stepSelect.disabled = true;
    if (addStep) addStep.disabled = true;
    catalogStatus.textContent = "Loading examples and step snippets...";
    fetch(projectRoot + "/catalog").then(readResponse).then(function (catalog) {
      if (!Array.isArray(catalog.examples) || !catalog.stepYaml || typeof catalog.stepYaml !== "object")
        throw new Error("Invalid catalog response");
      buildPalette(catalog.stepYaml);
      buildGallery(catalog.examples);
      if (stepSelect) stepSelect.disabled = false;
      if (addStep) addStep.disabled = false;
    }).catch(function () {
      catalogStatus.textContent = "Could not load examples and step snippets. Retry or continue editing the definition directly.";
      if (gallery) {
        gallery.textContent = "";
        gallery.appendChild(catalogStatus);
        var retry = el("button", "secondary", "Retry examples");
        retry.type = "button";
        retry.addEventListener("click", function () {
          retry.disabled = true;
          loadCatalog();
        });
        gallery.appendChild(retry);
      }
    });
  }
  loadCatalog();
  buildCheck();
})();
