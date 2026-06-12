
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
        stderr_text="$(read_bounded_file_sample "$curl_stderr" "$CURL_STDERR_SAMPLE_BYTES" "curl stderr for ${label}")"
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
