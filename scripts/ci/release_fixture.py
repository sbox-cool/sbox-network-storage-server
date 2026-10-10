#!/usr/bin/env python3
"""Local stand-in for GitHub Releases and the sboxcool.com release feed, for install and update tests.

The install matrix points github.com, api.github.com and sboxcool.com at 127.0.0.1 and trusts a
throwaway CA, so the real installer and updater run unmodified against releases signed with a
throwaway key. Releases live under ROOT/v<version>/; ROOT/latest holds the version the feed and
/releases/latest report.

  release_fixture.py serve --root DIR --cert CERT --key KEY [--port 443] [--repo owner/name]
"""
import argparse
import json
import os
import ssl
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    serve = sub.add_parser("serve")
    serve.add_argument("--root", required=True)
    serve.add_argument("--cert", required=True)
    serve.add_argument("--key", required=True)
    serve.add_argument("--port", type=int, default=443)
    serve.add_argument("--repo", default="sbox-cool/sbox-network-storage-server")
    args = parser.parse_args()

    root = os.path.abspath(args.root)
    repo = args.repo

    def latest():
        with open(os.path.join(root, "latest"), encoding="utf-8") as handle:
            return handle.read().strip()

    def release(version):
        directory = os.path.join(root, "v" + version)
        if not os.path.isdir(directory):
            return None
        names = sorted(os.listdir(directory))
        return {
            "tag_name": "v" + version,
            "name": "v" + version,
            "draft": False,
            "prerelease": "-" in version,
            "assets": [
                {"name": name, "browser_download_url": f"https://github.com/{repo}/releases/download/v{version}/{name}"}
                for name in names
            ],
        }

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *values):
            sys.stderr.write("fixture: %s %s\n" % (self.headers.get("Host", "?"), fmt % values))

        def send_json(self, value, status=200):
            body = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def send_file(self, path):
            if not os.path.isfile(path):
                return self.send_json({"message": "Not Found"}, 404)
            with open(path, "rb") as handle:
                body = handle.read()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            host = (self.headers.get("Host") or "").split(":")[0].lower()
            path = urlsplit(self.path).path
            if host == "sboxcool.com" and path == "/api/network-storage/releases/latest":
                manifest = os.path.join(root, "v" + latest(), "release.json")
                with open(manifest, encoding="utf-8") as handle:
                    feed = json.load(handle)
                feed["channel"] = "stable"
                return self.send_json(feed)
            if host == "api.github.com":
                prefix = f"/repos/{repo}/releases"
                if path == prefix + "/latest":
                    return self.send_json(release(latest()))
                if path.startswith(prefix + "/tags/v"):
                    found = release(path[len(prefix + "/tags/v"):])
                    return self.send_json(found) if found else self.send_json({"message": "Not Found"}, 404)
                if path == prefix:
                    versions = sorted(name[1:] for name in os.listdir(root) if name.startswith("v"))
                    return self.send_json([release(version) for version in versions])
            if host == "github.com":
                prefix = f"/{repo}/releases/"
                if path.startswith(prefix + "latest/download/"):
                    return self.send_file(os.path.join(root, "v" + latest(), path[len(prefix + "latest/download/"):]))
                if path.startswith(prefix + "download/v"):
                    rest = path[len(prefix + "download/"):]
                    tag, _, name = rest.partition("/")
                    if "/" not in name and ".." not in name:
                        return self.send_file(os.path.join(root, tag, name))
            return self.send_json({"message": "Not Found"}, 404)

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(args.cert, args.key)
    server = ThreadingHTTPServer(("0.0.0.0", args.port), Handler)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    print(f"release fixture serving {root} on :{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
