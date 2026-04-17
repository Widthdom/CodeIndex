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
GITHUB_BASE_URL="${CDIDX_GITHUB_BASE_URL:-https://github.com}"
GITHUB_API_BASE_URL="${CDIDX_GITHUB_API_BASE_URL:-https://api.github.com}"
# Normalize optional base URL overrides by removing a trailing slash.
# 末尾スラッシュ付きでも URL 連結が壊れないようにする。
GITHUB_BASE_URL="${GITHUB_BASE_URL%/}"
GITHUB_API_BASE_URL="${GITHUB_API_BASE_URL%/}"
TMPDIR_CLEANUP=""
STAGE_DIR_CLEANUP=""
BACKUP_DIR_CLEANUP=""
LOCAL_MIRROR_DIR_CLEANUP=""
LOCAL_MIRROR_PID=""
EXISTING_BIN=""
EXISTING_VERSION=""
EXPLICIT_VERSION_REQUESTED=0

# --- Helpers / ヘルパー ---

info()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$1" >&2; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; exit 1; }
report_error() { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; }

cleanup() {
    if [ -n "$TMPDIR_CLEANUP" ]; then
        rm -rf "$TMPDIR_CLEANUP"
    fi
    if [ -n "$STAGE_DIR_CLEANUP" ]; then
        rm -rf "$STAGE_DIR_CLEANUP"
    fi
    if [ -n "$BACKUP_DIR_CLEANUP" ]; then
        rm -rf "$BACKUP_DIR_CLEANUP"
    fi
    if [ -n "$LOCAL_MIRROR_PID" ]; then
        kill "$LOCAL_MIRROR_PID" > /dev/null 2>&1 || true
    fi
    if [ -n "$LOCAL_MIRROR_DIR_CLEANUP" ]; then
        rm -rf "$LOCAL_MIRROR_DIR_CLEANUP"
    fi
}
trap cleanup EXIT

preserve_recovery_artifacts() {
    report_error "Rollback incomplete. Preserving recovery artifacts for manual recovery."
    if [ -n "${BACKUP_DIR_CLEANUP:-}" ]; then
        report_error "Backup: ${BACKUP_DIR_CLEANUP}"
    fi
    if [ -n "${STAGE_DIR_CLEANUP:-}" ]; then
        report_error "Stage: ${STAGE_DIR_CLEANUP}"
    fi

    BACKUP_DIR_CLEANUP=""
    STAGE_DIR_CLEANUP=""
}

need_cmd() {
    if ! command -v "$1" > /dev/null 2>&1; then
        error "Required command not found: $1"
    fi
}

strip_version_prefix() {
    printf '%s' "$1" | sed 's/^[^0-9]*//'
}

extract_release_tag_name() {
    local api_response="$1"
    local version=""

    if command -v jq > /dev/null 2>&1; then
        version="$(printf '%s' "$api_response" | jq -r '.tag_name // empty' 2>/dev/null || true)"
    fi

    if [ -z "$version" ]; then
        version="$(printf '%s' "$api_response" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
    fi

    printf '%s' "$version"
}

default_self_test_version() {
    local script_dir
    local version_file
    local version

    script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
    version_file="${script_dir}/version.json"
    version=""

    if [ -f "$version_file" ]; then
        if command -v jq > /dev/null 2>&1; then
            version="$(jq -r '.version // empty' "$version_file" 2>/dev/null || true)"
        fi

        if [ -z "$version" ]; then
            version="$(grep '"version"' "$version_file" | head -1 | sed 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
        fi
    fi

    if [ -z "$version" ]; then
        printf '%s' "v0.0.0"
        return 0
    fi

    case "$version" in
        v*) printf '%s' "$version" ;;
        *)  printf 'v%s' "$version" ;;
    esac
}

curl_http_get() {
    local url="$1"
    local output_path="$2"
    local http_code

    if http_code="$(curl -sSL -o "$output_path" -w '%{http_code}' "$url")"; then
        printf '%s' "$http_code"
        return 0
    else
        local curl_status=$?

        case "$curl_status" in
            6|7|28|35|52|56)
                report_error "Network error reaching GitHub while fetching $url (curl exit $curl_status). Check your connection or corporate proxy."
                ;;
            *)
                report_error "curl failed while fetching $url (exit $curl_status)."
                ;;
        esac

        return 1
    fi
}

