"use strict";
// Examples and live definition checks. Block additions use the parsed AST model;
// the source textarea remains authoritative and saving still validates on the server.
(function () {
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
  if (typeof module !== "undefined" && module.exports) module.exports = { projectPaths: projectPaths };

  var textarea = document.getElementById("definition");
  if (!textarea) return;
  var paths = projectPaths(window.location.pathname);
  if (!paths) return;
  var projectRoot = paths.projectRoot;
  var kind = paths.kind;


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


  function buildGallery(examples, templates, templateError) {
    var host = document.getElementById("example-gallery");
    if (!host) return;
    host.textContent = "";
    if (templateError && kind === "endpoint") host.appendChild(el("p", "muted", "Starter templates could not be loaded: " + templateError + " Examples are still available."));
    var shown = examples.concat(templates).filter(function (example) { return example.kind === kind; });
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
        if (example.note) card.appendChild(el("p", "muted", example.note));
        if (example.requires && example.requires.length > 0)
          card.appendChild(el("p", "muted", "Needs: " + example.requires.join(", ")));
        (example.calls || []).forEach(function (call) {
          card.appendChild(el("p", "muted", "Try: " + callSummary(call)));
        });
        var actions = el("div", "actions");
        var noun = example.template ? "template" : "example";
        var insertButton = el("button", "secondary", "Use " + noun);
        insertButton.type = "button";
        insertButton.addEventListener("click", function () {
          var source = example.source.replace(/\s+$/, "") + "\n";
          var showGuided = function () { document.getElementById("edit-tab").click(); };
          if (textarea.value === source) { showGuided(); return; }
          var apply = function () {
            textarea.value = source;
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
            showGuided();
          };
          // The gallery only exists with JavaScript; the confirm dialog is the
          // same shared one destructive buttons use. Without it, keep the
          // editor text untouched rather than discarding unsaved changes.
          if (!textarea.value.trim()) { apply(); return; }
          if (window.OwnerDialog) window.OwnerDialog.confirm({
            title: "Use " + noun,
            message: "Replace the editor contents with this " + noun + "? Unsaved changes will be lost.",
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
        var preview = el("details", "example-source");
        preview.appendChild(el("summary", null, "Preview source"));
        var code = el("pre", null, example.source);
        code.dataset.language = "yaml";
        preview.appendChild(code);
        card.appendChild(preview);
        document.dispatchEvent(new CustomEvent("owner:highlight", { detail: code }));
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
    var timer;
    var active;
    var generation = 0;

    function check() {
      clearTimeout(timer);
      if (active) active.abort();
      var request = ++generation;
      active = new AbortController();
      button.disabled = true;
      button.hidden = true;
      results.dataset.checked = "false";
      results.setAttribute("aria-busy", "true");
      textarea.removeAttribute("aria-invalid");
      results.textContent = "Checking definition...";
      var checkedDefinition = textarea.value;
      var form = new FormData();
      form.append("definition", checkedDefinition);
      var id = document.querySelector("input[name=id]");
      if (id) form.append("id", id.value);
      fetch(projectRoot + "/" + kind + "/check", {
        method: "POST",
        headers: { RequestVerificationToken: token() },
        body: new URLSearchParams(form),
        signal: active.signal
      }).then(readResponse).then(function (result) {
        if (request !== generation || textarea.value !== checkedDefinition) return;
        if (typeof result.ok !== "boolean" || !Array.isArray(result.diagnostics))
          throw new Error("Invalid check response");
        results.textContent = "";
        results.dataset.checked = "true";
        if (!result.ok) textarea.setAttribute("aria-invalid", "true");
        results.appendChild(el("p", result.ok ? "muted" : "notice",
          result.ok
            ? "Source checked automatically. See the editor for live type diagnostics. Nothing was saved or executed."
            : "Definition errors found. Fix the messages below. Nothing was saved or executed."));
        if (result.diagnostics.length) {
          var list = el("ul", null);
          result.diagnostics.forEach(function (item) {
            list.appendChild(el("li", null, (item.isError ? "Error " : "Warning ") + item.code + " at " + item.path + ": " + item.message));
          });
          results.appendChild(list);
        }
      }).catch(function (error) {
        if (request !== generation || error.name === "AbortError") return;
        results.textContent = "Automatic source checking is unavailable. Retry the check; saving still runs server validation.";
        button.hidden = false;
      }).finally(function () {
        if (request === generation) {
          active = null;
          button.disabled = false;
          results.setAttribute("aria-busy", "false");
        }
      });
    }

    button.addEventListener("click", check);
    textarea.addEventListener("input", function () {
      clearTimeout(timer);
      ++generation;
      if (active) { active.abort(); active = null; }
      button.disabled = false;
      button.hidden = true;
      results.dataset.checked = "false";
      results.setAttribute("aria-busy", "true");
      results.textContent = "Source changed. Checking automatically...";
      textarea.removeAttribute("aria-invalid");
      timer = setTimeout(check, 450);
    });
    check();
  }

  var gallery = document.getElementById("example-gallery");
  var catalogStatus = el("p", "muted");
  catalogStatus.setAttribute("role", "status");
  catalogStatus.setAttribute("aria-live", "polite");
  if (gallery) {
    gallery.textContent = "";
    gallery.appendChild(catalogStatus);
  }
  function loadCatalog() {
    document.dispatchEvent(new Event("owner:catalog-loading"));
    catalogStatus.textContent = "Loading examples and building blocks...";
    fetch(projectRoot + "/catalog").then(readResponse).then(function (catalog) {
      if (!Array.isArray(catalog.examples) || !catalog.stepDefaults || typeof catalog.stepDefaults !== "object")
        throw new Error("Invalid catalog response");
      buildGallery(catalog.examples, Array.isArray(catalog.templates) ? catalog.templates : [], catalog.templateError || null);
      document.dispatchEvent(new CustomEvent("owner:catalog", { detail: catalog }));
    }).catch(function () {
      document.dispatchEvent(new Event("owner:catalog-error"));
      catalogStatus.textContent = "Could not load examples and building blocks. Retry or continue editing existing blocks or Raw source.";
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
  document.addEventListener("owner:catalog-retry", loadCatalog);
  loadCatalog();
  buildCheck();
})();
