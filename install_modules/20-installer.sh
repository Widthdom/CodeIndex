
existing_install_is_reusable() {
    if [ -z "$EXISTING_VERSION" ] || [ "$EXISTING_VERSION" = "0.0.0" ]; then
        return 1
    fi

    if [ ! -f "${INSTALL_DIR}/version.json" ]; then
        return 1
    fi
    if ! grep -Eq '"integrity_ok"[[:space:]]*:[[:space:]]*true' "${INSTALL_DIR}/version.json"; then
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

calculate_sha256() {
    local path="$1"

    if command -v sha256sum > /dev/null 2>&1; then
        sha256sum "$path" | awk '{print $1}'
    elif command -v shasum > /dev/null 2>&1; then
        shasum -a 256 "$path" | awk '{print $1}'
    elif command -v openssl > /dev/null 2>&1; then
        openssl dgst -sha256 "$path" | awk '{print $NF}'
    else
        error "No checksum tool found (need sha256sum, shasum, or openssl). Cannot verify release payload integrity."
    fi
}

validate_archive_members() {
    local archive="$1"
    local member

    tar tzf "$archive" | while IFS= read -r member || [ -n "$member" ]; do
        case "$member" in
            ""|/*|..|../*|*/../*|*/.. )
                error "Release archive contains unsafe member path before extraction: ${member:-<empty>}"
                ;;
        esac
    done
}

verify_payload_manifest() {
    local extract_dir="$1"
    local manifest="${extract_dir}/MANIFEST.sha256"
    local manifest_paths line expected path actual extracted_paths

    if [ ! -f "$manifest" ]; then
        if semver_ge "${VERSION#v}" "$MANIFEST_REQUIRED_VERSION"; then
            error "Release payload is missing MANIFEST.sha256. Refusing to install without per-file integrity metadata."
        fi

        warn "Release payload is missing MANIFEST.sha256; falling back to archive-level checksum verification for legacy release ${VERSION}."
        return 0
    fi

    if ! manifest_paths="$(mktemp)"; then
        error "Failed to create temporary manifest path list."
    fi
    if ! extracted_paths="$(mktemp)"; then
        rm -f "$manifest_paths"
        error "Failed to create temporary extracted path list."
    fi

    while IFS= read -r line || [ -n "$line" ]; do
        [ -n "$line" ] || continue
        expected="${line%%  *}"
        path="${line#*  }"
        case "$path" in
            ""|/*|*"/../"*|../*|*"/.." )
                error "Invalid path in release payload manifest: ${path}"
                ;;
        esac
        printf '%s\n' "$path" >> "$manifest_paths"
        if [ ! -f "${extract_dir}/${path}" ]; then
            rm -f "$manifest_paths" "$extracted_paths"
            error "Release payload manifest entry missing after extraction: ${path}"
        fi
        actual="$(calculate_sha256 "${extract_dir}/${path}")"
        if [ "$actual" != "$expected" ]; then
            rm -f "$manifest_paths" "$extracted_paths"
            error "Release payload checksum mismatch for ${path}.\n  Expected: ${expected}\n  Actual:   ${actual}"
        fi
    done < "$manifest"

    (
        cd "$extract_dir"
        find . -type f ! -name MANIFEST.sha256 | sed 's#^\./##' | LC_ALL=C sort
    ) > "$extracted_paths"

    while IFS= read -r path || [ -n "$path" ]; do
        [ -n "$path" ] || continue
        if ! grep -Fxq "$path" "$manifest_paths"; then
            rm -f "$manifest_paths" "$extracted_paths"
            error "Release payload contains file not listed in MANIFEST.sha256: ${path}"
        fi
    done < "$extracted_paths"

    rm -f "$manifest_paths" "$extracted_paths"
}

write_integrity_version_json() {
    local target="$1"
    local version="${VERSION#v}"

    printf '{"version":"%s","integrity_ok":true}\n' "$version" > "$target"
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

is_expected_release_asset_name() {
    case "$1" in
        "$BINARY_NAME"|version.json|libe_sqlite3.so|libe_sqlite3.dylib|LICENSE|COMMERCIAL_LICENSE.md|INTEGRATION_POLICY.md|TRADEMARKS.md|MANIFEST.sha256|LICENSES)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

validate_promoted_asset_name() {
    local asset="$1"

    case "$asset" in
        ""|"."|".."|/*|*/*|*\\*)
            report_error "Refusing to remove unsafe rollback asset name: ${asset:-<empty>}"
            return 1
            ;;
    esac

    if ! is_expected_release_asset_name "$asset"; then
        report_error "Refusing to remove unexpected rollback asset name: ${asset}"
        return 1
    fi

    return 0
}