fetch_latest_release_version() {
    need_cmd curl
    need_cmd mktemp

    local api_url="${GITHUB_API_BASE_URL}/repos/${REPO}/releases/latest"
    local response_file
    if ! response_file="$(mktemp)"; then
        error "Failed to create temporary file for latest-release lookup."
    fi

    local http_code
    if ! http_code="$(curl_http_get "$api_url" "$response_file")"; then
        rm -f "$response_file"
        return 1
    fi
    local api_response
    api_response="$(cat "$response_file")"
    rm -f "$response_file"

    case "$http_code" in
        200) ;;
        403)
            if printf '%s' "$api_response" | grep -qi "rate limit"; then
                report_error "GitHub API rate limit exceeded while fetching the latest release. Retry later, or pass an explicit version: 'curl ... | bash -s -- vX.Y.Z'."
                return 1
            fi
            report_error "GitHub API returned HTTP 403 while fetching the latest release. Check your GitHub access or proxy configuration."
            return 1
            ;;
        404)
            report_error "GitHub API returned HTTP 404 while fetching the latest release. Check that REPO=${REPO} exists."
            return 1
            ;;
        5??)
            report_error "GitHub API returned HTTP $http_code while fetching the latest release. GitHub may be temporarily unavailable; retry in a few minutes."
            return 1
            ;;
        *)
            report_error "GitHub API returned HTTP $http_code while fetching the latest release."
            return 1
            ;;
    esac

    local version
    version="$(extract_release_tag_name "$api_response")"
    if [ -z "$version" ]; then
        report_error "Could not determine latest version from GitHub API response."
        return 1
    fi

    printf '%s' "$version"
    return 0
}

existing_install_is_reusable() {
    if [ -z "$EXISTING_VERSION" ] || [ "$EXISTING_VERSION" = "0.0.0" ]; then
        return 1
    fi

    if [ ! -f "${INSTALL_DIR}/version.json" ]; then
        return 1
    fi

    case "${OS_NAME:-}" in
        linux)
            [ -f "${INSTALL_DIR}/libe_sqlite3.so" ] || return 1
            ;;
        osx)
            [ -f "${INSTALL_DIR}/libe_sqlite3.dylib" ] || return 1
            ;;
    esac

    return 0
}

restore_backed_up_files() {
    local backup_dir="$1"
    local install_dir="$2"
    local backed_up_files="$3"
    local asset

    for asset in $backed_up_files; do
        if [ -e "${backup_dir}/${asset}" ]; then
            if ! mv "${backup_dir}/${asset}" "${install_dir}/${asset}"; then
                report_error "Failed to restore previous install file ${asset} from backup at ${backup_dir}. Manual recovery may be required."
                return 1
            fi
        fi
    done

    return 0
}

remove_promoted_files() {
    local install_dir="$1"
    local promoted_files="$2"
    local asset

    for asset in $promoted_files; do
        if [ -e "${install_dir}/${asset}" ]; then
            if ! rm -f "${install_dir}/${asset}"; then
                report_error "Failed to remove partially installed file ${install_dir}/${asset} during rollback. Manual recovery may be required."
                return 1
            fi
        fi
    done

    return 0
}

