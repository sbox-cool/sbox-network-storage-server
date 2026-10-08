#!/bin/sh
# sbox Network Storage Server installer for Linux and macOS.
#
#   curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | sh
#
# One line from a fresh VPS to a running server with a project and keys:
#
#   curl -fsSL https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.sh | SBOX_NS_PROJECT="My Game" sh
#
# Environment variables:
#   SBOX_NS_PROJECT      Configure non-interactively (SQLite), create or reuse this project with
#                        public and secret keys, and print the line to add to your game.
#   SBOX_NS_PUBLIC_URL   Address players use, e.g. https://ns.example.com (used with SBOX_NS_PROJECT).
#   SBOX_NS_VERSION      Install this version (for example 0.3.0 or v0.3.0) instead of the latest release.
#   SBOX_NS_PRERELEASE   Set to 1 to resolve the newest release including prereleases.
#   SBOX_NS_NO_SETUP     Set to 1 to skip running `sbox-ns setup`.
#   SBOX_NS_NO_SERVICE   Set to 1 to skip `sbox-ns service install` on systemd hosts.
#   GITHUB_TOKEN         Optional token used for GitHub API requests (avoids rate limits).
#
# Options:
#   --print-platform     Print the detected release platform (for example linux-x64) and exit.
#
# Licensed under AGPL-3.0-only. https://github.com/sbox-cool/sbox-network-storage-server

set -eu

REPO="sbox-cool/sbox-network-storage-server"
BIN_NAME="sbox-ns"
SERVICE_USER="sbox-ns"
LINUX_CONFIG_DIR="/etc/sbox-ns"
LINUX_DATA_DIR="/var/lib/sbox-ns"

say() {
    printf '%s\n' "$*"
}

warn() {
    printf 'warning: %s\n' "$*" >&2
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

has() {
    command -v "$1" >/dev/null 2>&1
}

# Maps an OS name ($1, as from `uname -s`) and CPU name ($2, as from `uname -m`)
# to a release platform identifier such as linux-x64 or osx-arm64.
detect_platform() {
    case "$1" in
        Linux | linux) _os="linux" ;;
        Darwin | darwin) _os="osx" ;;
        *) die "unsupported operating system: $1 (use the Docker image, or install.ps1 on Windows)" ;;
    esac

    case "$2" in
        x86_64 | amd64 | x64) _arch="x64" ;;
        aarch64 | arm64 | armv8*) _arch="arm64" ;;
        *) die "unsupported CPU architecture: $2 (supported: x64, arm64)" ;;
    esac

    printf '%s-%s\n' "$_os" "$_arch"
}

