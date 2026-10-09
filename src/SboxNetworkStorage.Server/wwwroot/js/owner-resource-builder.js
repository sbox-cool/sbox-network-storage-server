"use strict";
// Guided builders for endpoint and collection definitions. The YAML textarea
// stays authoritative: builders only insert snippets or set top-level scalar
// lines, then the operator reviews and saves the raw text.
(function () {
  var textarea = document.getElementById("definition");
  if (!textarea) return;
  function insert(text) {
    var start = textarea.selectionStart === null ? textarea.value.length : textarea.selectionStart;
    var end = textarea.selectionEnd === null ? start : textarea.selectionEnd;
    textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
    var caret = start + text.length;
    textarea.setSelectionRange(caret, caret);
    textarea.focus();
  }
  function setScalar(key, value) {
    var pattern = new RegExp("^" + key + ":.*$", "m");
    if (pattern.test(textarea.value)) textarea.value = textarea.value.replace(pattern, key + ": " + value);
    else {
      if (textarea.value.length > 0 && textarea.value[textarea.value.length - 1] !== "\n") textarea.value += "\n";
      textarea.value += key + ": " + value + "\n";
    }
    textarea.focus();
  }
  // Step snippets come from the catalog API (owner-builder-catalog.js), which serves
  // one working default per supported step type from the builder tour definition.
  function on(id, handler) {
    var node = document.getElementById(id);
    if (node) node.addEventListener("click", handler);
  }
  on("builder-apply-endpoint", function () {
    setScalar("method", document.getElementById("builder-method").value);
    setScalar("enabled", document.getElementById("builder-enabled").value);
  });
  on("builder-apply-collection", function () {
    setScalar("collectionType", document.getElementById("builder-collection-type").value);
  });
  on("builder-add-field", function () {
    var name = (document.getElementById("builder-field-name").value || "field").trim() || "field";
    var type = document.getElementById("builder-field-type").value;
    insert(name + ":\n  type: \"" + type + "\"\n");
  });
})();
