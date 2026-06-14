
# --- PATH guidance / PATHガイダンス ---

active_cdidx_path() {
    command -v "$BINARY_NAME" 2>/dev/null || true
}

list_path_cdidx_binaries() {
    local old_ifs="$IFS"
    local dir
    IFS=:
    for dir in ${PATH:-}; do
        [ -n "$dir" ] || dir="."
        if [ -x "${dir}/${BINARY_NAME}" ]; then
            printf '%s\n' "${dir}/${BINARY_NAME}"
        fi
    done
    IFS="$old_ifs"
}

print_path_cdidx_versions() {
    local binary
    list_path_cdidx_binaries | while IFS= read -r binary; do
        [ -n "$binary" ] || continue
        printf '    %s -> %s\n' "$binary" "$("$binary" --version 2>/dev/null || printf '%s' "unavailable")"
    done
}

candidate_shell_profile() {
    local shell_name
    shell_name="$(basename "${SHELL:-/bin/bash}")"
    case "$shell_name" in
        zsh)  printf '%s' "${HOME}/.zshrc" ;;
        bash)
            if [ -f "${HOME}/.bash_profile" ]; then
                printf '%s' "${HOME}/.bash_profile"
            else
                printf '%s' "${HOME}/.bashrc"
            fi
            ;;
        *)    printf '%s' "${HOME}/.profile" ;;
    esac
}

append_path_to_shell_profile() {
    local profile_path
    profile_path="$(candidate_shell_profile)"
    local profile_update_path
    profile_update_path="$profile_path"
    if [ -L "$profile_path" ]; then
        local link_target
        if ! link_target="$(readlink "$profile_path")" || [ -z "$link_target" ]; then
            report_error "Failed to resolve symlinked shell profile: ${profile_path}"
            return 1
        fi
        case "$link_target" in
            /*) profile_update_path="$link_target" ;;
            *) profile_update_path="$(dirname "$profile_path")/${link_target}" ;;
        esac
    fi

    local profile_dir
    profile_dir="$(dirname "$profile_update_path")"
    if ! mkdir -p "$profile_dir"; then
        report_error "Failed to create shell profile directory: ${profile_dir}"
        return 1
    fi

    if [ -f "$profile_update_path" ] && grep -F "export PATH=\"${INSTALL_DIR}:\$PATH\"" "$profile_update_path" >/dev/null 2>&1; then
        info "PATH export already present in ${profile_update_path}"
        return 0
    fi

    local tmp_path
    if ! tmp_path="$(mktemp "${profile_update_path}.cdidx-tmp.XXXXXX")"; then
        report_error "Failed to create temporary shell profile file next to ${profile_update_path}"
        return 1
    fi

    if [ -f "$profile_update_path" ]; then
        if ! cp -p "$profile_update_path" "$tmp_path"; then
            report_error "Failed to copy ${profile_update_path} before updating PATH."
            rm -f "$tmp_path"
            return 1
        fi
    else
        if ! : > "$tmp_path"; then
            report_error "Failed to initialize temporary shell profile file: ${tmp_path}"
            rm -f "$tmp_path"
            return 1
        fi
    fi

    {
        printf '\n# Added by cdidx installer\n'
        printf 'export PATH="%s:$PATH"\n' "$INSTALL_DIR"
    } >> "$tmp_path" || {
        report_error "Failed to append PATH update to temporary shell profile file: ${tmp_path}"
        rm -f "$tmp_path"
        return 1
    }

    if [ -f "$profile_update_path" ]; then
        local backup_path
        if ! backup_path="$(mktemp "${profile_update_path}.cdidx-backup.XXXXXX")"; then
            report_error "Failed to create shell profile backup path for ${profile_update_path}"
            rm -f "$tmp_path"
            return 1
        fi
        if ! cp -p "$profile_update_path" "$backup_path"; then
            report_error "Failed to back up ${profile_update_path} to ${backup_path}"
            rm -f "$tmp_path" "$backup_path"
            return 1
        fi
        info "Backed up ${profile_update_path} to ${backup_path}"
    fi

    if ! mv -f "$tmp_path" "$profile_update_path"; then
        report_error "Failed to atomically replace ${profile_update_path}. Temporary file preserved at ${tmp_path}"
        return 1
    fi

    info "Added ${INSTALL_DIR} to PATH in ${profile_update_path}"
}

check_path() {
    if [ "${SELF_TEST_LOCAL_MIRROR:-0}" = "1" ]; then
        return 0
    fi

    local installed_bin="${INSTALL_DIR}/${BINARY_NAME}"
    local active_bin
    active_bin="$(active_cdidx_path)"

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

    if [ "${CDIDX_INSTALL_UPDATE_PATH:-0}" = "1" ]; then
        append_path_to_shell_profile
        case ":${PATH}:" in
            *":${INSTALL_DIR}:"*) ;;
            *) PATH="${INSTALL_DIR}:${PATH}"; export PATH ;;
        esac
    else
        echo "  To let the installer update your shell profile, rerun with CDIDX_INSTALL_UPDATE_PATH=1."
        echo ""
    fi

    active_bin="$(active_cdidx_path)"
    if [ -n "$active_bin" ] && [ "$active_bin" != "$installed_bin" ]; then
        warn "The active cdidx on PATH is ${active_bin}, not the newly installed ${installed_bin}."
        warn "An earlier PATH entry is shadowing the new install."
        echo ""
        echo "  cdidx binaries found on PATH:"
        print_path_cdidx_versions
        echo ""
        echo "  Put ${INSTALL_DIR} before the earlier directory in PATH, or rerun with CDIDX_INSTALL_UPDATE_PATH=1."
        echo ""
        return 0
    fi

    if [ -n "$active_bin" ]; then
        if ! "$active_bin" --version >/dev/null 2>&1; then
            warn "The active cdidx at ${active_bin} failed to run --version. Check architecture and native runtime assets."
        fi
    fi
}
