using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    // Reject JS/TS HOC candidate matches whose captured RHS uses the bare `styled.`
    // or `styled(` forms without a tagged-template backtick on the same statement.
    // The HOC regex accepts `styled[.(` `]` as the first post-identifier token so
    // the real tagged-template bindings (`styled.div\`...\``, `styled(Box)\`...\``)
    // still match, but it also lets through the factory-capture and plain-call
    // shapes (`const F = styled.div;`, `const F = styled(Box);`) which do not
    // declare a rendered component and must not be surfaced as function symbols.
    // The gate reads the raw (unmasked) source because
    // StructuralLineMasker.MaskJsTsTemplateLiteralContents replaces template
    // delimiters with space, so the masked line cannot distinguish the shapes.
    // The backtick scan is statement-local: only characters between the match end
    // and the next `;` (or next statement) are inspected, so an unrelated template
    // literal on another statement does not reopen the gate. The scanner is also
    // multi-line aware - Prettier-style styled bindings place the backtick on the
    // line after `styled.div` / `styled(Component)`, so the scan walks forward
    // across raw lines while carrying block-comment state, bounded to a short
    // lookahead window. A line that starts with a JS/TS statement-starter keyword
    // (`const`, `let`, `var`, `function`, `class`, `return`, `import`, etc.)
    // terminates the scan to model implicit ASI: `const X = styled.div\nconst Y =
    // 5;` must stay rejected even though no `;` appears on the `styled.div` line.
    // The scanner also understands line comments (`//`), block comments
    // (`/* ... */`), and plain string literals (`'...'`, `"..."`), so a backtick
    // that only lives inside a comment or string does not keep a non-template
    // binding alive, and a `;` that only lives inside a comment does not fence
    // a real backtick off from a subsequent tagged template on the same
    // statement. Closes #240 follow-up (codex review #5, #7, #8, and #9 blockers).
    // JS/TS 行における HOC 候補のうち、`styled.` / `styled(` を素のまま使い、同じ文内に
    // タグ付きテンプレートのバッククォートを持たない形（`const F = styled.div;`、
    // `const F = styled(Box);`）を弾く。HOC regex は識別子直後の `styled[.(`、`]`
    // を受け付けるためタグ付きテンプレート形（`styled.div\`...\``、`styled(Box)\`...\``）
    // はマッチさせつつ、factory 捕捉 / 素の呼び出し形も通過させてしまう。これらは
    // コンポーネントを生成しないため function シンボルとして surface してはいけない。
    // ゲートは raw 行（マスク前）を参照する - `StructuralLineMasker.MaskJsTsTemplateLiteralContents`
    // がテンプレート区切りを空白にマスクするため、マスク後では形状を区別できないのが理由。
    // バッククォート探索は文ローカル（match 終端から次の `;` または次の文まで）に限定し、
    // 別の文として配置された無関係なテンプレートリテラルでゲートを誤って解除しない。
    // さらに Prettier 整形のように `styled.div` / `styled(Component)` の次行にバッククォートを
    // 置くケースへ対応するため、スキャナはブロックコメント状態を引き継ぎつつ複数行を前方走査する
    // （行数上限付き）。継続行の最初の実トークンがタグ付きテンプレートの継続として妥当な
    // 文字（バッククォート・`.`・`<`）でない場合は ASI による文終端として走査を打ち切る。
    // これにより `const X = styled.div\nfoo(\`...\`)` や `const X = styled.div\nawait foo(\`...\`)`
    // のような「次行が式文」のケースでも phantom `function` シンボルを出さない。さらに
    // `const X = styled.div\nconst Y = 5;` のような「次行が宣言文」のケースも引き続き除外される。
    // 加えて行コメント（`//`）・ブロックコメント（`/* ... */`）・通常の文字列リテラル
    // （`'...'` / `"..."`）を構文として理解し、コメントや文字列内のバッククォートが非テンプレート
    // 束縛を延命させたり、コメント内の `;` が同一文内の本物のバッククォートより先に文終端として
    // 扱われて実タグ付きテンプレートを落とすことを防ぐ。
    // Closes #240 follow-up（codex レビュー #5・#7・#8・#9・#10・#13 の blocker 対応）。
    // The lookahead window is intentionally generous - Prettier-formatted
    // styled bindings with long `.attrs((props) => ({ ... }))` argument
    // objects routinely span more than ten lines before the backtick, and
    // truncating the scan would silently drop the binding's `function`
    // symbol. 32 lines is large enough for realistic shapes while still
    // keeping the cost bounded per match.
    // lookahead window は意図的に広めに取る - Prettier 整形で
    // `.attrs((props) => ({ ... }))` の引数オブジェクトを持つ styled 束縛は
    // 10 行を超えてからバッククォートに到達することが珍しくなく、走査を
    // 短く打ち切ると binding の `function` シンボルを silently 落としてしまう。
    // 32 行あれば実運用で見られる形は概ねカバーでき、1 マッチあたりの
    // コストも有限に保てる。
    private const int JsTsStyledFactoryGateMaxLookaheadLines = 32;

    private static bool ShouldSkipJavaScriptTypeScriptStyledFactoryCandidate(
        string? lang,
        SymbolPattern pattern,
        Match match,
        int matchOffset,
        string[] lines,
        int lineIndex)
    {
        if (lang is not ("javascript" or "typescript"))
            return false;
        if (pattern.Kind != "function" || pattern.BodyStyle != BodyStyle.None)
            return false;

        var matched = match.Value;
        var styledIdx = matched.IndexOf("styled", StringComparison.Ordinal);
        if (styledIdx < 0)
            return false;

        var afterStyled = styledIdx + "styled".Length;
        if (afterStyled >= matched.Length)
            return false;

        var next = matched[afterStyled];
        if (next != '.' && next != '(' && next != '`')
            return false;

        // `styled\`...\`` form - the match itself ends with a backtick, so it is a
        // tagged-template binding and must be kept.
        // `styled\`...\`` 形 - match 自身がバッククォートで終わるため、タグ付きテンプレート
        // 束縛として維持する。
        if (next == '`')
            return false;

        // Forward-scan raw source starting from the match's absolute end
        // position, walking across raw lines within a bounded lookahead
        // window so that Prettier-style multi-line tagged templates still
        // resolve to a real backtick. Comments (`//`, `/* ... */`) and plain
        // string literals (`'...'`, `"..."`) are skipped so only real source
        // characters drive the accept/reject decision. Block-comment state
        // carries across line boundaries.
        //
        // Two phases of operator-rejection are needed:
        //
        //   (a) BEFORE the tag-head backtick: a depth-0 operator character
        //       between the match end and the backtick (e.g.
        //       `styled.div + \`not a tag\``) breaks the tag-head chain and
        //       must reject.
        //   (b) AFTER the tag-head backtick: an operator character at depth 0
        //       that follows the closing backtick on the same expression
        //       (e.g. `styled.div\`color: red\` + theme`) also indicates the
        //       binding is a composition expression rather than a styled
        //       component, so it must reject too.
        //
        // To support (b), once a real depth-0 backtick is seen the scanner
        // walks across the entire template body - including substitutions
        // (`${ ... }`) and across raw line boundaries - to the matching
        // closing backtick, sets `tagHeadConsumed`, and continues scanning
        // for post-template operators. After tagHeadConsumed:
        //   - depth-0 `;` -> accept (statement terminator).
        //   - depth-0 operator -> reject (binary continuation).
        //   - End of lookahead window -> accept (binding is complete).
        //
        // On every continuation line (li > lineIndex) the first real
        // (non-whitespace, non-comment) character is checked:
        //   - tagHeadConsumed=false: must be `.` or backtick (tagged-template
        //     continuation), else ASI-inserted statement termination ->
        //     reject. `<` is intentionally NOT whitelisted because
        //     `<Foo>...` at statement start is a JSX element (or TS cast),
        //     not a tagged-template generic continuation - styled-components
        //     generics always appear before the backtick on the same
        //     expression.
        //   - tagHeadConsumed=true: an operator character means binary
        //     continuation of the styled expression (`styled.div\`...\`\n  +
        //     theme`) -> reject; anything else (identifier, `<`, `;`, `.`)
        //     indicates the binding has cleanly terminated -> accept.
        //
        // Within the scan we additionally track parenthesis / bracket /
        // angle / brace depth so that a backtick belonging to a nested
        // expression (e.g. inside `.attrs({ ... })`) does not count as the
        // tag head. When the pattern match already consumed an opening
        // paren (styled(, memo(, connect(, etc.) the scan starts with depth
        // -1 so the upcoming matching `)` restores the balance to 0 rather
        // than going further negative.
        // match 終端から raw ソースを前方走査する。Prettier 整形で複数行に
        // 跨がるタグ付きテンプレートにも追従できるよう、所定の行数まで改行を
        // またいで走査する。コメント（`//`、`/* ... */`）と通常文字列（`'...'`、
        // `"..."`）はスキップし、実ソース文字だけで判定する。ブロックコメント
        // 状態は行境界を跨いで持ち越す。
        //
        // 演算子による除外は 2 段階必要:
        //   (a) tag-head バッククォートの **前** - 例
        //       `styled.div + \`not a tag\``。match 終端と最初の depth 0
        //       バッククォートの間に depth 0 の演算子があれば即除外。
        //   (b) tag-head バッククォートの **後** - 例
        //       `styled.div\`color: red\` + theme`。closing backtick 以降で
        //       depth 0 の演算子が現れた場合、それは合成式（テーマ計算等）
        //       であって styled component の束縛ではないため除外する。
        //
        // (b) を成立させるため、depth 0 の本物のバッククォートを検出した
        // 時点でテンプレート本体（substitution `${ ... }` と複数行を含む）を
        // 閉じバッククォートまで一括スキップし、`tagHeadConsumed` を立てて
        // post-template operator 判定を続行する。tagHeadConsumed 後:
        //   - depth 0 の `;` -> 採用（文の終端）。
        //   - depth 0 の演算子 -> 除外（二項演算子継続）。
        //   - lookahead window の終端 -> 採用（束縛は完成）。
        //
        // 継続行（li > lineIndex）の最初の実文字に対して:
        //   - tagHeadConsumed=false: `.` または backtick でなければ ASI に
        //     よる文終端として除外。`<` は JSX 要素 / TS キャストの開始に
        //     もなるため意図的に許可しない（styled-components の generics は
        //     常に同一式内で backtick の前に書かれ、新しい行の先頭には
        //     現れない）。
        //   - tagHeadConsumed=true: 演算子文字なら二項演算子の継続として
        //     除外、それ以外（識別子・`<`・`;`・`.` 等）なら束縛は綺麗に
        //     終わったとして採用する。
        //
        // 走査中は paren / bracket / angle / brace の depth を追跡し、ネスト
        // 式（例: `.attrs({ ... })` の内側）のバッククォートを tag head と
        // 誤認しないようにする。match 側が既に開き括弧（`styled(`、`memo(`
        // 等）を消費している場合は depth を -1 から始め、対応する `)` で
        // 0 に戻るようにする。
        int depth = matched.Length > 0 && matched[^1] == '(' ? -1 : 0;
        bool inBlockComment = false;
        bool tagHeadConsumed = false;
        int maxLine = Math.Min(lines.Length - 1, lineIndex + JsTsStyledFactoryGateMaxLookaheadLines);
        int li = lineIndex;
        int i = matchOffset + match.Index + match.Length;
        bool firstCharChecked = true;
        while (li <= maxLine)
        {
            var raw = lines[li];
            while (i < raw.Length)
            {
                if (inBlockComment)
                {
                    if (i + 1 < raw.Length && raw[i] == '*' && raw[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i += 2;
                        continue;
                    }
                    i++;
                    continue;
                }

                var c = raw[i];
                // Whitespace - skip so the first-meaningful-char check sees
                // the actual continuation token.
                // 空白 - 継続行先頭判定は実トークンまで進めるためスキップする。
                if (c == ' ' || c == '\t')
                {
                    i++;
                    continue;
                }

                // Line comment - the rest of this raw line is comment.
                // 行コメント - 同一 raw 行の残りは全てコメント。
                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
                    break;

                // Block comment - skip through to the matching `*/`, possibly on
                // a later raw line (state carries via `inBlockComment`).
                // ブロックコメント - `*/` まで読み飛ばし、閉じない場合は `inBlockComment`
                // を次行へ持ち越す。
                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                if (!firstCharChecked)
                {
                    firstCharChecked = true;
                    if (depth > 0)
                    {
                        // Inside a nested expression (e.g. line 2+ of a
                        // multi-line `.attrs((props) => ({ ... }))` argument
                        // object). Continuation lines here are just
                        // expression continuation - ASI does not insert,
                        // and the leading character can be anything
                        // (identifier, `}`, etc.). Skip the first-char
                        // check and let the regular scan handle it.
                        // ネスト式の内側（例: 複数行 `.attrs((props) => ({ ... }))`
                        // 引数オブジェクトの 2 行目以降）では、継続行は単なる
                        // 式の継続であり ASI は入らない。先頭文字は識別子でも
                        // `}` でもよいので first-char 判定はスキップし通常走査
                        // に委ねる。
                    }
                    else if (tagHeadConsumed)
                    {
                        // Tag head already consumed on a previous line.
                        // Operator at the start of this line means binary
                        // continuation (`\`...\`\n  + theme`) - reject.
                        // Anything else means the styled binding has ended
                        // cleanly - accept.
                        // tag head は既に消費済み。継続行の先頭が演算子なら
                        // 二項継続なので除外、それ以外（識別子・`;`・`.` 等）
                        // なら束縛は綺麗に終わったとして採用する。
                        if (IsJsTsStyledTagHeadBreakingOperator(c))
                            return true;
                        return false;
                    }
                    else if (c != '`' && c != '.')
                    {
                        return true;
                    }
                }

                // Plain string literal - skip to the matching closing quote on
                // the same raw line. Unterminated plain strings are invalid JS/TS
                // and fall off the end of the line.
                // 通常の文字列リテラル - 同一 raw 行内の閉じクォートまで読み飛ばす。
                // 閉じない文字列は JS/TS として不正だが、そのまま行末で抜ける。
                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    i++;
                    while (i < raw.Length)
                    {
                        if (raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (raw[i] == quote)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '`')
                {
                    if (depth <= 0)
                    {
                        // Real tag head. Skip across the entire template body
                        // (potentially multi-line, including `${ ... }`
                        // substitutions) so that post-template operators on
                        // the same expression can still reject. Set
                        // `tagHeadConsumed` to switch the gate into
                        // post-template mode.
                        // 本物の tag head。post-template operator を検出できる
                        // よう、テンプレート本体（複数行・`${ ... }` 補間を
                        // 含む）を閉じバッククォートまで一括で読み飛ばし、
                        // `tagHeadConsumed` を立てて post-template モードに
                        // 切り替える。
                        i++;
                        int subDepth = 0;
                        bool closed = false;
                        while (li <= maxLine && !closed)
                        {
                            raw = lines[li];
                            while (i < raw.Length)
                            {
                                var tc = raw[i];
                                if (tc == '\\' && i + 1 < raw.Length)
                                {
                                    i += 2;
                                    continue;
                                }

                                if (subDepth == 0 && tc == '`')
                                {
                                    closed = true;
                                    i++;
                                    break;
                                }

                                if (subDepth == 0 && tc == '$' && i + 1 < raw.Length && raw[i + 1] == '{')
                                {
                                    subDepth = 1;
                                    i += 2;
                                    continue;
                                }

                                if (subDepth > 0)
                                {
                                    if (tc == '{') subDepth++;
                                    else if (tc == '}') subDepth--;
                                }

                                i++;
                            }

                            if (!closed)
                            {
                                li++;
                                i = 0;
                            }
                        }

                        if (!closed)
                        {
                            // Template did not close within the lookahead
                            // window - accept conservatively (the candidate
                            // still looks like a tagged template).
                            // テンプレートが lookahead window 内で閉じなかった
                            // - タグ付きテンプレート束縛と推定して保守的に採用。
                            return false;
                        }

                        tagHeadConsumed = true;
                        continue;
                    }

                    // depth > 0: nested template literal (e.g. an argument inside
                    // `.attrs(...)`). Not our tag head - skip over its body on
                    // this raw line to the matching closing backtick without
                    // interpreting `${...}` interpolation (good enough for the
                    // operator-detection pass since depth > 0 content is already
                    // outside the tag-head continuation chain).
                    // depth > 0: ネストしたテンプレートリテラル（例: `.attrs(...)` の引数内）。
                    // tag head ではないため、同一 raw 行内で閉じバッククォートまで読み飛ばす。
                    // `${...}` の補間は解釈しないが、depth > 0 のコンテンツは既に tag-head
                    // チェーン外なので operator 判定には影響しない。
                    i++;
                    while (i < raw.Length && raw[i] != '`')
                    {
                        if (raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                    }

                    if (i < raw.Length) i++;
                    continue;
                }

                // Arrow function token `=>` - skip as a unit so neither the
                // `=` operator branch nor the `>` close-bracket branch fires.
                // Without this, `(props) => ({})` would falsely treat `>` as
                // closing an angle-bracket and decrement depth, exposing
                // subsequent depth-0 operator characters (e.g. `?`, `:`,
                // `+`) inside the arrow body to false rejection.
                // 矢印関数 `=>` を一括スキップ。これがないと `(props) => ({})`
                // で `>` が close-bracket と誤解釈され、depth が不正に減って
                // arrow body 内の depth 0 演算子（`?`・`:`・`+` 等）が誤除外
                // されてしまう。
                if (c == '=' && i + 1 < raw.Length && raw[i + 1] == '>')
                {
                    i += 2;
                    continue;
                }

                if (c == ';')
                {
                    if (depth <= 0)
                        return !tagHeadConsumed;
                    i++;
                    continue;
                }

                if (tagHeadConsumed && depth <= 0 && (c == '<' || c == '>'))
                    return true;

                if (c == '(' || c == '[' || c == '<' || c == '{')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (c == ')' || c == ']' || c == '>' || c == '}')
                {
                    depth--;
                    i++;
                    continue;
                }

                // Depth-0 operator characters break the tag-head continuation
                // chain - the candidate is not a styled tagged-template binding.
                // After tagHeadConsumed, the same operator characters indicate
                // a post-template binary expression (`\`...\` + theme`), which
                // is also not a styled binding.
                // depth 0 の演算子文字は tag-head 継続チェーンを切るため除外する。
                // tagHeadConsumed 後でも同様で、テンプレート後の二項演算式
                // （`\`...\` + theme` 等）は styled 束縛ではない。
                if (depth <= 0 && IsJsTsStyledTagHeadBreakingOperator(c))
                    return true;

                i++;
            }

            li++;
            i = 0;
            firstCharChecked = false;
        }

        return !tagHeadConsumed;
    }

    private static bool IsJsTsStyledTagHeadBreakingOperator(char c) => c switch
    {
        '+' or '-' or '*' or '%' or '?' or '!' or '&' or '|' or '^' or '=' or ',' or ':' or '<' or '>' or '/' => true,
        _ => false,
    };
}
