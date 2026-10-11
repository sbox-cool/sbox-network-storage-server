"use strict";
// Shared modal and confirm dialogs for the owner dashboard.
// Modal forms are ordinary server-rendered <form method="post"> blocks inside a
// <dialog> element, so every action still works with JavaScript disabled: the
// trigger links to ?dialog=<id> and the server renders the same form inline.
// With JavaScript, buttons with data-dialog="<id>" open the dialog with
// showModal (native focus trap, Escape, top layer) and focus returns to the
// opener on close. Buttons with data-confirm ask in a shared confirm dialog.
(function () {
  // Pure helper: which element should receive focus after a dialog closes.
  // The opener wins while it is still in the document; otherwise the main
  // content landmark is a stable fallback. Exported for node:test.
  function resolveReturnTarget(opener, doc) {
    if (opener && typeof opener.focus === "function") {
      var connected = false;
      if (doc && typeof doc.contains === "function") connected = doc.contains(opener);
      else if (typeof opener.isConnected === "boolean") connected = opener.isConnected;
      if (connected) return opener;
    }
    if (doc && typeof doc.getElementById === "function") {
      var main = doc.getElementById("main-content");
      if (main) return main;
    }
    return (doc && doc.body) || null;
  }

  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) module.exports = { resolveReturnTarget: resolveReturnTarget };

  if (typeof document === "undefined" || typeof window === "undefined") return;

  var openerByDialog = new WeakMap();

  function focusReturn(dialog) {
    var target = resolveReturnTarget(openerByDialog.get(dialog), document);
    if (target && typeof target.focus === "function") {
      try { target.focus(); } catch (_) {}
    }
  }

  function openDialog(dialog, opener) {
    if (!dialog || typeof dialog.showModal !== "function") return false;
    if (opener) openerByDialog.set(dialog, opener);
    // The server renders validation failures with the plain `open` attribute
    // so the form shows without JavaScript; upgrade that to a modal here.
    if (dialog.open) {
      try { dialog.close(); } catch (_) { return true; }
    }
    try { dialog.showModal(); } catch (_) { return false; }
    var focusable = dialog.querySelector("[data-autofocus]") || dialog.querySelector("input, select, textarea, button");
    if (focusable && typeof focusable.focus === "function") {
      try { focusable.focus(); } catch (_) {}
    }
    return true;
  }

  function openById(id, opener) {
    if (!id) return false;
    var dialog = document.getElementById(id);
    if (!dialog || dialog.tagName !== "DIALOG") return false;
    return openDialog(dialog, opener);
  }

  var pendingConfirm = null;
  // Runs at parse time so dialog openers appear as soon as JavaScript is known
  // to work (the CSS hides them until this class lands).
  try { document.documentElement.classList.add("owner-js"); } catch (_) {}

  function confirmDialog(options) {
    var dialog = document.getElementById("owner-confirm");
    if (!dialog || typeof dialog.showModal !== "function") return Promise.resolve(true);
    var title = dialog.querySelector("[data-confirm-title]");
    var message = dialog.querySelector("[data-confirm-message]");
    var button = dialog.querySelector("[data-confirm-ok]");
    if (title) title.textContent = options.title || "Are you sure?";
    if (message) message.textContent = options.message || "";
    if (button) {
      button.textContent = options.confirmLabel || "Confirm";
      button.classList.toggle("danger", options.danger !== false);
    }
    if (pendingConfirm) pendingConfirm(false);
    return new Promise(function (resolve) {
      pendingConfirm = resolve;
      openDialog(dialog, options.opener || null);
    });
  }

  window.OwnerDialog = { open: openById, confirm: confirmDialog };

  document.addEventListener("DOMContentLoaded", function () {
    // Shared confirm dialog result.
    var confirmBox = document.getElementById("owner-confirm");
    if (confirmBox) {
      confirmBox.addEventListener("close", function () {
        var resolve = pendingConfirm;
        pendingConfirm = null;
        if (resolve) resolve(confirmBox.returnValue === "confirm");
        focusReturn(confirmBox);
      });
    }

    // Modal openers.
    document.querySelectorAll("button[data-dialog]").forEach(function (button) {
      button.addEventListener("click", function () {
        openById(button.getAttribute("data-dialog"), button);
      });
    });

    // Return focus whenever any owner dialog closes.
    document.querySelectorAll("dialog.owner-dialog").forEach(function (dialog) {
      if (dialog.id !== "owner-confirm") dialog.addEventListener("close", function () { focusReturn(dialog); });
    });

    // Destructive buttons: ask in the shared confirm dialog, then resubmit.
    // The flag survives only for the resubmitted click so a failed validation
    // still asks again next time. Without JavaScript the form POSTs directly.
    document.querySelectorAll("button[data-confirm]").forEach(function (button) {
      button.addEventListener("click", function (event) {
        var form = button.form || button.closest("form");
        if (!form || form.dataset.ownerConfirmed === "1") return;
        event.preventDefault();
        confirmDialog({
          title: button.getAttribute("data-confirm-title") || "Confirm",
          message: button.getAttribute("data-confirm") || "",
          confirmLabel: button.getAttribute("data-confirm-ok") || button.textContent.trim() || "Confirm",
          opener: button,
        }).then(function (ok) {
          if (!ok) return;
          form.dataset.ownerConfirmed = "1";
          // The server confirm page is the no-JavaScript fallback: mark this
          // resubmission as already confirmed so it is skipped.
          var flag = form.querySelector('input[name="confirmed"]');
          if (!flag) {
            flag = document.createElement("input");
            flag.type = "hidden";
            flag.name = "confirmed";
            form.appendChild(flag);
          }
          flag.value = "true";
          try { button.click(); } finally { form.dataset.ownerConfirmed = ""; }
        });
      });
    });

    // A server validation failure re-renders the page with data-open-dialog,
    // so the same dialog opens again with the entered values and the error.
    var reopen = document.body ? document.body.getAttribute("data-open-dialog") : null;
    if (reopen) openById(reopen, null);
  });
})();