promote_staged_install() {
    local stage_dir="$1"
    local backup_dir="$2"
    local install_dir="$3"
    local required_files="$4"
    local required_assets="$5"
    local asset
    local backed_up_files=""
    local promoted_files=""

    for asset in $required_files; do
        if [ -e "${install_dir}/${asset}" ]; then
            if ! mv "${install_dir}/${asset}" "${backup_dir}/${asset}"; then
                report_error "Failed to stage existing ${asset} into backup at ${backup_dir}. Install aborted before replacing the current install."
                if [ -n "$backed_up_files" ]; then
                    if restore_backed_up_files "$backup_dir" "$install_dir" "$backed_up_files"; then
                        rm -rf "$backup_dir"
                    else
                        preserve_recovery_artifacts
                    fi
                fi
                return 1
            fi
            backed_up_files="${backed_up_files} ${asset}"
        fi
    done

    for asset in $required_assets; do
        if ! mv "${stage_dir}/${asset}" "${install_dir}/${asset}"; then
            report_error "Failed to install ${asset} into ${install_dir}. Restoring previous install."
            if [ -n "$promoted_files" ] && ! remove_promoted_files "$install_dir" "$promoted_files"; then
                preserve_recovery_artifacts
                return 1
            fi
            if [ -n "$backed_up_files" ]; then
                if restore_backed_up_files "$backup_dir" "$install_dir" "$backed_up_files"; then
                    rm -rf "$backup_dir"
                else
                    preserve_recovery_artifacts
                fi
            fi
            return 1
        fi
        promoted_files="${promoted_files} ${asset}"
    done

    if ! mv "${stage_dir}/${BINARY_NAME}" "${install_dir}/${BINARY_NAME}"; then
        report_error "Failed to install ${BINARY_NAME} into ${install_dir}. Restoring previous install."
        if [ -n "$promoted_files" ] && ! remove_promoted_files "$install_dir" "$promoted_files"; then
            preserve_recovery_artifacts
            return 1
        fi
        if [ -n "$backed_up_files" ]; then
            if restore_backed_up_files "$backup_dir" "$install_dir" "$backed_up_files"; then
                rm -rf "$backup_dir"
            else
                preserve_recovery_artifacts
            fi
        fi
        return 1
    fi

    rm -rf "$backup_dir"
    return 0
}

download_release_file() {
    local url="$1"
    local output_path="$2"
    local description="$3"

    local http_code
    if ! http_code="$(curl_http_get "$url" "$output_path")"; then
        return 1
    fi

    case "$http_code" in
        200) ;;
        403)
            report_error "Failed to download ${description} from $url (HTTP 403). GitHub may be rate-limiting or blocking the request."
            return 1
            ;;
        404)
            report_error "Failed to download ${description} from $url (HTTP 404). Check that version ${VERSION} exists and publishes ${RID} assets."
            return 1
            ;;
        5??)
            report_error "Failed to download ${description} from $url (HTTP $http_code). GitHub may be temporarily unavailable; retry in a few minutes."
            return 1
            ;;
        *)
            report_error "Failed to download ${description} from $url (HTTP $http_code)."
            return 1
            ;;
    esac

    return 0
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
    EXPLICIT_VERSION_REQUESTED=0

    if [ -n "${1:-}" ]; then
        EXPLICIT_VERSION_REQUESTED=1
        VERSION="$1"
        # Ensure v prefix / vプレフィックスを補完
        case "$VERSION" in
            v*) ;;
            *)  VERSION="v${VERSION}" ;;
        esac
    else
        info "Fetching latest release version..."
        if ! VERSION="$(fetch_latest_release_version)"; then
            return 1
        fi
    fi

    info "Version: $VERSION"
    return 0
}

# --- Check existing installation / 既存インストール確認 ---

detect_existing_install() {
    EXISTING_BIN="${INSTALL_DIR}/${BINARY_NAME}"
    EXISTING_VERSION=""

    if [ -x "$EXISTING_BIN" ]; then
        local raw_version
        raw_version="$("$EXISTING_BIN" --version 2>/dev/null || echo "unknown")"
        # Strip any prefix like "cdidx " or "cdidx v" / プレフィックスを除去
        EXISTING_VERSION="$(strip_version_prefix "$raw_version")"
    fi

    return 0
}

check_existing() {
    if [ -n "$EXISTING_VERSION" ]; then
        local target_version="${VERSION#v}"
        if [ "$EXISTING_VERSION" = "$target_version" ]; then
            if existing_install_is_reusable && [ "$EXPLICIT_VERSION_REQUESTED" != "1" ]; then
                info "cdidx $target_version is already installed at $EXISTING_BIN. Skipping."
                exit 0
            fi

            if [ "$EXPLICIT_VERSION_REQUESTED" = "1" ]; then
                info "Reinstalling cdidx $target_version because it was requested explicitly..."
                return 0
            fi

            info "Reinstalling cdidx $target_version because the existing install is incomplete..."
            return 0
        fi
        info "Switching cdidx from $EXISTING_VERSION to ${VERSION#v}..."
    fi

    return 0
}

# --- Download and verify / ダウンロード・検証 ---

