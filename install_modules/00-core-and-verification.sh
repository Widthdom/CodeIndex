#!/usr/bin/env bash
# install.sh — One-liner installer for cdidx (CodeIndex)
# cdidxワンライナーインストーラー
#
# Usage / 使い方:
#   curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/v1.5.0/install.sh | bash -s -- v1.5.0
#   export CDIDX_ALLOW_RISKY_INSTALL_DIR=1 CDIDX_INSTALL_DIR=/usr/local/bin; curl -fsSL ... | bash
#   bash ./install.sh --self-test-local-mirror [--self-test-allow-overwrite] [vX.Y.Z]
#   bash ./install.sh --reinstall-real vX.Y.Z
#   bash ./install.sh --doctor [vX.Y.Z]
#   bash ./install.sh --uninstall [--purge-cache]
#   bash ./install.sh --verify-policy strict vX.Y.Z
#   HTTPS_PROXY=http://proxy.example:8080 bash ./install.sh --doctor
#   CDIDX_GITHUB_BASE_URL=https://github.example.internal \
#     CDIDX_GITHUB_API_BASE_URL=https://github.example.internal/api/v3 \
#     bash ./install.sh --doctor vX.Y.Z
#
# Optional env vars / 任意環境変数:
#   CDIDX_GITHUB_BASE_URL       Release download base URL override
#   CDIDX_GITHUB_API_BASE_URL   API base URL override for latest-release lookup
#   CDIDX_VERIFY_POLICY=compat|strict Verification policy (default: compat)
#   CDIDX_REQUIRE_ATTESTATION=1 Require GitHub provenance verification via gh
#   CDIDX_STRICT_VERIFY=1       Require GPG checksum-manifest signature verification
#   CDIDX_RELEASE_GPG_FINGERPRINT Expected checksum signer fingerprint
#   CDIDX_ALLOW_RISKY_INSTALL_DIR=1 Allow root/home/system install targets
#   CDIDX_LOCAL_MIRROR_PORT     Local self-test HTTP server port (default: 18765)
#   HTTPS_PROXY / HTTP_PROXY    Proxy used by curl for release and API probes
#   NO_PROXY                    Hosts that should bypass the proxy
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
INSTALL_DIR="${CDIDX_INSTALL_DIR-${HOME:-}/.local/bin}"
BINARY_NAME="cdidx"
MANIFEST_REQUIRED_VERSION="1.24.6"
GITHUB_BASE_URL="${CDIDX_GITHUB_BASE_URL:-https://github.com}"
GITHUB_API_BASE_URL="${CDIDX_GITHUB_API_BASE_URL:-https://api.github.com}"
CURL_STDERR_SAMPLE_BYTES=8192
LATEST_RELEASE_RESPONSE_MAX_BYTES=65536
VERIFY_POLICY="${CDIDX_VERIFY_POLICY:-compat}"
REQUIRE_ATTESTATION="${CDIDX_REQUIRE_ATTESTATION:-0}"
STRICT_VERIFY="${CDIDX_STRICT_VERIFY:-0}"
DEFAULT_RELEASE_GPG_FINGERPRINT=""
RELEASE_GPG_FINGERPRINT="${CDIDX_RELEASE_GPG_FINGERPRINT:-$DEFAULT_RELEASE_GPG_FINGERPRINT}"
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
INSTALL_LOCK_DIR_CLEANUP=""
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
PURGE_CACHE_ON_UNINSTALL=0

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
    if [ -n "$INSTALL_LOCK_DIR_CLEANUP" ]; then
        rm -rf "$INSTALL_LOCK_DIR_CLEANUP"
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

has_cmd() {
    command -v "$1" > /dev/null 2>&1
}

apply_verification_policy() {
    case "$VERIFY_POLICY" in
        ""|compat)
            VERIFY_POLICY="compat"
            ;;
        strict)
            REQUIRE_ATTESTATION=1
            STRICT_VERIFY=1
            ;;
        *)
            error "CDIDX_VERIFY_POLICY must be 'compat' or 'strict' (got '${VERIFY_POLICY}')."
            ;;
    esac
}

release_attestation_supported() {
    if [ "${CDIDX_INSTALL_SH_LIB_ONLY:-0}" = "1" ] && [ "${CDIDX_TEST_ENABLE_ATTESTATION:-0}" != "1" ]; then
        return 1
    fi

    [ "$GITHUB_BASE_URL" = "https://github.com" ] && [ "${SELF_TEST_LOCAL_MIRROR:-0}" != "1" ]
}

