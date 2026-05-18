#!/usr/bin/env bash
# install.sh — One-liner installer for cdidx (CodeIndex)
# cdidxワンライナーインストーラー
#
# Usage / 使い方:
#   curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/v1.5.0/install.sh | bash -s -- v1.5.0
#   export CDIDX_INSTALL_DIR=/usr/local/bin; curl -fsSL ... | bash
#   bash ./install.sh --self-test-local-mirror [--self-test-allow-overwrite] [vX.Y.Z]
#   bash ./install.sh --reinstall-real vX.Y.Z
#   bash ./install.sh --doctor [vX.Y.Z]
#
# Optional env vars / 任意環境変数:
#   CDIDX_GITHUB_BASE_URL       Release download base URL override
#   CDIDX_GITHUB_API_BASE_URL   API base URL override for latest-release lookup
#   CDIDX_LOCAL_MIRROR_PORT     Local self-test HTTP server port (default: 18765)
#
# Self-test mock payload safety / セルフテスト mock 上書き防止:
#   The --self-test-local-mirror path installs a **mock** cdidx that only
#   handles --version. To prevent that mock from silently replacing a real
#   ~/.local/bin/cdidx when CDIDX_INSTALL_DIR is pre-exported to a
#   well-known system/user install path or to a directory that already
#   holds a cdidx binary, the self-test aborts unless the caller also
#   passes --self-test-allow-overwrite.
#   --self-test-local-mirror は --version だけを返す mock cdidx を配置する。
#   CDIDX_INSTALL_DIR が既知のシステム/ユーザー install 先や、既に cdidx を
#   持つディレクトリを指しているときは、--self-test-allow-overwrite を明示
#   しない限り self-test を中断して real install の上書きを防ぐ。
#
# Real reinstall validation / 実リリースの再インストール検証:
#   --reinstall-real vX.Y.Z downloads the real published release (no mock)
#   into an **isolated temp dir** — it never touches the user's real install
#   — and verifies the binary end-to-end: `cdidx --version` plus a real
#   `cdidx . --db <tmp>` indexing run against a minimal scratch project.
#   Catches regressions that --self-test-local-mirror cannot (symbol
#   extraction, SQLite loading, FTS, etc.) because the mock only handles
#   --version. CDIDX_INSTALL_DIR is intentionally ignored for this mode.
#   --reinstall-real vX.Y.Z は、公開済みリリースを **隔離された temp dir** に
#   実ダウンロードし（ユーザーの実インストールには触らない）、`cdidx --version`
#   だけでなく最小スクラッチプロジェクトに対する `cdidx . --db <tmp>` 実行まで
#   含めた end-to-end 検証を行う。--self-test-local-mirror の mock は --version
#   しか返さないため拾えない、シンボル抽出・SQLite ロード・FTS 等のリグレッション
#   を検出できる。このモードでは CDIDX_INSTALL_DIR は意図的に無視する。
#
# Network diagnostics / ネットワーク診断:
#   --doctor [vX.Y.Z] does not install anything. It prints the active proxy
#   environment variables and probes the installer's upstream URLs (the
#   latest-release API endpoint plus the release tarball and sha256sums asset
#   URLs for the requested version — or the version recorded in version.json
#   if no version is provided) with `curl -sSI`. Each probe reports its HTTP
#   status. On `CONNECT tunnel failed, response 403` (curl exit 56) the doctor
#   prints the canonical upstream-proxy guidance so users get a single,
#   actionable next step without needing prior network knowledge. Exits 0 when
#   every probe returns a 2xx/3xx response, 1 otherwise.
#   --doctor [vX.Y.Z] は何もインストールせず、有効な proxy 環境変数と、
#   installer が叩く upstream URL（latest-release API と、指定バージョン
#   または version.json 記載バージョンのリリース tarball / sha256sums）を
#   `curl -sSI` で probe し、各結果の HTTP status を表示する。
#   `CONNECT tunnel failed, response 403` (curl exit 56) を検知した場合は、
#   upstream proxy / egress policy 側の拒否であり経路差し替えでは解消しない
#   という定型ガイダンスを出力し、ユーザーがネットワーク知識なしで次の一手
#   を取れるようにする。全 probe が 2xx/3xx を返したら exit 0、それ以外は 1。

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
SELF_TEST_INSTALL_DIR_CLEANUP=""
REINSTALL_SCRATCH_CLEANUP=""
SELF_TEST_LOCAL_MIRROR=0
# Only set via the --self-test-allow-overwrite CLI flag. We intentionally do
# NOT inherit this from the environment so that a stale SELF_TEST_ALLOW_OVERWRITE=1
# in the caller's shell / CI cannot silently bypass the install-dir guard.
# CLI フラグ --self-test-allow-overwrite 経由でのみ 1 になる。環境変数からは
# 継承しない (呼び出し側のシェルに残った SELF_TEST_ALLOW_OVERWRITE=1 が
# install-dir ガードを黙って無効化しないようにするため)。
SELF_TEST_ALLOW_OVERWRITE=0
EXISTING_BIN=""
EXISTING_VERSION=""
EXPLICIT_VERSION_REQUESTED=0

# --- Helpers / ヘルパー ---

