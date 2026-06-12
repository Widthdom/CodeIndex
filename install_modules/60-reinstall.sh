
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
    local reinstall_version_output_for_token_check="$reinstall_version_output"
    case "$reinstall_version_output_for_token_check" in
        *" [A newer release is available: v"*"]")
            reinstall_version_output_for_token_check="${reinstall_version_output_for_token_check% \[A newer release is available: v*\]}"
            ;;
    esac
    local reinstall_found_versions
    reinstall_found_versions="$(printf '%s\n' "$reinstall_version_output_for_token_check" \
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
    # <clean|dirty>)`, followed by the exact #1626 update-check hint when
    # the validated version is older than the latest release. No bare
    # trailing text is permitted. Require two
    # invariants:
    #   (a) EXACTLY one non-empty line in the output, rejecting multi-line
    #       shapes such as `cdidx v1.2.3\nwarning: expected package v1.2.3
    #       missing` where the first line is exact but a trailing diagnostic
    #       line slips through the token enumeration with the same single
    #       distinct version token.
    #   (b) That single non-empty line EITHER EXACTLY equals `${BINARY_NAME}
    #       v<requested>` OR equals `${BINARY_NAME} v<requested> (<build
    #       metadata>)`, with an optional exact update-check hint suffix.
    #       Trailing-diagnostic shapes such as
    #       `cdidx v1.2.3 warning: expected package missing` (no parens
    #       around the trailing text) are rejected.
    # single-token の診断文だけで silent pass しないよう、`cdidx --version` の
    # 出力全体が 1 行の非空行で、その行が `${BINARY_NAME} v<要求版>` か
    # `${BINARY_NAME} v<要求版> (<build metadata>)` に、必要なら #1626 の定型
    # update-check hint が続く形と完全一致することを要求する（#1550 以降、末尾に
    # 括弧で囲ったメタデータが付くケースを許容する）。末尾に括弧無しの診断文が続く
    # `cdidx v1.2.3 warning: ...` や、
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
    local reinstall_version_line_core="$reinstall_first_version_line"
    case "$reinstall_first_version_line" in
        *" [A newer release is available: v"*"]")
            reinstall_version_line_core="${reinstall_first_version_line% \[A newer release is available: v*\]}"
            ;;
    esac
    local reinstall_version_line_ok=0
    if [ "$reinstall_version_line_core" = "$reinstall_version_head" ]; then
        reinstall_version_line_ok=1
    else
        case "$reinstall_version_line_core" in
            "${reinstall_version_head} ("*")")
                reinstall_version_line_ok=1
                ;;
        esac
    fi
    if [ "$reinstall_version_line_ok" != "1" ]; then
        error "Real reinstall validation: first non-empty line of ${BINARY_NAME} --version must be exactly '${reinstall_version_head}', '${reinstall_version_head} (<build metadata>)', or either form with the standard update hint suffix but got: ${reinstall_first_version_line:-<empty>}."
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
