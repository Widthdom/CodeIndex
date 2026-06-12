
temp_root() {
    printf '%s' "${TMPDIR:-/tmp}"
}

probe_temp_root() {
    local root
    root="$(temp_root)"

    if [ ! -d "$root" ]; then
        error "TMPDIR not usable: ${root} is not a directory. Set TMPDIR to a writable directory with at least 100 MiB free."
    fi

    local probe
    if ! probe="$(mktemp "${root%/}/.cdidx-install-probe.XXXXXX")"; then
        error "TMPDIR not writable: ${root}. Set TMPDIR to a writable directory and rerun the installer."
    fi
    rm -f "$probe"

    local free_kb=""
    if command -v df > /dev/null 2>&1; then
        free_kb="$(df -Pk "$root" 2>/dev/null | awk 'NR==2 {print $4}' || true)"
    fi

    if [ -n "$free_kb" ] && [ "$free_kb" -lt 102400 ] 2>/dev/null; then
        error "Insufficient temp space in ${root}: ${free_kb} KiB available; need at least 102400 KiB. Set TMPDIR to a larger writable directory."
    fi

    if command -v mount > /dev/null 2>&1 && mount 2>/dev/null | awk -v root="$root" '
        index($0, " on " root " ") && index($0, "noexec") { found = 1 }
        END { exit found ? 0 : 1 }
    '; then
        warn "TMPDIR appears to be on a noexec filesystem: ${root}. The installer will avoid executing staged files from TMPDIR."
    fi
}

verify_temp_path_space() {
    local path="$1"
    local free_kb=""

    if command -v df > /dev/null 2>&1; then
        free_kb="$(df -Pk "$path" 2>/dev/null | awk 'NR==2 {print $4}' || true)"
    fi

    if [ -n "$free_kb" ] && [ "$free_kb" -lt 102400 ] 2>/dev/null; then
        error "Insufficient temp space for ${path}: ${free_kb} KiB available; need at least 102400 KiB."
    fi
}

acquire_install_lock() {
    mkdir -p "$INSTALL_DIR"

    local lock_path="${INSTALL_DIR}/.cdidx-install.lock"

    if command -v flock > /dev/null 2>&1; then
        exec 9>"$lock_path"
        if ! flock -n 9; then
            error "Another cdidx install is already running for ${INSTALL_DIR}. Retry after it finishes."
        fi
        return 0
    fi

    local lock_dir="${INSTALL_DIR}/.cdidx-install.lockdir"
    if ! mkdir "$lock_dir" 2>/dev/null; then
        error "Another cdidx install is already running for ${INSTALL_DIR}. Retry after it finishes."
    fi
    INSTALL_LOCK_DIR_CLEANUP="$lock_dir"
    return 0
}

strip_version_prefix() {
    printf '%s' "$1" | sed 's/^[^0-9]*//'
}

semver_core() {
    printf '%s' "$1" | sed 's/^[^0-9]*//' | sed 's/[^0-9.].*$//'
}

semver_ge() {
    local left right
    left="$(semver_core "$1")"
    right="$(semver_core "$2")"

    awk -v left="$left" -v right="$right" '
        BEGIN {
            split(left, l, ".")
            split(right, r, ".")
            for (i = 1; i <= 3; i++) {
                li = (l[i] == "" ? 0 : l[i]) + 0
                ri = (r[i] == "" ? 0 : r[i]) + 0
                if (li > ri) exit 0
                if (li < ri) exit 1
            }
            exit 0
        }'
}

verify_cdidx_binary() {
    local binary_path="$1"
    local expected_version="${VERSION#v}"
    local version_output=""
    local actual_version=""

    if [ ! -x "$binary_path" ]; then
        report_error "Installed binary is not executable: ${binary_path}."
        return 1
    fi

    if ! version_output="$("$binary_path" --version 2>&1)"; then
        report_error "Installed binary failed to run: ${binary_path} --version."
        report_error "Output: ${version_output}"
        report_error "Likely causes include a wrong-architecture release asset or missing native runtime dependency next to the binary."
        return 1
    fi

    actual_version="$(semver_core "$version_output")"
    if [ -z "$actual_version" ]; then
        report_error "Installed binary returned an unparsable version from ${binary_path}: ${version_output}"
        return 1
    fi

    if [ -n "$expected_version" ] && [ "$actual_version" != "$expected_version" ]; then
        report_error "Installed binary version mismatch at ${binary_path}: expected ${expected_version}, got ${actual_version}."
        report_error "Check for a stale release archive, wrong architecture, or PATH shadowing by an older cdidx."
        return 1
    fi

    return 0
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

allow_risky_install_dir() {
    [ "${CDIDX_ALLOW_RISKY_INSTALL_DIR:-0}" = "1" ]
}

expand_install_dir_path() {
    local dir="$1"

    case "$dir" in
        "~"|"~/"*)
            if [ -z "${HOME:-}" ] || [ "$HOME" = "/" ]; then
                report_error "Cannot expand cdidx install directory ${dir}: HOME is empty or root."
                return 1
            fi
            dir="${HOME}${dir#\~}"
            ;;
    esac

    while [ "${#dir}" -gt 1 ]; do
        case "$dir" in
            */) dir="${dir%/}" ;;
            *) break ;;
        esac
    done

    printf '%s\n' "$dir"
}

