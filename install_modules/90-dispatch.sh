
# --- Main / メイン ---

main() {
    info "cdidx installer"
    if [ "${SELF_TEST_LOCAL_MIRROR:-0}" != "1" ]; then
        if ! validate_normal_install_dir; then
            exit 1
        fi
    fi
    detect_platform
    info "Detected platform: ${RID}"
    acquire_install_lock
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
    while [ "${1:-}" = "--strict-verify" ]; do
        STRICT_VERIFY=1
        shift
    done

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
        --uninstall)
            shift
            while [ $# -gt 0 ]; do
                case "$1" in
                    --purge-cache)
                        PURGE_CACHE_ON_UNINSTALL=1
                        shift
                        ;;
                    --*)
                        error "Unknown uninstall option: $1"
                        ;;
                    *)
                        error "--uninstall does not accept a version argument."
                        ;;
                esac
            done
            uninstall_cdidx
            ;;
        *)
            main "$@"
            ;;
    esac
fi
