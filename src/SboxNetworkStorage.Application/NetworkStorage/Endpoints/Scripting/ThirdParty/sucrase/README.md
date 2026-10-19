# Vendored sucrase type-stripper

`strip.js` is the TypeScript type-stripper the server runs inside an isolated
Jint engine to turn `runtime: typescript` endpoint scripts into JavaScript. It is
embedded in the server assembly. It is not a dependency of the server build.

## Contents

| Package | Version | Licence |
| --- | --- | --- |
| sucrase | 3.35.1 | MIT |
| lines-and-columns | 1.2.4 | MIT |
| ts-interface-checker | 0.1.13 | Apache-2.0 |
| @jridgewell/gen-mapping | 0.3.13 | MIT |
| @jridgewell/trace-mapping | 0.3.31 | MIT |
| @jridgewell/resolve-uri | 3.1.2 | MIT |
| @jridgewell/sourcemap-codec | 1.6.0 | MIT |

`LICENSE.txt` reproduces each licence text, including the Apache-2.0 text for
ts-interface-checker.

## Files

- `strip-entry.js`: the entry point. It exposes `__nsTypeScript.strip(source)`.
- `package.json` and `package-lock.json`: the pinned build inputs. The direct
  versions are exact, and the lockfile pins every transitive package.
- `build.sh`: rebuilds `strip.js` from the entry and the lockfile.
- `strip.js`: the build output. Do not edit it by hand.

## Rebuilding

```sh
sh build.sh
```

The script needs Node.js 20 or later and npm. It installs the locked packages
into a temporary directory, bundles with esbuild 0.25.12 (`--bundle
--format=iife --platform=browser --target=es2020 --minify
--legal-comments=none`), and writes `strip.js`. Running it twice gives the same
bytes.

To change the sucrase version, edit `package.json`, run `npm install
--package-lock-only --ignore-scripts` in this folder, run `sh build.sh`, and
run the TypeScript endpoint tests. Update the table above in the same change.
