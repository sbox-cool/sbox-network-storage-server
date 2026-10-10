---
name: sbox-ns-setup
description: Install or connect a self-hosted s&box Network Storage server (sbox-ns), create a project and keys, and connect an s&box game to it. Use when the user wants to set up sbox-ns, get their NetworkStorage.Configure line, or fix a game that cannot reach the server.
---

# Set up sbox-ns and connect a game

sbox-ns is a self-hosted backend for s&box games. The game uses the existing
Network Storage library and only changes its base URL. Guide:
`sbox-ns://docs/client-setup` (MCP resource) or docs/client-setup.md.

## 1. Is the server reachable through MCP?

Call `server_status`. If the `sbox-ns` MCP tools are missing:

- Server on this computer or a fresh VPS: install it with
  `curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | sudo sh`
  (see the README for Windows and options), then the plugin's `sbox-ns mcp` works.
- Server on a VPS: tell the user to run
  `claude mcp add sbox-ns -- ssh <host> sudo sbox-ns mcp`. SSH must use a key, and
  sudo must not ask for a password.

Explain any line of `server_status` that is not OK in plain words.

## 2. Create the project

Call `quickstart` with the game's name. For a project used only for local
testing without s&box sign-in, pass `requireSboxAuth: false` and say why.
Real games keep the default (true).

From the result, give the user:

1. The `NetworkStorage.Configure(...)` line. It goes in the game's startup
   code, for example a GameObjectSystem or the first scene's component.
2. The secret key (only shown when newly created). It goes only in
   **Editor > Network Storage > Setup** in the s&box editor. It must never be
   in game code or committed to git.

## 3. Check the address

Call `client_snippet` and act on its notes:

- `localhost` works only on this computer. Players on other computers need
  `server.public_url` set to an address they can reach (`config_set`, then
  `service_restart`).
- Plain HTTP may be blocked by s&box outside the editor. The easiest HTTPS is
  `tunnel_enable` (hosted address, no domain needed), then `service_restart`.

## 4. Did the game reach the server?

After the user runs the game, call `logs_requests`. No rows means the game
never reached the server: check firewall, port forwarding and the base URL.
401 rows mean a wrong key, a disabled key, or a project that requires s&box
sign-in while the client has no token. Use the `explanation` field.

## 5. The dashboard

`dashboard_link` gives a one-time sign-in link (it is a credential; keep it to
the user). Over plain HTTP from another computer the dashboard refuses
sign-in; the page explains the SSH port forward, tunnel and TLS options.
