namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private static void MaskScalaTripleStringContents(string[] lines)
    {
        var insideTriple = false;
        // Whether the currently-open triple is an interpolator form (prefixed by an
        // identifier): only interpolators recognize ${expr} holes. Plain `"""..."""`
        // has no interpolation.
        // 現在開いている triple が interpolator 形式か。interpolator のみ ${expr}
        // を補間として扱う。プレーン `"""..."""` は補間なし。
        var isInterpolator = false;
        var blockCommentDepth = 0;
        var holeBraceDepth = -1;
        // Persistent across lines: a nested `"""..."""` literal opened inside the
        // current `${ ... }` hole. While true, the nested literal acts like its own
        // mini triple body — interpolator-prefixed nested triples (`s"""`, `f"""`,
        // `raw"""`, ...) keep `${expr}` holes alive so real call edges still reach
        // the reference graph (closes #996), while plain nested `"""..."""` masks
        // everything (closes #992).
        // ホール内で開いた nested triple-quoted string の状態。interpolator 付きの
        // nested triple は内部の `${expr}` ホールを保存して real call を残し、
        // プレーンな nested triple は全文を masking する。
        var nestedTripleOpen = false;
        // Whether the nested triple-quoted literal in the current hole was opened
        // with an identifier prefix (interpolator form).
        // ホール内で開いた nested triple が interpolator 形式かどうか。
        var nestedTripleIsInterpolator = false;
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
                        // ${expr} ホール内: 本文を保存。block / line コメントを先に
                        // 認識して `/* } */` のようなコメント内 `}` でホールを早閉じ
                        // しないようにする。
                        if (nestedTripleOpen)
                        {
                            if (nestedHoleBraceDepth >= 0)
                            {
                                // Inside the interpolator-prefixed nested triple's own
                                // ${expr} hole: preserve body chars so real call edges
                                // land in the reference graph. Closes #996.
                                // interpolator 付き nested triple 内の `${expr}` ホール
                                // 内: 本物の call を残す。
                                if (deepNestedTripleDepth > 0)
                                {
                                    // 3+ level deep triple body: keep masking through
                                    // nested open/close pairs so a 4th opener cannot
                                    // unwind the 3-deep frame early.
                                    // 3 段以上深い triple 本文: ネスト open/close を
                                    // 追跡し、4 段目の opener で 3 段深い frame が
                                    // 早抜けしないようにする。
                                    var deepHashes = CountRun(line, pos, '#');
                                    if (pos + deepHashes + 2 < line.Length
                                        && line[pos + deepHashes] == '"'
                                        && line[pos + deepHashes + 1] == '"'
                                        && line[pos + deepHashes + 2] == '"')
                                    {
                                        var looksLikeNestedOpen = LooksLikeDeepTripleOpenerContext(lines, i, pos, deepHashes + 3);
                                        if (looksLikeNestedOpen)
                                        {
                                            ReplaceWithSpaces(GetMaskedLine(), pos, deepHashes + 3);
                                            pos += deepHashes + 3;
                                            deepNestedTripleDepth++;
                                            deepNestedTripleHashCounts.Push(deepHashes);
                                            continue;
                                        }

                                        var currentDeepHashCount = deepNestedTripleHashCounts.Count > 0
                                            ? deepNestedTripleHashCounts.Peek()
                                            : 0;
                                        if (deepHashes == currentDeepHashCount)
                                        {
                                            ReplaceWithSpaces(GetMaskedLine(), pos, 3 + deepHashes);
                                            pos += 3 + deepHashes;
                                            deepNestedTripleDepth--;
                                            if (deepNestedTripleHashCounts.Count > 0)
                                                deepNestedTripleHashCounts.Pop();
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
                                // 3rd-level triple opener inside the inner hole.
                                // Detect before the single-line-string skipper so the
                                // leading `"` does not advance us into the literal
                                // body via SkipJsSingleLineString and break brace counting.
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
                            // outer hole. Recognize a closing `"""`; only interpolator-
                            // prefixed nested triples honor `${...}` holes, plain ones
                            // mask everything.
                            // 外側ホール内で開いた nested triple 本体。閉じ `"""`、
                            // interpolator 付きでは `${...}` を内部ホールとして開く、
                            // それ以外は body としてマスク。
                            if (pos + 2 < line.Length
                                && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                pos += 3;
                                nestedTripleOpen = false;
                                nestedTripleIsInterpolator = false;
                                nestedHoleBraceDepth = -1;
                                deepNestedTripleDepth = 0;
                                deepNestedTripleHashCounts.Clear();
                                continue;
                            }
                            if (nestedTripleIsInterpolator
                                && pos + 1 < line.Length
                                && line[pos] == '$' && line[pos + 1] == '{')
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
                            // Interpolator detection: an identifier character immediately
                            // before the nested `"""` marks this as a prefixed form
                            // (s""", f""", raw""", or user-defined).
                            // interpolator 判定: 直前が識別子文字なら prefix 付き。
                            var nestedPrefixIsInterpolator = pos > 0 && IsIdentifierPart(line[pos - 1]);
                            ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                            pos += 3;
                            nestedTripleOpen = true;
                            nestedTripleIsInterpolator = nestedPrefixIsInterpolator;
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
                        isInterpolator = false;
                        // Defensive: outer triple owns any nested-triple state from a
                        // hole, so reset it as well when the outer literal closes.
                        // 防御的に、外側 triple が閉じた時点で nested-triple 状態も解除する。
                        nestedTripleOpen = false;
                        nestedTripleIsInterpolator = false;
                        nestedHoleBraceDepth = -1;
                        deepNestedTripleDepth = 0;
                        deepNestedTripleHashCounts.Clear();
                        continue;
                    }

                    if (isInterpolator
                        && pos + 1 < line.Length
                        && line[pos] == '$' && line[pos + 1] == '{')
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
                    // Interpolator detection: an identifier character immediately before
                    // `"""` marks this as a prefixed form (s""", f""", raw""", or a
                    // user-defined interpolator). Only those forms honor ${expr} holes.
                    // interpolator 判定: `"""` の直前が識別子文字なら prefix 付き
                    // （`s"""` / `f"""` / `raw"""` / ユーザー定義）で、${expr} ホールを有効化する。
                    var prefixIsInterpolator = pos > 0 && IsIdentifierPart(line[pos - 1]);
                    ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                    pos += 3;
                    insideTriple = true;
                    isInterpolator = prefixIsInterpolator;
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


}