info()  { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$1" >&2; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; exit 1; }
report_error() { printf '\033[1;31mERROR:\033[0m %s\n' "$1" >&2; }

published_release_rids() {
    printf '%s' "linux-x64, linux-arm64, osx-arm64, win-x64, win-arm64"
}

platform_support_request_url() {
    printf 'https://github.com/%s/issues/new?title=Request%%20official%%20release%%20asset%%20for%%20RID' "$REPO"
}

is_published_release_rid() {
    case "$1" in
        linux-x64|linux-arm64|osx-arm64|win-x64|win-arm64)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

validate_published_release_rid() {
    if is_published_release_rid "$RID"; then
        return 0
    fi

    error "Unsupported release asset RID: ${RID}. Official release assets are published for $(published_release_rids). Install via 'dotnet tool install -g cdidx' with the .NET SDK, build from source with 'dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r ${RID} --self-contained true', or request official platform support at $(platform_support_request_url). See https://github.com/${REPO}/blob/main/docs/platform-support.md."
}

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
    if [ -n "$SELF_TEST_INSTALL_DIR_CLEANUP" ]; then
        rm -rf "$SELF_TEST_INSTALL_DIR_CLEANUP"
    fi
    if [ -n "$REINSTALL_SCRATCH_CLEANUP" ]; then
        rm -rf "$REINSTALL_SCRATCH_CLEANUP"
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

latest_release_api_url() {
    printf '%s/repos/%s/releases/latest' "$GITHUB_API_BASE_URL" "$REPO"
}

latest_release_api_diagnostic_label() {
    if [ "$GITHUB_API_BASE_URL" = "https://api.github.com" ]; then
        printf '%s' "GitHub API"
    else
        printf 'configured latest-release API (%s)' "$GITHUB_API_BASE_URL"
    fi
}

release_host_diagnostic_label() {
    if [ "$GITHUB_BASE_URL" = "https://github.com" ]; then
        printf '%s' "GitHub release host"
    else
        printf 'configured release host (%s)' "$GITHUB_BASE_URL"
    fi
}

is_loopback_url() {
    case "$1" in
        http://127.0.0.1:*|https://127.0.0.1:*|http://localhost:*|https://localhost:*)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

append_loopback_no_proxy_list() {
    local current_value="${1:-}"

    if [ -n "$current_value" ]; then
        printf '%s,%s' "$current_value" "127.0.0.1,localhost"
    else
        printf '%s' "127.0.0.1,localhost"
    fi
}

prepare_loopback_no_proxy_env() {
    NO_PROXY="$(append_loopback_no_proxy_list "${NO_PROXY:-}")"
    no_proxy="$(append_loopback_no_proxy_list "${no_proxy:-}")"
    export NO_PROXY no_proxy
}

run_curl_with_optional_loopback_bypass() {
    if is_loopback_url "$1"; then
        shift
        curl --noproxy 127.0.0.1,localhost "$@"
    else
        shift
        curl "$@"
    fi
}

has_explicit_self_test_install_dir() {
    [ -n "${CDIDX_INSTALL_DIR:-}" ]
}

# Decide whether an explicit CDIDX_INSTALL_DIR is risky enough to refuse the
# self-test mock install. A "risky" dir is either a well-known system/user
# install path (where a real cdidx would normally live) or any directory that
# already contains an executable cdidx binary. Callers can opt out of this
# guard with --self-test-allow-overwrite.
# 明示指定された CDIDX_INSTALL_DIR が、既知のシステム/ユーザー install 先か
# 既に cdidx を持つディレクトリなら、mock での上書きを拒否する。解除には
# --self-test-allow-overwrite を使う。
is_self_test_install_dir_risky() {
    local dir="$1"

    if [ -z "$dir" ]; then
        return 1
    fi

    # Expand a leading ~ manually; bash does not expand ~ inside env values.
    # 先頭の ~ は env の値内では展開されないので手動で置換する。
    case "$dir" in
        "~"|"~/"*)
            if [ -n "${HOME:-}" ]; then
                dir="${HOME}${dir#\~}"
            fi
            ;;
    esac

    # Normalize trailing slashes so /usr/local/bin and /usr/local/bin/ (or
    # "$HOME/.local/bin/") match the well-known-path branches below. Leave a
    # lone "/" intact so we don't turn it into an empty string.
    # 末尾スラッシュを正規化し、/usr/local/bin と /usr/local/bin/ などを同一視する。
    # ルート "/" は空文字にならないよう保持する。
    while [ "${#dir}" -gt 1 ]; do
        case "$dir" in
            */) dir="${dir%/}" ;;
            *) break ;;
        esac
    done

    case "$dir" in
        /usr/local/bin|/usr/bin|/opt/homebrew/bin|/opt/local/bin)
            return 0
            ;;
    esac

    if [ -n "${HOME:-}" ] && [ "$dir" = "${HOME}/.local/bin" ]; then
        return 0
    fi

    if [ -x "${dir}/${BINARY_NAME}" ]; then
        return 0
    fi

    return 1
}

release_download_base_url() {
    printf '%s/%s/releases/download/%s' "$GITHUB_BASE_URL" "$REPO" "$VERSION"
}

is_proxy_tunnel_403() {
    printf '%s' "$1" | grep -Eqi 'CONNECT tunnel failed, response 403|HTTP code 403 from proxy after CONNECT'
}

curl_http_get() {
    local url="$1"
    local output_path="$2"
    local source_label="${3:-remote host}"
    local http_code
    local curl_stderr

    if ! curl_stderr="$(mktemp)"; then
        report_error "Failed to create temporary curl stderr capture while fetching ${source_label} at $url."
        return 1
    fi

    if http_code="$(run_curl_with_optional_loopback_bypass "$url" -sSL -o "$output_path" -w '%{http_code}' "$url" 2>"$curl_stderr")"; then
        rm -f "$curl_stderr"
        printf '%s' "$http_code"
        return 0
    else
        local curl_status=$?
        local stderr_text=""
        if [ -f "$curl_stderr" ]; then
            stderr_text="$(cat "$curl_stderr")"
            rm -f "$curl_stderr"
        fi

        if [ "$curl_status" -eq 56 ] && is_proxy_tunnel_403 "$stderr_text"; then
            if [ -n "$stderr_text" ]; then
                printf '%s\n' "$stderr_text" >&2
            fi
            report_error "CONNECT tunnel failed with HTTP 403 while reaching ${source_label} at $url (curl exit 56). This deny is happening in an upstream proxy/egress policy before TLS."
            report_error "If every HTTPS endpoint fails with a CONNECT-stage HTTP 403, route substitution alone will not fix it."
            report_error "Ask your network administrator to allow-list at least one required API or artifact host path."
            return 1
        fi

        if [ -n "$stderr_text" ]; then
            printf '%s\n' "$stderr_text" >&2
        fi

        case "$curl_status" in
            6|7|28|35|52|56)
                report_error "Network error reaching ${source_label} while fetching $url (curl exit $curl_status). Check your connection, proxy, or configured mirror."
                ;;
            *)
                report_error "curl failed while fetching ${source_label} at $url (exit $curl_status)."
                ;;
        esac

        return 1
    fi
}

