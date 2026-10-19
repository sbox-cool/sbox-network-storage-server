import { transform } from "./node_modules/sucrase/dist/esm/index.js";

// Entry point consumed by the server's Jint runtime. Takes TypeScript source text and returns a JSON envelope:
// {"ok":true,"code":"<javascript>"} or {"ok":false,"message":"...","line":N,"column":N} (1-based, TypeScript source).
globalThis.__nsTypeScript = {
  strip(source) {
    try {
      const code = transform(source, { transforms: ["typescript"], disableESTransforms: true }).code;
      return JSON.stringify({ ok: true, code });
    } catch (error) {
      const loc = error && error.loc;
      const message = String((error && error.message) || error).replace(/\s*\(\d+:\d+\)$/, "");
      return JSON.stringify({ ok: false, message, line: loc ? loc.line : 1, column: loc ? loc.column : 1 });
    }
  },
};