download_and_install() {
    need_cmd curl
    need_cmd tar
    need_cmd mktemp

    local archive_name="CodeIndex-${RID}.tar.gz"
    local base_url="${GITHUB_BASE_URL}/${REPO}/releases/download/${VERSION}"
    local archive_url="${base_url}/${archive_name}"
    local checksums_url="${base_url}/sha256sums.txt"

    local tmpdir
    if ! tmpdir="$(mktemp -d)"; then
        error "Failed to create temporary working directory for install."
    fi
    TMPDIR_CLEANUP="$tmpdir"

    info "Downloading ${archive_name}..."
    download_release_file "$archive_url" "${tmpdir}/${archive_name}" "${archive_name}"

    info "Downloading checksums..."
    download_release_file "$checksums_url" "${tmpdir}/sha256sums.txt" "sha256sums.txt"

    # Verify checksum / チェックサム検証
    info "Verifying checksum..."
    local expected_checksum
    expected_checksum="$(awk -v name="$archive_name" '$2 == name { print $1; exit }' "${tmpdir}/sha256sums.txt")"

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

    # Validate the extracted payload before copying anything into INSTALL_DIR.
    # This avoids overwriting a healthy install with a partially broken one
    # when the tarball is missing required files.
    # INSTALL_DIR に何か書き込む前に展開済み payload 全体を検証する。
    # tarball の必須ファイルが欠けているときに、健全な install を
    # 部分的に壊れた内容で上書きしないため。
    #
    # Install runtime assets alongside the binary. Fail fast if any required
    # asset is missing rather than silently installing a partially broken
    # binary that will crash on first use.
    # - cdidx loads version.json via AppContext.BaseDirectory (the binary's dir),
    #   so without it `cdidx --version` reports v0.0.0.
    # - The native SQLite library (libe_sqlite3.so on Linux, libe_sqlite3.dylib
    #   on macOS) must live next to the binary for P/Invoke to resolve; without
    #   it every command crashes with DllNotFoundException at startup.
    # Required assets are OS-specific, so we match on $OS_NAME instead of
    # "copy whatever happens to be in the archive". This keeps the installer
    # compatible with bash 3.2 (the default /bin/bash on macOS) — no arrays,
    # no `mapfile`, no `find` — and works for all currently published tarballs.
    # ランタイム資産をバイナリの隣へ配置する。必須資産が欠落している場合は、
    # 部分的に壊れたインストールを黙って進めず即時失敗させる（起動直後の
    # クラッシュを防ぐため）。
    # - cdidx は AppContext.BaseDirectory（バイナリのディレクトリ）から
    #   version.json を読むため、これが無いと --version が v0.0.0 になる。
    # - ネイティブ SQLite ライブラリ（Linux は libe_sqlite3.so、macOS は
    #   libe_sqlite3.dylib）は P/Invoke 解決のためバイナリの隣に必要で、
    #   無いと起動直後に DllNotFoundException で全コマンドが落ちる。
    # 必須資産は OS ごとに異なるため「アーカイブにあるものを何でも」ではなく
    # $OS_NAME で分岐する。macOS の既定 /bin/bash 3.2 でも動くよう、配列・
    # `mapfile`・`find` は使わず、現行リリースの tarball 配置前提で実装する。
    local required_assets
    case "$OS_NAME" in
        linux) required_assets="version.json libe_sqlite3.so"   ;;
        osx)   required_assets="version.json libe_sqlite3.dylib" ;;
        *)     error "Internal error: unknown OS_NAME '$OS_NAME' for asset selection." ;;
    esac

    local required_files="${BINARY_NAME} ${required_assets}"
    local asset
    for asset in $required_files; do
        if [ ! -f "${extract_dir}/${asset}" ]; then
            if [ "$asset" = "$BINARY_NAME" ]; then
                error "Required release payload missing from tarball: ${asset}. Refusing to install a partially broken binary. Please report this at https://github.com/${REPO}/issues."
            fi

            error "Required runtime asset missing from release tarball: ${asset}. Refusing to install a partially broken binary. Please report this at https://github.com/${REPO}/issues."
        fi
    done

    mkdir -p "$INSTALL_DIR"

    local stage_dir
    if ! stage_dir="$(mktemp -d "${INSTALL_DIR}/.cdidx-stage.XXXXXX")"; then
        error "Failed to create staging directory under ${INSTALL_DIR}."
    fi
    STAGE_DIR_CLEANUP="$stage_dir"

    for asset in $required_files; do
        cp "${extract_dir}/${asset}" "${stage_dir}/${asset}"
    done
    chmod +x "${stage_dir}/${BINARY_NAME}"

    local backup_dir
    if ! backup_dir="$(mktemp -d "${INSTALL_DIR}/.cdidx-backup.XXXXXX")"; then
        error "Failed to create backup directory under ${INSTALL_DIR}."
    fi
    BACKUP_DIR_CLEANUP="$backup_dir"

    if ! promote_staged_install "$stage_dir" "$backup_dir" "$INSTALL_DIR" "$required_files" "$required_assets"; then
        return 1
    fi

    rm -rf "$stage_dir"
    STAGE_DIR_CLEANUP=""
    rm -rf "$backup_dir"
    BACKUP_DIR_CLEANUP=""

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