fetch_latest_release_version() {
    need_cmd curl
    need_cmd mktemp

    local api_url="https://api.github.com/repos/${REPO}/releases/latest"
    local api_url
    local api_label
    api_url="$(latest_release_api_url)"
    api_label="$(latest_release_api_diagnostic_label)"
    local response_file
    if ! response_file="$(mktemp)"; then
        error "Failed to create temporary file for latest-release lookup."
    fi

    local http_code
    if ! http_code="$(curl_http_get "$api_url" "$response_file" "$api_label")"; then
        rm -f "$response_file"
        return 1
    fi
    local api_response
    api_response="$(cat "$response_file")"
    rm -f "$response_file"
    local explicit_version_examples
    explicit_version_examples="rerun the installer with an explicit version (for example: 'curl -fsSL https://raw.githubusercontent.com/${REPO}/vX.Y.Z/install.sh | bash -s -- vX.Y.Z', or 'bash ./install.sh vX.Y.Z' from a checkout)"

    case "$http_code" in
        200) ;;
        403)
            if printf '%s' "$api_response" | grep -qi "rate limit"; then
                report_error "${api_label} rate limit exceeded while fetching ${api_url}. Retry later, or pass an explicit version: 'curl ... | bash -s -- vX.Y.Z'."
                return 1
            fi
            if [ "$GITHUB_API_BASE_URL" = "https://api.github.com" ]; then
                report_error "${api_label} returned HTTP 403 while fetching ${api_url}. ${explicit_version_examples} to skip the latest-release API call, or set CDIDX_GITHUB_API_BASE_URL to a reachable internal mirror API."
            else
                report_error "${api_label} returned HTTP 403 while fetching ${api_url}. Check the configured API endpoint, credentials, path ACL, or proxy policy. You can also ${explicit_version_examples} to skip the latest-release API call."
            fi
            report_error "If every HTTPS endpoint fails with 'CONNECT tunnel failed, response 403', this is an upstream proxy/egress policy deny before TLS; route substitution alone will not fix it."
            return 1
            ;;
        404)
            report_error "${api_label} returned HTTP 404 while fetching ${api_url}. Check that REPO=${REPO} and the configured API base are correct."
            return 1
            ;;
        5??)
            report_error "${api_label} returned HTTP $http_code while fetching ${api_url}. The configured API endpoint may be temporarily unavailable; retry in a few minutes."
            return 1
            ;;
        *)
            report_error "${api_label} returned HTTP $http_code while fetching ${api_url}."
            return 1
            ;;
    esac

    local version
    version="$(extract_release_tag_name "$api_response")"
    if [ -z "$version" ]; then
        report_error "Could not determine latest version from ${api_label} response at ${api_url}."
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

    [ -f "${INSTALL_DIR}/LICENSE" ] || return 1
    [ -f "${INSTALL_DIR}/COMMERCIAL_LICENSE.md" ] || return 1
    [ -f "${INSTALL_DIR}/INTEGRATION_POLICY.md" ] || return 1
    [ -f "${INSTALL_DIR}/TRADEMARKS.md" ] || return 1
    [ -f "${INSTALL_DIR}/LICENSES/FSL-1.1-ALv2.txt" ] || return 1
    [ -f "${INSTALL_DIR}/LICENSES/Apache-2.0.txt" ] || return 1

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
            if ! rm -rf "${install_dir}/${asset}"; then
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
    local release_host_label
    release_host_label="$(release_host_diagnostic_label)"

    local http_code
    if ! http_code="$(curl_http_get "$url" "$output_path" "$release_host_label")"; then
        return 1
    fi

    case "$http_code" in
        200) ;;
        403)
            report_error "Failed to download ${description} from ${release_host_label} at $url (HTTP 403)."
            if [ "$GITHUB_BASE_URL" = "https://github.com" ]; then
                report_error "GitHub may be blocking or rate-limiting this route."
            else
                report_error "Check the configured mirror/proxy path, credentials, or access policy."
            fi
            report_error "If both github.com and the configured mirror/proxy host fail at CONNECT tunnel stage with 403, ask your network administrator to allow-list at least one artifact host path."
            return 1
            ;;
        404)
            report_error "Failed to download ${description} from ${release_host_label} at $url (HTTP 404). Check that version ${VERSION} exists and that the configured release host publishes ${RID} assets."
            return 1
            ;;
        5??)
            report_error "Failed to download ${description} from ${release_host_label} at $url (HTTP $http_code). The configured release host may be temporarily unavailable; retry in a few minutes."
            return 1
            ;;
        *)
            report_error "Failed to download ${description} from ${release_host_label} at $url (HTTP $http_code)."
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
        *)              error "Unsupported architecture: $arch. Official release assets are published for $(published_release_rids). Other RIDs such as linux-x86, osx-x64, and win-x86 are not currently shipped. Install via 'dotnet tool install -g cdidx' with the .NET SDK, build from source with 'dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r <rid> --self-contained true', or request official platform support at $(platform_support_request_url). See https://github.com/${REPO}/blob/main/docs/platform-support.md." ;;
    esac

    RID="${OS_NAME}-${ARCH_NAME}"

    # osx-x64 is not published / osx-x64 はリリースしていない
    if [ "$RID" = "osx-x64" ]; then
        error "macOS x86_64 (Intel) binaries are not published as CodeIndex-osx-x64.tar.gz. Official release assets are published for $(published_release_rids). Install via 'dotnet tool install -g cdidx' with the .NET SDK, build from source with 'dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r osx-x64 --self-contained true', or request official platform support at $(platform_support_request_url). See https://github.com/${REPO}/blob/main/docs/platform-support.md."
    fi

    validate_published_release_rid

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
    local base_url
    base_url="$(release_download_base_url)"
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

    # License, integration-policy, and trademark notices are shipped when
    # present, but older mirrors may still lack them. Treat them as best-effort
    # extras so we can keep supporting older release archives while ensuring new
    # releases install the legal files that the release workflow now verifies.
    # LICENSE / 統合ポリシー / 商用ライセンス / 商標の案内は存在すれば
    # 一緒に配置するが、古い mirror にはまだ無い可能性があるため必須には
    # しない。古い release archive を壊さず、新しい release では workflow
    # が検証する法務ファイルを確実にインストールできるようにする。
    local required_files="${BINARY_NAME} ${required_assets}"
    local optional_assets="LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md LICENSES"
    local staged_assets="$required_assets"
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
    for asset in $optional_assets; do
        if [ -f "${extract_dir}/${asset}" ]; then
            cp "${extract_dir}/${asset}" "${stage_dir}/${asset}"
        elif [ -d "${extract_dir}/${asset}" ]; then
            cp -R "${extract_dir}/${asset}" "${stage_dir}/${asset}"
        fi
        if [ -e "${stage_dir}/${asset}" ]; then
            staged_assets="${staged_assets} ${asset}"
        fi
    done
    chmod +x "${stage_dir}/${BINARY_NAME}"

    local backup_dir
    if ! backup_dir="$(mktemp -d "${INSTALL_DIR}/.cdidx-backup.XXXXXX")"; then
        error "Failed to create backup directory under ${INSTALL_DIR}."
    fi
    BACKUP_DIR_CLEANUP="$backup_dir"

    if ! promote_staged_install "$stage_dir" "$backup_dir" "$INSTALL_DIR" "$required_files" "$staged_assets"; then
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
    if [ "${SELF_TEST_LOCAL_MIRROR:-0}" = "1" ]; then
        return 0
    fi

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

