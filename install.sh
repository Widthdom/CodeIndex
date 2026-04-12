#!/usr/bin/env bash
# install.sh — One-liner installer for cdidx (CodeIndex)
# cdidxワンライナーインストーラー
#
# Usage / 使い方:
#   curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/v1.5.0/install.sh | bash -s -- v1.5.0
#   export CDIDX_INSTALL_DIR=/usr/local/bin; curl -fsSL ... | bash

set -euo pipefail

REPO="Widthdom/CodeIndex"
INSTALL_DIR="${CDIDX_INSTALL_DIR:-$HOME/.local/bin}"
BINARY_NAME="cdidx"
TMPDIR_CLEANUP=""

# --- Helpers / ヘルパー ---

info()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$1" >&2; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; exit 1; }

cleanup() { [ -n "$TMPDIR_CLEANUP" ] && rm -rf "$TMPDIR_CLEANUP"; }
trap cleanup EXIT

need_cmd() {
    if ! command -v "$1" > /dev/null 2>&1; then
        error "Required command not found: $1"
    fi
}

# --- Detect OS and architecture / OS・アーキテクチャ検出 ---

detect_platform() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"

    case "$os" in
        Linux)  OS_NAME="linux" ;;
        Darwin) OS_NAME="osx"   ;;
        *)      error "Unsupported OS: $os (supported: Linux, macOS)" ;;
    esac

    case "$arch" in
        x86_64|amd64)   ARCH_NAME="x64"   ;;
        aarch64|arm64)  ARCH_NAME="arm64"  ;;
        *)              error "Unsupported architecture: $arch (supported: x86_64, arm64)" ;;
    esac

    RID="${OS_NAME}-${ARCH_NAME}"

    # osx-x64 is not published / osx-x64 はリリースしていない
    if [ "$RID" = "osx-x64" ]; then
        error "macOS x86_64 (Intel) binaries are not published. Use Rosetta 2 with osx-arm64 or install via 'dotnet tool install -g cdidx'."
    fi

    # Reject musl-based Linux (e.g. Alpine) — published binaries require glibc
    # musl系Linux（Alpine等）を拒否 — リリースバイナリはglibcが必要
    if [ "$OS_NAME" = "linux" ]; then
        if command -v ldd > /dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
            error "musl-based Linux (e.g. Alpine) is not supported. Published binaries require glibc. Use a glibc-based image (e.g. debian, ubuntu) or install via 'dotnet tool install -g cdidx'."
        fi
    fi
}

# --- Resolve version / バージョン解決 ---

resolve_version() {
    if [ -n "${1:-}" ]; then
        VERSION="$1"
        # Ensure v prefix / vプレフィックスを補完
        case "$VERSION" in
            v*) ;;
            *)  VERSION="v${VERSION}" ;;
        esac
    else
        info "Fetching latest release version..."
        need_cmd curl
        local api_response
        api_response="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest")" \
            || error "Failed to fetch latest release from GitHub API. Check your network connection."

        VERSION="$(printf '%s' "$api_response" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"

        if [ -z "$VERSION" ]; then
            error "Could not determine latest version from GitHub API response."
        fi
    fi

    info "Version: $VERSION"
}

# --- Check existing installation / 既存インストール確認 ---

check_existing() {
    local existing_bin="${INSTALL_DIR}/${BINARY_NAME}"
    if [ -x "$existing_bin" ]; then
        local existing_version
        local raw_version
        raw_version="$("$existing_bin" --version 2>/dev/null || echo "unknown")"
        # Strip any prefix like "cdidx " or "cdidx v" / プレフィックスを除去
        existing_version="$(printf '%s' "$raw_version" | sed 's/^[^0-9]*//')"
        local target_version="${VERSION#v}"
        if [ "$existing_version" = "$target_version" ]; then
            info "cdidx $target_version is already installed at $existing_bin. Skipping."
            exit 0
        fi
        info "Upgrading cdidx from $existing_version to ${VERSION#v}..."
    fi
}

# --- Download and verify / ダウンロード・検証 ---

