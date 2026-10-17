/* Theme preference. "system" is the default: it resolves to "dark" (near-black)
   in a dark color scheme and to "light" (white) in a light one. Retired hue
   keys map forward so stored preferences survive: cooldark, slate, mint and
   warm render as dark, coollight renders as light. High contrast is the only
   extra theme ("contrast") because it does a different job. */
"use strict";
(function () {
  var themes = ["system", "dark", "light", "contrast"];
  var legacy = { cooldark: "dark", coollight: "light", mint: "dark", slate: "dark", warm: "dark" };
  var preference = "system";
  var media = window.matchMedia("(prefers-color-scheme: light)");
  try {
    var saved = localStorage.getItem("sbox-ns-theme");
    if (themes.indexOf(saved) !== -1) preference = saved;
    else if (Object.prototype.hasOwnProperty.call(legacy, saved)) preference = legacy[saved];
  } catch (_) {}
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
  document.addEventListener("DOMContentLoaded", function () {
    var toggle = document.getElementById("nav-toggle");
    var sidebar = document.getElementById("owner-nav");
    if (!toggle || !sidebar) return;
    function close() {
      sidebar.classList.remove("open");
      toggle.setAttribute("aria-expanded", "false");
    }
    toggle.addEventListener("click", function () {
      var open = sidebar.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
    sidebar.addEventListener("click", function (event) {
      if (event.target.closest("a") && window.innerWidth <= 760) close();
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && sidebar.classList.contains("open")) { close(); toggle.focus(); }
    });
  });
  document.addEventListener("DOMContentLoaded", function () {
    var menu = document.getElementById("owner-menu");
    if (menu) {
      document.addEventListener("click", function (event) {
        if (menu.hasAttribute("open") && !event.target.closest("#owner-menu")) menu.removeAttribute("open");
      });
      document.addEventListener("keydown", function (event) {
        if (event.key === "Escape" && menu.hasAttribute("open")) {
          menu.removeAttribute("open");
          var summary = menu.querySelector("summary");
          if (summary) summary.focus();
        }
      });
    }
    var strip = document.querySelector(".alert-strip");
    if (!strip) return;
    var sessionHidden = {};
    try { sessionHidden = JSON.parse(sessionStorage.getItem("sbox-ns-alerts") || "{}"); } catch (_) {}
    // Security warnings about plain HTTP stay dismissed for a week, across sessions, then return.
    var snoozeMs = { "http": 7 * 864e5, "http-enabled": 7 * 864e5 };
    var snoozed = {};
    try { snoozed = JSON.parse(localStorage.getItem("sbox-ns-alerts-until") || "{}"); } catch (_) {}
    function isHidden(item) {
      var key = item.getAttribute("data-alert");
      if (key === "update") {
        try { return localStorage.getItem("sbox-ns-update-seen") === item.getAttribute("data-version"); } catch (_) { return false; }
      }
      if (snoozeMs[key]) return typeof snoozed[key] === "number" && snoozed[key] > Date.now();
      return !!sessionHidden[key];
    }
    strip.querySelectorAll("[data-alert]").forEach(function (item) { if (isHidden(item)) item.remove(); });
    if (!strip.querySelector("[data-alert]")) { strip.remove(); return; }
    strip.querySelectorAll("[data-alert-close]").forEach(function (button) {
      button.addEventListener("click", function () {
        var item = button.closest("[data-alert]");
        if (!item) return;
        var key = item.getAttribute("data-alert");
        try {
          if (key === "update") localStorage.setItem("sbox-ns-update-seen", item.getAttribute("data-version") || "");
          else if (snoozeMs[key]) { snoozed[key] = Date.now() + snoozeMs[key]; localStorage.setItem("sbox-ns-alerts-until", JSON.stringify(snoozed)); }
          else { sessionHidden[key] = true; sessionStorage.setItem("sbox-ns-alerts", JSON.stringify(sessionHidden)); }
        } catch (_) {}
        item.remove();
        if (!strip.querySelector("[data-alert]")) strip.remove();
      });
    });
  });
})();