report_local_mirror_start_failure() {
    local local_mirror_port="$1"
    local local_mirror_log="$2"

    report_error "Local mirror self-test could not start a loopback HTTP server on 127.0.0.1:${local_mirror_port}."
    report_error "This is a self-test harness failure, not an external network/proxy problem."
    if [ -f "$local_mirror_log" ]; then
        report_error "Local mirror log tail (${local_mirror_log}):"
        if command -v tail > /dev/null 2>&1; then
            tail -n 20 "$local_mirror_log" >&2 || true
        else
            cat "$local_mirror_log" >&2 || true
        fi

        if grep -qi 'Address already in use' "$local_mirror_log"; then
            error "Local mirror self-test aborted because 127.0.0.1:${local_mirror_port} is already in use. Set CDIDX_LOCAL_MIRROR_PORT to a free port."
        fi

        if grep -Eqi 'PermissionError|Operation not permitted|Permission denied' "$local_mirror_log"; then
            error "Local mirror self-test aborted because this environment does not permit binding a loopback TCP port. Run it in a less-restricted shell or use a pre-hosted mirror."
        fi
    fi
    error "Local mirror self-test aborted before download. Check the local mirror error above."
}

wait_for_local_mirror_ready() {
    local ready_url="$1"
    local local_mirror_port="$2"
    local local_mirror_log="$3"
    local attempt=0
    local http_code=""

    while [ "$attempt" -lt 5 ]; do
        if ! kill -0 "$LOCAL_MIRROR_PID" > /dev/null 2>&1; then
            report_local_mirror_start_failure "$local_mirror_port" "$local_mirror_log"
        fi

        http_code="$(run_curl_with_optional_loopback_bypass "$ready_url" -sS -o /dev/null -w '%{http_code}' "$ready_url" 2>/dev/null || true)"
        if [ "$http_code" = "200" ]; then
            return 0
        fi

        attempt=$((attempt + 1))
        sleep 1
    done

    report_local_mirror_start_failure "$local_mirror_port" "$local_mirror_log"
}

run_local_mirror_self_test() {
    need_cmd curl
    need_cmd python3
    need_cmd tar
    need_cmd mktemp
    need_cmd awk
    need_cmd sleep

    detect_platform

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
    local local_mirror_log
    local local_mirror_base_url
    local self_test_install_dir=""
    local archive_name="CodeIndex-${RID}.tar.gz"
    local runtime_asset
    local checksum

    case "$OS_NAME" in
        linux) runtime_asset="libe_sqlite3.so" ;;
        osx)   runtime_asset="libe_sqlite3.dylib" ;;
        *)     error "Internal error: unknown OS_NAME '$OS_NAME' for local mirror self-test." ;;
    esac

    if ! local_mirror_root="$(mktemp -d /tmp/cdidx-local-mirror.XXXXXX)"; then
        error "Failed to create local mirror directory for self-test."
    fi
    LOCAL_MIRROR_DIR_CLEANUP="$local_mirror_root"
    local_mirror_log="${local_mirror_root}/local-mirror.log"

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
    : > "${local_payload_dir}/${runtime_asset}"

    (
        cd "$local_payload_dir"
        tar czf "../${archive_name}" "${BINARY_NAME}" version.json "${runtime_asset}"
    )

    if command -v sha256sum > /dev/null 2>&1; then
        checksum="$(sha256sum "${local_release_base}/${archive_name}" | awk '{print $1}')"
    elif command -v shasum > /dev/null 2>&1; then
        checksum="$(shasum -a 256 "${local_release_base}/${archive_name}" | awk '{print $1}')"
    elif command -v openssl > /dev/null 2>&1; then
        checksum="$(openssl dgst -sha256 "${local_release_base}/${archive_name}" | awk '{print $NF}')"
    else
        error "No checksum tool found (need sha256sum, shasum, or openssl) for local mirror self-test."
    fi
    printf '%s  %s\n' "$checksum" "$archive_name" > "${local_release_base}/sha256sums.txt"

    if has_explicit_self_test_install_dir; then
        if is_self_test_install_dir_risky "$INSTALL_DIR" && [ "${SELF_TEST_ALLOW_OVERWRITE:-0}" != "1" ]; then
            report_error "CDIDX_INSTALL_DIR=\"$INSTALL_DIR\" points at a real install path; refusing to run the mock self-test there."
            report_error "The self-test installs a mock cdidx that only handles --version, which would silently break the real binary."
            report_error "Unset CDIDX_INSTALL_DIR to run the self-test in an isolated temp dir, or pass --self-test-allow-overwrite if you truly want to inspect the mock layout in place."
            error "Local mirror self-test aborted to protect an existing install at ${INSTALL_DIR}."
        fi
    else
        if ! self_test_install_dir="$(mktemp -d /tmp/cdidx-self-test-install.XXXXXX)"; then
            error "Failed to create isolated install directory for local mirror self-test."
        fi
        SELF_TEST_INSTALL_DIR_CLEANUP="$self_test_install_dir"
        INSTALL_DIR="$self_test_install_dir"
    fi

    python3 -m http.server "$local_mirror_port" --bind 127.0.0.1 --directory "$local_mirror_root" > "$local_mirror_log" 2>&1 &
    LOCAL_MIRROR_PID=$!
    local_mirror_base_url="http://127.0.0.1:${local_mirror_port}"
    prepare_loopback_no_proxy_env
    wait_for_local_mirror_ready "${local_mirror_base_url}/${REPO}/releases/download/${rehearsal_version}/${archive_name}" "$local_mirror_port" "$local_mirror_log"

    info "Running local mirror self-test against ${local_mirror_base_url}/"
    if has_explicit_self_test_install_dir; then
        info "Using explicit self-test install dir: ${INSTALL_DIR}"
    else
        info "Using isolated self-test install dir: ${INSTALL_DIR}"
    fi
    SELF_TEST_LOCAL_MIRROR=1
    GITHUB_BASE_URL="${local_mirror_base_url}"
    main "$rehearsal_version"
    "${INSTALL_DIR}/${BINARY_NAME}" --version
    info "Local mirror self-test passed."
}

