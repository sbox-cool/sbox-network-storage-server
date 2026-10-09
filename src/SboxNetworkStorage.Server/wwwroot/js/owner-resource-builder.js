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
  var steps = {
    read: "- id: \"load-player\"\n  type: \"read\"\n  collection: \"players\"\n  key: \"{{steamId}}\"\n",
    write: "- id: \"grant\"\n  type: \"write\"\n  collection: \"players\"\n  key: \"{{steamId}}\"\n  ops:\n    - op: \"increment\"\n      path: \"coins\"\n      amount: \"{{input.amount}}\"\n",
    condition: "- id: \"has-profile\"\n  type: \"condition\"\n  check:\n    field: \"{{player.coins}}\"\n    op: \"exists\"\n  onFail:\n    status: 409\n    error: \"PROFILE_MISSING\"\n",
    transform: "- id: \"total\"\n  type: \"transform\"\n  expression: \"{{input.amount}}\"\n",
    filter: "- id: \"rich\"\n  type: \"filter\"\n  collection: \"players\"\n  where:\n    field: \"coins\"\n    op: \">=\"\n    value: 100\n",
    webhook: "- id: \"notify\"\n  type: \"webhook\"\n  url: \"https://discord.com/api/webhooks/…\"\n  title: \"Grant\"\n  description: \"{{steamId}} received {{input.amount}} coins\"\n"
  };
  function on(id, handler) {
    var node = document.getElementById(id);
    if (node) node.addEventListener("click", handler);
  }
  on("builder-apply-endpoint", function () {
    setScalar("method", document.getElementById("builder-method").value);
    setScalar("enabled", document.getElementById("builder-enabled").value);
  });
  on("builder-add-step", function () {
    insert(steps[document.getElementById("builder-step").value] || steps.read);
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