normalize_install_dir_path() {
    local path="$1"
    local existing="$path"
    local suffix=""
    local base
    local normalized_existing

    while [ ! -e "$existing" ] && [ "$existing" != "/" ]; do
        base="$(basename -- "$existing")"
        suffix="/${base}${suffix}"
        existing="$(dirname -- "$existing")"
    done

    if [ ! -d "$existing" ]; then
        report_error "Install directory ancestor is not a directory: ${existing}"
        return 1
    fi

    normalized_existing="$(CDPATH= cd -P -- "$existing" && pwd)" || return 1
    if [ "$normalized_existing" = "/" ]; then
        if [ -n "$suffix" ]; then
            printf '%s\n' "$suffix"
        else
            printf '/\n'
        fi
    else
        printf '%s%s\n' "$normalized_existing" "$suffix"
    fi
}

normalized_home_dir() {
    local home_dir="${HOME:-}"

    if [ -z "$home_dir" ]; then
        return 1
    fi

    while [ "${#home_dir}" -gt 1 ]; do
        case "$home_dir" in
            */) home_dir="${home_dir%/}" ;;
            *) break ;;
        esac
    done

    if [ -d "$home_dir" ]; then
        (CDPATH= cd -P -- "$home_dir" && pwd)
    else
        printf '%s\n' "$home_dir"
    fi
}

is_high_risk_install_dir() {
    local dir="$1"
    local home_dir

    case "$dir" in
        /|/bin|/sbin|/tmp|/var|/var/tmp|/private/tmp|/private/var|/private/var/tmp|/usr|/usr/bin|/usr/sbin|/usr/local|/usr/local/bin|/usr/local/sbin|/usr/share|/usr/local/share|/usr/lib|/usr/local/lib|/opt|/opt/bin|/opt/homebrew|/opt/homebrew/bin|/opt/local|/opt/local/bin|/Applications|/Library|/System)
            return 0
            ;;
    esac

    home_dir="$(normalized_home_dir || true)"
    if [ -n "$home_dir" ] && [ "$dir" = "$home_dir" ]; then
        return 0
    fi

    return 1
}

validate_normal_install_dir() {
    local expanded
    local normalized

    if [ -z "${CDIDX_INSTALL_DIR+x}" ] && { [ -z "${HOME:-}" ] || [ "$HOME" = "/" ]; }; then
        report_error "Cannot safely derive cdidx install directory: HOME is empty or root."
        return 1
    fi

    if ! expanded="$(expand_install_dir_path "$INSTALL_DIR")"; then
        return 1
    fi

    case "$expanded" in
        "")
            report_error "Refusing empty cdidx install directory. Set CDIDX_INSTALL_DIR to an absolute directory."
            return 1
            ;;
        //*)
            report_error "Refusing ambiguous cdidx install directory: ${expanded}"
            return 1
            ;;
        "."|".."|./*|../*|*/./*|*/../*|*/.|*/..)
            report_error "Refusing ambiguous cdidx install directory: ${expanded}"
            return 1
            ;;
        /*) ;;
        *)
            report_error "Refusing non-absolute cdidx install directory: ${expanded}"
            return 1
            ;;
    esac

    if ! normalized="$(normalize_install_dir_path "$expanded")"; then
        return 1
    fi

    if is_high_risk_install_dir "$normalized" && ! allow_risky_install_dir; then
        report_error "Refusing risky install directory: ${normalized}. Set CDIDX_ALLOW_RISKY_INSTALL_DIR=1 to override."
        return 1
    fi

    INSTALL_DIR="$normalized"
    return 0
}

normalize_existing_or_parent_directory() {
    local path="$1"
    local parent
    local base
    local normalized_parent

    while [ "${#path}" -gt 1 ]; do
        case "$path" in
            */) path="${path%/}" ;;
            *) break ;;
        esac
    done

    if [ -d "$path" ]; then
        (CDPATH= cd -P -- "$path" && pwd)
        return
    fi

    parent="$(dirname -- "$path")"
    base="$(basename -- "$path")"
    if [ ! -d "$parent" ]; then
        report_error "Cache root parent does not exist: ${parent}"
        return 1
    fi

    normalized_parent="$(CDPATH= cd -P -- "$parent" && pwd)" || return 1
    if [ "$normalized_parent" = "/" ]; then
        printf '/%s\n' "$base"
    else
        printf '%s/%s\n' "$normalized_parent" "$base"
    fi
}