# Download the real release for the requested version into an isolated temp
# dir and exercise the installed binary end-to-end (--version + cdidx . --db).
# Never writes to the user's real install location, even if CDIDX_INSTALL_DIR
# is set — validation must not carry the risk of clobbering a working install.
# 実リリースを隔離された temp dir にダウンロードし、`cdidx --version` と
# 最小プロジェクトに対する `cdidx . --db <tmp>` 実行まで行う。CDIDX_INSTALL_DIR
# が設定されていても、ユーザーの実インストールには絶対に書き込まない。
run_reinstall_real() {
    local version="${1:-}"
    if [ -z "$version" ]; then
        error "--reinstall-real requires a version argument (e.g. v1.5.0)."
    fi
    case "$version" in
        v*) ;;
        *)  version="v${version}" ;;
    esac

    need_cmd curl
    need_cmd tar
    need_cmd mktemp

    detect_platform

    # Always install to an isolated temp dir. CDIDX_INSTALL_DIR is ignored
    # here on purpose: a validation mode must never risk replacing a working
    # real install with a freshly-downloaded build that turns out to be broken.
    # CDIDX_INSTALL_DIR は無視する。検証モードは実インストールを上書きしない。
    local reinstall_dir
    if ! reinstall_dir="$(mktemp -d /tmp/cdidx-reinstall-real.XXXXXX)"; then
        error "Failed to create isolated install directory for --reinstall-real."
    fi
    SELF_TEST_INSTALL_DIR_CLEANUP="$reinstall_dir"
    INSTALL_DIR="$reinstall_dir"

    info "Real reinstall validation: installing ${version} into isolated dir ${INSTALL_DIR}"

    # Signal main() to skip the trailing "quick start" banner; this is a
    # validation run, not a user-facing install.
    # main() の "quick start" バナーを抑止する。
    SELF_TEST_LOCAL_MIRROR=1
    main "$version"

    local reinstall_cdidx="${INSTALL_DIR}/${BINARY_NAME}"
    if [ ! -x "$reinstall_cdidx" ]; then
        error "Real reinstall validation: installed binary not found at ${reinstall_cdidx}."
    fi

    info "Verifying ${BINARY_NAME} --version"
    local reinstall_version_output
    if ! reinstall_version_output="$("$reinstall_cdidx" --version 2>&1)"; then
        error "Real reinstall validation: ${BINARY_NAME} --version failed."
    fi
    printf '%s\n' "$reinstall_version_output"
    local reinstall_expected_version="${version#v}"
    # Extract every v<semver> token in the output and require that the only
    # distinct token present equals v<requested>. A plain "contains the
    # requested version" check false-passes mixed output such as
    # "warning: requested v1.2.3 not installed; running v9.9.9", because the
    # requested tag appears in a diagnostic while a different version is
    # actually running. Enumerating all tokens also catches right-side
    # boundary violations (e.g. v1.2.30 captures as v1.2.30, which is not
    # equal to v1.2.3) and suffix mismatches (e.g. v1.2.3 vs v1.2.3-rc.1).
    # `grep -oE` alone has no left-boundary awareness, so `prefixv1.2.3`
    # would still extract `v1.2.3` and silently pass; awk's match() lets us
    # reject any candidate whose preceding character is itself an identifier
    # char (`[A-Za-z0-9._+-]`, the same class used for the right-side suffix
    # capture). POSIX awk's match() / RSTART / RLENGTH are supported on both
    # macOS (BSD awk) and Linux (gawk / mawk) so this stays portable.
    # ミラー取り違えや version.json ずれ、診断文に要求タグが紛れ込むケースを
    # silent pass させないため、出力中の v<semver> token を全て抽出し、
    # 唯一の値が v<要求版> と一致することを検証する。`grep -oE` だけでは
    # `prefixv1.2.3` の左境界違反が素通りするため、awk の match() で直前文字が
    # 識別子クラスなら棄却する。POSIX awk の RSTART/RLENGTH は BSD awk・gawk・
    # mawk すべて対応しているためポータブル。
    local reinstall_found_versions
    reinstall_found_versions="$(printf '%s\n' "$reinstall_version_output" \
        | awk '{
            line = $0
            while (match(line, /v[0-9]+\.[0-9]+\.[0-9]+([A-Za-z0-9._+-]*)?/)) {
                if (RSTART == 1 || substr(line, RSTART - 1, 1) !~ /[A-Za-z0-9._+-]/)
                    print substr(line, RSTART, RLENGTH)
                line = substr(line, RSTART + RLENGTH)
            }
        }' \
        | sort -u || true)"
    if [ "$reinstall_found_versions" != "v${reinstall_expected_version}" ]; then
        error "Real reinstall validation: expected exactly one version token v${reinstall_expected_version} in output, got: ${reinstall_version_output:-<empty>}."
    fi
    # Token enumeration alone still false-passes a diagnostic-only output
    # whose single extracted token happens to equal the requested tag but
    # does not represent the binary's own reported version, e.g.
    # "warning: expected package v1.2.3" or "see /releases/v1.2.3/notes".
    # Real `cdidx --version` output is exactly one non-empty line that
    # starts with `cdidx v<ver>` and, since #1550, optionally ends with a
    # parenthesized build-metadata block `(commit <sha>, built <date>,
    # <clean|dirty>)`. No bare trailing text is permitted. Require two
    # invariants:
    #   (a) EXACTLY one non-empty line in the output, rejecting multi-line
    #       shapes such as `cdidx v1.2.3\nwarning: expected package v1.2.3
    #       missing` where the first line is exact but a trailing diagnostic
    #       line slips through the token enumeration with the same single
    #       distinct version token.
    #   (b) That single non-empty line EITHER EXACTLY equals `${BINARY_NAME}
    #       v<requested>` OR equals `${BINARY_NAME} v<requested> (<build
    #       metadata>)`. Trailing-diagnostic shapes such as
    #       `cdidx v1.2.3 warning: expected package missing` (no parens
    #       around the trailing text) are rejected.
    # single-token の診断文だけで silent pass しないよう、`cdidx --version` の
    # 出力全体が 1 行の非空行で、その行が `${BINARY_NAME} v<要求版>` か
    # `${BINARY_NAME} v<要求版> (<build metadata>)` のいずれかと完全一致する
    # ことを要求する（#1550 以降、末尾に括弧で囲ったメタデータが付くケースを
    # 許容する）。末尾に括弧無しの診断文が続く `cdidx v1.2.3 warning: ...` や、
    # 先頭行の後に診断行が続く `cdidx v1.2.3\nwarning: ...` のような shape は
    # これで弾く。
    local reinstall_nonempty_line_count
    reinstall_nonempty_line_count="$(printf '%s\n' "$reinstall_version_output" | awk 'NF { count++ } END { print count + 0 }')"
    if [ "$reinstall_nonempty_line_count" != "1" ]; then
        error "Real reinstall validation: ${BINARY_NAME} --version must emit exactly one non-empty line but got ${reinstall_nonempty_line_count} non-empty lines: ${reinstall_version_output:-<empty>}."
    fi
    local reinstall_first_version_line
    reinstall_first_version_line="$(printf '%s\n' "$reinstall_version_output" | awk 'NF { print; exit }')"
    local reinstall_version_head="${BINARY_NAME} v${reinstall_expected_version}"
    local reinstall_version_line_ok=0
    if [ "$reinstall_first_version_line" = "$reinstall_version_head" ]; then
        reinstall_version_line_ok=1
    else
        case "$reinstall_first_version_line" in
            "${reinstall_version_head} ("*")")
                reinstall_version_line_ok=1
                ;;
        esac
    fi
    if [ "$reinstall_version_line_ok" != "1" ]; then
        error "Real reinstall validation: first non-empty line of ${BINARY_NAME} --version must be exactly '${reinstall_version_head}' or '${reinstall_version_head} (<build metadata>)' but got: ${reinstall_first_version_line:-<empty>}."
    fi

    # Build a tiny scratch project and exercise `cdidx . --db <tmp>` so that
    # the validation covers the real indexing path (symbol extraction, SQLite
    # FTS5, version.json load, native SQLite lib load). --self-test-local-mirror's
    # mock only handles --version, so regressions in those paths are invisible there.
    # 最小プロジェクトで `cdidx . --db <tmp>` を走らせ、シンボル抽出・FTS5・
    # version.json ロード・ネイティブ SQLite ロードまで通ることを確認する。
    local scratch_project
    if ! scratch_project="$(mktemp -d /tmp/cdidx-reinstall-scratch.XXXXXX)"; then
        error "Failed to create scratch project for --reinstall-real."
    fi
    REINSTALL_SCRATCH_CLEANUP="$scratch_project"

    cat > "${scratch_project}/sample.py" <<'PY'
