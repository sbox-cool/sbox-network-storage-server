// Type-check the unchanged handler in a private, contextually typed module. Function parameters in
// an ordinary `export default function handle(req, ns)` otherwise remain `any`, even with ns.d.ts.
(function (global) {
  "use strict";
  var OWNER = "owner-script-types";

  function text(parts) {
    if (typeof parts === "string") return parts;
    return (parts || []).map(function (part) { return part.text; }).join("");
  }

  function message(diagnostic) {
    if (typeof diagnostic === "string") return diagnostic;
    return diagnostic.messageText + (diagnostic.next ? "\n" + diagnostic.next.map(message).join("\n") : "");
  }

  function documentation(details) {
    var value = text(details.documentation);
    (details.tags || []).forEach(function (tag) {
      var content = text(tag.text);
      value += "\n\n**@" + tag.name + "**\n" + (tag.name === "example" ? "````javascript\n" + content + "\n````" : content);
    });
    return { value: value, isTrusted: false };
  }

  function project(source, language, tree) {
    var nodes = tree.childItems || [];
    var target = nodes.find(function (node) { return /^export\s+default\s+/.test(source.slice(node.spans[0].start)); });
    var edit = null;
    var privateBinding = null;
    if (target) {
      var start = target.spans[0].start;
      var prefix = /^export\s+default\s+/.exec(source.slice(start));
      var body = source.slice(start + prefix[0].length);
      var alias = /^([A-Za-z_$][\w$]*)\s*;?\s*$/.exec(body.slice(0, target.spans[0].length - prefix[0].length));
      if (alias) {
        var declaration = nodes.find(function (node) { return node.kind === "function" && node.text === alias[1]; });
        if (declaration) {
          start = declaration.spans[0].start;
          prefix = /^(?:export\s+)?(?:async\s+)?function\s+/.exec(source.slice(start));
          if (prefix) {
            var exported = /^export\s/.test(prefix[0]) ? "export " : "";
            var async = /\basync\b/.test(prefix[0]) ? "async " : "";
            edit = { start: start, length: prefix[0].length, replacement: exported + "const " + alias[1] + (language === "typescript" ? ": EndpointHandler" : "") + " = " + async + "function " };
          }
        }
      } else {
        var named = /^(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(/.exec(body);
        var binding = named ? named[1] : "__sbox_ns_handler";
        if (!named) {
          while (source.indexOf(binding) >= 0) binding += "_";
          privateBinding = binding;
        }
        edit = { start: start, length: prefix[0].length, replacement: "const " + binding + (language === "typescript" ? ": EndpointHandler" : "") + " = " };
      }
    }
    if (edit && language === "javascript") edit.replacement = "/** @type {EndpointHandler} */\n" + edit.replacement;
    var checked = edit ? source.slice(0, edit.start) + edit.replacement + source.slice(edit.start + edit.length) : source;
    return {
      source: checked + "\nexport {};\n",
      privateBinding: privateBinding,
      toChecked: function (offset) {
        if (!edit || offset < edit.start) return offset;
        if (offset < edit.start + edit.length) return edit.start;
        return offset + edit.replacement.length - edit.length;
      },
      toSource: function (offset) {
        if (edit && offset >= edit.start) offset = offset < edit.start + edit.replacement.length
          ? edit.start : offset - edit.replacement.length + edit.length;
        return offset > source.length ? null : offset;
      }
    };
  }

  function install(monaco, editor) {
    var checkedModel = null;
    var cached = null;
    var timer = null;
    var disposed = false;
    var registrations = [];
    var notice = document.getElementById("monaco-status");
    var unavailable = "Typed checking is unavailable. Source checks still run automatically; saving remains server-validated.";

    function current(result) {
      return result && !disposed && editor.getModel() === result.model && !result.model.isDisposed() && result.model.getVersionId() === result.version;
    }

    function failure() {
      if (notice) { notice.textContent = unavailable; notice.hidden = false; }
    }

    function projection() {
      var model = editor.getModel();
      if (!model || model.isDisposed()) return Promise.resolve(null);
      var version = model.getVersionId();
      if (cached && cached.model === model && cached.version === version) return cached.promise;
      var state = { model: model, version: version };
      state.promise = (async function () {
        var language = model.getLanguageId();
        var source = model.getValue();
        var getWorker = await (language === "typescript" ? monaco.languages.typescript.getTypeScriptWorker() : monaco.languages.typescript.getJavaScriptWorker());
        var worker = await getWorker(model.uri);
        if (await worker.getScriptVersion(model.uri.toString()) !== String(version)) {
          schedule();
          return null;
        }
        var tree = await worker.getNavigationTree(model.uri.toString());
        if (!current(state)) return null;
        var mapping = project(source, language, tree);
        if (!checkedModel || checkedModel.getLanguageId() !== language) {
          if (checkedModel) checkedModel.dispose();
          checkedModel = monaco.editor.createModel(mapping.source, language, monaco.Uri.parse("file:///owner/.typed-endpoint." + (language === "typescript" ? "ts" : "js")));
        } else if (checkedModel.getValue() !== mapping.source) {
          checkedModel.setValue(mapping.source);
        }
        var checkedVersion = checkedModel.getVersionId();
        var checkedWorker = await getWorker(checkedModel.uri);
        if (!current(state)) return null;
        if (await checkedWorker.getScriptVersion(checkedModel.uri.toString()) !== String(checkedVersion)) {
          schedule();
          return null;
        }
        return Object.assign(state, mapping, { worker: checkedWorker, uri: checkedModel.uri.toString() });
      })();
      cached = state;
      return state.promise;
    }

    async function validate() {
      try {
        var p = await projection();
        if (!current(p)) return;
        var results = await Promise.all([p.worker.getSyntacticDiagnostics(p.uri), p.worker.getSemanticDiagnostics(p.uri)]);
        if (!current(p)) return;
        var markers = [];
        results.flat().forEach(function (diagnostic) {
          var start = p.toSource(diagnostic.start || 0);
          var end = p.toSource((diagnostic.start || 0) + (diagnostic.length || 1));
          if (start === null || end === null) return;
          var from = p.model.getPositionAt(start), to = p.model.getPositionAt(Math.max(start + 1, end));
          markers.push({
            severity: diagnostic.category === 1 ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
            code: String(diagnostic.code), message: message(diagnostic.messageText),
            startLineNumber: from.lineNumber, startColumn: from.column,
            endLineNumber: to.lineNumber, endColumn: to.column
          });
        });
        monaco.editor.setModelMarkers(p.model, OWNER, markers);
        if (notice && notice.textContent === unavailable) notice.hidden = true;
      } catch (error) { if (!disposed) failure(); }
    }

    function schedule() {
      clearTimeout(timer);
      cached = null;
      var model = editor.getModel();
      if (model && !model.isDisposed()) monaco.editor.setModelMarkers(model, OWNER, []);
      timer = setTimeout(validate, 350);
    }

    function range(model, span, p, position) {
      if (span) {
        var start = p.toSource(span.start), end = p.toSource(span.start + span.length);
        if (start !== null && end !== null) {
          var from = model.getPositionAt(start), to = model.getPositionAt(end);
          return new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column);
        }
      }
      var word = model.getWordUntilPosition(position);
      return new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
    }

    ["javascript", "typescript"].forEach(function (language) {
      registrations.push(monaco.languages.registerCompletionItemProvider(language, {
        triggerCharacters: [".", "\"", "'"],
        provideCompletionItems: async function (model, position, context, token) {
          if (model !== editor.getModel()) return { suggestions: [] };
          try {
            var p = await projection();
            if (!current(p) || token.isCancellationRequested) return { suggestions: [] };
            var offset = p.toChecked(model.getOffsetAt(position));
            var result = await p.worker.getCompletionsAtPosition(p.uri, offset);
            if (!current(p) || token.isCancellationRequested || !result) return { suggestions: [] };
            return { suggestions: result.entries.filter(function (entry) { return entry.name !== p.privateBinding; }).map(function (entry) {
              return {
                label: entry.name, insertText: entry.insertText || entry.name,
                kind: /method|function/i.test(entry.kind) ? monaco.languages.CompletionItemKind.Function : monaco.languages.CompletionItemKind.Field,
                range: range(model, entry.replacementSpan || result.optionalReplacementSpan, p, position),
                sortText: "0" + entry.sortText, detail: entry.kind,
                _typed: { offset: offset, version: p.version }
              };
            }) };
          } catch (error) { failure(); return { suggestions: [] }; }
        },
        resolveCompletionItem: async function (item, token) {
          try {
            var p = await projection();
            if (!current(p) || token.isCancellationRequested || !item._typed || item._typed.version !== p.version) return item;
            var details = await p.worker.getCompletionEntryDetails(p.uri, item._typed.offset, item.label);
            if (details && current(p)) { item.detail = text(details.displayParts); item.documentation = documentation(details); }
          } catch (error) { failure(); }
          return item;
        }
      }));
      registrations.push(monaco.languages.registerHoverProvider(language, {
        provideHover: async function (model, position, token) {
          if (model !== editor.getModel()) return null;
          try {
            var p = await projection();
            if (!current(p) || token.isCancellationRequested) return null;
            var details = await p.worker.getQuickInfoAtPosition(p.uri, p.toChecked(model.getOffsetAt(position)));
            if (!details || !current(p) || token.isCancellationRequested) return null;
            return { range: range(model, details.textSpan, p, position), contents: [{ value: "````typescript\n" + text(details.displayParts) + "\n````", isTrusted: false }, documentation(details)] };
          } catch (error) { failure(); return null; }
        }
      }));
      registrations.push(monaco.languages.registerSignatureHelpProvider(language, {
        signatureHelpTriggerCharacters: ["(", ","],
        signatureHelpRetriggerCharacters: [")"],
        provideSignatureHelp: async function (model, position, token) {
          if (model !== editor.getModel()) return null;
          try {
            var p = await projection();
            if (!current(p) || token.isCancellationRequested) return null;
            var help = await p.worker.getSignatureHelpItems(p.uri, p.toChecked(model.getOffsetAt(position)));
            if (!help || !current(p) || token.isCancellationRequested) return null;
            return {
              value: {
                activeSignature: help.selectedItemIndex,
                activeParameter: help.argumentIndex,
                signatures: help.items.map(function (item) {
                  return {
                    label: text(item.prefixDisplayParts) + item.parameters.map(function (parameter) { return text(parameter.displayParts); }).join(text(item.separatorDisplayParts)) + text(item.suffixDisplayParts),
                    documentation: documentation(item),
                    parameters: item.parameters.map(function (parameter) {
                      return { label: text(parameter.displayParts), documentation: text(parameter.documentation) };
                    })
                  };
                })
              },
              dispose: function () {}
            };
          } catch (error) { failure(); return null; }
        }
      }));
    });
    registrations.push(editor.onDidChangeModelContent(schedule));
    registrations.push(editor.onDidChangeModel(schedule));
    editor.onDidDispose(function () {
      disposed = true;
      clearTimeout(timer);
      registrations.forEach(function (item) { item.dispose(); });
      if (checkedModel) checkedModel.dispose();
    });
    schedule();
  }

  global.OwnerScriptLanguage = { install: install };
})(window);
