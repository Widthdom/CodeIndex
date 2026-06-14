
remove_uninstall_file() {
    local path="$1"
    if rm -f "$path"; then
        info "Removed ${path}"
        return 0
    fi

    report_error "Failed to remove install file during uninstall: ${path}"
    return 1
}

remove_uninstall_directory() {
    local path="$1"
    if rm -rf "$path"; then
        info "Removed ${path}"
        return 0
    fi

    report_error "Failed to remove install directory during uninstall: ${path}"
    return 1
}

uninstall_cdidx() {
    info "cdidx uninstaller"
    if ! validate_normal_install_dir; then
        return 1
    fi
    acquire_install_lock

    local removed=0
    local removal_failed=0
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
            if remove_uninstall_file "$path"; then
                removed=1
            else
                removal_failed=1
            fi
        fi
    done

    if [ -d "${INSTALL_DIR}/LICENSES" ]; then
        if remove_uninstall_directory "${INSTALL_DIR}/LICENSES"; then
            removed=1
        else
            removal_failed=1
        fi
    fi

    if [ "$PURGE_CACHE_ON_UNINSTALL" = "1" ]; then
        if [ -d "$cache_dir" ]; then
            if remove_uninstall_directory "$cache_dir"; then
                removed=1
            else
                removal_failed=1
            fi
        fi
    fi

    if [ "$removal_failed" = "1" ]; then
        report_error "Uninstall incomplete because one or more files or directories could not be removed."
        return 1
    fi

    if [ "$removed" = "0" ]; then
        warn "No cdidx install files were found under ${INSTALL_DIR}."
    fi

    echo ""
    info "Uninstall complete."
    echo "Not removed: project-local .cdidx/ directories, shell profile PATH edits, shell completion scripts, or global-tool installs managed by dotnet/Homebrew."
    echo "To remove cached update metadata too, rerun with --uninstall --purge-cache."
}