def greet(name):
    return f"hello {name}"


def main():
    print(greet("world"))


if __name__ == "__main__":
    main()
PY

    local scratch_db="${scratch_project}/.cdidx/codeindex.db"
    info "Running ${BINARY_NAME} . --db ${scratch_db} against scratch project"
    if ! "$reinstall_cdidx" "$scratch_project" --db "$scratch_db"; then
        error "Real reinstall validation: ${BINARY_NAME} could not index a scratch project."
    fi
    if [ ! -s "$scratch_db" ]; then
        error "Real reinstall validation: ${BINARY_NAME} did not produce a populated index DB at ${scratch_db}."
    fi

    # Human-readable output covers the default user path. Current trimmed
    # releases are expected to support --json via source-generated CLI DTOs;
    # JsonOutputFailure is only a fallback for old/custom binaries that miss
    # serializer coverage.
    # 人間向け出力で既定のユーザー経路を検証する。現在の公式 trimmed release は
    # source-generated CLI JSON DTO により --json が動作する前提で、exit 4 は
    # serializer 登録が欠けた古い/カスタムバイナリ向けの fallback。
    info "Running ${BINARY_NAME} search greet --db ${scratch_db} to verify FTS"
    local reinstall_search_output
    if ! reinstall_search_output="$("$reinstall_cdidx" search greet --db "$scratch_db" 2>&1)"; then
        error "Real reinstall validation: ${BINARY_NAME} search returned a non-zero exit code."
    fi
    # Require a structured match block anchored at the scratch file path AND
    # the verbatim source-code signature `def greet(name):` from the scratch
    # sample.py appearing as an EXACT full-line match inside that block.
    # A successful human-readable search prints:
    #     sample.py:1-6
    #       def greet(name):
    #           return f"hello {name}"
    # with a strict path-range header (no trailing text) at column 0 and the
    # first snippet line indented with exactly two spaces followed by the
    # real Python source line. The optional single-line "grep-like" form is
    # `path:line:code` with a colon immediately after the line number and
    # nothing between the colon and the source. Earlier iterations matched
    # any header starting with `^sample\.py:[0-9]` and accepted any `greet`
    # / `def greet` / `def greet(name):` substring inside the line, so
    # adversarial shapes such as
    #     sample.py:1: warning: expected code signature def greet(name): missing    (grep-header diagnostic carrying the verbatim signature as a substring)
    #     sample.py:1-6\n  warning: expected code signature def greet(name): missing (indented diagnostic carrying the verbatim signature as a substring)
    #     sample.py:1-6\n  warning: no matches\n  def greet(name):                  (non-adjacent indented signature after a decoy diagnostic)
    # could false-pass even though no real FTS hit had occurred. The
    # state machine below enforces exact-line semantics so any line that
    # only embeds the verbatim signature as a substring of a longer
    # diagnostic, or that appears in the block after a non-matching
    # indented line, is rejected. The state machine:
    #   1. Accepts the single-line grep form only when the entire line is
    #      exactly `sample.py:<N>:def greet(name):` (end anchored — no
    #      trailing diagnostic prose, no space between the colon and the
    #      source signature).
    #   2. Enters block mode only on a strict range-form header
    #      `^sample\.py:[0-9]+-[0-9]+$` (no trailing text) and arms a
    #      one-shot "expect the first indented snippet line" flag.
    #   3. Inside an armed block, accepts only a line that is exactly
    #      `  def greet(name):` (two-space indent + the verbatim source
    #      signature + nothing else). The flag is consumed on the first
    #      two-space-indented line, so a later indented line that happens
    #      to carry the signature is rejected.
    #   4. Any other line (blank line, one-space line, non-indented
    #      diagnostic, `(N results in M files)` summary footer, an
    #      unrelated header) clears the flag, so the block is abandoned
    #      the moment the expected adjacency is broken.
    # 構造化ヘッダ（厳密な grep 形 `^sample\.py:[0-9]+:` または末尾アンカー付き
    # 範囲形 `^sample\.py:[0-9]+-[0-9]+$`）と、同じ match block 内で scratch の
    # sample.py の実ソース行 `def greet(name):` を full-line で要求する 1 つの
    # awk 状態機械。grep 形ではヘッダ行自体を `sample.py:<N>:def greet(name):`
    # に完全一致させ（コロン直後に診断文も空白も許さない）、範囲形ではヘッダ
    # 直後の 1 行目が exactly `  def greet(name):` であることを要求する
    # one-shot フラグを立てる。block 内で最初の 2 スペースインデント行が完全
    # 一致しなければフラグを消費して block を諦めるため、途中に decoy の
    # 診断行を挟んで signature 行を後置するシェイプも弾ける。`def greet
    # missing` のような「def greet を含むが引数リストを伴わない」診断文も、
    # `def greet(name): missing` のように verbatim な署名を substring として
    # 埋め込んだ診断文も、両方とも完全一致を外すため false-pass しない。
    if ! printf '%s\n' "$reinstall_search_output" | awk '
        /^sample\.py:[0-9]+:def greet\(name\):$/ {
            # Strict grep form: entire line must be exactly
            # `sample.py:<N>:def greet(name):`. Anchors both ends so a
            # diagnostic like `sample.py:1: warning: ... def greet(name):
            # missing` is rejected even though it contains the verbatim
            # signature as a substring.
            # 厳密な grep 形。行全体を `sample.py:<N>:def greet(name):` に
            # 完全一致させ、末尾に診断文が付くシェイプや、コロンと署名の間に
            # 空白が入るシェイプを弾く。
            found = 1
            exit 0
        }
        /^sample\.py:[0-9]+-[0-9]+$/ {
            # Strict range-form header — no trailing text. Arm the
            # one-shot "expect first indented line to be the verbatim
            # signature" flag; the first `^  /` line we see under this
            # header will either match exactly or consume the flag and
            # cause the block to be abandoned.
            # 厳密な range 形ヘッダ。末尾の余計なテキストを許さず、block
            # モードに入って「直後の 1 行目が exactly `  def greet(name):`
            # であるべき」という one-shot フラグを立てる。最初の
            # 2 スペースインデント行で flag を消費して完全一致を判定する。
            expect_first_indent = 1
            next
        }
        /^  / {
            # First two-space-indented line under an armed range header
            # must equal exactly `  def greet(name):`. Any other indent
            # (even one that later happens to carry the verbatim
            # signature) consumes the flag and kills the block.
            # 範囲形ヘッダ直後の最初の 2 スペースインデント行は exactly
            # `  def greet(name):` でなければならない。それ以外（途中に
            # 署名を後置するシェイプも含む）は flag を消費して block を
            # 放棄する。
            if (expect_first_indent) {
                expect_first_indent = 0
                if ($0 == "  def greet(name):") {
                    found = 1
                    exit 0
                }
            }
            next
        }
        # Any other line (blank, one-space, non-indented diagnostic,
        # unrelated header, footer) clears the adjacency flag so the
        # block is abandoned the moment adjacency is broken.
        # その他の行（空行・1 スペース行・非インデント診断行・無関係な
        # ヘッダ・フッタ）は隣接フラグを落として block を放棄する。
        { expect_first_indent = 0 }
        END { exit (found ? 0 : 1) }
    '; then
        error "Real reinstall validation: ${BINARY_NAME} search did not return a structured match block at sample.py whose first snippet line is the exact verbatim scratch-source signature 'def greet(name):'. Output: ${reinstall_search_output:-<empty>}."
    fi

    info "Real reinstall validation passed for ${version}."
}

