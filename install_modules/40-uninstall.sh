
uninstall_cdidx() {
    info "cdidx uninstaller"
    if ! validate_normal_install_dir; then
        return 1
    fi
    acquire_install_lock

    local removed=0
    local path
    local cache_dir=""
    if [ "$PURGE_CACHE_ON_UNINSTALL" = "1" ]; then
        if ! cache_dir="$(resolve_purge_cache_dir)"; then
            return 1
        fi
    fi

    for path in \
        "${INSTALL_DIR}/${BINARY_NAME}" \
        "${INSTALL_DIR}/version.json" \
        "${INSTALL_DIR}/libe_sqlite3.so" \
        "${INSTALL_DIR}/libe_sqlite3.dylib" \
        "${INSTALL_DIR}/LICENSE" \
        "${INSTALL_DIR}/COMMERCIAL_LICENSE.md" \
        "${INSTALL_DIR}/INTEGRATION_POLICY.md" \
        "${INSTALL_DIR}/TRADEMARKS.md" \
        "${INSTALL_DIR}/MANIFEST.sha256"; do
        if [ -e "$path" ]; then
            rm -f "$path"
            info "Removed ${path}"
            removed=1
        fi
    done

    if [ -d "${INSTALL_DIR}/LICENSES" ]; then
        rm -rf "${INSTALL_DIR}/LICENSES"
        info "Removed ${INSTALL_DIR}/LICENSES"
        removed=1
    fi

    if [ "$PURGE_CACHE_ON_UNINSTALL" = "1" ]; then
        if [ -d "$cache_dir" ]; then
            rm -rf "$cache_dir"
            info "Removed ${cache_dir}"
            removed=1
        fi
    fi

    if [ "$removed" = "0" ]; then
        warn "No cdidx install files were found under ${INSTALL_DIR}."
    fi

    echo ""
    info "Uninstall complete."
    echo "Not removed: project-local .cdidx/ directories, shell profile PATH edits, shell completion scripts, or global-tool installs managed by dotnet/Homebrew."
    echo "To remove cached update metadata too, rerun with --uninstall --purge-cache."
}
