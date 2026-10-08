# Pointing your s&box game at a self-hosted server

sbox Network Storage Server speaks the same HTTP API as the managed service at
[sbox.cool](https://sbox.cool/tools/network-storage). The published s&box
client library works without changes: you only change its base URL.

- s&box library: <https://sbox.game/sboxcool/network-storage>
- Client source: <https://github.com/sbox-cool/sbox-network-storage>
- API and library docs: <https://sbox.cool/wiki/network-storage-v3>

The client appends the API version itself (`/v3/...`), so the base URL is just
the scheme, host and port of your server, with no path and no trailing slash.

## 1. Find your server URL

| How players reach the server | Base URL |
| --- | --- |
| Domain with HTTPS (`tls.mode` = `acme` or `certificate`, or a reverse proxy) | `https://ns.example.com` |
| Domain over plain HTTP | `http://ns.example.com:8080` |
| Public IP and port | `http://203.0.113.10:8080` |
| Same machine as the game (development) | `http://localhost:8080` |

Check the server responds from the machine running s&box:

```sh
curl -i http://your-server:8080/health
curl http://your-server:8080/v3/server-info
```

Make sure the port is open in your firewall and forwarded on your router if the
server is behind NAT.

## 2. Create a project and API keys

The server has no web dashboard yet. On the server, use the CLI:

```sh
sbox-ns project create "My Game"                          # prints the project ID
sbox-ns key create <projectId> --type public --label game
sbox-ns key create <projectId> --type secret --label editor
```

## 3. Configure the editor

1. Open your game in the s&box editor.
2. Open **Editor > Network Storage > Setup**.
3. Enter the **Project ID**, **Public API Key** and **Secret Key** from step 2.
4. Set **Base URL** to your server URL from step 1.
5. Leave **CDN URL** empty unless you serve a CDN in front of your server.
6. Save, then use the Sync Tool as usual to push collections, endpoints and workflows.

The Setup window stores the base URL in your project's Network Storage
credentials, and the generated config is picked up automatically by
`NetworkStorage.AutoConfigure()` at runtime.

## 4. Or configure in code

```csharp
NetworkStorage.Configure( projectId, apiKey, "http://your-server:8080" );
```

`Configure` also accepts an optional API version and CDN URL, exactly as with the
managed service. Only pass the public API key in game code; the secret key
belongs in the editor only.

## Client reachability

Whether s&box allows game clients to call a given kind of URL has **not been
tested yet** against a self-hosted server. Every row below is untested. Please
report what works for you in an issue so this table can be filled in.

| Server URL type | Example | Status |
| --- | --- | --- |
| HTTPS domain | `https://ns.example.com` | **untested** |
| HTTP domain | `http://ns.example.com:8080` | **untested** |
| IP address and port | `http://203.0.113.10:8080` | **untested** |
| localhost | `http://localhost:8080` | **untested** |

If plain HTTP or bare IP addresses turn out to be blocked by the s&box sandbox,
put the server behind a reverse proxy with a real domain and a TLS certificate
(see [self-hosting.md](self-hosting.md#https)).

## Moving an existing project from the managed service

1. Create the project and keys on your server with `sbox-ns project create` and `sbox-ns key create`.
2. Point the editor Setup window at your server (above) and sync your
   `Editor/Network Storage/` source files with the Sync Tool to recreate
   collections, endpoints and workflows.
3. Publish a new build of your game.

Player data stored on the managed service is not copied by syncing. Moving
existing player records is not covered by this guide yet.

Players running an older build keep talking to whichever server that build was
configured for.