# Probe a single URL for the doctor diagnostic. Prints HTTP status on success,
# surfaces CONNECT-tunnel 403 with the canonical upstream-proxy guidance, and
# returns 0 iff curl exited cleanly AND the response code was 2xx/3xx.
# This path uses HEAD (`-I`) because the doctor is about reachability, not
# content, and so a multi-MB release tarball does not need to be downloaded.
# doctor 用の URL probe。curl が 0 で終了し、かつ HTTP ステータスが 2xx/3xx の
# ときだけ 0 を返す。CONNECT-tunnel 403 (curl exit 56) を検知したら定型の
# 上流 proxy ガイダンスを出す。reachability 確認が目的なので HEAD (`-I`) を
# 使い、数 MB のリリース tarball を実ダウンロードしない。
probe_doctor_url() {
    local url="$1"
    local label="$2"
    info "Probing ${label}: ${url}"

    local curl_stderr
    if ! curl_stderr="$(mktemp)"; then
        report_error "${label}: failed to create curl stderr capture."
        return 1
    fi

    local http_code=""
    local curl_status=0
    # Run curl in a conditional context so `set -e` does not abort the script
    # on a non-zero curl exit; we want to inspect curl_status and surface a
    # doctor-specific error, not die here.
    # `set -e` 下で curl の失敗時にスクリプトを中断させないよう条件文脈で呼ぶ。
    # curl_status を読んで doctor 専用のエラーメッセージに変換する。
    if http_code="$(run_curl_with_optional_loopback_bypass "$url" -sSI -o /dev/null -w '%{http_code}' "$url" 2>"$curl_stderr")"; then
        curl_status=0
    else
        curl_status=$?
    fi

    local stderr_text=""
    if [ -f "$curl_stderr" ]; then
        stderr_text="$(cat "$curl_stderr")"
        rm -f "$curl_stderr"
    fi

    if [ "$curl_status" -eq 0 ]; then
        info "Result: HTTP ${http_code}"
        case "$http_code" in
            2??|3??) return 0 ;;
        esac
        report_error "${label}: HTTP ${http_code} is not a 2xx/3xx response; release reachability is not confirmed."
        return 1
    fi

    if [ "$curl_status" -eq 56 ] && is_proxy_tunnel_403 "$stderr_text"; then
        if [ -n "$stderr_text" ]; then
            printf '%s\n' "$stderr_text" >&2
        fi
        report_error "${label}: CONNECT tunnel failed with HTTP 403 (curl exit 56). This deny is happening in an upstream proxy/egress policy before TLS."
        report_error "Route substitution alone will not fix it."
        report_error "Ask your network administrator to allow-list at least one artifact host path, or point CDIDX_GITHUB_BASE_URL / CDIDX_GITHUB_API_BASE_URL at a reachable internal mirror."
        return 1
    fi

    if [ -n "$stderr_text" ]; then
        printf '%s\n' "$stderr_text" >&2
    fi
    case "$curl_status" in
        6|7|28|35|52|56)
            report_error "${label}: network error (curl exit ${curl_status}) while reaching ${url}. Check your connection, proxy, or configured mirror."
            ;;
        *)
            report_error "${label}: curl exit ${curl_status} while reaching ${url}."
            ;;
    esac
    return 1
}

