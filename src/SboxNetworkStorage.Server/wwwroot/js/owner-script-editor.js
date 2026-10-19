// Monaco editors for the endpoint definition (raw tab). The plain textarea #definition stays the form
// field and the source of truth: the YAML editor and the script pane mirror it, and every edit is
// written back to it. Monaco loads lazily when the raw tab is first shown. If it fails to load, the
// plain textarea stays in use.
(function () {
  "use strict";

  var MONACO_BASE = "/owner-assets/vendor/monaco";
  var TYPINGS_FALLBACK = "/owner-assets/endpoint-api/ns.d.ts";
  var MARKER_OWNER = "owner-check";
  var WORKERS = {
    editor: "vs/assets/editor.worker-_vAIFJDs.js",
    json: "vs/assets/json.worker-Csf_TLih.js",
    typescript: "vs/assets/ts.worker-g3pjEjl4.js"
  };
  var THEME_BASE = { dark: "vs-dark", light: "vs", contrast: "hc-black" };
  var COLOR_VARIABLES = [
    ["editor.background", "--code-bg"],
    ["editor.foreground", "--text"],
    ["editorLineNumber.foreground", "--text-3"],
    ["editorLineNumber.activeForeground", "--text-2"],
    ["editorCursor.foreground", "--accent-fg"],
    ["editor.lineHighlightBackground", "--fill"],
    ["editor.selectionBackground", "--accent-fg", "33"],
    ["editorWidget.background", "--bg-raised"],
    ["editorWidget.border", "--border"]
  ];
  var TOKEN_VARIABLES = [
    ["comment", "--syn-comment"],
    ["keyword", "--syn-keyword"],
    ["string", "--syn-string"],
    ["number", "--syn-number"],
    ["regexp", "--syn-regex"],
    ["type", "--syn-type"],
    ["delimiter", "--syn-punctuation"],
    ["operator", "--syn-operator"],
    ["identifier", "--syn-variable"],
    ["function", "--syn-function"],
    ["predefined", "--syn-builtin"],
    ["key", "--syn-property"]
  ];
  var AMBIENT_MODULES = 'declare module "globals/*" { const mod: any; export = mod; }';
  var FAILED = "The code editor could not load. Edit the definition in the plain text box below.";
  var NO_BLOCK = "Add a script: | block to the YAML to edit the script here.";
  var SHORTCUTS = "Tab indents in the editor. Press Ctrl+M to move focus out of it. Ctrl / \u2318 + S to save.";

  function workerFile(label) {
    if (label === "json") return WORKERS.json;
    if (label === "typescript" || label === "javascript") return WORKERS.typescript;
    return WORKERS.editor;
  }

  // Monaco gives a model created from empty text the platform EOL (CRLF on Windows). Pin LF so the
  // script text never gains carriage returns when it is written back to the YAML.
  function createLfModel(text, language, path) {
    var model = monaco.editor.createModel(text, language, monaco.Uri.parse(path));
    model.setEOL(monaco.editor.EndOfLineSequence.LF);
    return model;
  }

  // Smallest single replacement that turns `current` into `next`: common prefix and suffix are kept,
  // so the cursor and undo history of the Monaco model survive most edits.
  function minimalEdit(current, next) {
    var max = Math.min(current.length, next.length);
    var start = 0;
    while (start < max && current.charCodeAt(start) === next.charCodeAt(start)) start++;
    var tail = 0;
    while (tail < max - start &&
      current.charCodeAt(current.length - 1 - tail) === next.charCodeAt(next.length - 1 - tail)) tail++;
    return {
      start: start,
      end: current.length - tail,
      text: next.slice(start, next.length - tail)
    };
  }

  // Parses one line of the check results list, for example
  // "Error SCRIPT_SYNTAX at /script: Line 3, column 5: Unexpected token".
  function parseCheckResult(text) {
    var match = /^(Error|Warning) (\S+) at (\S*): ([\s\S]*)$/.exec(String(text || "").trim());
    if (!match) return null;
    var position = /Line (\d+), column (\d+)/.exec(match[4]);
    return {
      severity: match[1] === "Error" ? "error" : "warning",
      code: match[2],
      path: match[3],
      message: match[4],
      line: position ? Number(position[1]) : 0,
      column: position ? Number(position[2]) : 0
    };
  }

  // True when the column of a line falls in code rather than a comment, string or regular expression.
  // `lineTokens` is one line of monaco.editor.tokenize output.
  function isPlainCode(lineTokens, column) {
    var type = "";
    for (var index = 0; index < lineTokens.length; index++) {
      if (lineTokens[index].offset > column) break;
      type = lineTokens[index].type;
    }
    return !/^(comment|string|regexp)/.test(type);
  }

  var api = {
    WORKERS: WORKERS,
    workerFile: workerFile,
    minimalEdit: minimalEdit,
    parseCheckResult: parseCheckResult,
    isPlainCode: isPlainCode
  };

  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
    return;
  }

  window.OwnerScriptEditor = api;

  function setStatus(text, isError) {
    var status = document.getElementById("monaco-status");
    if (!status) return;
    status.textContent = text;
    status.hidden = !text;
    status.dataset.state = isError ? "error" : "loading";
  }

  function fetchText(url) {
    return fetch(url, { credentials: "same-origin" }).then(function (response) {
      if (!response.ok) throw new Error("HTTP " + response.status + " for " + url);
      return response.text();
    });
  }

  // Project typings first; the shared ns.d.ts is the fallback. Null when neither loads.
  function loadTypings(url) {
    return fetchText(url || TYPINGS_FALLBACK)
      .catch(function () { return fetchText(TYPINGS_FALLBACK); })
      .catch(function () { return null; });
  }

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var script = document.createElement("script");
      script.src = src;
      script.async = true;
      script.onload = resolve;
      script.onerror = function () { reject(new Error("Could not load " + src)); };
      document.head.appendChild(script);
    });
  }

  function loadMonaco() {
    if (window.monaco) return Promise.resolve(window.monaco);
    window.MonacoEnvironment = {
      getWorkerUrl: function (moduleId, label) { return MONACO_BASE + "/" + workerFile(label); }
    };
    return loadScript(MONACO_BASE + "/vs/loader.js").then(function () {
      window.require.config({ paths: { vs: MONACO_BASE + "/vs" } });
      return new Promise(function (resolve, reject) {
        window.require(["vs/editor/editor.main"], function (loaded) {
          resolve(window.monaco || loaded);
        }, reject);
      });
    });
  }

  function start() {
    var host = document.getElementById("definition-monaco");
    if (!host) return;
    setStatus("Loading the code editor...", false);
    Promise.all([loadMonaco(), loadTypings(host.dataset.typingsUrl)])
      .then(function (results) { mount(results[0], results[1], host); })
      .catch(function (error) {
        console.error(error);
        host.hidden = true;
        setStatus(FAILED, true);
      });
  }

  var booted = false;

  function boot() {
    var raw = document.getElementById("definition-raw");
    var textarea = document.getElementById("definition");
    if (!raw || !textarea || !textarea.form) return;
    if (!("IntersectionObserver" in window)) {
      start();
      return;
    }
    var observer = new IntersectionObserver(function (entries) {
      if (!entries.some(function (entry) { return entry.isIntersecting; })) return;
      observer.disconnect();
      start();
    });
    observer.observe(raw);
  }

  function mount(monaco, typings, host) {
    if (booted) return;
    booted = true;

    var textarea = document.getElementById("definition");
    var raw = document.getElementById("definition-raw");
    var yamlHost = document.getElementById("definition-yaml");
    var pane = document.getElementById("script-pane");
    var scriptHost = document.getElementById("script-editor");
    var notice = document.getElementById("script-pane-notice");
    var languageLabel = document.getElementById("script-pane-language");
    var position = document.getElementById("source-position");
    var checks = document.getElementById("check-results");
    var docsFilter = document.getElementById("script-docs-filter");
    var docsStatus = document.getElementById("script-docs-status");
    var docsList = document.getElementById("script-docs-list");
    var docsDetail = document.getElementById("script-docs-detail");
    var docsSignature = document.getElementById("script-docs-signature");
    var docsText = document.getElementById("script-docs-text");
    var insertButton = document.getElementById("script-docs-insert");

    var applying = false;
    var active = null;
    var apiDecorations = [];
    var scriptHasBlock = false;
    var selectedDoc = null;
    var docEntries = typings === null ? null : OwnerScriptDocs.entries(OwnerScriptDocs.parse(typings));

    // Unhidden before creation so Monaco measures fonts in a laid-out container.
    host.hidden = false;
    try {
      applyTheme();
      addTypings(typings);

      var yamlModel = createLfModel(textarea.value, "yaml", "file:///owner/definition.yaml");
      var scriptModel = createLfModel("", "javascript", "file:///owner/endpoint-script.js");
      var style = window.getComputedStyle(textarea);
      var common = {
        automaticLayout: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        wordWrap: "off",
        tabSize: 2,
        insertSpaces: true,
        fontFamily: style.fontFamily,
        fontSize: parseFloat(style.fontSize) || 14
      };
      var yamlEditor = monaco.editor.create(yamlHost, Object.assign({
        model: yamlModel,
        ariaLabel: "Endpoint definition (YAML)"
      }, common));
      var scriptEditor = monaco.editor.create(scriptHost, Object.assign({
        model: scriptModel,
        ariaLabel: "Endpoint script",
        readOnly: true
      }, common));
      setupDocs();

      function withApplying(change) {
        var previous = applying;
        applying = true;
        try { change(); } finally { applying = previous; }
      }

      function applyEdit(model, edit) {
        if (edit.start === edit.end && !edit.text) return;
        var from = model.getPositionAt(edit.start);
        var to = model.getPositionAt(edit.end);
        withApplying(function () {
          model.applyEdits([{
            range: new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column),
            text: edit.text,
            forceMoveMarkers: true
          }]);
        });
      }

      function showPosition(editor) {
        var cursor = editor && editor.getPosition();
        if (!cursor || editor !== active || !position) return;
        position.textContent = "Line " + cursor.lineNumber + ", col " + cursor.column;
      }

      // Writes the YAML model into the textarea form field. The workbench's input listener updates
      // the dirty state; the cursor status is written afterwards so it reflects Monaco, not the textarea.
      function publishYaml() {
        var text = yamlModel.getValue();
        if (textarea.value !== text) {
          textarea.value = text;
          textarea.dispatchEvent(new Event("input", { bubbles: true }));
        }
        showPosition(active);
      }

      function clearMarkers() {
        monaco.editor.setModelMarkers(scriptModel, MARKER_OWNER, []);
      }

      function markerFor(item) {
        if (item.path !== "/script" || item.line < 1) return null;
        var line = Math.min(item.line, scriptModel.getLineCount());
        var maxColumn = scriptModel.getLineMaxColumn(line);
        var column = Math.min(Math.max(item.column, 1), maxColumn);
        var word = scriptModel.getWordAtPosition({ lineNumber: line, column: column });
        return {
          severity: item.severity === "error" ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
          message: item.message,
          code: item.code,
          startLineNumber: line,
          startColumn: column,
          endLineNumber: line,
          endColumn: word ? word.endColumn : Math.min(column + 1, maxColumn)
        };
      }

      // Server check results that point at the script body (line and column relative to the script).
      // Results without a position stay in the results list only.
      function applyCheckMarkers() {
        var items = checks
          ? Array.prototype.map.call(checks.querySelectorAll("li"), function (li) { return parseCheckResult(li.textContent); })
          : [];
        var markers = items.filter(Boolean).map(markerFor).filter(Boolean);
        monaco.editor.setModelMarkers(scriptModel, MARKER_OWNER, markers);
      }

      function apiTokenRanges(model) {
        var ranges = [];
        var tokens = monaco.editor.tokenize(model.getValue(), model.getLanguageId());
        for (var index = 0; index < tokens.length; index++) {
          var lineNumber = index + 1;
          var line = model.getLineContent(lineNumber);
          var pattern = /(^|[^\w$.])(req|ns|globals|workflows)\b/g;
          var match;
          while ((match = pattern.exec(line)) !== null) {
            var column = match.index + match[1].length;
            if (isPlainCode(tokens[index], column)) {
              ranges.push(new monaco.Range(lineNumber, column + 1, lineNumber, column + 1 + match[2].length));
            }
          }
        }
        return ranges;
      }

      function refreshApiTokens() {
        var ranges = apiTokenRanges(scriptModel);
        apiDecorations = scriptModel.deltaDecorations(apiDecorations, ranges.map(function (range) {
          return { range: range, options: { inlineClassName: "owner-api-token" } };
        }));
      }

      function swapScriptModel(runtime) {
        var previous = scriptModel;
        scriptModel = createLfModel(previous.getValue(), runtime,
          runtime === "typescript" ? "file:///owner/endpoint-script.ts" : "file:///owner/endpoint-script.js");
        apiDecorations = [];
        scriptEditor.setModel(scriptModel);
        previous.dispose();
      }

      // Shows or hides the script pane from the YAML and copies the script block into the script model.
      function syncScript(text) {
        var runtime = OwnerScriptBlock.runtimeOf(text);
        host.classList.toggle("has-script", !!runtime);
        pane.hidden = !runtime;
        if (!runtime) return;
        if (scriptModel.getLanguageId() !== runtime) swapScriptModel(runtime);
        var block = OwnerScriptBlock.find(text);
        scriptHasBlock = !!block;
        applyEdit(scriptModel, minimalEdit(scriptModel.getValue(), block ? block.body : ""));
        scriptEditor.updateOptions({ readOnly: !block });
        notice.hidden = !!block;
        notice.textContent = block ? "" : NO_BLOCK;
        languageLabel.textContent = runtime === "typescript" ? "TypeScript" : "JavaScript";
        insertButton.disabled = !selectedDoc || !scriptHasBlock;
        refreshApiTokens();
      }

      // The textarea changed outside Monaco (guided editor, example gallery).
      function setYaml(text) {
        applyEdit(yamlModel, minimalEdit(yamlModel.getValue(), text));
        syncScript(yamlModel.getValue());
      }

      yamlEditor.onDidChangeModelContent(function () {
        clearMarkers();
        if (applying) return;
        publishYaml();
        syncScript(yamlModel.getValue());
      });

      scriptEditor.onDidChangeModelContent(function () {
        clearMarkers();
        if (applying) return;
        refreshApiTokens();
        if (!scriptHasBlock) return;
        var current = yamlModel.getValue();
        var next = OwnerScriptBlock.replace(current, scriptModel.getValue());
        if (next === current) return;
        applyEdit(yamlModel, minimalEdit(current, next));
        publishYaml();
      });

      yamlEditor.onDidFocusEditorText(function () { active = yamlEditor; showPosition(yamlEditor); });
      scriptEditor.onDidFocusEditorText(function () { active = scriptEditor; showPosition(scriptEditor); });
      yamlEditor.onDidChangeCursorPosition(function () { showPosition(yamlEditor); });
      scriptEditor.onDidChangeCursorPosition(function () { showPosition(scriptEditor); });

      textarea.addEventListener("input", function () {
        if (textarea.value !== yamlModel.getValue()) setYaml(textarea.value);
      });

      // Monaco replaces the textarea in the raw tab, so the browser's required check can no longer
      // focus it. An empty definition is caught here instead.
      textarea.required = false;
      textarea.form.addEventListener("submit", function (event) {
        if (textarea.value.trim()) return;
        event.preventDefault();
        setStatus("Enter a definition before saving.", true);
        yamlEditor.focus();
      });

      var shortcuts = document.querySelector(".editor-shortcuts");
      if (shortcuts) shortcuts.textContent = SHORTCUTS;

      if (checks) {
        new MutationObserver(applyCheckMarkers).observe(checks, { childList: true, subtree: true, characterData: true });
      }
      new MutationObserver(applyTheme).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });

      syncScript(yamlModel.getValue());
      applyCheckMarkers();
      raw.classList.add("monaco-active");
      setStatus("", false);
    } catch (error) {
      host.hidden = true;
      throw error;
    }

    function currentMode() {
      var mode = document.documentElement.dataset.theme;
      return THEME_BASE[mode] ? mode : "dark";
    }

    function cssColor(name) {
      var value = window.getComputedStyle(document.documentElement).getPropertyValue(name).trim();
      return /^#[0-9a-fA-F]{6}$/.test(value) ? value : null;
    }

    function themeData(mode) {
      var colors = {};
      COLOR_VARIABLES.forEach(function (row) {
        var color = cssColor(row[1]);
        if (color) colors[row[0]] = row[2] ? color + row[2] : color;
      });
      var rules = [];
      TOKEN_VARIABLES.forEach(function (row) {
        var color = cssColor(row[1]);
        if (color) rules.push({ token: row[0], foreground: color.slice(1) });
      });
      return { base: THEME_BASE[mode], inherit: true, rules: rules, colors: colors };
    }

    function applyTheme() {
      var mode = currentMode();
      var name = "owner-" + mode;
      monaco.editor.defineTheme(name, themeData(mode));
      monaco.editor.setTheme(name);
    }

    function addTypings(text) {
      var languages = monaco.languages.typescript;
      if (!languages || text === null) return;
      var options = {
        allowNonTsExtensions: true,
        allowJs: true,
        checkJs: true,
        noEmit: true,
        target: languages.ScriptTarget.ES2020,
        module: languages.ModuleKind.ESNext
      };
      [languages.javascriptDefaults, languages.typescriptDefaults].forEach(function (defaults) {
        defaults.setCompilerOptions(options);
        defaults.setDiagnosticsOptions({ noSemanticValidation: false, noSyntaxValidation: false });
        defaults.addExtraLib(text, "file:///owner/endpoint-api.d.ts");
        defaults.addExtraLib(AMBIENT_MODULES, "file:///owner/globals.d.ts");
      });
    }

    function setupDocs() {
      if (!docsFilter || !docsList) return;
      if (docEntries === null) {
        docsStatus.textContent = "The Script API could not be loaded.";
        docsStatus.hidden = false;
        return;
      }
      docsFilter.addEventListener("input", renderDocs);
      insertButton.addEventListener("click", function () {
        if (!selectedDoc || !scriptHasBlock) return;
        var editor = scriptEditor;
        editor.executeEdits("owner-docs", [{
          range: editor.getSelection(),
          text: selectedDoc.insert,
          forceMoveMarkers: true
        }]);
        editor.focus();
      });
      renderDocs();
    }

    function renderDocs() {
      var items = OwnerScriptDocs.filter(docEntries, docsFilter.value);
      docsList.replaceChildren();
      items.forEach(function (entry) {
        var item = document.createElement("li");
        var button = document.createElement("button");
        button.type = "button";
        button.className = "script-docs-item";
        button.textContent = entry.path;
        button.setAttribute("aria-pressed", String(entry === selectedDoc));
        button.addEventListener("click", function () { selectDoc(entry); });
        item.appendChild(button);
        docsList.appendChild(item);
      });
      docsStatus.textContent = items.length ? "" : (docEntries.length ? "No matches." : "No API entries are listed.");
      docsStatus.hidden = items.length > 0;
    }

    function selectDoc(entry) {
      selectedDoc = entry;
      docsSignature.textContent = entry.signature;
      docsText.textContent = entry.doc || "No description.";
      docsDetail.hidden = false;
      insertButton.disabled = !scriptHasBlock;
      renderDocs();
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
