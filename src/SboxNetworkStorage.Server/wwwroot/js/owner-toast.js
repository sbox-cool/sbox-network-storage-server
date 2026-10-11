"use strict";
// Toast notifications for the owner dashboard. _Layout renders TempData flashes
// as .toast items inside a polite live region; this script auto-dismisses them
// (at least 5 seconds, pausable on hover/focus) and wires the close buttons.
// Without JavaScript the same markup stays visible as an inline notice.
(function () {
  var DefaultDurationMs = 6000;

  // Pure schedule helpers with an injectable clock, exported for node:test.
  function startSchedule(durationMs, now) {
    return { durationMs: durationMs, elapsedMs: 0, pausedAt: null, lastTick: now };
  }
  function pauseSchedule(schedule, now) {
    if (schedule.pausedAt === null) {
      schedule.elapsedMs += now - schedule.lastTick;
      schedule.pausedAt = now;
    }
    return schedule;
  }
  function resumeSchedule(schedule, now) {
    if (schedule.pausedAt !== null) {
      schedule.pausedAt = null;
      schedule.lastTick = now;
    }
    return schedule;
  }
  function remainingMs(schedule, now) {
    var elapsed = schedule.elapsedMs;
    if (schedule.pausedAt === null) elapsed += now - schedule.lastTick;
    return Math.max(0, schedule.durationMs - elapsed);
  }

  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) {
    module.exports = {
      DefaultDurationMs: DefaultDurationMs,
      startSchedule: startSchedule,
      pauseSchedule: pauseSchedule,
      resumeSchedule: resumeSchedule,
      remainingMs: remainingMs,
    };
  }

  if (typeof document === "undefined" || typeof window === "undefined") return;

  function dismiss(toast) {
    if (toast && toast.parentNode) toast.parentNode.removeChild(toast);
  }

  function arm(toast) {
    var duration = parseInt(toast.getAttribute("data-dismiss-after") || "", 10);
    if (!(duration >= 1000)) duration = DefaultDurationMs;
    var schedule = startSchedule(duration, Date.now());
    var timer = null;
    function clear() { if (timer !== null) { window.clearTimeout(timer); timer = null; } }
    function plan() {
      clear();
      var wait = remainingMs(schedule, Date.now());
      if (wait <= 0) { dismiss(toast); return; }
      timer = window.setTimeout(function () { dismiss(toast); }, wait);
    }
    toast.addEventListener("mouseenter", function () { pauseSchedule(schedule, Date.now()); clear(); });
    toast.addEventListener("mouseleave", function () { resumeSchedule(schedule, Date.now()); plan(); });
    toast.addEventListener("focusin", function () { pauseSchedule(schedule, Date.now()); clear(); });
    toast.addEventListener("focusout", function () { resumeSchedule(schedule, Date.now()); plan(); });
    var close = toast.querySelector("[data-toast-close]");
    if (close) close.addEventListener("click", function () { clear(); dismiss(toast); });
    plan();
  }

  document.addEventListener("DOMContentLoaded", function () {
    document.querySelectorAll(".toasts .toast").forEach(arm);
  });
})();