# Redact the userinfo portion of a proxy URL so credentials in values such as
# `http://user:password@proxy:8080` do not get printed into logs, issue
# attachments, or support transcripts when users share `--doctor` output.
# Handles `scheme://user@host` and `scheme://user:password@host`, leaves
# credential-less URLs and non-URL values untouched, and preserves the rest of
# the URL so the host/port is still visible for diagnosing reachability.
# `http://user:password@proxy:8080` のような proxy URL の資格情報部分を
# redact し、`--doctor` の出力を log / issue / サポート窓口に貼っても秘密が
# 漏れないようにする。`scheme://user@host` / `scheme://user:password@host` の
# 両形を処理し、資格情報を含まない URL や URL 以外の値はそのまま返す。
# host/port は reachability 診断のため保持する。
redact_proxy_userinfo() {
    local value="$1"
    case "$value" in
        *://*@*)
            local scheme="${value%%://*}"
            local rest="${value#*://}"
            local hostpart="${rest#*@}"
            printf '%s://<redacted>@%s' "$scheme" "$hostpart"
            ;;
        *)
            printf '%s' "$value"
            ;;
    esac
}

# Print the active proxy environment so users can see what curl will inherit
# before the probes run. This is the first thing the doctor prints because
# misconfigured proxy env vars are the single most common cause of CONNECT
# tunnel 403 / network-policy-style failures. Values are routed through
# `redact_proxy_userinfo` so embedded credentials never surface in the output.
# curl に引き継がれる proxy 系環境変数を probe 前に表示する。
# 誤った proxy 設定は CONNECT 403 系の失敗原因として最も多いため最初に出す。
# 出力は `redact_proxy_userinfo` を通し、URL 中の資格情報が漏れないようにする。
print_doctor_proxy_env() {
    info "Proxy environment variables (inherited by curl; URL credentials redacted):"
    local var val redacted
    for var in HTTP_PROXY HTTPS_PROXY ALL_PROXY NO_PROXY http_proxy https_proxy all_proxy no_proxy; do
        # Use `printenv` instead of bash indirection so an unset variable
        # under `set -u` does not abort the function.
        # `set -u` 下で未設定変数を参照して落ちないよう `printenv` を使う。
        val="$(printenv "$var" 2>/dev/null || true)"
        if [ -n "$val" ]; then
            redacted="$(redact_proxy_userinfo "$val")"
            printf '  %s=%s\n' "$var" "$redacted"
        else
            printf '  %s=(unset)\n' "$var"
        fi
    done
}

# Network diagnostics for the installer's upstream URLs. Does not install
# anything and never writes outside /tmp. Exits 0 when every probe is reachable
# (2xx/3xx), 1 otherwise. See `is_proxy_tunnel_403` for the CONNECT-403
# advisory path used by all probes.
# installer が叩く upstream URL のネットワーク診断。インストールはしない。
# 全 probe が reachability を確認できたら exit 0、それ以外は exit 1。
# CONNECT 403 系の定型ガイダンスは `is_proxy_tunnel_403` を使い全 probe で共有する。
run_doctor() {
    local version="${1:-}"

    need_cmd curl
    need_cmd mktemp

    detect_platform

    info "cdidx installer doctor"
    info "Detected platform: ${RID}"

    # Resolve a probe version without requiring network access: explicit
    # argument first, then version.json alongside this script; fall back to
    # "no version" if neither is available so the API probe still runs and
    # the user gets a useful diagnostic instead of a hard abort.
    # probe 用バージョン解決。引数 -> version.json -> なし の順で、
    # どれも無ければ API probe だけでも走らせて診断情報を出す。
    local probe_version=""
    local probe_version_source=""
    if [ -n "$version" ]; then
        case "$version" in
            v*) probe_version="$version" ;;
            *)  probe_version="v${version}" ;;
        esac
        probe_version_source="explicit argument"
    else
        local resolved
        resolved="$(default_self_test_version)"
        if [ -n "$resolved" ] && [ "$resolved" != "v0.0.0" ]; then
            probe_version="$resolved"
            probe_version_source="version.json"
        fi
    fi

    if [ -n "$probe_version" ]; then
        info "Probing version: ${probe_version} (${probe_version_source})"
    else
        info "Probing version: unknown (no explicit version and no version.json). Only the latest-release API probe will run."
    fi

    print_doctor_proxy_env

    local api_url
    api_url="$(latest_release_api_url)"
    local api_label
    api_label="$(latest_release_api_diagnostic_label)"
    local api_status=0
    probe_doctor_url "$api_url" "$api_label" || api_status=$?

    local asset_url=""
    local asset_status=0
    local checksums_url=""
    local checksums_status=0
    if [ -n "$probe_version" ]; then
        local release_label
        release_label="$(release_host_diagnostic_label)"
        local base_url="${GITHUB_BASE_URL}/${REPO}/releases/download/${probe_version}"
        asset_url="${base_url}/CodeIndex-${RID}.tar.gz"
        checksums_url="${base_url}/sha256sums.txt"
        probe_doctor_url "$asset_url" "${release_label} (release asset ${probe_version})" || asset_status=$?
        probe_doctor_url "$checksums_url" "${release_label} (sha256sums for ${probe_version})" || checksums_status=$?
    fi

    info "Doctor summary:"
    printf '  API probe: %s\n' "$(format_doctor_probe_status "$api_status")"
    if [ -n "$probe_version" ]; then
        printf '  Release asset probe: %s\n' "$(format_doctor_probe_status "$asset_status")"
        printf '  Checksums probe: %s\n' "$(format_doctor_probe_status "$checksums_status")"
    else
        printf '  Release asset probe: skipped (no version)\n'
        printf '  Checksums probe: skipped (no version)\n'
    fi

    if [ "$api_status" -ne 0 ] || [ "$asset_status" -ne 0 ] || [ "$checksums_status" -ne 0 ]; then
        report_error "Doctor detected at least one unreachable endpoint. See the probe output above for the specific failure and next step."
        return 1
    fi

    info "Doctor: all probed endpoints are reachable."
    return 0
}

format_doctor_probe_status() {
    if [ "$1" -eq 0 ]; then
        printf '%s' "reachable"
    else
        printf '%s' "FAILED (see probe output above)"
    fi
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

    if [ "${SELF_TEST_LOCAL_MIRROR:-0}" = "1" ]; then
        return 0
    fi

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
            while [ $# -gt 0 ]; do
                case "$1" in
                    --self-test-allow-overwrite)
                        SELF_TEST_ALLOW_OVERWRITE=1
                        shift
                        ;;
                    --*)
                        error "Unknown self-test option: $1"
                        ;;
                    *)
                        break
                        ;;
                esac
            done
            run_local_mirror_self_test "${1:-}"
            ;;
        --reinstall-real)
            shift
            if [ $# -eq 0 ]; then
                error "--reinstall-real requires a version argument (e.g. v1.5.0)."
            fi
            run_reinstall_real "$1"
            ;;
        --doctor)
            shift
            run_doctor "${1:-}"
            ;;
        *)
            main "$@"
            ;;
    esac
fi
