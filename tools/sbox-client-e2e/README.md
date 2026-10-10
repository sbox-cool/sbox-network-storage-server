# s&box client end-to-end check

Runs the real s&box Network Storage client library
([sbox-cool/sbox-network-storage](https://github.com/sbox-cool/sbox-network-storage)) against a
freshly built `sbox-ns`, inside the s&box engine, without a window or a Steam login.

```sh
tools/sbox-client-e2e/run.sh --engine ~/Projects/sbox-public/game
```

It needs a built engine from [Facepunch/sbox-public](https://github.com/Facepunch/sbox-public)
(`./Setup.sh`; Windows, Linux x64 or Apple Silicon) and the .NET 10 SDK. Without `--library` the
script clones the client library's default branch; pass `--library <checkout>` to test a branch.

What it does:

1. Builds this repository and starts `sbox-ns` on `127.0.0.1:8080` with a throwaway config and data
   folder. Game code may only reach loopback on ports 80, 443, 8080 or 8443, so `--port` accepts
   only those.
2. Creates a project with s&box auth disabled (a headless engine has no player token) and pushes the
   game values, collections, endpoints and queries from `tests/parity/corpus/01-management-push.json`.
   Package sync is left out so the revision handshake runs against a project without a game package.
3. Compiles the library's `Code/` against the engine and runs `runner/ClientAlignmentTests.cs` in a
   headless engine (MSTest + `TestAppSystem`): revision handshake, document save/update/read/delete,
   endpoints, game values and queries, all through the library's own HTTP stack.
4. Starts `sbox-server` with the `game/` project (library under `Libraries/`) and the dedicated
   secret key, and checks a secret-key document write and read.

The script prints `CLIENT E2E PASS` and exits 0, or prints the server log tail and exits 1.

Not covered: real s&box token verification (needs a logged-in client), encrypted endpoint envelopes
and auth sessions. Server tests and the parity corpus cover those routes on the HTTP level.
