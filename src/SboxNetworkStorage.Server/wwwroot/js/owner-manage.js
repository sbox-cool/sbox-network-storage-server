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

  // Copy buttons sit right after the snippet they copy.
  document.querySelectorAll("[data-copy-previous]").forEach(function (button) {
    button.addEventListener("click", function () {
      var source = button.previousElementSibling;
      if (!source || !navigator.clipboard) return;
      navigator.clipboard.writeText(source.textContent).then(function () {
        var label = button.textContent;
        button.textContent = "Copied";
        setTimeout(function () { button.textContent = label; }, 1500);
      });
    });
  });

  // Inline style attributes are blocked by the panel CSP; color swatches are set through CSSOM.
  document.querySelectorAll(".manage-swatch[data-color]").forEach(function (swatch) {
    var color = swatch.getAttribute("data-color") || "";
    if (/^#?[0-9a-fA-F]{6}$/.test(color)) swatch.style.backgroundColor = color.charAt(0) === "#" ? color : "#" + color;
  });
})();