remove_promoted_files() {
    local install_dir="$1"
    local promoted_files="$2"
    local asset

    for asset in $promoted_files; do
        if ! validate_promoted_asset_name "$asset"; then
            return 1
        fi
    done

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

    for asset in ${BINARY_NAME} $required_assets; do
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

download_optional_release_file() {
    local url="$1"
    local output_path="$2"
    local release_host_label
    release_host_label="$(release_host_diagnostic_label)"

    local http_code
    if ! http_code="$(curl_http_get "$url" "$output_path" "$release_host_label")"; then
        return 1
    fi

    [ "$http_code" = "200" ]
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
    need_cmd awk

    local archive_name="CodeIndex-${RID}.tar.gz"
    local base_url
    base_url="$(release_download_base_url)"
    local archive_url="${base_url}/${archive_name}"
    local checksums_url="${base_url}/sha256sums.txt"
    local checksums_signature_url="${base_url}/sha256sums.txt.asc"

    local tmpdir
    probe_temp_root
    if ! tmpdir="$(mktemp -d)"; then
        error "Failed to create temporary working directory for install."
    fi
    verify_temp_path_space "$tmpdir"
    TMPDIR_CLEANUP="$tmpdir"

    info "Downloading ${archive_name}..."
    download_release_file "$archive_url" "${tmpdir}/${archive_name}" "${archive_name}"
    verify_release_attestation "${tmpdir}/${archive_name}" "$archive_name"

    info "Downloading checksums..."
    download_release_file "$checksums_url" "${tmpdir}/sha256sums.txt" "sha256sums.txt"
    verify_release_attestation "${tmpdir}/sha256sums.txt" "sha256sums.txt"

    if checksum_signature_supported; then
        info "Downloading checksum signature..."
        if download_optional_release_file "$checksums_signature_url" "${tmpdir}/sha256sums.txt.asc"; then
            verify_checksum_signature "${tmpdir}/sha256sums.txt" "${tmpdir}/sha256sums.txt.asc"
        elif [ "$STRICT_VERIFY" = "1" ]; then
            error "Failed to download sha256sums.txt.asc while strict verification is enabled."
        else
            warn "Skipping GPG signature verification: sha256sums.txt.asc was not available. Set CDIDX_STRICT_VERIFY=1 to fail closed."
        fi
    fi

    # Verify checksum / チェックサム検証
    info "Verifying checksum..."
    local expected_checksum
    expected_checksum="$(awk -v name="$archive_name" '$2 == name { print $1; exit }' "${tmpdir}/sha256sums.txt")"

    if [ -z "$expected_checksum" ]; then
        error "Checksum for $archive_name not found in sha256sums.txt."
    fi

    local actual_checksum
    actual_checksum="$(calculate_sha256 "${tmpdir}/${archive_name}")"

    if [ "$actual_checksum" != "$expected_checksum" ]; then
        error "Checksum mismatch!\n  Expected: $expected_checksum\n  Actual:   $actual_checksum"
    fi

    # Extract into a dedicated subdirectory so we don't mix extracted files
    # with the downloaded archive/checksums when copying.
    # 展開用サブディレクトリを使い、アーカイブや checksum ファイルと混在させない。
    local extract_dir="${tmpdir}/extract"
    mkdir -p "$extract_dir"
    info "Checking archive member paths..."
    validate_archive_members "${tmpdir}/${archive_name}"
    info "Extracting..."
    tar xzf "${tmpdir}/${archive_name}" -C "$extract_dir"
    info "Verifying extracted payload..."
    verify_payload_manifest "$extract_dir"

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
    if ! chmod 700 "$stage_dir"; then
        error "Failed to restrict staging directory permissions under ${INSTALL_DIR}."
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
    write_integrity_version_json "${stage_dir}/version.json"
    chmod +x "${stage_dir}/${BINARY_NAME}"
    if ! verify_cdidx_binary "${stage_dir}/${BINARY_NAME}"; then
        return 1
    fi

    local backup_dir
    if ! backup_dir="$(mktemp -d "${INSTALL_DIR}/.cdidx-backup.XXXXXX")"; then
        error "Failed to create backup directory under ${INSTALL_DIR}."
    fi
    BACKUP_DIR_CLEANUP="$backup_dir"
    if ! chmod 0700 "$backup_dir"; then
        error "Failed to restrict backup directory permissions under ${INSTALL_DIR}."
    fi

    if ! promote_staged_install "$stage_dir" "$backup_dir" "$INSTALL_DIR" "$required_files" "$staged_assets"; then
        return 1
    fi
    chmod +x "${INSTALL_DIR}/${BINARY_NAME}"
    if ! verify_cdidx_binary "${INSTALL_DIR}/${BINARY_NAME}"; then
        return 1
    fi

    rm -rf "$stage_dir"
    STAGE_DIR_CLEANUP=""
    rm -rf "$backup_dir"
    BACKUP_DIR_CLEANUP=""

    info "Installed cdidx to ${INSTALL_DIR}/${BINARY_NAME}"
}
