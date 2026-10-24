"use strict";
// Owner management pages: endpoint test helpers, copy buttons, project filter
// and color swatches. Destructive confirmations live in owner-dialog.js, which
// is loaded before this file. Nothing here calls window.confirm.
(function () {
  // Pure helper: server-side ?q= matching mirrored for client enhancement.
  // Matches project name or id, case-insensitive, on any substring.
  function matchesProject(name, id, query) {
    var q = (query || "").trim().toLowerCase();
    if (!q) return true;
    return (name || "").toLowerCase().indexOf(q) !== -1 || (id || "").toLowerCase().indexOf(q) !== -1;
  }

  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) module.exports = { matchesProject: matchesProject };

  if (typeof document === "undefined" || typeof window === "undefined") return;

  // Endpoint tests: switching endpoints swaps in that endpoint's input skeleton,
  // unless the owner already edited the input.
  var endpoint = document.querySelector("select[data-test-endpoint]");
  var input = document.querySelector("textarea[data-test-input]");
  if (endpoint && input) {
    var template = function () {
      var option = endpoint.options[endpoint.selectedIndex];
      return option ? option.getAttribute("data-template") || "{}" : "{}";
    };
    var lastTemplate = template();
    endpoint.addEventListener("change", function () {
      if (input.value.trim() === lastTemplate.trim() || input.value.trim() === "" || input.value.trim() === "{}") {
        input.value = template();
        input.dispatchEvent(new Event("change", { bubbles: true }));
      }
      lastTemplate = template();
    });
  }

  // Copy buttons sit right after the snippet they copy. Without the clipboard API (plain HTTP on a
  // remote address), or when it is refused, the snippet is selected so the owner can press Ctrl+C.
  // Delegated so buttons injected later (quick preview modal) work without rebinding.
  function flash(button, text) {
    var label = button.getAttribute("data-label") || button.textContent;
    button.setAttribute("data-label", label);
    button.textContent = text;
    setTimeout(function () { button.textContent = label; }, 2500);
  }
  function select(source) {
    var range = document.createRange();
    range.selectNodeContents(source);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }
  function copyFrom(button) {
    var source = button.previousElementSibling;
    if (!source) return;
    var fallback = function () { select(source); flash(button, "Press Ctrl+C"); };
    if (!navigator.clipboard || !window.isSecureContext) { fallback(); return; }
    navigator.clipboard.writeText(source.textContent).then(function () { flash(button, "Copied"); }, fallback);
  }
  document.addEventListener("click", function (event) {
    var button = event.target && event.target.closest ? event.target.closest("[data-copy-previous]") : null;
    if (button && document.contains(button)) copyFrom(button);
  });

  // Recovery codes are generated only into this authenticated response. Download them locally without posting them back.
  document.addEventListener("click", function (event) {
    var button = event.target && event.target.closest ? event.target.closest("[data-download-previous]") : null;
    if (!button || !document.contains(button)) return;
    var row = button.closest(".copy-row");
    var source = row && row.querySelector("pre");
    if (!source) return;
    var blob = new Blob([source.textContent + "\n"], { type: "text/plain;charset=utf-8" });
    var url = URL.createObjectURL(blob);
    var link = document.createElement("a");
    link.href = url;
    link.download = "sbox-ns-recovery-codes.txt";
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 0);
  });

  // Project filter enhancement (server renders data-enhance only above 8
  // projects). Without JavaScript the same form submits as GET ?q=.
  var filter = document.getElementById("project-filter");
  var grid = document.getElementById("project-grid");
  if (filter && grid && filter.getAttribute("data-enhance") === "true") {
    var cards = Array.prototype.slice.call(grid.querySelectorAll("[data-project-name]"));
    var emptyNote = document.getElementById("project-filter-empty");
    filter.addEventListener("input", function () {
      var visible = 0;
      cards.forEach(function (card) {
        var show = matchesProject(card.getAttribute("data-project-name"), card.getAttribute("data-project-id"), filter.value);
        card.hidden = !show;
        if (show) visible += 1;
      });
      if (emptyNote) emptyNote.hidden = visible !== 0;
    });
  }

  // New-resource dialog: validate the id, then open a pristine draft at
  // .../resources/<kind>/new?draft=<id>. Works from the list and the detail
  // page alike. Navigation is a plain location change, so the normal
  // unsaved-source warning still applies.
  (function () {
    var confirm = document.getElementById("new-resource-confirm");
    if (!confirm) return;
    function draftBase() {
      var match = window.location.pathname.match(/\/dashboard\/projects\/([^/]+)\/resources\/([^/]+)/);
      if (!match) return null;
      return "/dashboard/projects/" + match[1] + "/resources/" + match[2] + "/new?draft=";
    }
    confirm.addEventListener("click", function () {
      var field = document.getElementById("new-resource-id");
      var status = document.getElementById("new-resource-status");
      var id = field ? field.value.trim() : "";
      if (!/^[A-Za-z0-9_-]{1,128}$/.test(id)) {
        if (status) status.textContent = "Use 1-128 letters, numbers, underscore or hyphen.";
        if (field) field.focus();
        return;
      }
      var base = draftBase();
      if (!base) return;
      var starter = document.getElementById("new-resource-template");
      var templateQuery = starter && starter.value ? "&template=" + encodeURIComponent(starter.value) : "";
      window.location.assign(base + encodeURIComponent(id) + templateQuery);
    });
    var field = document.getElementById("new-resource-id");
    if (field) field.addEventListener("keydown", function (event) {
      if (event.key === "Enter") { event.preventDefault(); confirm.click(); }
    });
  })();

  // Resource list filter: narrows the table rows by name or id. Progressive;
  // without JavaScript the input is inert and every row stays visible.
  (function () {
    var box = document.getElementById("resource-filter");
    var table = document.getElementById("resource-table");
    if (!box || !table || !table.tBodies[0]) return;
    var rows = Array.prototype.slice.call(table.tBodies[0].rows);
    var empty = document.getElementById("resource-filter-empty");
    box.addEventListener("input", function () {
      var term = box.value.toLowerCase();
      var visible = 0;
      rows.forEach(function (row) {
        var show = !term || row.textContent.toLowerCase().indexOf(term) !== -1;
        row.hidden = !show;
        if (show) visible += 1;
      });
      if (empty) empty.hidden = visible !== 0;
    });
  })();

  // Quick preview: fetch the preview partial into one shared dialog. Without
  // JavaScript the same link opens the detail page. Loading and error states
  // are plain text. Focus returns to the row link on close via owner-dialog.js.
  (function () {
    var body = document.getElementById("quick-preview-body");
    var dialog = document.getElementById("quick-preview");
    if (!body || !dialog) return;
    function highlightInjected() {
      body.querySelectorAll("pre[data-language]").forEach(function (pre) {
        try { document.dispatchEvent(new CustomEvent("owner:highlight", { detail: pre })); } catch (_) {}
      });
    }
    document.addEventListener("click", function (event) {
      var link = event.target && event.target.closest ? event.target.closest("a[data-preview-url]") : null;
      if (!link || !document.contains(link)) return;
      if (dialog.contains(link)) return;
      if (!link.getAttribute("data-preview-url")) return;
      event.preventDefault();
      body.textContent = "Loading preview.";
      dialog.setAttribute("aria-labelledby", "quick-preview-title");
      if (window.OwnerDialog) window.OwnerDialog.open("quick-preview", link);
      else if (typeof dialog.showModal === "function") dialog.showModal();
      fetch(link.getAttribute("data-preview-url"), { credentials: "same-origin", headers: { Accept: "text/html" } }).then(function (response) {
        if (!response.ok) throw new Error("Preview answered " + response.status + ". Open the resource instead.");
        return response.text();
      }).then(function (html) {
        body.innerHTML = html;
        highlightInjected();
        var title = body.querySelector("h2");
        if (title) { title.id = "quick-preview-title"; dialog.setAttribute("aria-labelledby", "quick-preview-title"); }
      }).catch(function (error) {
        body.textContent = error && error.message ? error.message : "Could not load the preview. Open the resource instead.";
      });
    });
  })();

  // Detail tabs: leaving Edit with unapplied block input asks through the
  // block editor first; otherwise the beforeunload warning covers navigation.
  (function () {
    document.addEventListener("click", function (event) {
      var link = event.target && event.target.closest ? event.target.closest("a[data-resource-tab]") : null;
      if (!link || !window.OwnerBlockEditor) return;
      if (link.getAttribute("aria-current") === "page") { event.preventDefault(); return; }
      var verdict = null;
      try { verdict = window.OwnerBlockEditor.leaveGuided(link); } catch (_) { return; }
      if (verdict && typeof verdict.then === "function") {
        event.preventDefault();
        verdict.then(function (ok) { if (ok) window.location.assign(link.href); });
      } else if (verdict === false) {
        event.preventDefault();
      }
    });
  })();

  // Pinned projects float to the top of the dashboard grid. Pins live in
  // localStorage, so they never touch server data.
  var pinKey = "sbox-ns-pinned-projects";
  function pinnedIds() {
    try { return JSON.parse(localStorage.getItem(pinKey) || "[]"); } catch (e) { return []; }
  }
  function orderPinned() {
    var grid = document.querySelector(".project-grid");
    if (!grid) return;
    var pinned = pinnedIds();
    var columns = Array.prototype.slice.call(grid.children);
    columns.forEach(function (column) {
      var card = column.querySelector(".project-card");
      // The card is wrapped in a .project-cell carrying data-project-id; the
      // anchor itself holds no data attributes so it stays a plain link.
      var on = pinned.indexOf(column.getAttribute("data-project-id") || "") >= 0;
      if (card) card.classList.toggle("pinned", on);
      var button = column.querySelector("[data-pin-project]");
      if (button) button.setAttribute("aria-pressed", on ? "true" : "false");
    });
    columns.sort(function (a, b) {
      var pa = a.querySelector(".project-card.pinned") ? 1 : 0;
      var pb = b.querySelector(".project-card.pinned") ? 1 : 0;
      return pb - pa;
    });
    columns.forEach(function (column) { grid.appendChild(column); });
  }
  document.querySelectorAll("[data-pin-project]").forEach(function (button) {
    button.addEventListener("click", function () {
      var id = button.getAttribute("data-pin-project") || "";
      var pinned = pinnedIds().filter(function (entry) { return entry !== id; });
      if (pinned.length === pinnedIds().length) pinned.push(id);
      try { localStorage.setItem(pinKey, JSON.stringify(pinned)); } catch (e) {}
      orderPinned();
    });
  });
  orderPinned();

  // Picking a project colour turns Automatic colour off, so the pick is what gets saved.
  document.querySelectorAll("input[type='color'][name='color']").forEach(function (picker) {
    var auto = picker.form && picker.form.querySelector("input[type='checkbox'][name='color'][value='auto']");
    if (auto) picker.addEventListener("input", function () { auto.checked = false; });
  });

  // Inline style attributes are blocked by the panel CSP; color swatches are set through CSSOM.
  document.querySelectorAll(".manage-swatch[data-color]").forEach(function (swatch) {
    var color = swatch.getAttribute("data-color") || "";
    if (/^#?[0-9a-fA-F]{6}$/.test(color)) swatch.style.backgroundColor = color.charAt(0) === "#" ? color : "#" + color;
  });
})();
