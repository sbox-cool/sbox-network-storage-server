"use strict";
(function () {
  // The textarea remains authoritative. Visual mode is opt-in and keeps unknown
  // fields. JSON mode stays available without JavaScript.
  function kind(value) { return value === null ? "null" : Array.isArray(value) ? "array" : typeof value; }
  function initial(type) { return { string: "", number: 0, boolean: false, null: null, object: {}, array: [] }[type]; }
  function put(object, key, value) { Object.defineProperty(object, key, { value: value, enumerable: true, writable: true, configurable: true }); }
  function decimal(token) {
    var parts = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(token);
    var digits = (parts[2] + (parts[3] || "")).replace(/^0+/, "") || "0";
    var exponent = BigInt(parts[4] || "0") - BigInt((parts[3] || "").length);
    while (digits.length > 1 && digits.endsWith("0")) { digits = digits.slice(0, -1); exponent++; }
    return parts[1] + digits + "e" + (digits === "0" ? "0" : exponent.toString());
  }
  function exactNumber(token) {
    token = token.trim();
    var number = Number(token);
    return Number.isFinite(number) && decimal(token) === decimal(JSON.stringify(number));
  }
  function scanProblems(text) {
    // Skip quoted strings when checking numeric lexemes. JSON.parse would otherwise
    // round long decimal values before the editor knows they were present.
    var tokens = text.match(/"(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|[{}\[\]:]/g) || [];
    var stack = [];
    for (var index = 0; index < tokens.length; index++) {
      var token = tokens[index];
      if (token === "{") stack.push(new Set());
      else if (token === "[") stack.push(null);
      else if (token === "}" || token === "]") stack.pop();
      else if (token[0] === '"' && tokens[index + 1] === ":") {
        var keys = stack[stack.length - 1], key = JSON.parse(token);
        if (keys.has(key)) return "Duplicate object keys cannot be represented in visual mode. Use JSON mode to preserve them.";
        keys.add(key);
      } else if (/^-?\d/.test(token) && !exactNumber(token)) return "A number would lose precision in visual mode. Use JSON mode to preserve it.";
    }
    return null;
  }
  function parse(text) {
    var value = JSON.parse(text);
    var problem = scanProblems(text);
    if (problem) throw new Error(problem);
    function check(node) {
      if (typeof node === "number" && (!Number.isFinite(node) || (Number.isInteger(node) && !Number.isSafeInteger(node)))) throw new Error("A number exceeds the visual editor's safe precision. Use JSON mode to preserve it.");
      if (node && typeof node === "object") Object.keys(node).forEach(function (key) { check(node[key]); });
    }
    check(value);
    if (kind(value) !== "object") throw new Error("The definition must be a JSON object.");
    if (Object.prototype.hasOwnProperty.call(value, "sourceText") && typeof value.sourceText === "string" && value.sourceText.trim()) throw new Error("This definition has authoritative sourceText. Edit its source in JSON mode; visual changes to compiled fields would be overwritten.");
    return value;
  }
  function element(tag, text, className) {
    var node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (className) node.className = className;
    return node;
  }
  document.querySelectorAll("textarea[data-json-editor]").forEach(function (textarea) {
    var root, active = false;
    var controls = element("div", undefined, "actions json-editor-controls");
    var visualButton = element("button", "Visual", "secondary");
    var jsonButton = element("button", "JSON", "secondary");
    visualButton.type = jsonButton.type = "button";
    visualButton.setAttribute("aria-pressed", "false"); jsonButton.setAttribute("aria-pressed", "true");
    controls.append(visualButton, jsonButton);
    var message = element("p", "", "error"); message.hidden = true; message.setAttribute("role", "alert");
    var panel = element("div", undefined, "json-visual-editor"); panel.hidden = true;
    textarea.before(controls, message, panel);
    function error(text) { message.textContent = text; message.hidden = !text; }
    function sync() { textarea.value = JSON.stringify(root, null, 2); textarea.dispatchEvent(new Event("input", { bubbles: true })); }
    function drawValue(parent, key, depth) {
      var value = parent[key], type = kind(value);
      var row = element("div", undefined, "json-field");
      var label = element("label", String(key));
      var selector = element("select"); selector.setAttribute("aria-label", String(key) + " type");
      ["string", "number", "boolean", "null", "object", "array"].forEach(function (t) { var o = element("option", t); o.value = t; selector.appendChild(o); }); selector.value = type;
      selector.addEventListener("change", function () { put(parent, key, initial(selector.value)); sync(); render(); });
      row.append(label, selector);
      if (type === "object" || type === "array") {
        var details = element("details"); details.open = depth < 2;
        details.appendChild(element("summary", type === "array" ? value.length + " items" : Object.keys(value).length + " fields"));
        Object.keys(value).forEach(function (child) { details.appendChild(drawValue(value, child, depth + 1)); });
        var addRow = element("div", undefined, "actions");
        var name = element("input"); name.type = "text"; name.placeholder = "Field name"; name.setAttribute("aria-label", String(key) + " new field name");
        if (type === "object") addRow.appendChild(name);
        var add = element("button", type === "array" ? "Add item" : "Add field", "secondary"); add.type = "button";
        add.addEventListener("click", function () {
          if (type === "array") value.push("");
          else {
            if (!name.value || Object.prototype.hasOwnProperty.call(value, name.value)) { error("Enter a new, unique field name."); return; }
            put(value, name.value, "");
          }
          error(""); sync(); render();
        }); addRow.appendChild(add); details.appendChild(addRow); row.appendChild(details);
      } else if (type !== "null") {
        var input = element(type === "string" && value.indexOf("\n") !== -1 ? "textarea" : "input");
        if (type === "boolean") { input.type = "checkbox"; input.checked = value; }
        else { input.type = "text"; input.value = String(value); if (type === "number") input.inputMode = "decimal"; }
        input.setAttribute("aria-label", String(key) + " value");
        input.addEventListener("input", function () {
          var next = input.value;
          if (type === "boolean") next = input.checked;
          if (type === "number") {
            try { next = JSON.parse(next); } catch (_) { input.setCustomValidity("Enter a JSON number."); return; }
            if (typeof next !== "number" || !exactNumber(input.value) || (Number.isInteger(next) && !Number.isSafeInteger(next))) { input.setCustomValidity("Enter a finite number within safe precision."); return; }
          }
          input.setCustomValidity(""); put(parent, key, next); sync();
        }); row.appendChild(input);
      }
      var remove = element("button", "Remove", "secondary"); remove.type = "button"; remove.setAttribute("aria-label", "Remove " + key);
      remove.addEventListener("click", function () { if (Array.isArray(parent)) parent.splice(Number(key), 1); else delete parent[key]; sync(); render(); }); row.appendChild(remove);
      return row;
    }
    function render() {
      panel.replaceChildren();
      Object.keys(root).forEach(function (key) { panel.appendChild(drawValue(root, key, 0)); });
      var addRow = element("div", undefined, "actions");
      var name = element("input"); name.placeholder = "Field name"; name.setAttribute("aria-label", "New root field name");
      var add = element("button", "Add field", "secondary"); add.type = "button";
      add.addEventListener("click", function () { if (!name.value || Object.prototype.hasOwnProperty.call(root, name.value)) { error("Enter a new, unique field name."); return; } put(root, name.value, ""); error(""); sync(); render(); });
      addRow.append(name, add); panel.appendChild(addRow);
    }
    visualButton.addEventListener("click", function () {
      if (active) return;
      try { root = parse(textarea.value); render(); error(""); }
      catch (e) { error(e.message); return; }
      active = true; panel.hidden = false; textarea.hidden = true;
      visualButton.setAttribute("aria-pressed", "true"); jsonButton.setAttribute("aria-pressed", "false");
    });
    function visualValid() {
      return Array.from(panel.querySelectorAll("input, select, textarea")).every(function (input) { return input.reportValidity(); });
    }
    jsonButton.addEventListener("click", function () {
      if (active) {
        if (!visualValid()) return;
        sync();
      }
      active = false; panel.hidden = true; textarea.hidden = false; error("");
      visualButton.setAttribute("aria-pressed", "false"); jsonButton.setAttribute("aria-pressed", "true");
    });
    textarea.form.addEventListener("submit", function (event) {
      if (!active) return;
      if (!visualValid()) { event.preventDefault(); return; }
      sync();
    });
  });
  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) module.exports = { decimal: decimal, exactNumber: exactNumber, scanProblems: scanProblems, parse: parse };
})();
