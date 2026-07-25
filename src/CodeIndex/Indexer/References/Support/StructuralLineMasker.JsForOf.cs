namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private static void FilterJsForOfHeaderHits(string[] lines, List<JsTaggedTemplateHit> hits)
    {
        // Build a scan buffer that additionally blanks string literals, regex literals,
        // and line comments. The outer masker already blanked template bodies and block
        // comments, but string / regex / `//` content survives, so a literal `)` inside
        // `":"` or `/)/` or `// for (a;b;c)` would corrupt paren and `;` counting in the
        // for-of header probe. Blanking them here keeps the structural walk structural.
        // paren と `;` のカウントが文字列 / regex / 行コメント内の `)` や `;` に引きずられ
        // ないよう、外側 masker が空白化していない要素も追加で空白化したスキャンバッファを
        // 作る。template 本体と block コメントは外側で既に空白化済みのためここでは触らない。
        var scanBuffer = BuildJsForOfScanBuffer(lines);
        for (int h = hits.Count - 1; h >= 0; h--)
        {
            var hit = hits[h];
            if (hit.Name != "of")
                continue;
            if (IsJsForOfHeaderContext(scanBuffer, hit.Line - 1, hit.Column - 1))
                hits.RemoveAt(h);
        }
    }

    // Returns the masker output with single/double-quoted string spans, regex
    // literals, and `//` line-comment tails blanked out. Template literal bodies and
    // block comments are already blanked by the outer masker, so we only need to
    // handle the three remaining kinds. Unchanged lines are reused; any returned
    // replacement keeps identical column offsets so hit coordinates (Line, Column)
    // remain valid.
    // 外側の masker の出力に対し、文字列リテラル・regex リテラル・`//` 行コメント末尾を
    // 追加で空白化して返す。template 本体と block コメントは既に空白化済みなので、残る
    // 3 種類だけを処理する。未変更行は再利用し、置換行も列オフセットは元の buffer と
    // 一致するため Hit 座標は
    // そのまま利用できる。
    private static string[] BuildJsForOfScanBuffer(string[] lines)
    {
        string[]? result = null;
        var lexState = default(JsLexState);
        var activeJsStringQuote = '\0';
        lexState.Reset();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                if (result != null)
                    result[i] = line;
                continue;
            }
            char[]? buf = null;
            char[] GetBuffer() => buf ??= line.ToCharArray();
            int pos = 0;
            while (pos < line.Length)
            {
                if (activeJsStringQuote != '\0')
                {
                    pos = MaskJsTemplateHoleString(line, pos, GetBuffer(), activeJsStringQuote, startsInsideString: true, out var continuesOnNextLine);
                    if (continuesOnNextLine)
                        break;

                    activeJsStringQuote = '\0';
                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                {
                    var buffer = GetBuffer();
                    for (int k = pos; k < line.Length; k++)
                        buffer[k] = ' ';
                    pos = line.Length;
                    break;
                }
                char ch = line[pos];
                if (ch == '"' || ch == '\'')
                {
                    var quote = ch;
                    pos = MaskJsTemplateHoleString(line, pos, GetBuffer(), quote, startsInsideString: false, out var continuesOnNextLine);
                    if (continuesOnNextLine)
                    {
                        activeJsStringQuote = quote;
                        break;
                    }

                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }
                if (ch == '/' && CanStartJsRegexLiteral(lexState))
                {
                    int end = SkipJsRegexLiteral(line, pos);
                    var buffer = GetBuffer();
                    for (int k = pos; k < end; k++)
                        buffer[k] = ' ';
                    pos = end;
                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }
                pos = AdvanceJsToken(line, pos, ref lexState);
            }
            var outputLine = buf is null ? line : new string(buf);
            if (result != null)
            {
                result[i] = outputLine;
            }
            else if (!ReferenceEquals(outputLine, line))
            {
                result = (string[])lines.Clone();
                result[i] = outputLine;
            }
        }
        return result ?? lines;
    }

    // From (lineIdx, colIdx) pointing at the start of the `of` token, decide whether `of`
    // is the iterator keyword of a for-of / for-await-of header. Classic `for (init; cond;
    // step)` keeps `of` visible as a real tagged-template call.
    // `of` トークン先頭 (lineIdx, colIdx) を起点に、その `of` が for-of / for-await-of の
    // 反復子キーワードかを判定する。古典形 `for (init; cond; step)` 内の `of` はタグとして
    // 残す。
    private static bool IsJsForOfHeaderContext(string[] lines, int lineIdx, int colIdx)
    {
        if (lineIdx < 0 || lineIdx >= lines.Length)
            return false;

        if (!TryFindEnclosingOpenParen(lines, lineIdx, colIdx, out var openLine, out var openCol))
            return false;

        if (!PrecedingTokenIsForKeyword(lines, openLine, openCol))
            return false;

        return HasNoTopLevelSemicolonInParenGroup(lines, openLine, openCol);
    }

    // Walk backward from just before (startLine, startCol) through masked lines to find the
    // nearest unmatched `(`. Balanced `()` / `[]` / `{}` groups are skipped. Escaping an
    // unmatched `[` or `{` means `of` is not inside a paren-group at all; return false.
    // (startLine, startCol) の直前から masked lines を後方に走査し、釣り合っていない最
    // 近傍の `(` を探す。釣り合いのとれた `()` / `[]` / `{}` は飛ばす。未対応の `[` / `{`
    // を抜ける場合は paren-group 内にないため false を返す。
    private static bool TryFindEnclosingOpenParen(string[] lines, int startLine, int startCol, out int openLine, out int openCol)
    {
        openLine = -1;
        openCol = -1;
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        int curCol = startCol - 1;
        for (int li = startLine; li >= 0; li--)
        {
            var line = lines[li];
            if (li != startLine)
                curCol = line.Length - 1;
            for (int c = curCol; c >= 0; c--)
            {
                char ch = line[c];
                if (ch == ')') { parenDepth++; continue; }
                if (ch == ']') { bracketDepth++; continue; }
                if (ch == '}') { braceDepth++; continue; }
                if (ch == '[')
                {
                    if (bracketDepth > 0) { bracketDepth--; continue; }
                    return false;
                }
                if (ch == '{')
                {
                    if (braceDepth > 0) { braceDepth--; continue; }
                    return false;
                }
                if (ch == '(')
                {
                    if (parenDepth > 0) { parenDepth--; continue; }
                    openLine = li;
                    openCol = c;
                    return true;
                }
            }
        }
        return false;
    }

    // Check whether the token immediately before the `(` at (openLine, openCol) is `for`
    // (optionally followed by an `await` token between `for` and `(`). Whitespace and
    // line breaks between the keyword and `(` are tolerated.
    // (openLine, openCol) の `(` 直前トークンが `for`（`for` と `(` の間に `await` が入る
    // 形も許容）であるかを判定する。キーワードと `(` の間の空白・改行は許容する。
    private static bool PrecedingTokenIsForKeyword(string[] lines, int openLine, int openCol)
    {
        int li = openLine;
        int c = openCol - 1;
        if (!SkipWhitespaceBackward(lines, ref li, ref c))
            return false;
        if (!TryReadIdentifierBackward(lines, ref li, ref c, out var token1))
            return false;
        if (token1 == "for")
            return true;
        if (token1 != "await")
            return false;
        if (!SkipWhitespaceBackward(lines, ref li, ref c))
            return false;
        if (!TryReadIdentifierBackward(lines, ref li, ref c, out var token2))
            return false;
        return token2 == "for";
    }

    // Starting from `(` at (openLine, openCol), walk forward to the matching `)` and
    // report whether the paren group contains zero top-level `;`. Zero means for-of /
    // for-await-of shape; any top-level `;` means classic `for (init; cond; step)`.
    // (openLine, openCol) の `(` から対応する `)` までを前方走査し、トップレベルの `;` が
    // 1 つも無ければ for-of / for-await-of 形、1 つ以上あれば古典形 `for (init; cond;
    // step)` と判断する。
    private static bool HasNoTopLevelSemicolonInParenGroup(string[] lines, int openLine, int openCol)
    {
        int parenDepth = 1;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (int li = openLine; li < lines.Length; li++)
        {
            var line = lines[li];
            int startCol = (li == openLine) ? openCol + 1 : 0;
            for (int c = startCol; c < line.Length; c++)
            {
                char ch = line[c];
                if (ch == '(') { parenDepth++; continue; }
                if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                        return true;
                    continue;
                }
                if (ch == '[') { bracketDepth++; continue; }
                if (ch == ']') { if (bracketDepth > 0) bracketDepth--; continue; }
                if (ch == '{') { braceDepth++; continue; }
                if (ch == '}') { if (braceDepth > 0) braceDepth--; continue; }
                if (ch == ';' && parenDepth == 1 && bracketDepth == 0 && braceDepth == 0)
                    return false;
            }
        }
        return false;
    }

    private static bool SkipWhitespaceBackward(string[] lines, ref int li, ref int c)
    {
        while (true)
        {
            while (c < 0)
            {
                li--;
                if (li < 0)
                    return false;
                c = lines[li].Length - 1;
            }
            char ch = lines[li][c];
            if (IsJsInterTokenWhitespace(ch))
            {
                c--;
                continue;
            }
            return true;
        }
    }

    // ECMAScript treats inter-token whitespace as any WhiteSpace (TAB / VT / FF / SP, NBSP
    // `U+00A0`, BOM `U+FEFF`, every `Zs` category codepoint) or LineTerminator. Our per-line
    // buffer is already split on `\r` / `\n`, but non-ASCII whitespace such as NBSP and
    // U+3000 survives inside the line and must still be recognised when backing up between
    // tokens. `char.IsWhiteSpace` matches `Zs` plus common ASCII controls, but in .NET 8
    // `char.IsWhiteSpace('\uFEFF')` is `false` (BOM is categorised as `Cf`/Format), so BOM
    // must be added explicitly. ZWSP `U+200B` is deliberately excluded — ECMAScript does
    // not treat it as WhiteSpace and `char.IsWhiteSpace` already returns false for it.
    // ECMAScript のトークン間スペースは WhiteSpace（TAB / VT / FF / SP、NBSP `U+00A0`、BOM
    // `U+FEFF`、`Zs` 全域）および LineTerminator。行バッファは既に `\r` / `\n` で分割済み
    // だが、NBSP や U+3000 のような非 ASCII 空白は行内に残るため、トークン間の後方走査でも
    // 取り扱う必要がある。.NET 8 では `char.IsWhiteSpace('\uFEFF')` は `false`（BOM は
    // `Cf`/Format 扱い）なので BOM は明示的に足す必要がある。ZWSP `U+200B` は ECMAScript
    // の WhiteSpace ではなく、`char.IsWhiteSpace` も false を返すため意図通りに除外される。
    private static bool IsJsInterTokenWhitespace(char c) => c == '\uFEFF' || char.IsWhiteSpace(c);

    private static bool TryReadIdentifierBackward(string[] lines, ref int li, ref int c, out string token)
    {
        token = string.Empty;
        if (li < 0 || li >= lines.Length || c < 0)
            return false;
        var line = lines[li];
        if (c >= line.Length || !IsJsIdentifierPart(line[c]))
            return false;
        int end = c + 1;
        while (c >= 0 && IsJsIdentifierPart(line[c]))
            c--;
        int start = c + 1;
        if (!IsJsIdentifierStart(line[start]))
            return false;
        token = line.Substring(start, end - start);
        return true;
    }

    // Advance past one JS/TS token (identifier run, numeric run, single non-string/regex char)
    // and update lexer state so the next `/` can be classified as regex-start or division.
    // 識別子の連続や数値、単一文字を 1 token として進め、次の `/` を regex / division に
    // 振り分けられるよう lex state を更新する。
    private static int AdvanceJsToken(string line, int pos, ref JsLexState lexState)
    {
        var c = line[pos];
        if (char.IsWhiteSpace(c))
            return pos + 1;

        if (IsJsIdentifierStart(c))
        {
            int start = pos;
            pos++;
            while (pos < line.Length && IsJsIdentifierPart(line[pos]))
                pos++;
            lexState.SetIdentifier(line.Substring(start, pos - start));
            return pos;
        }

        if (char.IsDigit(c))
        {
            while (pos < line.Length && (char.IsLetterOrDigit(line[pos]) || line[pos] == '.' || line[pos] == '_'))
                pos++;
            lexState.SetKind(JsPrevTokenKind.Numeric);
            return pos;
        }

        // Postfix / prefix `++` and `--` both produce a numeric-typed expression,
        // so the following `/` must be division, not a regex start. Consume as one
        // 2-char token to stop the second `+` / `-` from being classified as `Other`.
        // postfix / prefix の `++` と `--` は数値を生むため、続く `/` は division と
        // 扱う必要がある。2 文字 token として消費し、2 文字目が `Other` に落ちて
        // 直後の `/` を regex と誤判定するのを防ぐ。
        if ((c == '+' || c == '-') && pos + 1 < line.Length && line[pos + 1] == c)
        {
            lexState.SetKind(JsPrevTokenKind.Numeric);
            return pos + 2;
        }

        switch (c)
        {
            case '(':
                // Remember whether this `(` opens a statement-head control-flow
                // clause. Its matching `)` will need to keep the following `/`
                // regex-legal rather than flipping to division.
                // この `(` が statement-head control-flow（`if (x)` など）を
                // 開いているかを stack に記録し、対応する `)` の直後の `/` を
                // division ではなく regex literal として扱えるようにする。
                var openIsStmtHead = lexState.PrevTokenKind == JsPrevTokenKind.Identifier
                    && IsJsStatementHeadKeyword(lexState.PrevIdentifier);
                lexState.ParenStatementHead?.Push(openIsStmtHead);
                lexState.SetKind(JsPrevTokenKind.Other);
                break;
            case ')':
                var closeIsStmtHead = lexState.ParenStatementHead is { Count: > 0 }
                    && lexState.ParenStatementHead.Pop();
                // Statement-head `)` tags the following `/` as regex-legal and the
                // following `{` as a statement block; other `)` flips `/` to division
                // and `{` to an object-literal-style expression brace.
                // statement-head の `)` は続く `/` を regex、続く `{` を block と扱う。
                // それ以外の `)` は `/` を division、`{` を object literal 的な
                // expression brace と扱う。
                lexState.SetKind(closeIsStmtHead ? JsPrevTokenKind.StatementHeadCloseParen : JsPrevTokenKind.CloseParen);
                break;
            case ']':
                lexState.SetKind(JsPrevTokenKind.CloseBracket);
                break;
            case ':':
                // `case expr :` / `default :` — the case-label colon. Treat it
                // as such only when the paren depth is back to what it was at
                // the `case` / `default` keyword, so object-key, ternary, and
                // type-annotation colons inside the case expression do not
                // consume the hint.
                // `case expr :` / `default :` の case ラベル終端 `:`。paren
                // 深さが `case` / `default` 時点と同じに戻ったときだけ使い、
                // case 式内の object-key / ternary / type annotation の `:`
                // でヒントを消費しないようにする。
                if (lexState.CaseLabelPending
                    && (lexState.ParenStatementHead?.Count ?? 0) == lexState.CaseLabelBaseParenDepth)
                {
                    lexState.CaseLabelPending = false;
                    lexState.CaseColonBlockPending = true;
                }
                lexState.SetKind(JsPrevTokenKind.Other);
                break;
            case ';':
                // `;` terminates any in-progress case-label tracking.
                // `;` で case ラベル追跡を打ち切る。
                lexState.CaseLabelPending = false;
                lexState.CaseColonBlockPending = false;
                lexState.SetKind(JsPrevTokenKind.Other);
                break;
            case '>':
                // `=>` is the only 2-char JS token we need to distinguish here: `{`
                // following `=>` opens an arrow-function body (a statement block, so
                // the next `/` inside is regex), while `{` following most other tokens
                // opens an object literal / expression brace.
                // `=>` は 2 文字 token のうち本マスカーで必要な唯一のケース。続く `{`
                // が arrow body（statement block）か object literal / expression
                // brace かを分けるフラグとして使う。
                if (pos > 0 && line[pos - 1] == '=')
                    lexState.SetKind(JsPrevTokenKind.Arrow);
                else
                    lexState.SetKind(JsPrevTokenKind.Other);
                break;
            // `}` in normal JS / TS code is context-dependent: after a statement block
            // (`if (x) {}`) a `/` legitimately starts a regex; after an object literal
            // in expression position a `/` is division. We classify as `Other` so the
            // regex scanner still runs — that lets us correctly skip `/regex/` literals
            // that may contain backticks or braces which would otherwise open a phantom
            // template literal. Inside template-literal holes the closing brace is
            // handled separately (see `JsPrevTokenKind.CloseBrace` path below).
            // 通常コードの `}` は文脈依存で、`if (x) {}` のあとは regex、object literal
            // のあとは division。ここでは `Other` として regex scanner に任せ、中に
            // backtick や brace を含む `/regex/` を取りこぼして phantom template を
            // 開かないようにする。テンプレート hole 内のブレース close は別扱い。
            default:
                lexState.SetKind(JsPrevTokenKind.Other);
                break;
        }

        return pos + 1;
    }

    private static bool IsJsIdentifierStart(char c) =>
        c == '_' || c == '$' || char.IsLetter(c);

    private static bool IsJsIdentifierPart(char c) =>
        c == '_' || c == '$' || char.IsLetterOrDigit(c);

    // Backward-scan the masked buffer at a template-literal opener backtick for a tag
    // identifier such as `gql`, `styled.div` (last segment), or `html<T>` (generics are
    // skipped). Whitespace between the identifier and the backtick is tolerated so
    // `html \`...\`` still matches. `IsIgnoredCallName` downstream filters out keywords
    // like `return` / `throw` / `await` / `typeof` that can legally precede a plain
    // template literal.
    // マスク済みバッファを opener バッククォート位置から後方スキャンし、`gql` や
    // `styled.div`（最後のセグメント）、`html<T>`（ジェネリクスを読み飛ばす）の
    // タグ識別子を取り出す。識別子とバッククォートの間の空白は許容し、
    // `return` / `throw` / `await` / `typeof` のようなプレーンテンプレートの前に
    // 立ちうるキーワードは呼び出し側の `IsIgnoredCallName` で除外する。
}