download_and_install() {
    need_cmd curl
    need_cmd tar
    need_cmd mktemp

    local archive_name="CodeIndex-${RID}.tar.gz"
    local base_url="https://github.com/${REPO}/releases/download/${VERSION}"
    local archive_url="${base_url}/${archive_name}"
    local checksums_url="${base_url}/sha256sums.txt"

    local tmpdir
    tmpdir="$(mktemp -d)"
    TMPDIR_CLEANUP="$tmpdir"

    info "Downloading ${archive_name}..."
    curl -fsSL -o "${tmpdir}/${archive_name}" "$archive_url" \
        || error "Failed to download $archive_url. Check that version $VERSION exists and has a ${RID} binary."

    info "Downloading checksums..."
    curl -fsSL -o "${tmpdir}/sha256sums.txt" "$checksums_url" \
        || error "Failed to download checksums from $checksums_url."

    # Verify checksum / チェックサム検証
    info "Verifying checksum..."
    local expected_checksum
    expected_checksum="$(grep "$archive_name" "${tmpdir}/sha256sums.txt" | awk '{print $1}')"

    if [ -z "$expected_checksum" ]; then
        error "Checksum for $archive_name not found in sha256sums.txt."
    fi

    local actual_checksum
    if command -v sha256sum > /dev/null 2>&1; then
        actual_checksum="$(sha256sum "${tmpdir}/${archive_name}" | awk '{print $1}')"
    elif command -v shasum > /dev/null 2>&1; then
        actual_checksum="$(shasum -a 256 "${tmpdir}/${archive_name}" | awk '{print $1}')"
    elif command -v openssl > /dev/null 2>&1; then
        actual_checksum="$(openssl dgst -sha256 "${tmpdir}/${archive_name}" | awk '{print $NF}')"
    else
        error "No checksum tool found (need sha256sum, shasum, or openssl). Cannot verify download integrity."
    fi

    if [ "$actual_checksum" != "$expected_checksum" ]; then
        error "Checksum mismatch!\n  Expected: $expected_checksum\n  Actual:   $actual_checksum"
    fi

    # Extract into a dedicated subdirectory so we don't mix extracted files
    # with the downloaded archive/checksums when copying.
    # 展開用サブディレクトリを使い、アーカイブや checksum ファイルと混在させない。
    local extract_dir="${tmpdir}/extract"
    mkdir -p "$extract_dir"
    info "Extracting..."
    tar xzf "${tmpdir}/${archive_name}" -C "$extract_dir"

    # Resolve extraction root. Most releases extract files at archive root,
    # but some repacks may wrap them in a single top-level directory.
    # 展開ルートを解決。通常はアーカイブ直下だが、再パック時に
    # 単一トップレベルディレクトリで包まれる場合にも対応する。
    local payload_root="$extract_dir"
    if [ ! -f "${payload_root}/${BINARY_NAME}" ]; then
        local top_dirs
        top_dirs="$(find "$extract_dir" -mindepth 1 -maxdepth 1 -type d | wc -l | awk '{print $1}')"
        if [ "$top_dirs" = "1" ]; then
            payload_root="$(find "$extract_dir" -mindepth 1 -maxdepth 1 -type d | head -1)"
        fi
    fi

    if [ ! -f "${payload_root}/${BINARY_NAME}" ]; then
        error "Archive does not contain ${BINARY_NAME} at an expected location."
    fi

    # Install binary / バイナリをインストール
    mkdir -p "$INSTALL_DIR"
    cp "${payload_root}/${BINARY_NAME}" "${INSTALL_DIR}/${BINARY_NAME}"
    chmod +x "${INSTALL_DIR}/${BINARY_NAME}"

    # Install runtime assets alongside the binary.
    # - cdidx loads version.json via AppContext.BaseDirectory (the binary's dir),
    #   so without it `cdidx --version` reports v0.0.0.
    # - The native SQLite library (libe_sqlite3.so on Linux, libe_sqlite3.dylib
    #   on macOS) must live next to the binary for P/Invoke to resolve; without
    #   it every command crashes with DllNotFoundException at startup.
    # ランタイム資産をバイナリの隣へ配置する。
    # - cdidx は AppContext.BaseDirectory（バイナリのディレクトリ）から
    #   version.json を読むため、これが無いと --version が v0.0.0 になる。
    # - ネイティブ SQLite ライブラリ（Linux は libe_sqlite3.so、macOS は
    #   libe_sqlite3.dylib）は P/Invoke 解決のためバイナリの隣に必要で、
    #   無いと起動直後に DllNotFoundException で全コマンドが落ちる。
    local required_assets="version.json"
    if [ "$OS_NAME" = "linux" ]; then
        required_assets="${required_assets} libe_sqlite3.so"
    elif [ "$OS_NAME" = "osx" ]; then
        required_assets="${required_assets} libe_sqlite3.dylib"
    fi

    local asset
    for asset in $required_assets; do
        if [ ! -f "${payload_root}/${asset}" ]; then
            error "Archive is missing required runtime asset: ${asset}"
        fi
        cp "${payload_root}/${asset}" "${INSTALL_DIR}/${asset}"
    done

    info "Installed cdidx to ${INSTALL_DIR}/${BINARY_NAME}"
}

# --- PATH guidance / PATHガイダンス ---

check_path() {
    case ":${PATH}:" in
        *":${INSTALL_DIR}:"*) ;;
        *)
            warn "${INSTALL_DIR} is not in your PATH."
            echo ""
            echo "  Add it to your shell profile:"
            echo ""
            local shell_name
            shell_name="$(basename "${SHELL:-/bin/bash}")"
            case "$shell_name" in
                zsh)
                    echo "    echo 'export PATH=\"${INSTALL_DIR}:\$PATH\"' >> ~/.zshrc"
                    echo "    source ~/.zshrc"
                    ;;
                bash)
                    echo "    echo 'export PATH=\"${INSTALL_DIR}:\$PATH\"' >> ~/.bashrc"
                    echo "    source ~/.bashrc"
                    ;;
                fish)
                    echo "    fish_add_path ${INSTALL_DIR}"
                    ;;
                *)
                    echo "    export PATH=\"${INSTALL_DIR}:\$PATH\""
                    ;;
            esac
            echo ""
            ;;
    esac
}

# --- Main / メイン ---

main() {
    info "cdidx installer"
    detect_platform
    info "Detected platform: ${RID}"
    resolve_version "${1:-}"
    check_existing
    download_and_install
    check_path

    echo ""
    info "Done! Run 'cdidx --version' to verify."
    echo ""
    echo "  Quick start:"
    echo "    cdidx .              # Index current directory"
    echo "    cdidx search <query> # Search your code"
    echo "    cdidx mcp            # Start MCP server for AI tools"
    echo ""
}

main "$@"
