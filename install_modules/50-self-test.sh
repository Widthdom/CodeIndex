
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
    printf '{"version":"%s","integrity_ok":true}\n' "$rehearsal_version_no_prefix" > "${local_payload_dir}/version.json"
    : > "${local_payload_dir}/${runtime_asset}"

    (
        cd "$local_payload_dir"
        {
            calculate_sha256 "${BINARY_NAME}" | awk -v file="${BINARY_NAME}" '{ print $1 "  " file }'
            calculate_sha256 version.json | awk '{ print $1 "  version.json" }'
            calculate_sha256 "${runtime_asset}" | awk -v file="${runtime_asset}" '{ print $1 "  " file }'
        } > .MANIFEST.sha256.tmp
        mv .MANIFEST.sha256.tmp MANIFEST.sha256
        tar czf "../${archive_name}" MANIFEST.sha256 "${BINARY_NAME}" version.json "${runtime_asset}"
    )

    checksum="$(calculate_sha256 "${local_release_base}/${archive_name}")"
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
