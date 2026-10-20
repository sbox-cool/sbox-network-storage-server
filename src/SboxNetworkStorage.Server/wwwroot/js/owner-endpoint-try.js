// Try-it on the endpoint page: one dry run of the saved definition, rendered as text only.
(function () {
  "use strict";
  var root = document.getElementById("try-it");
  if (!root) return;
  var input = document.getElementById("try-it-input");
  var steam = document.getElementById("try-it-steam");
  var server = document.getElementById("try-it-server");
  var run = document.getElementById("try-it-run");
  var result = document.getElementById("try-it-result");
  var url = root.getAttribute("data-try-url");
  var endpoint = root.getAttribute("data-endpoint");

  function token() {
    var field = document.querySelector("input[name=__RequestVerificationToken]");
    return field ? field.value : "";
  }

  function element(tag, text, className) {
    var node = document.createElement(tag);
    if (text !== undefined && text !== null) node.textContent = text;
    if (className) node.className = className;
    return node;
  }

  function clear() {
    while (result.firstChild) result.removeChild(result.firstChild);
  }

  function show(message) {
    clear();
    result.appendChild(element("p", message, "muted"));
  }

  function pretty(value) {
    return JSON.stringify(value === undefined ? null : value, null, 2);
  }

  function render(body) {
    clear();
    var verdict = body.passed ? "Passed" : "Failed";
    var status = body.result && body.result.status !== undefined ? body.result.status : "unknown";
    var total = body.timing && body.timing.total !== undefined ? body.timing.total : "?";
    result.appendChild(element("p", verdict + ". HTTP status " + status + ", " + total + " ms.", "try-it-verdict"));
    if (body.reason) result.appendChild(element("p", body.reason, "notice"));

    var steps = Array.isArray(body.steps) ? body.steps : [];
    if (steps.length) {
      result.appendChild(element("h3", "Steps"));
      var list = element("ol", null, "try-it-steps");
      steps.forEach(function (step) {
        var item = element("li", step.id + " (" + step.type + "): " + (step.passed ? "ok" : "failed"));
        item.appendChild(element("pre", pretty(step.result), "try-it-json"));
        list.appendChild(item);
      });
      result.appendChild(list);
    }

    var writes = Array.isArray(body.pendingWrites) ? body.pendingWrites : [];
    result.appendChild(element("h3", "Pending writes"));
    if (!writes.length) {
      result.appendChild(element("p", "No writes. A real call would not change stored data.", "muted"));
    } else {
      var writeList = element("ul", null, "try-it-writes");
      writes.forEach(function (write) {
        writeList.appendChild(element("li", write.op + " " + write.collection + " / " + write.key));
      });
      result.appendChild(writeList);
    }

    result.appendChild(element("h3", "Response body"));
    var response = body.result ? body.result.body : null;
    result.appendChild(element("pre", pretty(response), "try-it-json"));
  }

  function fail(message) {
    show(message);
  }

  function submit() {
    var parsed;
    try {
      parsed = JSON.parse(input.value.trim() || "{}");
    } catch (error) {
      fail("Input is not valid JSON: " + error.message);
      return;
    }
    if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
      fail("Input must be a JSON object, for example {\"amount\": 5}.");
      return;
    }
    run.disabled = true;
    show("Running...");
    fetch(url, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", RequestVerificationToken: token() },
      body: JSON.stringify({ endpoint: endpoint, input: parsed, steamId: steam.value.trim(), asServer: server.checked })
    }).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (data) {
        if (!response.ok) throw new Error(data.error || "Request failed with status " + response.status + ".");
        return data;
      });
    }).then(render, function (error) {
      fail(error.message);
    }).then(function () {
      run.disabled = false;
    });
  }

  run.addEventListener("click", submit);
  input.addEventListener("keydown", function (event) {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      submit();
    }
  });
})();
