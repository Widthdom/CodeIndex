namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private enum JsPrevTokenKind { None, Identifier, Numeric, Literal, CloseParen, StatementHeadCloseParen, CloseBracket, CloseBrace, Arrow, Other }

    private struct JsLexState
    {
        public JsPrevTokenKind PrevTokenKind;
        public string PrevIdentifier;
        // Tracks whether each open `(` was preceded by a statement-head keyword
        // (`if`/`while`/`for`/`switch`/`catch`/`with`). After the matching `)`,
        // a following `/` begins a regex literal, not division.
        // 各 `(` の直前が statement-head キーワード（`if` / `while` / `for` /
        // `switch` / `catch` / `with`）だったかを追跡し、対応する `)` の直後に
        // 続く `/` を division ではなく regex literal として扱えるようにする。
        public Stack<bool> ParenStatementHead;
        // True after a declaration keyword (`class`, TypeScript `enum` /
        // `interface` / `namespace` / `module`) until the next `{` opens its
        // body. Forces `class Foo {}`, `enum Local {}`, `interface Local {}`,
        // `namespace Local {}`, and `module Local {}` to be classified as a
        // statement block instead of an object-literal expression brace, so
        // the matching `}` stays regex-legal and a following `/regex/` does
        // not flip to division and swallow backticks as a phantom template
        // opener.
        // `class` や TypeScript の `enum` / `interface` / `namespace` /
        // `module` キーワードの後から次の `{` で body が開くまで true。
        // `class Foo {}` / `enum Local {}` / `interface Local {}` /
        // `namespace Local {}` / `module Local {}` の `{` を object literal
        // ではなく statement block として扱わせ、対応する `}` を regex-legal
        // に保つことで、続く `/regex/` が division に倒れて regex 本文の
        // backtick を phantom template 開始として読んでしまうのを防ぐ。
        public bool ClassHeaderPending;
        // True after `case` / `default` keyword and cleared at the first `:` at
        // paren depth 0. Used to recognize the following `:` as a case-label
        // colon (not object-key / ternary / type-annotation colon).
        // `case` / `default` キーワード直後に true、paren 深さ 0 の `:` で解除。
        // 以降の `:` が case ラベル終端の `:` か（object key / ternary / type
        // annotation の `:` でないか）を区別するために使う。
        public bool CaseLabelPending;
        // Paren depth captured when `case` / `default` was seen. The matching
        // case-label `:` appears at the same depth; a `:` at a deeper paren
        // level belongs to an object-literal key, ternary, or type annotation
        // inside the case expression and must not flip CaseColonBlockPending.
        // Capturing the base depth (instead of requiring depth 0) is required
        // because the enclosing template-hole expression may itself be wrapped
        // in one or more `(` — e.g. `${(() => { switch (x) { case 1: ... }})()}`.
        // `case` / `default` を読んだ時点の paren 深さ。case ラベル終端の `:`
        // は同じ深さに現れ、それより深い `:` は case 式内の object key /
        // ternary / type annotation の `:` で、case label 扱いにしてはならない。
        // テンプレートホール全体が `(() => { ... })()` 等でラップされている
        // 場合に count==0 を要求すると case 内の `:` をスキップできないため、
        // `case` 時点の深さを基準値として保存する。
        public int CaseLabelBaseParenDepth;
        // True after a case-label `:` is consumed; the next `{` opens a
        // statement block (`case 1: {}`, `default: {}`), so the matching `}`
        // must keep `/regex/` regex-legal. Consumed by the next `{`.
        // case ラベル終端の `:` 消費直後に true。次の `{` は statement block
        // （`case 1: {}` / `default: {}`）として扱い、対応する `}` 後の
        // `/regex/` を regex-legal に保つ。次の `{` で消費。
        public bool CaseColonBlockPending;

        public void Reset()
        {
            PrevTokenKind = JsPrevTokenKind.None;
            PrevIdentifier = string.Empty;
            ClassHeaderPending = false;
            CaseLabelPending = false;
            CaseLabelBaseParenDepth = 0;
            CaseColonBlockPending = false;
            if (ParenStatementHead is null)
                ParenStatementHead = new Stack<bool>();
            else
                ParenStatementHead.Clear();
        }

        public void SetKind(JsPrevTokenKind kind)
        {
            PrevTokenKind = kind;
            PrevIdentifier = string.Empty;
        }

        public void SetIdentifier(string word)
        {
            PrevTokenKind = JsPrevTokenKind.Identifier;
            PrevIdentifier = word;
            if (IsJsDeclarationBodyKeyword(word))
                ClassHeaderPending = true;
            if (word == "case" || word == "default")
            {
                CaseLabelPending = true;
                CaseLabelBaseParenDepth = ParenStatementHead?.Count ?? 0;
            }
        }
    }

    private static bool IsJsStatementHeadKeyword(string word) =>
        word is "if" or "while" or "for" or "switch" or "catch" or "with";

    // Keywords whose body is a statement block, not an object-literal expression brace.
    // `class` is JS/TS; `enum`, `interface`, `namespace`, and `module` are TypeScript
    // declarations whose `{...}` body must also keep a following `/regex/` regex-legal.
    // body が statement block になる宣言キーワード。`class` は JS/TS、
    // `enum` / `interface` / `namespace` / `module` は TypeScript の宣言で、
    // 対応する `}` の直後の `/regex/` を regex-legal に保つ必要がある。
    private static bool IsJsDeclarationBodyKeyword(string word) =>
        word is "class" or "enum" or "interface" or "namespace" or "module";

    // JavaScript/TypeScript template literals: `...` with ${expr} interpolation holes.
    // Interpolation hole contents are preserved (not masked) so the call-graph keeps real call edges.
    // Regex literals are skipped at the outer and hole scopes so a backtick inside a regex
    // does not start a phantom template and a `}` inside a regex does not close a hole early.
    // JavaScript/TypeScript のテンプレートリテラル `...` と ${expr} 補間ホール。
    // ホール内の本物のコードは参照抽出に見せるためマスクしない。
    // regex literal は外側と hole 内の両方でスキップし、regex 中の backtick が template を
    // 誤って開始したり `}` が hole を早く閉じたりするのを避ける。
    private static void MaskJsTsTemplateLiteralContents(
        string[] lines,
        bool collectTaggedTemplateHits,
        ref List<JsTaggedTemplateHit>? taggedTemplateHits,
        string? lang = null)
    {
        // `<...>` before a backtick is a TypeScript-only generic type-argument form. In plain
        // JavaScript the same character sequence is always a comparison chain (`foo<bar>\`x\``
        // is `(foo<bar)>\`x\``), so never strip the bracketed range when indexing JS.
        // `<...>` 付きのタグ付きテンプレートは TypeScript 限定のジェネリクス構文。プレーン
        // な JavaScript では同じ並びが常に比較式になるため、JS を索引するときは剥がさない。
        var allowGenericTag = string.Equals(lang, "typescript", StringComparison.Ordinal);
        var frames = new Stack<ScannerFrame>();
        // `lexState` must persist across lines so that multi-line expressions in
        // template-literal holes keep the preceding token context. For example,
        // `${(() => value\n  / 2 + runTask())()}` continues on a new line: the `/`
        // at the start of line 2 is division (prev token `value`), not a regex
        // opener. Resetting state per line caused `lexState.PrevTokenKind` to be
        // `None`, flipping `/` into regex mode and swallowing the closing `}` and
        // backtick.
        // `lexState` はホール内の複数行式で直前トークンを保持するため、行をまたいで
        // 維持する必要がある。行頭で Reset すると継続行の `/` が常に regex 扱いに
        // なり、hole を閉じる `}` やバッククォートを巻き込んでしまう。
        var lexState = default(JsLexState);
        // Active quote for a JS/TS single- or double-quoted string that started
        // inside the current top-most template hole and continued past a physical
        // line boundary via trailing `\`. The continuation can only belong to the
        // current top frame at the start of the next line, so a single scanner-wide
        // state slot is enough.
        // テンプレートホール内で始まり、行末 `\` により次行へ継続した JS/TS 単/二重
        // 引用符文字列の active quote。行境界で継続可能なのは次行開始時の最上位 hole
        // だけなので、scanner 全体で 1 スロット持てば十分。
        var activeJsHoleStringQuote = '\0';
        // Top-level JS/TS single- or double-quoted string that continues across a
        // physical line boundary. The next line must resume inside the string before
        // any brace/comment/template logic runs.
        // 行をまたいで継続する top-level の JS/TS 単/二重引用符文字列。
        // 次行は brace/comment/template の前に string 内として再開しなければならない。
        var activeJsTopLevelStringQuote = '\0';
        lexState.Reset();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            char[]? masked = null;
            char[] GetMaskedLine() => masked ??= line.ToCharArray();
            var pos = 0;

            while (pos < line.Length)
            {
                if (frames.TryPeek(out var active))
                {
                    if (active is BlockCommentFrame)
                    {
                        if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                        {
                            // Blank the `*/` closer together with the body so
                            // template-hole block comments like `${/* f(); */ g()}`
                            // never leak `f` as a phantom reference.
                            // テンプレートホール内の `${/* f(); */ g()}` のような
                            // block comment で `f` が疑似参照として残らないよう、
                            // `*/` 自体も本文と同様に空白化する。
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            frames.Pop();
                            pos += 2;
                            continue;
                        }

                        // Blank the body so identifiers inside a template-hole
                        // block comment do not survive into reference extraction.
                        // ホール内 block comment 本文は空白化し、内部の識別子が
                        // 参照抽出まで残らないようにする。
                        GetMaskedLine()[pos] = ' ';
                        pos++;
                        continue;
                    }

                    if (active is JsTemplateLiteralFrame tplFrame)
                    {
                        if (line[pos] == '\\')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, Math.Min(2, line.Length - pos));
                            pos += Math.Min(2, line.Length - pos);
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '$' && line[pos + 1] == '{')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            pos += 2;
                            frames.Push(new JsTemplateHoleFrame());
                            lexState = default;
                            lexState.Reset();
                            continue;
                        }

                        if (line[pos] == '`')
                        {
                            // Restore the lex state captured when this template opened so the
                            // paren stack, class-header hint, case-label hint, etc. carry
                            // through to the token after the closing backtick.
                            // テンプレート開始時に退避した lex state を復元し、閉じ backtick
                            // の後ろに paren stack や class header hint、case label hint を
                            // 引き継ぐ。
                            GetMaskedLine()[pos] = ' ';
                            pos++;
                            lexState = tplFrame.SavedLexState;
                            lexState.SetKind(JsPrevTokenKind.Literal);
                            frames.Pop();
                            continue;
                        }

                        GetMaskedLine()[pos] = ' ';
                        pos++;
                        continue;
                    }

                    if (active is JsTemplateHoleFrame holeFrame)
                    {
                        if (activeJsHoleStringQuote != '\0')
                        {
                            pos = MaskJsTemplateHoleString(line, pos, GetMaskedLine(), activeJsHoleStringQuote, startsInsideString: true, out var continuesOnNextLine);
                            if (continuesOnNextLine)
                                break;

                            activeJsHoleStringQuote = '\0';
                            lexState.SetKind(JsPrevTokenKind.Literal);
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                        {
                            // Blank the `//` comment tail so later passes (including the
                            // multi-line tagged-template backward scan that reads prior
                            // `lines[li]`) cannot mistake a comment identifier for code.
                            // `//` コメント以降を空白化し、後続処理 — とくに前行の
                            // `lines[li]` を読む複数行タグ走査 — がコメント内の識別子を
                            // コードと誤認しないようにする。
                            ReplaceWithSpaces(GetMaskedLine(), pos, line.Length - pos);
                            break;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            // Blank the `/*` opener so the hole's block comment
                            // span is fully whitespace for downstream extraction.
                            // ホールの block comment 開始 `/*` を空白化し、
                            // 下流抽出から見えるスパン全体を空白化する。
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            frames.Push(new BlockCommentFrame());
                            pos += 2;
                            continue;
                        }

                        if (line[pos] == '/' && CanStartJsRegexLiteral(lexState))
                        {
                            pos = SkipJsRegexLiteral(line, pos);
                            lexState.SetKind(JsPrevTokenKind.Literal);
                            continue;
                        }

                        if (line[pos] == '`')
                        {
                            // Save the hole's lex state so the closing backtick can
                            // restore paren/state context for the token that follows.
                            // hole 側の lex state を退避し、閉じ backtick 後に paren
                            // などの context を元に戻せるようにする。
                            if (collectTaggedTemplateHits)
                                TryRecordJsTaggedTemplateHit(lines, GetMaskedLine(), i, pos, ref taggedTemplateHits, allowGenericTag);
                            pos++;
                            frames.Push(new JsTemplateLiteralFrame { SavedLexState = lexState });
                            lexState = default;
                            lexState.Reset();
                            continue;
                        }

                        if (line[pos] == '"' || line[pos] == '\'')
                        {
                            var quote = line[pos];
                            pos = MaskJsTemplateHoleString(line, pos, GetMaskedLine(), quote, startsInsideString: false, out var continuesOnNextLine);
                            if (continuesOnNextLine)
                            {
                                activeJsHoleStringQuote = quote;
                                break;
                            }

                            lexState.SetKind(JsPrevTokenKind.Literal);
                            continue;
                        }

                        if (line[pos] == '{')
                        {
                            holeFrame.NestedBraceDepth++;
                            // Classify the nested `{` as expression brace (object literal
                            // or `() => ({})` body) vs. statement block (arrow body
                            // `=> {...}`, `if (x) {...}`, or `class Foo {...}`). Block
                            // braces preserve `}`→regex behavior; expression braces set
                            // `}`→division so `${({a:1} / 2)}` stays parseable. A pending
                            // class header always opens a class body, regardless of what
                            // identifier or `extends` clause token came last.
                            // ネスト `{` を expression brace（object literal / `() => ({})`）
                            // と statement block（arrow body / `if (x) {}` / `class Foo {}`）
                            // に分類する。block は `}` の次の `/` を regex にし、expression
                            // は division にする。これで `${({a:1} / 2)}` が壊れない。
                            // class header pending 中は直前トークンが何であっても class
                            // body とみなす。
                            var isExpressionBrace = !lexState.ClassHeaderPending
                                && !lexState.CaseColonBlockPending
                                && IsJsExpressionBraceContext(lexState);
                            lexState.ClassHeaderPending = false;
                            lexState.CaseColonBlockPending = false;
                            // A new block scope starts; clear any half-complete case
                            // label tracking so a stray `case` keyword seen earlier
                            // does not tag the wrong `:` in the inner scope.
                            // 新しい block scope の開始。半端な case ラベル追跡を
                            // クリアし、外側の `case` が内側の無関係な `:` を
                            // case-label colon と誤判定しないようにする。
                            lexState.CaseLabelPending = false;
                            holeFrame.InnerBraceIsExpression.Push(isExpressionBrace);
                            pos++;
                            lexState.SetKind(JsPrevTokenKind.Other);
                            continue;
                        }

                        if (line[pos] == '}')
                        {
                            if (holeFrame.NestedBraceDepth == 0)
                            {
                                // Mask the hole's closing `}` to keep brace balance intact
                                // for downstream symbol-body brace counting.
                                // ホールを閉じる `}` もマスクし、後段の symbol 本体の
                                // brace 数え上げで brace バランスを崩さないようにする。
                                GetMaskedLine()[pos] = ' ';
                                frames.Pop();
                                pos++;
                                lexState.SetKind(JsPrevTokenKind.Other);
                                continue;
                            }

                            holeFrame.NestedBraceDepth--;
                            pos++;
                            var wasExpression = holeFrame.InnerBraceIsExpression.Count > 0
                                && holeFrame.InnerBraceIsExpression.Pop();
                            // Expression brace close → division context (CloseBrace).
                            // Block brace close → preserve regex-legal state (Other) so
                            // `if (x) {} /regex/` inside an arrow body is still skipped
                            // correctly and does not consume backticks as division noise.
                            // expression brace の閉じは CloseBrace で division 優先。
                            // block brace の閉じは Other に戻し、arrow body 内の
                            // `if (x) {} /regex/` でも regex を正しく取り込めるようにする。
                            lexState.SetKind(wasExpression ? JsPrevTokenKind.CloseBrace : JsPrevTokenKind.Other);
                            continue;
                        }

                        pos = AdvanceJsToken(line, pos, ref lexState);
                        continue;
                    }
                }

                if (activeJsTopLevelStringQuote != '\0')
                {
                    pos = MaskJsTemplateHoleString(line, pos, GetMaskedLine(), activeJsTopLevelStringQuote, startsInsideString: true, out var continuesOnNextLine);
                    if (continuesOnNextLine)
                        break;

                    activeJsTopLevelStringQuote = '\0';
                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                {
                    // Blank the `//` comment tail so the multi-line tagged-template
                    // backward scan (which reads prior `lines[li]` directly) cannot
                    // mistake a comment identifier like `comment` in
                    // `return tag // trailing comment` for the tag itself.
                    // `//` コメント以降を空白化し、前行の `lines[li]` を直接読む複数行
                    // タグ走査が `return tag // trailing comment` の `comment` のような
                    // コメント内識別子をタグと誤認しないようにする。
                    ReplaceWithSpaces(GetMaskedLine(), pos, line.Length - pos);
                    break;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    // Blank the top-level `/*` opener to match the hole-side
                    // behavior and keep downstream extraction consistent.
                    // 先頭レベルでも `/*` 開始を空白化し、ホール側と挙動を揃える。
                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                    frames.Push(new BlockCommentFrame());
                    pos += 2;
                    continue;
                }

                if (line[pos] == '/' && CanStartJsRegexLiteral(lexState))
                {
                    pos = SkipJsRegexLiteral(line, pos);
                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }

                if (line[pos] == '`')
                {
                    // Save the top-level lex state so the closing backtick can restore
                    // the paren stack / statement-head hints that preceded the template.
                    // テンプレート直前の lex state を退避し、閉じ backtick で paren
                    // stack や statement-head hint を復元できるようにする。
                    if (collectTaggedTemplateHits)
                        TryRecordJsTaggedTemplateHit(lines, GetMaskedLine(), i, pos, ref taggedTemplateHits, allowGenericTag);
                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    frames.Push(new JsTemplateLiteralFrame { SavedLexState = lexState });
                    lexState = default;
                    lexState.Reset();
                    continue;
                }

                if (line[pos] == '"' || line[pos] == '\'')
                {
                    var quote = line[pos];
                    var start = pos;
                    pos = SkipJsSingleLineStringContinuation(line, pos, out var continuesOnNextLine);
                    if (continuesOnNextLine)
                    {
                        ReplaceWithSpaces(GetMaskedLine(), start, pos - start);
                        activeJsTopLevelStringQuote = quote;
                        break;
                    }

                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }

                pos = AdvanceJsToken(line, pos, ref lexState);
            }

            if (masked is not null)
                lines[i] = new string(masked);
        }

        // Post-pass: drop `of` hits whose enclosing `for (...)` header is a for-of or
        // for-await-of loop. `of` is not a reserved word in ECMAScript, so `const of =
        // ...; of\`x\`` must stay visible — only the loop-header form should be silenced.
        // The check is done against the fully masked buffer so the template body cannot
        // inject false tokens, and it walks across line boundaries to cover multi-line
        // headers like `for (\n  const ch of \`abc\`\n)`.
        // 後段パス: 囲む `for (...)` ヘッダが for-of / for-await-of の場合のみ `of` ヒット
        // を除外する。`of` は ECMAScript の予約語ではなく `const of = ...; of\`x\`` は正当
        // なので、ループヘッダ形だけを静かにする必要がある。マスク後バッファに対して
        // 検査するため template 本体が誤トークンを混入させることがなく、
        // `for (\n  const ch of \`abc\`\n)` のような複数行ヘッダも行境界を越えて処理する。
        if (collectTaggedTemplateHits && taggedTemplateHits != null && taggedTemplateHits.Count > 0)
            FilterJsForOfHeaderHits(lines, taggedTemplateHits);
    }

}
