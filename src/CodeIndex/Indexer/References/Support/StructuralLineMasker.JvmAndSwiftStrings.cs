namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private static int MaskSwiftSingleLineRawString(string line, int startIndex, int hashCount, char[] masked)
    {
        // Mask leading `<N>#"` (hashCount + 1 chars).
        ReplaceWithSpaces(masked, startIndex, hashCount + 1);
        var q = startIndex + hashCount + 1;
        while (q < line.Length)
        {
            // Closing `"<N>#` with matching hash count.
            // 一致 hash 数の閉じ `"<N>#`。
            if (line[q] == '"' && HasHashRun(line, q + 1, hashCount))
            {
                ReplaceWithSpaces(masked, q, 1 + hashCount);
                return q + 1 + hashCount;
            }
            // Interpolation hole opener `\<N>#(` with matching hash run. Mask the
            // `\<N>#(` opener but preserve the body until the matching `)` so the
            // real call inside the hole survives masking.
            // 一致 hash 数の補間ホール `\<N>#(`。`\<N>#(` 自体はマスクし、本文は本物の
            // call を残すために保存し、対応する `)` で閉じる。
            if (line[q] == '\\'
                && HasHashRun(line, q + 1, hashCount)
                && q + 1 + hashCount < line.Length
                && line[q + 1 + hashCount] == '(')
            {
                ReplaceWithSpaces(masked, q, 2 + hashCount);
                q += 2 + hashCount;
                var holeDepth = 0;
                while (q < line.Length)
                {
                    // Nested single-line raw string inside the hole. Recurse so the
                    // nested `\<hashes>(...)` bodies remain visible too.
                    // ホール内に入れ子の単行 raw 文字列があれば再帰処理し、
                    // 内側の `\<hashes>(...)` 本文も見えるままにする。
                    var nestedHashCount = CountRun(line, q, '#');
                    if (nestedHashCount > 0
                        && q + nestedHashCount < line.Length
                        && line[q + nestedHashCount] == '"')
                    {
                        q = MaskSwiftSingleLineRawString(line, q, nestedHashCount, masked);
                        continue;
                    }

                    if (line[q] == '"' || line[q] == '\'')
                    {
                        q = SkipJsSingleLineString(line, q);
                        continue;
                    }
                    if (line[q] == '(')
                    {
                        holeDepth++;
                        q++;
                        continue;
                    }
                    if (line[q] == ')')
                    {
                        if (holeDepth == 0)
                        {
                            masked[q] = ' ';
                            q++;
                            break;
                        }
                        holeDepth--;
                        q++;
                        continue;
                    }
                    q++;
                }
                continue;
            }
            masked[q] = ' ';
            q++;
        }
        return q;
    }

    private static int SkipJsSingleLineString(string line, int startIndex)
    {
        var quote = line[startIndex];
        var p = startIndex + 1;
        while (p < line.Length && line[p] != quote)
        {
            if (line[p] == '\\' && p + 1 < line.Length)
                p += 2;
            else
                p++;
        }
        if (p < line.Length)
            p++;
        return p;
    }

    private static int SkipJsSingleLineStringContinuation(string line, int startIndex, out bool continuesOnNextLine)
    {
        var quote = line[startIndex];
        var p = startIndex + 1;
        while (p < line.Length && line[p] != quote)
        {
            if (line[p] == '\\')
            {
                if (p + 1 == line.Length)
                {
                    continuesOnNextLine = true;
                    return p + 1;
                }

                if (p + 2 == line.Length && line[p + 1] == '\r')
                {
                    continuesOnNextLine = true;
                    return line.Length;
                }

                p += 2;
                continue;
            }

            p++;
        }

        if (p < line.Length)
            p++;

        continuesOnNextLine = false;
        return p;
    }

    // Kotlin multi-line raw string literals: """...""".
    // Body is raw (no backslash escape processing). Interpolation: $identifier and
    // ${expression}. Only ${expr} hole contents are preserved so downstream reference
    // extraction still sees real call edges; $ident is a bare identifier that cannot
    // be a call by itself, so masking the surrounding body is safe.
    // Regression target: issue #385.
    // Kotlin の複数行 raw 文字列 """...""" を扱う。本文は raw（\ エスケープなし）。
    // 補間は $identifier と ${expression}。${expr} ホール内の本物の呼び出しを
    // 参照抽出に残すため、ホール内は保存する。$ident は単独識別子で call にならないため
    // 周囲本体と一緒にマスクしてよい。回帰対象: issue #385。
    private static void MaskKotlinTripleStringContents(string[] lines)
    {
        var insideTriple = false;
        var blockCommentDepth = 0;
        // Hole state persists across lines so multi-line ${ ... } bodies keep real
        // call edges and do not accidentally close at the wrong `}`.
        // -1 when outside a hole, >=0 = nested `{` depth inside the hole (0 = top).
        // ホール状態は行をまたいで保持する。ホール外は -1、ホール内は `{` 深さ（0 が最上位）。
        var holeBraceDepth = -1;
        // Persistent across lines: a nested `"""..."""` literal opened inside the
        // current `${ ... }` hole. While true, the nested literal acts like its own
        // mini triple body — `${...}` holes inside it still preserve real call
        // edges (closes #996), but body chars between holes are masked through to
        // the next `"""` closer so call-shaped identifiers cannot leak (closes #992).
        // ホール内に開いた nested triple-quoted string の状態。nested literal 内も
        // 自身の `${...}` ホールでは本物の call を残しつつ、本文は次の `"""` まで
        // 空白化して phantom call の漏れを防ぐ。
        var nestedTripleOpen = false;
        // -1 when not inside a nested-triple ${...} hole, >=0 = brace depth of that
        // inner hole. The inner hole preserves real call edges inside the nested
        // triple-quoted literal.
        // nested triple 内 `${...}` ホールの brace 深さ。-1 はホール外。
        var nestedHoleBraceDepth = -1;
        // Defensive depth tracking for triple-quoted literals opened 3+ levels deep
        // (i.e. inside the nested triple's own `${...}` hole). >0 = current 3+ deep
        // body. While >0, every char is masked and `"""` toggles depth so phantom
        // calls cannot leak. Real calls 4+ levels deep are not preserved — full
        // stack tracking would be needed for that — but masking soundness is.
        // 3 段以上のネスト triple に対する防御的な深さ追跡。> 0 の間は本文をマスクし、
        // 4 段以上の本物の call は保持しないが、phantom の漏れは防ぐ。
        var deepNestedTripleDepth = 0;
        var deepNestedTripleHashCounts = new Stack<int>();

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
                if (blockCommentDepth > 0)
                {
                    if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                        blockCommentDepth++;
                        pos += 2;
                        continue;
                    }
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                        blockCommentDepth--;
                        pos += 2;
                        continue;
                    }
                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    continue;
                }

                if (insideTriple)
                {
                    if (holeBraceDepth >= 0)
                    {
                        // Inside ${expr} hole: preserve body. Block comments and line
                        // comments must be recognized first so a legal `/* } */` inside
                        // the hole does not close the hole at the comment body's `}`.
                        // Nested single-line strings and char literals are also skipped
                        // so their `}` does not close the hole, and nested `{` / `}`
                        // are tracked for lambdas / object literals.
                        // ${expr} ホール内: 本文を保存。block / line コメントを先に
                        // 認識して `/* } */` のようなコメント内 `}` でホールを早閉じ
                        // しないようにする。単行文字列・char リテラルも同様にスキップし、
                        // lambda / object literal 用のネスト `{` / `}` を追跡する。
                        if (nestedTripleOpen)
                        {
                            if (nestedHoleBraceDepth >= 0)
                            {
                                // Inside the nested triple's own ${expr} hole: preserve
                                // body chars so real call edges land in the reference
                                // graph. Closes #996.
                                // nested triple 内の `${expr}` ホール内: 本文を保存し、
                                // 本物の call が reference graph に届くようにする。
                                if (deepNestedTripleDepth > 0)
                                {
                                    // 3+ level deep triple body: keep masking through
                                    // nested open/close pairs so a 4th opener cannot
                                    // unwind the 3-deep frame early.
                                    // 3 段以上深い triple 本文: ネスト open/close を
                                    // 追跡し、4 段目の opener で 3 段深い frame が
                                    // 早抜けしないようにする。
                                    if (pos + 2 < line.Length
                                        && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                                    {
                                        var looksLikeNestedOpen = LooksLikeDeepTripleOpenerContext(lines, i, pos, 3);
                                        if (looksLikeNestedOpen)
                                        {
                                            ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                            pos += 3;
                                            deepNestedTripleDepth++;
                                            deepNestedTripleHashCounts.Push(0);
                                            continue;
                                        }

                                        ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                        pos += 3;
                                        deepNestedTripleDepth--;
                                        if (deepNestedTripleHashCounts.Count > 0)
                                            deepNestedTripleHashCounts.Pop();
                                        continue;
                                    }
                                    GetMaskedLine()[pos] = ' ';
                                    pos++;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, line.Length - pos);
                                    pos = line.Length;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                    blockCommentDepth = 1;
                                    pos += 2;
                                    continue;
                                }
                                // 3rd-level triple opener inside the inner hole.
                                // Detect before the single-line-string skipper so the
                                // leading `"` does not advance us into the literal
                                // body via SkipJsSingleLineString and break paren / brace
                                // counting.
                                // 3 段目の triple opener。先頭 `"` が単行スキッパーへ
                                // 渡って literal 本体に進まないよう先に検知する。
                                if (pos + 2 < line.Length
                                    && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                    pos += 3;
                                    deepNestedTripleDepth = 1;
                                    deepNestedTripleHashCounts.Push(0);
                                    continue;
                                }
                                if (line[pos] == '"' || line[pos] == '\'')
                                {
                                    pos = SkipJsSingleLineString(line, pos);
                                    continue;
                                }
                                if (line[pos] == '{')
                                {
                                    nestedHoleBraceDepth++;
                                    pos++;
                                    continue;
                                }
                                if (line[pos] == '}')
                                {
                                    if (nestedHoleBraceDepth == 0)
                                    {
                                        GetMaskedLine()[pos] = ' ';
                                        nestedHoleBraceDepth = -1;
                                        pos++;
                                        continue;
                                    }
                                    nestedHoleBraceDepth--;
                                    pos++;
                                    continue;
                                }
                                pos++;
                                continue;
                            }

                            // Inside a nested `"""..."""` literal opened earlier in this
                            // outer hole. Recognize a closing `"""`, an opening `${...}`
                            // hole inside the nested literal (so real calls inside it
                            // still reach the reference graph), and otherwise mask.
                            // 外側ホール内で開いた nested triple 本体。閉じ `"""`、内側
                            // `${...}` ホール、それ以外は body としてマスク。
                            if (pos + 2 < line.Length
                                && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                pos += 3;
                                nestedTripleOpen = false;
                                nestedHoleBraceDepth = -1;
                                deepNestedTripleDepth = 0;
                                deepNestedTripleHashCounts.Clear();
                                continue;
                            }
                            if (pos + 1 < line.Length && line[pos] == '$' && line[pos + 1] == '{')
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                nestedHoleBraceDepth = 0;
                                pos += 2;
                                continue;
                            }
                            GetMaskedLine()[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, line.Length - pos);
                            pos = line.Length;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            blockCommentDepth = 1;
                            pos += 2;
                            continue;
                        }

                        // Nested `"""..."""` literal opener inside the hole. Detect
                        // before the single-line-string skipper so the first `"` does
                        // not advance us into the literal body via `SkipJsSingleLineString`.
                        // ホール内で開く nested `"""..."""` の opener。先頭 `"` が単行
                        // 文字列スキッパーに渡って literal 本体へ進まないよう先に検知する。
                        if (pos + 2 < line.Length
                            && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                            pos += 3;
                            nestedTripleOpen = true;
                            nestedHoleBraceDepth = -1;
                            continue;
                        }

                        if (line[pos] == '"' || line[pos] == '\'')
                        {
                            pos = SkipJsSingleLineString(line, pos);
                            continue;
                        }

                        if (line[pos] == '{')
                        {
                            holeBraceDepth++;
                            pos++;
                            continue;
                        }

                        if (line[pos] == '}')
                        {
                            if (holeBraceDepth == 0)
                            {
                                GetMaskedLine()[pos] = ' ';
                                holeBraceDepth = -1;
                                pos++;
                                continue;
                            }

                            holeBraceDepth--;
                            pos++;
                            continue;
                        }

                        pos++;
                        continue;
                    }

                    if (pos + 2 < line.Length
                        && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                        pos += 3;
                        insideTriple = false;
                        // Defensive: any open nested-triple state is owned by the just-
                        // closed outer triple, so reset it as well.
                        // 防御的に、外側 triple を閉じた時点で nested-triple 状態も解除する。
                        nestedTripleOpen = false;
                        nestedHoleBraceDepth = -1;
                        deepNestedTripleDepth = 0;
                        deepNestedTripleHashCounts.Clear();
                        continue;
                    }

                    if (pos + 1 < line.Length && line[pos] == '$' && line[pos + 1] == '{')
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                        holeBraceDepth = 0;
                        pos += 2;
                        continue;
                    }

                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                    break;

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                    blockCommentDepth = 1;
                    pos += 2;
                    continue;
                }

                if (pos + 2 < line.Length
                    && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                {
                    ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                    pos += 3;
                    insideTriple = true;
                    continue;
                }

                if (line[pos] == '"' || line[pos] == '\'')
                {
                    pos = SkipJsSingleLineString(line, pos);
                    continue;
                }

                pos++;
            }

            if (masked is not null)
                lines[i] = new string(masked);
        }
    }

    // Swift multi-line string literals: """...""" and extended """#"""..."""# forms.
    // Plain form supports \(expr) interpolation; N-hash extended form needs \#(expr)
    // (matching hash count). Interpolation hole contents are preserved so downstream
    // reference extraction keeps real call edges inside \(...).
    // Regression target: issue #385.
    // Swift の複数行文字列 """...""" と拡張 #"""..."""# 系を扱う。通常形の補間は
    // \(expr)、N 個の # 付き拡張形は \#(expr)（個数一致）。\(...) ホール内は保存し、
    // 本物の call を参照抽出に見せる。回帰対象: issue #385。
    private static void MaskSwiftMultilineStringContents(string[] lines)
    {
        var insideTriple = false;
        // 0 for plain """...""", N for the extended """<N>#"""..."""<N># variant.
        // 通常 """...""" は 0、拡張形は一致させる # 個数 N。
        var tripleHashCount = 0;
        var blockCommentDepth = 0;
        // -1 when outside a \(...) interpolation hole, >=0 = nested `(` depth.
        // \(...) ホール外は -1、ホール内は `(` 深さ。
        var holeParenDepth = -1;
        // Persistent across lines: a nested `"""..."""` or `#"""..."""#` literal
        // opened inside the current `\(...)` hole. -1 when no nested triple is
        // open; >=0 = leading `#` count required at the matching close. While set,
        // the nested literal acts like its own mini triple body — its own
        // `\(...)` (or `\#(...)` / `\##(...)` etc.) interpolation holes still
        // preserve real call edges (closes #996), and body chars between holes
        // are masked through to the close so phantom calls cannot leak (closes #992).
        // ホール内に開いた nested `"""..."""` / `#"""..."""#` の状態。-1 は未オープン、
        // 0 以上は閉じに必要な `#` 個数。set 中は内部 `\(...)` ホールでも本物の call を残す。
        var nestedTripleHashCount = -1;
        // -1 when not inside the nested triple's own `\(...)` hole, >=0 = paren
        // depth of that inner hole. Preserves real call edges inside the nested
        // literal.
        // nested triple 内 `\(...)` ホールの paren 深さ。-1 はホール外。
        var nestedHoleParenDepth = -1;
        // Defensive depth tracking for triple-quoted literals opened 3+ levels deep
        // (i.e. inside the nested triple's own `\(...)` hole). >0 = current 3+ deep
        // body. While >0, every char is masked and the close requires the same
        // hash count as the deep open so phantom calls cannot leak even when the
        // deep triple is hash-delimited (`#"""..."""#` etc.). Closes #1000 — the
        // earlier version only matched plain `"""` for the close and could exit
        // the deep state at the wrong delimiter when the deep triple was raw.
        // Real calls 4+ levels deep are not preserved — full stack tracking would
        // be needed for that — but masking soundness is.
        // 3 段以上のネスト triple に対する防御的な深さ追跡。
        var deepNestedTripleDepth = 0;
        // Hash count required at each deep triple's matching close. Stack top
        // tracks the currently-open deep frame.
        // 各 deep triple の閉じに必要な hash 個数。スタック頂点が現在の deep frame。
        var deepNestedTripleHashCounts = new Stack<int>();

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
                if (blockCommentDepth > 0)
                {
                    if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                        blockCommentDepth++;
                        pos += 2;
                        continue;
                    }
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                        blockCommentDepth--;
                        pos += 2;
                        continue;
                    }
                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    continue;
                }

                if (insideTriple)
                {
                    if (holeParenDepth >= 0)
                    {
                        // Inside \(expr) hole: preserve body. Block comments and line
                        // comments must be recognized first so a legal `/* ) */` inside
                        // the hole does not close the hole at the comment body's `)`.
                        // Nested single-line strings are also skipped so their `)` does
                        // not close the hole, and nested `(` / `)` are tracked.
                        // \(expr) ホール内: 本文を保存。block / line コメントを先に
                        // 認識して `/* ) */` のようなコメント内 `)` でホールを早閉じ
                        // しないようにする。単行文字列もスキップし、ネスト `(` / `)` も追跡する。
                        if (nestedTripleHashCount >= 0)
                        {
                            if (nestedHoleParenDepth >= 0)
                            {
                                // Inside the nested triple's own `\(...)` hole: preserve
                                // body chars so real call edges land in the reference
                                // graph. Closes #996.
                                // nested triple 内の `\(...)` ホール内: 本物の call を残す。
                                if (deepNestedTripleDepth > 0)
                                {
                                    // 3+ level deep triple body: mask through nested
                                    // opener/close pairs so a 4th opener cannot unwind
                                    // the 3-deep frame early.
                                    // 3 段以上深い triple 本文: ネスト open/close を
                                    // 追跡し、4 段目の opener で 3 段深い frame が
                                    // 早抜けしないようにする。
                                    var deepBodyHashes = CountRun(line, pos, '#');
                                    if (pos + 2 < line.Length
                                        && line[pos] == '"'
                                        && line[pos + 1] == '"'
                                        && line[pos + 2] == '"')
                                    {
                                        var closeHashCount = CountRun(line, pos + 3, '#');
                                        if (closeHashCount > 0
                                            && !LooksLikeDeepTripleOpenerContext(lines, i, pos, 3 + closeHashCount))
                                        {
                                            ReplaceWithSpaces(GetMaskedLine(), pos, 3 + closeHashCount);
                                            pos += 3 + closeHashCount;
                                            deepNestedTripleDepth--;
                                            if (deepNestedTripleHashCounts.Count > 0)
                                                deepNestedTripleHashCounts.Pop();
                                            continue;
                                        }
                                        var currentDeepHashCount = deepNestedTripleHashCounts.Count > 0
                                            ? deepNestedTripleHashCounts.Peek()
                                            : 0;
                                        if (closeHashCount == 0
                                            && currentDeepHashCount == 0
                                            && !LooksLikeDeepTripleOpenerContext(lines, i, pos, 3))
                                        {
                                            ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                            pos += 3;
                                            deepNestedTripleDepth--;
                                            if (deepNestedTripleHashCounts.Count > 0)
                                                deepNestedTripleHashCounts.Pop();
                                            continue;
                                        }
                                    }
                                    if (pos + 2 < line.Length
                                        && line[pos] == '"'
                                        && line[pos + 1] == '"'
                                        && line[pos + 2] == '"'
                                        && LooksLikeDeepTripleOpenerContext(lines, i, pos, 3))
                                    {
                                        ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                        pos += 3;
                                        deepNestedTripleDepth++;
                                        deepNestedTripleHashCounts.Push(0);
                                        continue;
                                    }
                                    if (deepBodyHashes > 0
                                        && pos + deepBodyHashes + 2 < line.Length
                                        && line[pos + deepBodyHashes] == '"'
                                        && line[pos + deepBodyHashes + 1] == '"'
                                        && line[pos + deepBodyHashes + 2] == '"')
                                    {
                                        var looksLikeNestedOpen = LooksLikeDeepTripleOpenerContext(lines, i, pos, deepBodyHashes + 3);
                                        if (looksLikeNestedOpen)
                                        {
                                            ReplaceWithSpaces(GetMaskedLine(), pos, deepBodyHashes + 3);
                                            pos += deepBodyHashes + 3;
                                            deepNestedTripleDepth++;
                                            deepNestedTripleHashCounts.Push(deepBodyHashes);
                                            continue;
                                        }

                                    }

                                    GetMaskedLine()[pos] = ' ';
                                    pos++;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, line.Length - pos);
                                    pos = line.Length;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                    blockCommentDepth = 1;
                                    pos += 2;
                                    continue;
                                }
                                // 3rd-level triple opener (optionally with leading `#`)
                                // inside the inner hole. Detect before the single-line
                                // string skipper so the leading `"` does not advance into
                                // the literal body via SkipJsSingleLineString and break
                                // paren counting.
                                // 3 段目の triple opener。先頭 `"` が単行スキッパーへ
                                // 渡って literal 本体に進まないよう先に検知する。
                                var deepHashes = CountRun(line, pos, '#');
                                if (pos + deepHashes + 2 < line.Length
                                    && line[pos + deepHashes] == '"'
                                    && line[pos + deepHashes + 1] == '"'
                                    && line[pos + deepHashes + 2] == '"')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, deepHashes + 3);
                                    pos += deepHashes + 3;
                                    deepNestedTripleDepth = 1;
                                    deepNestedTripleHashCounts.Push(deepHashes);
                                    continue;
                                }
                                // Single-line `#"..."#` raw string inside the inner hole.
                                // Preserve any matching `\#(...)` interpolation hole bodies
                                // so real call edges inside the raw string still reach the
                                // reference graph. Closes #1001.
                                // 単行 `#"..."#` 拡張 raw 文字列。内側の `\#(...)` ホール本文は
                                // 残し、本物の call を reference graph に届ける。
                                if (deepHashes > 0
                                    && pos + deepHashes < line.Length
                                    && line[pos + deepHashes] == '"')
                                {
                                    pos = MaskSwiftSingleLineRawString(line, pos, deepHashes, GetMaskedLine());
                                    continue;
                                }
                                if (line[pos] == '"' || line[pos] == '\'')
                                {
                                    pos = SkipJsSingleLineString(line, pos);
                                    continue;
                                }
                                if (line[pos] == '(')
                                {
                                    nestedHoleParenDepth++;
                                    pos++;
                                    continue;
                                }
                                if (line[pos] == ')')
                                {
                                    if (nestedHoleParenDepth == 0)
                                    {
                                        GetMaskedLine()[pos] = ' ';
                                        nestedHoleParenDepth = -1;
                                        pos++;
                                        continue;
                                    }
                                    nestedHoleParenDepth--;
                                    pos++;
                                    continue;
                                }
                                pos++;
                                continue;
                            }

                            // Inside a nested `"""..."""` (optionally hash-delimited) literal
                            // opened earlier in this outer hole. Recognize the matching close,
                            // a `\(...)` (or `\#(...)` / `\##(...)` etc.) interpolation hole
                            // opener inside the nested literal so real calls inside it still
                            // reach the reference graph, and otherwise mask the body.
                            // 外側ホール内で開いた nested triple 本体。一致 hash 数の `"""`
                            // クローザ、内側 `\(...)` ホール（hash 数一致）、それ以外は body
                            // としてマスク。
                            if (pos + 2 < line.Length
                                && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"'
                                && HasHashRun(line, pos + 3, nestedTripleHashCount))
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 3 + nestedTripleHashCount);
                                pos += 3 + nestedTripleHashCount;
                                nestedTripleHashCount = -1;
                                nestedHoleParenDepth = -1;
                                deepNestedTripleDepth = 0;
                                deepNestedTripleHashCounts.Clear();
                                continue;
                            }
                            if (line[pos] == '\\'
                                && HasHashRun(line, pos + 1, nestedTripleHashCount)
                                && pos + 1 + nestedTripleHashCount < line.Length
                                && line[pos + 1 + nestedTripleHashCount] == '(')
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2 + nestedTripleHashCount);
                                pos += 2 + nestedTripleHashCount;
                                nestedHoleParenDepth = 0;
                                continue;
                            }
                            // Plain (non-raw) nested triple: `\\` is a literal backslash.
                            // 通常 nested triple 内: `\\` は literal backslash。
                            if (nestedTripleHashCount == 0 && line[pos] == '\\' && pos + 1 < line.Length)
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                pos += 2;
                                continue;
                            }
                            GetMaskedLine()[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, line.Length - pos);
                            pos = line.Length;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            blockCommentDepth = 1;
                            pos += 2;
                            continue;
                        }

                        // Nested triple-quoted string opener inside the hole: optional
                        // leading `#` run then `"""`. Detect before the single-line-string
                        // skipper so the first `"` of `"""` does not advance into the body.
                        // ホール内で開く nested triple の opener。先頭 `"` が単行文字列
                        // スキッパーに渡って literal 本体へ進まないよう先に検知する。
                        var holeNestedHashes = CountRun(line, pos, '#');
                        if (pos + holeNestedHashes + 2 < line.Length
                            && line[pos + holeNestedHashes] == '"'
                            && line[pos + holeNestedHashes + 1] == '"'
                            && line[pos + holeNestedHashes + 2] == '"')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, holeNestedHashes + 3);
                            pos += holeNestedHashes + 3;
                            nestedTripleHashCount = holeNestedHashes;
                            continue;
                        }

                        // Single-line `#"..."#` extended raw string inside the outer
                        // hole. The body may contain unescaped `"`, `(`, and `)`, so
                        // the generic single-line skipper would stop at the first `"`
                        // and leave the remainder visible — breaking the outer hole's
                        // paren counting. Use the shared raw-string helper to mask
                        // through to the matching `"<hashes>` close while preserving
                        // any `\<hashes>(...)` interpolation hole bodies. Closes #1001.
                        // ホール内の単行 `#"..."#` 拡張 raw 文字列。body に `"` / `(` / `)`
                        // を含むため通常スキッパーは早すぎて止まる。共有ヘルパーで
                        // `"<hashes>` クローザまでマスクし、`\<hashes>(...)` ホール本文は残す。
                        if (holeNestedHashes > 0
                            && pos + holeNestedHashes < line.Length
                            && line[pos + holeNestedHashes] == '"')
                        {
                            pos = MaskSwiftSingleLineRawString(line, pos, holeNestedHashes, GetMaskedLine());
                            continue;
                        }

                        if (line[pos] == '"' || line[pos] == '\'')
                        {
                            pos = SkipJsSingleLineString(line, pos);
                            continue;
                        }

                        if (line[pos] == '(')
                        {
                            holeParenDepth++;
                            pos++;
                            continue;
                        }

                        if (line[pos] == ')')
                        {
                            if (holeParenDepth == 0)
                            {
                                GetMaskedLine()[pos] = ' ';
                                holeParenDepth = -1;
                                pos++;
                                continue;
                            }

                            holeParenDepth--;
                            pos++;
                            continue;
                        }

                        pos++;
                        continue;
                    }

                    // Closing """[#...] with matching hash count.
                    // 閉じ """[#...]（hash 数一致）。
                    if (pos + 2 < line.Length
                        && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"'
                        && HasHashRun(line, pos + 3, tripleHashCount))
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 3 + tripleHashCount);
                        pos += 3 + tripleHashCount;
                        insideTriple = false;
                        tripleHashCount = 0;
                        // Defensive: outer triple owns any nested-triple state from a
                        // hole, so reset it as well when the outer literal closes.
                        // 防御的に、外側 triple が閉じた時点で nested-triple 状態も解除する。
                        nestedTripleHashCount = -1;
                        nestedHoleParenDepth = -1;
                        deepNestedTripleDepth = 0;
                        deepNestedTripleHashCounts.Clear();
                        continue;
                    }

                    if (line[pos] == '\\')
                    {
                        // \(expr) interpolation opener (for raw forms, needs matching
                        // `#` run: \#(, \##(, ...).
                        // \(expr) 補間の開始。拡張形では hash 数一致が必要: \#(、\##( など。
                        if (HasHashRun(line, pos + 1, tripleHashCount)
                            && pos + 1 + tripleHashCount < line.Length
                            && line[pos + 1 + tripleHashCount] == '(')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2 + tripleHashCount);
                            pos += 2 + tripleHashCount;
                            holeParenDepth = 0;
                            continue;
                        }

                        // Plain `"""..."""`: `\\` is a literal backslash — consume both
                        // so the second char cannot accidentally start a triple close or
                        // escape parser.
                        // 通常 `"""..."""`: `\\` は literal backslash。2 文字まとめて
                        // 消費し、2 文字目が triple close の一部と誤検出されないようにする。
                        if (tripleHashCount == 0 && pos + 1 < line.Length)
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            pos += 2;
                            continue;
                        }

                        // Extended form `#"""..."""#` (or more hashes): without a
                        // matching `\#` run the backslash is literal; advance one char.
                        // 拡張形 `#"""..."""#` など: hash 数が一致しない `\` は literal。
                        GetMaskedLine()[pos] = ' ';
                        pos++;
                        continue;
                    }

                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                    break;

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                    blockCommentDepth = 1;
                    pos += 2;
                    continue;
                }

                // Extended / plain triple-quoted opener: optional leading `#` run then `"""`.
                // 拡張または通常の triple 開始: 任意の `#` 列 + `"""`。
                var leadingHashes = CountRun(line, pos, '#');
                if (pos + leadingHashes + 2 < line.Length
                    && line[pos + leadingHashes] == '"'
                    && line[pos + leadingHashes + 1] == '"'
                    && line[pos + leadingHashes + 2] == '"')
                {
                    ReplaceWithSpaces(GetMaskedLine(), pos, leadingHashes + 3);
                    pos += leadingHashes + 3;
                    insideTriple = true;
                    tripleHashCount = leadingHashes;
                    continue;
                }

                // Single-line extended raw string `#"..."#` with matching `#` run.
                // The body may contain unescaped `"`, so the generic single-quote
                // skipper would stop too early. Use the shared helper to mask through
                // to the matching `"<hashes>` close while preserving any matching
                // `\<hashes>(...)` interpolation hole bodies (closes #1001).
                // 単行の `#"..."#` 拡張 raw 文字列。共有ヘルパーで `"<hashes>` まで
                // マスクし、内側の `\<hashes>(...)` ホール本文は残す。
                if (leadingHashes > 0
                    && pos + leadingHashes < line.Length
                    && line[pos + leadingHashes] == '"')
                {
                    pos = MaskSwiftSingleLineRawString(line, pos, leadingHashes, GetMaskedLine());
                    continue;
                }

                if (line[pos] == '"')
                {
                    pos = SkipJsSingleLineString(line, pos);
                    continue;
                }

                pos++;
            }

            if (masked is not null)
                lines[i] = new string(masked);
        }
    }

    // Scala multi-line string literals: """...""". Only interpolator-prefixed forms
    // (s""", f""", raw""", or any identifier-prefixed form) interpret $ident / ${expr}
    // holes; plain """...""" is a raw literal with no interpolation. ${expr} hole
    // contents are preserved so downstream reference extraction keeps real call
    // edges inside ${...}; bare $ident is not a call and is masked with the body.
    // Regression target: issue #385.
    // Scala の複数行文字列 """..."""。補間は interpolator prefix（`s"""` / `f"""` /
    // `raw"""`、または任意の識別子 prefix）のときだけ有効。プレーン """...""" は
    // 補間なしの raw。${expr} ホール内は本物の call を参照抽出に残すため保存、
    // `$ident` は単独識別子で call にならないため本体とともにマスクする。
    // 回帰対象: issue #385。
    private static bool IsIdentifierPart(char c) =>
        c == '_' || char.IsLetterOrDigit(c);
}