# Prints the release platform identifier for this machine.
host_platform() {
    _platform="$(detect_platform "$(uname -s)" "$(uname -m)")"
    # Rosetta 2 reports x86_64 for translated shells on Apple Silicon; prefer the native build.
    if [ "$_platform" = "osx-x64" ] && [ "$(sysctl -in sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
        _platform="osx-arm64"
    fi
    printf '%s\n' "$_platform"
}

http_get() {
    # $1 = url, $2 = output file ("-" for stdout)
    if has curl; then
        if [ -n "${GITHUB_TOKEN:-}" ] && case "$1" in https://api.github.com/*) true ;; *) false ;; esac; then
            curl -fsSL --proto '=https' --tlsv1.2 -H "Authorization: Bearer $GITHUB_TOKEN" -o "$2" "$1"
        else
            curl -fsSL --proto '=https' --tlsv1.2 -o "$2" "$1"
        fi
    elif has wget; then
        if [ -n "${GITHUB_TOKEN:-}" ] && case "$1" in https://api.github.com/*) true ;; *) false ;; esac; then
            wget -q --header="Authorization: Bearer $GITHUB_TOKEN" -O "$2" "$1"
        else
            wget -q -O "$2" "$1"
        fi
    else
        die "curl or wget is required"
    fi
}

resolve_version() {
    if [ -n "${SBOX_NS_VERSION:-}" ]; then
        printf '%s\n' "${SBOX_NS_VERSION#v}"
        return
    fi

    if [ "${SBOX_NS_PRERELEASE:-0}" = "1" ]; then
        _url="https://api.github.com/repos/$REPO/releases?per_page=1"
    else
        _url="https://api.github.com/repos/$REPO/releases/latest"
    fi

    _tag="$(http_get "$_url" - | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | sed -n '1p')"
    [ -n "$_tag" ] || die "could not resolve the latest release from $_url (set SBOX_NS_VERSION to pick one)"
    printf '%s\n' "${_tag#v}"
}

sha256_of() {
    if has sha256sum; then
        sha256sum "$1" | cut -d ' ' -f 1
    elif has shasum; then
        shasum -a 256 "$1" | cut -d ' ' -f 1
    else
        die "sha256sum or shasum is required to verify the download"
    fi
}

verify_checksum() {
    # $1 = archive path, $2 = SHA256SUMS path, $3 = archive file name
    _expected="$(awk -v f="$3" '{ name = $2; sub(/^\*/, "", name); if (name == f) { print $1; exit } }' "$2")"
    [ -n "$_expected" ] || die "$3 is not listed in SHA256SUMS; refusing to install"
    _actual="$(sha256_of "$1")"
    if [ "$_expected" != "$_actual" ]; then
        die "checksum mismatch for $3 (expected $_expected, got $_actual); refusing to install"
    fi
    say "Checksum verified: $_actual"
}

is_root() {
    [ "$(id -u)" -eq 0 ]
}

has_systemd() {
    [ -d /run/systemd/system ] && has systemctl
}

has_tty() {
    [ -t 1 ] && { : </dev/tty; } 2>/dev/null
}

ensure_service_user() {
    if id "$SERVICE_USER" >/dev/null 2>&1; then
        return
    fi
    say "Creating system user '$SERVICE_USER'"
    if has useradd; then
        useradd --system --home-dir "$LINUX_DATA_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER" \
            || useradd -r -d "$LINUX_DATA_DIR" -M -s /sbin/nologin "$SERVICE_USER"
    elif has adduser; then
        # BusyBox / Alpine
        adduser -S -D -H -h "$LINUX_DATA_DIR" -s /sbin/nologin "$SERVICE_USER"
    else
        die "cannot create user '$SERVICE_USER': neither useradd nor adduser is available"
    fi
}

main() {
    if [ "${1:-}" = "--print-platform" ]; then
        host_platform
        return
    fi

    platform="$(host_platform)"
    os="${platform%%-*}"
    version="$(resolve_version)"
    archive="$BIN_NAME-$version-$platform.tar.gz"
    base_url="https://github.com/$REPO/releases/download/v$version"

    # Choose the install layout.
    #   Linux as root:  /usr/local/bin/sbox-ns, config /etc/sbox-ns, data /var/lib/sbox-ns, systemd service.
    #   Otherwise:      <install dir>/sbox-ns with config/ and data/ beside it, linked into a bin dir on PATH.
    service_layout=0
    if is_root; then
        bin_dir="/usr/local/bin"
        if [ "$os" = "linux" ]; then
            service_layout=1
            install_dir="$bin_dir"
            config_dir="$LINUX_CONFIG_DIR"
            data_dir="$LINUX_DATA_DIR"
        else
            install_dir="/opt/sbox-ns"
            config_dir="$install_dir/config"
            data_dir="$install_dir/data"
        fi
    else
        bin_dir="$HOME/.local/bin"
        install_dir="${XDG_DATA_HOME:-$HOME/.local/share}/sbox-ns"
        config_dir="$install_dir/config"
        data_dir="$install_dir/data"
    fi

    say "Installing sbox Network Storage Server $version ($platform)"

    tmp="$(mktemp -d 2>/dev/null || mktemp -d -t sbox-ns)"
    trap 'rm -rf "$tmp"' EXIT INT TERM

    say "Downloading $archive"
    http_get "$base_url/$archive" "$tmp/$archive" || die "download failed: $base_url/$archive"
    http_get "$base_url/SHA256SUMS" "$tmp/SHA256SUMS" || die "download failed: $base_url/SHA256SUMS"
    verify_checksum "$tmp/$archive" "$tmp/SHA256SUMS" "$archive"

    mkdir -p "$tmp/extract"
    tar -xzf "$tmp/$archive" -C "$tmp/extract"
    [ -f "$tmp/extract/$BIN_NAME" ] || die "archive does not contain $BIN_NAME"

    mkdir -p "$install_dir" "$bin_dir"
    # Install via a temporary name and rename, so a running binary is replaced atomically.
    cp "$tmp/extract/$BIN_NAME" "$install_dir/.$BIN_NAME.new"
    chmod 0755 "$install_dir/.$BIN_NAME.new"
    mv -f "$install_dir/.$BIN_NAME.new" "$install_dir/$BIN_NAME"
    if [ "$install_dir" != "$bin_dir" ]; then
        ln -sf "$install_dir/$BIN_NAME" "$bin_dir/$BIN_NAME"
    fi
    if [ "$os" = "osx" ] && has xattr; then
        xattr -d com.apple.quarantine "$install_dir/$BIN_NAME" 2>/dev/null || true
    fi
    say "Installed $bin_dir/$BIN_NAME"

    if [ "$service_layout" -eq 1 ]; then
        ensure_service_user
        mkdir -p "$config_dir" "$data_dir"
    else
        mkdir -p "$config_dir" "$data_dir"
    fi

    ns="$install_dir/$BIN_NAME"
    dirs="--config-dir $config_dir --data-dir $data_dir"
    setup_cmd="$BIN_NAME setup $dirs"

    quickstart_out=""
    if [ -n "${SBOX_NS_PROJECT:-}" ]; then
        say ""
        say "Configuring the server and creating project \"$SBOX_NS_PROJECT\"..."
        # quickstart configures non-interactively when no config exists and is safe to re-run.
        if [ -n "${SBOX_NS_PUBLIC_URL:-}" ]; then
            # shellcheck disable=SC2086 # $dirs is intentionally split into flags
            quickstart_out=$("$ns" quickstart "$SBOX_NS_PROJECT" $dirs --public-url "$SBOX_NS_PUBLIC_URL") || die "quickstart failed; rerun: $BIN_NAME quickstart \"$SBOX_NS_PROJECT\" $dirs"
        else
            # shellcheck disable=SC2086 # $dirs is intentionally split into flags
            quickstart_out=$("$ns" quickstart "$SBOX_NS_PROJECT" $dirs) || die "quickstart failed; rerun: $BIN_NAME quickstart \"$SBOX_NS_PROJECT\" $dirs"
        fi
        configured=1
    elif [ -f "$config_dir/server.toml" ]; then
        say "Existing configuration found in $config_dir; keeping it."
        configured=1
    elif [ "${SBOX_NS_NO_SETUP:-0}" = "1" ]; then
        configured=0
    elif has_tty; then
        say ""
        say "Starting interactive setup..."
        # shellcheck disable=SC2086 # $dirs is intentionally split into flags
        "$ns" setup $dirs </dev/tty
        configured=1
    else
        configured=0
    fi

    if [ "$service_layout" -eq 1 ]; then
        chown -R "$SERVICE_USER:$SERVICE_USER" "$config_dir" "$data_dir"
        chmod 0750 "$config_dir" "$data_dir"
    fi

    service_installed=0
    if [ "$service_layout" -eq 1 ] && [ "$configured" -eq 1 ] && has_systemd && [ "${SBOX_NS_NO_SERVICE:-0}" != "1" ]; then
        say "Installing systemd service"
        # shellcheck disable=SC2086 # $dirs is intentionally split into flags
        if "$ns" service install $dirs; then
            service_installed=1
            "$ns" service start || warn "service start failed; check '$BIN_NAME logs'"
        else
            warn "service install failed; run '$BIN_NAME service install $dirs' manually"
        fi
    fi

    say ""
    say "sbox Network Storage Server $version is installed."
    say ""
    say "  Binary:  $bin_dir/$BIN_NAME"
    say "  Config:  $config_dir"
    say "  Data:    $data_dir"
    say ""
    say "Next steps:"
    _step=1
    if [ "$configured" -eq 0 ]; then
        say "  $_step. Run setup:            $setup_cmd"
        _step=$((_step + 1))
        if [ "$service_layout" -eq 1 ]; then
            say "  $_step. Fix ownership:        chown -R $SERVICE_USER:$SERVICE_USER $config_dir $data_dir"
            _step=$((_step + 1))
        fi
    fi
    if [ "$service_installed" -eq 1 ]; then
        say "  $_step. Check the service:    $BIN_NAME service status"
        _step=$((_step + 1))
        say "  $_step. Follow the logs:      $BIN_NAME logs -f"
        _step=$((_step + 1))
    elif [ "$service_layout" -eq 1 ] && has_systemd; then
        say "  $_step. Install the service:  $BIN_NAME service install $dirs && $BIN_NAME service start"
        _step=$((_step + 1))
    else
        say "  $_step. Start the server:     $BIN_NAME start $dirs"
        _step=$((_step + 1))
    fi
    # Run management commands as the service user so database files keep the right owner.
    as_user=""
    if [ "$service_layout" -eq 1 ]; then
        as_user="sudo -u $SERVICE_USER "
    fi
    if [ -n "$quickstart_out" ]; then
        say ""
        say "$quickstart_out"
    else
        say "  $_step. Create a project and keys, and get the line for your game:"
        say "                            $as_user$BIN_NAME quickstart \"My Game\" $dirs"
    fi

    case ":$PATH:" in
        *":$bin_dir:"*) ;;
        *)
            say ""
            warn "$bin_dir is not on your PATH. Add it, for example:"
            say "  echo 'export PATH=\"$bin_dir:\$PATH\"' >> ~/.profile"
            ;;
    esac

    say ""
    say "Docs: https://github.com/$REPO#readme"
}

main "$@"