run_local_mirror_self_test() {
    need_cmd python3
    need_cmd tar
    need_cmd mktemp
    need_cmd awk
    need_cmd sleep

    local rehearsal_version="${1:-$(default_self_test_version)}"
    case "$rehearsal_version" in
        v*) ;;
        *)  rehearsal_version="v${rehearsal_version}" ;;
    esac
    local rehearsal_version_no_prefix="${rehearsal_version#v}"
    local local_mirror_port="${CDIDX_LOCAL_MIRROR_PORT:-18765}"
    local local_mirror_root
    local local_release_base
    local local_payload_dir
    local checksum

    if ! local_mirror_root="$(mktemp -d /tmp/cdidx-local-mirror.XXXXXX)"; then
        error "Failed to create local mirror directory for self-test."
    fi
    LOCAL_MIRROR_DIR_CLEANUP="$local_mirror_root"

    local_release_base="${local_mirror_root}/${REPO}/releases/download/${rehearsal_version}"
    local_payload_dir="${local_release_base}/payload"
    mkdir -p "$local_payload_dir"

    cat > "${local_payload_dir}/${BINARY_NAME}" <<EOF
#!/usr/bin/env bash
if [ "\${1:-}" = "--version" ]; then
  echo "${BINARY_NAME} ${rehearsal_version}"
  exit 0
fi
echo "mock ${BINARY_NAME} (${rehearsal_version}) for local mirror self-test" >&2
exit 2
EOF
    chmod +x "${local_payload_dir}/${BINARY_NAME}"
    printf '{"version":"%s"}\n' "$rehearsal_version_no_prefix" > "${local_payload_dir}/version.json"
    : > "${local_payload_dir}/libe_sqlite3.so"

    (
        cd "$local_payload_dir"
        tar czf ../CodeIndex-linux-x64.tar.gz "${BINARY_NAME}" version.json libe_sqlite3.so
    )

    if command -v sha256sum > /dev/null 2>&1; then
        checksum="$(sha256sum "${local_release_base}/CodeIndex-linux-x64.tar.gz" | awk '{print $1}')"
    elif command -v shasum > /dev/null 2>&1; then
        checksum="$(shasum -a 256 "${local_release_base}/CodeIndex-linux-x64.tar.gz" | awk '{print $1}')"
    elif command -v openssl > /dev/null 2>&1; then
        checksum="$(openssl dgst -sha256 "${local_release_base}/CodeIndex-linux-x64.tar.gz" | awk '{print $NF}')"
    else
        error "No checksum tool found (need sha256sum, shasum, or openssl) for local mirror self-test."
    fi
    printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "${local_release_base}/sha256sums.txt"

    python3 -m http.server "$local_mirror_port" --directory "$local_mirror_root" > /tmp/cdidx-local-mirror-http.log 2>&1 &
    LOCAL_MIRROR_PID=$!
    sleep 1

    info "Running local mirror self-test against http://127.0.0.1:${local_mirror_port}/"
    GITHUB_BASE_URL="http://127.0.0.1:${local_mirror_port}"
    main "$rehearsal_version"
    "${INSTALL_DIR}/${BINARY_NAME}" --version
}

# --- Main / メイン ---

main() {
    info "cdidx installer"
    detect_platform
    info "Detected platform: ${RID}"
    detect_existing_install
    if ! resolve_version "${1:-}"; then
        exit 1
    fi
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

if [ "${CDIDX_INSTALL_SH_LIB_ONLY:-0}" != "1" ]; then
    case "${1:-}" in
        --self-test-local-mirror)
            shift
            run_local_mirror_self_test "${1:-}"
            ;;
        *)
            main "$@"
            ;;
    esac
fi
