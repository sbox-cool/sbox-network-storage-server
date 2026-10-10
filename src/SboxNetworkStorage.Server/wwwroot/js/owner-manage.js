"use strict";
(function () {
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
      }
      lastTemplate = template();
    });
  }

  // Copy buttons sit right after the snippet they copy. Without the clipboard API (plain HTTP on a
  // remote address), or when it is refused, the snippet is selected so the owner can press Ctrl+C.
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
  document.querySelectorAll("[data-copy-previous]").forEach(function (button) {
    button.addEventListener("click", function () {
      var source = button.previousElementSibling;
      if (!source) return;
      var fallback = function () { select(source); flash(button, "Press Ctrl+C"); };
      if (!navigator.clipboard || !window.isSecureContext) { fallback(); return; }
      navigator.clipboard.writeText(source.textContent).then(function () { flash(button, "Copied"); }, fallback);
    });
  });

  // Destructive buttons ask first. The question is in the button's data-confirm attribute.
  document.querySelectorAll("button[data-confirm]").forEach(function (button) {
    button.addEventListener("click", function (event) {
      if (!window.confirm(button.getAttribute("data-confirm"))) event.preventDefault();
    });
  });

  // Inline style attributes are blocked by the panel CSP; color swatches are set through CSSOM.
  document.querySelectorAll(".manage-swatch[data-color]").forEach(function (swatch) {
    var color = swatch.getAttribute("data-color") || "";
    if (/^#?[0-9a-fA-F]{6}$/.test(color)) swatch.style.backgroundColor = color.charAt(0) === "#" ? color : "#" + color;
  });
})();
