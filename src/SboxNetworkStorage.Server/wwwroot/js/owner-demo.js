"use strict";
document.querySelectorAll('form[method="post"]').forEach(function (form) {
  form.querySelectorAll('button[type="submit"], button:not([type]), input[type="submit"]').forEach(function (button) {
    button.disabled = true;
    button.title = "Unavailable in the read-only demo";
  });
  form.addEventListener("submit", function (event) { event.preventDefault(); });
});
