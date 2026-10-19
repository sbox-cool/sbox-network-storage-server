/* Owner console syntax highlighter. Dependency-free. Output is built with
 * createTextNode/createElement only; content is never parsed as HTML. */
(function (root, factory) {
  const api = factory(root);
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.OwnerHighlight = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function (root) {
  "use strict";

  function words(list) {
    return new RegExp("\\b(?:" + list.join("|") + ")(?![\\w$])");
  }

  const JS_KEYWORDS = [
    "abstract", "as", "async", "await", "break", "case", "catch", "class", "const", "continue",
    "debugger", "declare", "default", "delete", "do", "else", "enum", "export", "extends", "finally",
    "for", "from", "function", "if", "implements", "import", "in", "instanceof", "interface", "keyof",
    "let", "namespace", "new", "of", "private", "protected", "public", "readonly", "return", "satisfies",
    "static", "super", "switch", "this", "throw", "try", "typeof", "var", "void", "while", "with", "yield",
  ];
  const JS_BUILTINS = [
    "ns", "req", "res", "ctx", "console", "Math", "JSON", "Object", "Array", "String", "Number",
    "Promise", "Date", "Map", "Set", "RegExp", "Error", "process", "window", "document",
  ];
  const CS_KEYWORDS = [
    "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "class",
    "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit",
    "extern", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
    "internal", "is", "lock", "long", "namespace", "new", "object", "operator", "out", "override", "params",
    "partial", "private", "protected", "public", "readonly", "ref", "required", "return", "sbyte", "sealed",
    "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "try", "typeof",
    "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while",
    "yield",
  ];
  const SH_COMMANDS = [
    "sbox-ns", "ns", "dotnet", "npm", "npx", "git", "curl", "sudo", "docker", "echo", "cat", "cd", "ls",
    "grep", "sed", "awk", "chmod", "mkdir", "rm", "cp", "mv", "kill", "sh", "bash", "pwsh", "powershell",
  ];
  const SH_KEYWORDS = [
    "if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case", "esac", "in",
    "function", "return", "export", "local", "unset", "readonly", "source", "alias", "exit",
  ];
  const GENERIC_KEYWORDS = [
    "async", "await", "class", "const", "else", "export", "for", "function", "if", "import", "let",
    "new", "return", "static", "var", "while",
  ];

  // Each rule: [tokenType, RegExp, options?]. Patterns use only non-capturing groups and never match empty.
  // Options: { sub: "javascript" } tokenizes the matched {{ }} expression;
  //          { templates: true } splits {{ }} expressions out of a quoted scalar;
  //          { guard: fn(prev) } gates a match (used for regex literals).
  const REGEX_GUARD = { guard: regexAllowed };
  const SUB_JS = { sub: "javascript" };
  const TEMPLATED = { templates: true };

  const RULES = {
    json: [
      ["property", /"(?:[^"\\\n]|\\.)*"(?=\s*:)/],
      ["string", /"(?:[^"\\\n]|\\.)*"/],
      ["number", /-?\b\d+(?:\.\d+)?(?:[eE][-+]?\d+)?\b/],
      ["boolean", /\b(?:true|false|null)\b/],
      ["punctuation", /[{}\[\],:]/],
    ],
    yaml: [
      ["comment", /#[^\n]*/],
      ["template", /\{\{[\s\S]*?\}\}/, SUB_JS],
      ["property", /(?<=^[ \t]*(?:- +)?)(?:"[^"\n]*"|'[^'\n]*'|[^\s#'"\-][^:\n#]*?)(?=:(?:[ \t]|$))/],
      ["string", /"[^"\n]*\{\{[^"\n]*"|'[^'\n]*\{\{[^'\n]*'/, TEMPLATED],
      ["string", /"(?:[^"\\\n]|\\.)*"|'(?:[^'\n]|'')*'/],
      ["boolean", /\b(?:true|false|null|yes|no|on|off)(?![\w-])/],
      ["number", /-?\b\d+(?:\.\d+)?\b/],
      ["punctuation", /[\[\]{},]/],
      // Unquoted scalar values (after "key: " or a "- " list marker) read as strings, as in
      // VS Code. Earlier rules win ties, so keys, numbers, booleans, quotes and {{ }} keep
      // their own colours; block/flow indicators and anchors are left alone.
      ["string", /(?<=:[ \t]+|^[ \t]*-[ \t]+)(?![\[{|>&*!#"'\s])[^\n#{]*?(?=[ \t]+#|[ \t]*$|[ \t]*\{\{)/],
    ],
    javascript: [
      ["comment", /\/\/[^\n]*|\/\*[\s\S]*?(?:\*\/|(?![\s\S]))/],
      ["string", /`(?:[^`\\]|\\[\s\S])*`|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/],
      ["regex", /\/(?![*\/])(?:\\.|\[(?:\\.|[^\]\\\n])*\]|[^\/\\\n])+\/[dgimsuvy]*/, REGEX_GUARD],
      ["number", /\b(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][-+]?\d+)?)n?\b/],
      ["keyword", words(JS_KEYWORDS)],
      ["boolean", /\b(?:true|false|null|undefined|NaN|Infinity)\b/],
      ["builtin", words(JS_BUILTINS)],
      ["function", /[A-Za-z_$][\w$]*(?=\s*\()/],
      ["type", /\b[A-Z][\w$]*/],
      ["variable", /[A-Za-z_$][\w$]*/],
      ["operator", /=>|===|!==|==|!=|<=|>=|&&|\|\||\?\?|\?\.|\+\+|--|[-+*\/%&|^!~<>=?]/],
      ["punctuation", /[()[\]{};,.:]/],
    ],
    csharp: [
      ["comment", /\/\/[^\n]*|\/\*[\s\S]*?(?:\*\/|(?![\s\S]))/],
      ["string", /@"(?:[^"]|"")*"|\$?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/],
      ["keyword", /#\w+/],
      ["number", /\b(?:0[xX][0-9a-fA-F_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][-+]?\d+)?)[fFdDmMlLuU]*\b/],
      ["keyword", words(CS_KEYWORDS)],
      ["boolean", /\b(?:true|false|null)\b/],
      ["function", /[A-Za-z_]\w*(?=\s*[(<])/],
      ["type", /\b[A-Z]\w*/],
      ["variable", /[A-Za-z_]\w*/],
      ["operator", /=>|==|!=|<=|>=|&&|\|\||\?\?|\+\+|--|[-+*\/%&|^!~<>=?]/],
      ["punctuation", /[()[\]{};,.:]/],
    ],
    shell: [
      ["comment", /#[^\n]*/],
      ["string", /"(?:[^"\\\n]|\\.)*"|'[^'\n]*'/],
      ["variable", /\$\{[^}\n]*\}|\$[A-Za-z_]\w*|\$[0-9?#@*!$]/],
      ["keyword", words(SH_KEYWORDS)],
      ["builtin", new RegExp("\\b(?:" + SH_COMMANDS.join("|") + ")(?![\\w-])")],
      ["property", /(?:^|\s)--?[A-Za-z][\w-]*/],
      ["number", /\b\d+\b/],
      ["operator", /\|\||&&|>>|[|&;<>=]/],
      ["punctuation", /[()[\]{}]/],
    ],
    toml: [
      ["comment", /#[^\n]*/],
      ["type", /^[ \t]*\[\[?[^\]\n]+\]\]?/],
      ["string", /"""[\s\S]*?"""|'''[\s\S]*?'''|"(?:[^"\\\n]|\\.)*"|'[^'\n]*'/],
      ["property", /^[ \t]*[A-Za-z0-9_.\-"']+(?=[ \t]*=)/],
      ["boolean", /\b(?:true|false)\b/],
      ["number", /[-+]?\b\d[\d_:.\-TZ+]*/],
      ["operator", /=/],
      ["punctuation", /[\[\]{},.]/],
    ],
    generic: [
      ["string", /`(?:[^`\\]|\\[\s\S])*`|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/],
      ["boolean", /\b(?:true|false|null|undefined)(?![\w-])/],
      ["keyword", words(GENERIC_KEYWORDS)],
      ["builtin", /\b(?:ns|req|res|ctx)(?![\w-])/],
      ["number", /-?\b\d+(?:\.\d+)?\b/],
      ["property", /(?:^|\s)--?[A-Za-z][\w-]*/],
      ["function", /[A-Za-z_$][\w$]*(?=\()/],
      ["operator", /=+/],
      ["punctuation", /[()[\]{};,.:]/],
    ],
  };

  const ALIASES = {
    yaml: "yaml", yml: "yaml",
    json: "json", jsonc: "json", json5: "json",
    javascript: "javascript", js: "javascript", mjs: "javascript", cjs: "javascript", jsx: "javascript",
    typescript: "javascript", ts: "javascript", tsx: "javascript",
    csharp: "csharp", cs: "csharp", "c#": "csharp",
    shell: "shell", sh: "shell", bash: "shell", zsh: "shell", console: "shell", powershell: "shell", pwsh: "shell",
    toml: "toml",
    generic: "generic",
  };
  const PLAIN = { text: true, plain: true, plaintext: true, none: true, txt: true };

  const compiled = {};

  function compile(name) {
    if (!compiled[name]) {
      const rules = RULES[name];
      const source = rules.map(function (r) { return "(" + r[1].source + ")"; }).join("|");
      compiled[name] = {
        rules: rules.map(function (r) { return { type: r[0], opts: r[2] || null }; }),
        re: new RegExp(source, "gm"),
      };
    }
    return compiled[name];
  }

  // Returns the canonical grammar name, or null for plain (unhighlighted) text.
  function normalizeLanguage(name) {
    const key = String(name || "").trim().toLowerCase();
    if (!key || PLAIN[key]) return null;
    return ALIASES[key] || "generic";
  }

  // A "/" starts a regex literal only where an operand is expected.
  function regexAllowed(prev) {
    if (!prev) return true;
    if (prev.type === "keyword" || prev.type === "operator") return true;
    if (prev.type === "punctuation") return !/[)\]}]/.test(prev.text);
    if (prev.type === null) return /[(,=:[!&|?{};+\-*%<>~^]/.test(prev.text);
    return false;
  }

  function pushPlain(out, text) {
    if (!text) return;
    const last = out[out.length - 1];
    if (last && last.type === null) last.text += text;
    else out.push({ type: null, text: text });
  }

  function pushToken(out, type, text) {
    if (!text) return;
    if (type === null) pushPlain(out, text);
    else out.push({ type: type, text: text });
  }

  function appendAll(out, tokens) {
    for (const token of tokens) {
      if (token.type === null) pushPlain(out, token.text);
      else out.push(token);
    }
  }

  // Emits a {{ expression }}: delimiters as template, contents tokenized as javascript.
  function emitExpression(out, text) {
    pushToken(out, "template", text.slice(0, 2));
    appendAll(out, tokenizeWith("javascript", text.slice(2, -2)));
    pushToken(out, "template", text.slice(-2));
  }

  // Splits a quoted scalar around its {{ }} expressions; the rest keeps plainType.
  function emitTemplated(out, text, plainType) {
    const re = /\{\{[\s\S]*?\}\}/g;
    let last = 0;
    let m;
    while ((m = re.exec(text)) !== null) {
      pushToken(out, plainType, text.slice(last, m.index));
      emitExpression(out, m[0]);
      last = m.index + m[0].length;
    }
    pushToken(out, plainType, text.slice(last));
  }

  function lastSignificant(gap, prev) {
    const trimmed = gap.replace(/\s+$/, "");
    if (!trimmed) return prev;
    return { type: null, text: trimmed.charAt(trimmed.length - 1) };
  }

  function run(grammar, text) {
    const out = [];
    const re = grammar.re;
    let last = 0;
    let prev = null;
    let m;
    re.lastIndex = 0;
    while ((m = re.exec(text)) !== null) {
      const matched = m[0];
      if (matched.length === 0) {
        re.lastIndex = m.index + 1;
        continue;
      }
      let i = 0;
      while (m[i + 1] === undefined) i++;
      const rule = grammar.rules[i];
      if (rule.opts && rule.opts.guard && !rule.opts.guard(prev)) {
        re.lastIndex = m.index + 1;
        continue;
      }
      if (m.index > last) {
        const gap = text.slice(last, m.index);
        pushPlain(out, gap);
        prev = lastSignificant(gap, prev);
      }
      if (rule.opts && rule.opts.sub) {
        emitExpression(out, matched);
        prev = { type: "template", text: matched.slice(-2) };
      } else if (rule.opts && rule.opts.templates) {
        emitTemplated(out, matched, rule.type);
        prev = { type: rule.type, text: matched };
      } else {
        pushToken(out, rule.type, matched);
        if (rule.type !== "comment" && rule.type !== null) prev = { type: rule.type, text: matched };
      }
      last = m.index + matched.length;
      re.lastIndex = last;
    }
    if (last < text.length) pushPlain(out, text.slice(last));
    return out;
  }

  function tokenizeWith(name, text) {
    return run(compile(name), String(text));
  }

  // Splits text into [{ type, text }] tokens. type is null for plain text.
  // Concatenating every token's text reproduces the input exactly.
  function tokenize(text, language) {
    const name = normalizeLanguage(language);
    if (!name) return [{ type: null, text: String(text) }];
    return tokenizeWith(name, text);
  }

  function render(target, text, language) {
    const doc = target.ownerDocument || root.document;
    const frag = doc.createDocumentFragment();
    for (const token of tokenize(text, language)) {
      if (token.type === null) {
        frag.appendChild(doc.createTextNode(token.text));
      } else {
        const span = doc.createElement("span");
        span.className = "syntax-" + token.type;
        span.textContent = token.text;
        frag.appendChild(span);
      }
    }
    while (target.firstChild) target.removeChild(target.firstChild);
    target.appendChild(frag);
  }

  // Inline code gets light colouring only when it reads as code: member access, calls,
  // assignments, namespaces, paths, flags or known CLI commands. Prose stays plain.
  const CODE_HINT = /\w\.\w|\w\(|\w\/\w|[=\\]|::|(?:^|\s)[\/]?[\w.-]*\/[\w.-]|(?:^|\s)--?[A-Za-z]/;
  const CODE_COMMAND = new RegExp("^(?:" + SH_COMMANDS.join("|") + ")(?![\\w-])");
  function looksLikeCode(text) {
    const t = String(text || "").trim();
    if (!t) return false;
    if (CODE_COMMAND.test(t)) return true;
    if (/\s/.test(t)) return /\w\(|=|::|(?:^|\s)--?[A-Za-z]/.test(t);
    return CODE_HINT.test(t);
  }

  function languageOf(pre, code) {
    if (pre.dataset && pre.dataset.language) return pre.dataset.language;
    if (code) {
      if (code.dataset && code.dataset.language) return code.dataset.language;
      const match = /(?:^|\s)language-([\w#+-]+)/.exec(code.className || "");
      if (match) return match[1];
    }
    if (pre.classList.contains("json")) return "json";
    return null;
  }

  // Highlights every block and inline code under scope (an element or document).
  // Blocks with element children other than a single <code> are skipped so controls survive.
  function highlightAll(scope) {
    const base = scope || root.document;
    for (const pre of base.querySelectorAll("pre")) {
      const code = pre.querySelector(":scope > code");
      if (pre.children.length > (code ? 1 : 0)) continue;
      const target = code || pre;
      const language = languageOf(pre, code);
      if (language) render(target, target.textContent, language);
    }
    for (const code of base.querySelectorAll("code")) {
      if (code.closest("pre") || code.children.length > 0) continue;
      if (code.dataset && code.dataset.language) continue;
      const text = code.textContent;
      if (looksLikeCode(text)) render(code, text, "generic");
    }
  }

  return {
    normalizeLanguage: normalizeLanguage,
    tokenize: tokenize,
    render: render,
    highlightAll: highlightAll,
    looksLikeCode: looksLikeCode,
  };
});