verify_release_attestation() {
    local artifact_path="$1"
    local artifact_name="$2"

    if ! release_attestation_supported; then
        if [ "$REQUIRE_ATTESTATION" = "1" ]; then
            error "GitHub provenance attestation verification is required, but the release host is not github.com. Set CDIDX_VERIFY_POLICY=compat or unset CDIDX_REQUIRE_ATTESTATION, or install from the public GitHub release."
        fi
        return 0
    fi

    if ! has_cmd gh; then
        if [ "$REQUIRE_ATTESTATION" = "1" ]; then
            error "GitHub provenance attestation verification is required, but the 'gh' command was not found. Install GitHub CLI, set CDIDX_VERIFY_POLICY=compat, or unset CDIDX_REQUIRE_ATTESTATION."
        fi
        warn "Skipping GitHub provenance attestation for ${artifact_name}: 'gh' command not found. Set CDIDX_VERIFY_POLICY=strict or CDIDX_REQUIRE_ATTESTATION=1 to require this verification."
        return 0
    fi

    info "Verifying GitHub provenance attestation for ${artifact_name}..."
    if gh attestation verify "$artifact_path" -R "$REPO" > /dev/null; then
        return 0
    fi

    if [ "$REQUIRE_ATTESTATION" = "1" ]; then
        error "GitHub provenance attestation verification failed for ${artifact_name}."
    fi

    warn "GitHub provenance attestation verification failed for ${artifact_name}; continuing with checksum verification. Set CDIDX_VERIFY_POLICY=strict or CDIDX_REQUIRE_ATTESTATION=1 to fail closed."
}

checksum_signature_supported() {
    if [ "${CDIDX_INSTALL_SH_LIB_ONLY:-0}" = "1" ] && [ "${CDIDX_TEST_ENABLE_SIGNATURE_VERIFY:-0}" != "1" ]; then
        return 1
    fi

    return 0
}

normalize_gpg_fingerprint() {
    printf '%s' "$1" | tr -d '[:space:]' | tr '[:lower:]' '[:upper:]'
}

extract_validsig_fingerprint() {
    awk '$1 == "[GNUPG:]" && $2 == "VALIDSIG" { print $3; exit }' "$1"
}

verify_checksum_signature() {
    local checksums_path="$1"
    local signature_path="$2"

    if ! has_cmd gpg; then
        if [ "$STRICT_VERIFY" = "1" ]; then
            error "GPG signature verification is required, but the 'gpg' command was not found. Install GnuPG, set CDIDX_VERIFY_POLICY=compat, or unset CDIDX_STRICT_VERIFY."
        fi
        warn "Skipping GPG signature verification for sha256sums.txt: 'gpg' command not found. Set CDIDX_VERIFY_POLICY=strict or CDIDX_STRICT_VERIFY=1 to require this verification."
        return 0
    fi

    local gpg_status="${signature_path}.status"
    local gpg_stderr="${signature_path}.stderr"
    info "Verifying checksum signature..."
    if ! gpg --batch --status-fd 1 --verify "$signature_path" "$checksums_path" > "$gpg_status" 2> "$gpg_stderr"; then
        if [ "$STRICT_VERIFY" = "1" ]; then
            error "GPG signature verification failed for sha256sums.txt."
        fi
        warn "GPG signature verification failed for sha256sums.txt; continuing with checksum verification. Set CDIDX_VERIFY_POLICY=strict or CDIDX_STRICT_VERIFY=1 to fail closed."
        return 0
    fi

    local actual_fingerprint
    actual_fingerprint="$(extract_validsig_fingerprint "$gpg_status")"
    if [ -z "$actual_fingerprint" ]; then
        if [ "$STRICT_VERIFY" = "1" ]; then
            error "GPG signature verification did not report a signer fingerprint."
        fi
        warn "GPG signature verification succeeded but no signer fingerprint was reported; continuing without fingerprint pinning."
        return 0
    fi

    if [ -z "$RELEASE_GPG_FINGERPRINT" ]; then
        if [ "$STRICT_VERIFY" = "1" ]; then
            error "GPG signature verification is strict, but no expected release signer fingerprint is configured. Set CDIDX_RELEASE_GPG_FINGERPRINT to the trusted release signing key fingerprint, or set CDIDX_VERIFY_POLICY=compat."
        fi
        warn "GPG signature verification succeeded for sha256sums.txt, but no expected release signing fingerprint is configured. Set CDIDX_RELEASE_GPG_FINGERPRINT to pin the signer, or set CDIDX_VERIFY_POLICY=strict to require it."
        return 0
    fi

    local expected_fingerprint
    expected_fingerprint="$(normalize_gpg_fingerprint "$RELEASE_GPG_FINGERPRINT")"
    actual_fingerprint="$(normalize_gpg_fingerprint "$actual_fingerprint")"
    if [ "$actual_fingerprint" != "$expected_fingerprint" ]; then
        error "GPG signature fingerprint mismatch for sha256sums.txt. Expected ${expected_fingerprint}, got ${actual_fingerprint}."
    fi
}
