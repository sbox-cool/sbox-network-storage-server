"use strict";
(function () {
  var themes = ["system", "dark", "light", "slate", "warm"];
  var preference = "system";
  var media = window.matchMedia("(prefers-color-scheme: light)");
  try { var saved = localStorage.getItem("sbox-ns-theme"); if (themes.indexOf(saved) !== -1) preference = saved; } catch (_) {}
  function apply() {
    document.documentElement.dataset.theme = preference === "system" ? (media.matches ? "light" : "dark") : preference;
  }
  apply();
  media.addEventListener("change", function () { if (preference === "system") apply(); });
  document.addEventListener("DOMContentLoaded", function () {
    var picker = document.getElementById("theme-choice");
    if (!picker) return;
    picker.value = preference;
    picker.addEventListener("change", function () {
      if (themes.indexOf(picker.value) === -1) return;
      preference = picker.value;
      try { localStorage.setItem("sbox-ns-theme", preference); } catch (_) {}
      apply();
    });
  });
})();