resolve_purge_cache_dir() {
    local cache_root
    local normalized_root

    if [ -n "${XDG_CACHE_HOME:-}" ]; then
        cache_root="$XDG_CACHE_HOME"
    else
        if [ -z "${HOME:-}" ] || [ "$HOME" = "/" ]; then
            report_error "Cannot safely derive cdidx cache directory: HOME is empty or root."
            return 1
        fi
        cache_root="${HOME}/.cache"
    fi

    case "$cache_root" in
        ""|"/"|".")
            report_error "Refusing to purge cdidx cache from unsafe cache root: ${cache_root:-<empty>}"
            return 1
            ;;
        /*) ;;
        *)
            report_error "Refusing to purge cdidx cache from non-absolute cache root: ${cache_root}"
            return 1
            ;;
    esac

    if ! normalized_root="$(normalize_existing_or_parent_directory "$cache_root")"; then
        return 1
    fi

    case "$normalized_root" in
        ""|"/")
            report_error "Refusing to purge cdidx cache from unsafe normalized cache root: ${normalized_root:-<empty>}"
            return 1
            ;;
    esac

    printf '%s/cdidx\n' "$normalized_root"
}

release_download_base_url() {
    printf '%s/%s/releases/download/%s' "$GITHUB_BASE_URL" "$REPO" "$VERSION"
}

is_proxy_tunnel_403() {
    printf '%s' "$1" | grep -Eqi 'CONNECT tunnel failed, response 403|HTTP code 403 from proxy after CONNECT'
}

file_size_bytes() {
    wc -c < "$1" | tr -d '[:space:]'
}

read_bounded_file_sample() {
    local path="$1"
    local max_bytes="$2"
    local label="$3"
    local byte_count

    if ! byte_count="$(file_size_bytes "$path")"; then
        return 1
    fi

    if [ "${byte_count:-0}" -le "$max_bytes" ]; then
        cat "$path"
        return 0
    fi

    head -c "$max_bytes" "$path"
    printf '\n[cdidx installer truncated %s: showing first %s of %s bytes]\n' "$label" "$max_bytes" "$byte_count"
}

curl_http_get() {
    local url="$1"
    local output_path="$2"
    local source_label="${3:-remote host}"
    local http_code
    local curl_stderr

    probe_temp_root
    if ! curl_stderr="$(mktemp)"; then
        report_error "Failed to create temporary curl stderr capture while fetching ${source_label} at $url."
        return 1
    fi
    verify_temp_path_space "$curl_stderr"

    if http_code="$(run_curl_with_optional_loopback_bypass "$url" -sSL -o "$output_path" -w '%{http_code}' "$url" 2>"$curl_stderr")"; then
        rm -f "$curl_stderr"
        printf '%s' "$http_code"
        return 0
    else
        local curl_status=$?
        local stderr_text=""
        if [ -f "$curl_stderr" ]; then
            stderr_text="$(read_bounded_file_sample "$curl_stderr" "$CURL_STDERR_SAMPLE_BYTES" "curl stderr for ${source_label}")"
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
    probe_temp_root
    if ! response_file="$(mktemp)"; then
        error "Failed to create temporary file for latest-release lookup."
    fi
    verify_temp_path_space "$response_file"

    local http_code
    if ! http_code="$(curl_http_get "$api_url" "$response_file" "$api_label")"; then
        rm -f "$response_file"
        return 1
    fi
    local explicit_version_examples
    explicit_version_examples="rerun the installer with an explicit version (for example: 'curl -fsSL https://raw.githubusercontent.com/${REPO}/vX.Y.Z/install.sh | bash -s -- vX.Y.Z', or 'bash ./install.sh vX.Y.Z' from a checkout)"
    local api_response_bytes
    if ! api_response_bytes="$(file_size_bytes "$response_file")"; then
        rm -f "$response_file"
        report_error "Failed to inspect ${api_label} response size while fetching ${api_url}."
        return 1
    fi
    if [ "${api_response_bytes:-0}" -gt "$LATEST_RELEASE_RESPONSE_MAX_BYTES" ]; then
        rm -f "$response_file"
        report_error "${api_label} response exceeded the ${LATEST_RELEASE_RESPONSE_MAX_BYTES} byte limit before shell parsing while fetching ${api_url} (HTTP ${http_code}). ${explicit_version_examples} to skip the latest-release API call."
        return 1
    fi
    local api_response
    api_response="$(cat "$response_file")"
    rm -f "$response_file"

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
