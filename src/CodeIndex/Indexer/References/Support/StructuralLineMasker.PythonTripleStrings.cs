namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private static void MaskPythonTripleStringContents(string[] lines)
    {
        char tripleChar = '\0';
        bool isRaw = false;
        bool isFString = false;
        int holeBraceDepth = -1; // -1 when not inside an f-string hole, >=0 otherwise.
        // Nested triple-quoted string inside an f-string hole. Persists across lines so
        // multi-line nested triples do not leak `}` into the outer hole's brace depth.
        // f-string ホール内にネストした三重引用符文字列の状態。行をまたいで保持し、
        // 複数行にわたるネスト triple 内の `}` が外側のホール brace 数え上げを壊さないようにする。
        char nestedTripleChar = '\0';
        bool nestedTripleRaw = false;
        // Track whether a nested triple-quoted string is itself an f-string so
        // its own `{expr}` holes still surface real call edges.
        // ネストした三重引用符文字列自身が f-string かどうかを追跡し、その内部の
        // `{expr}` ホールに含まれる real call を参照抽出に見せる。
        bool nestedTripleIsFString = false;
        // -1 when outside the nested triple's own hole, >=0 when inside it (tracks `{` depth).
        // ネスト triple 自身のホール外は -1、ホール内は 0 以上（`{` の深さを追跡）。
        int nestedTripleHoleDepth = -1;
        // Triple-quoted string that appears *inside* the nested f-string's own
        // hole. Persists across lines so multi-line triples buried three levels
        // deep (outer f-string hole → nested triple f-string → its inner hole)
        // do not leak `}` into the inner hole's brace depth.
        // ネスト f-string の内側ホールに現れる三重引用符文字列の状態。行を
        // またいで保持し、3 段深いネスト（外側ホール → ネスト三重 f-string →
        // その内側ホール）に置かれた複数行三重が内側ホールの brace 数え上げに
        // `}` を漏らさないようにする。
        char innerHoleTripleChar = '\0';
        bool innerHoleTripleRaw = false;
        // Nested single-line f-string inside the outer hole. Persists across lines so
        // the body, its `{expr}` inner hole, and any triple-quoted string opened
        // inside that inner hole can straddle multiple source lines without losing
        // the inner hole's `}` back into the outer hole's brace depth.
        // 外側ホール内にあるネスト単行 f-string の状態。行をまたいで保持し、本体・
        // 内側 `{expr}` ホール・内側ホールで開かれた三重引用符文字列が複数行に
        // わたっても、内側ホールの `}` が外側ホールの brace 深度に漏れないようにする。
        char nestedSingleFStringQuote = '\0';
        bool nestedSingleFStringRaw = false;
        int nestedSingleFStringInnerHoleDepth = -1;
        char nestedSingleFStringInnerTripleChar = '\0';
        bool nestedSingleFStringInnerTripleRaw = false;

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
                if (tripleChar != '\0')
                {
                    if (holeBraceDepth >= 0)
                    {
                        // Inside an f-string `{expr}` hole: preserve chars so downstream
                        // regex extraction still sees real calls. Track nested braces so
                        // dict / set / nested f-string literal braces do not terminate early.
                        // Skip over nested string literals and `#` comments so their braces
                        // do not mis-close the hole.
                        // f-string `{expr}` ホール内: 文字を残して real call を抽出に見せる。
                        // dict/set/ネスト f-string のために brace 深度を追う。
                        // ホール内の文字列リテラルや `#` コメントはスキップし、内部の
                        // `{` / `}` が brace 深度に影響しないようにする。
                        if (nestedTripleChar != '\0')
                        {
                            // Scan contents of a nested triple-quoted string until we hit
                            // its closing triple. For plain nested triples mask all content
                            // to spaces so that indentation-sensitive downstream consumers
                            // (e.g. Python symbol-body extraction) still see a blank line
                            // here instead of stray `}` / `'''` at column 0. For nested
                            // triple f-strings, preserve the `{expr}` hole contents so real
                            // call edges inside the inner hole still survive; only the
                            // non-hole body is blanked.
                            // ネストした三重引用符文字列の本体を走査し、閉じ三重までの
                            // 全ての文字を空白に置き換える。インデント依存の後段
                            // （Python のシンボル本体抽出など）が 0 桁位置の `}` や
                            // `'''` を見てブロックを早終了しないようにする。ネスト三重
                            // f-string の場合は、内部 `{expr}` ホールの内容を保持して
                            // 実呼び出し edge を残し、ホール外の本体のみ空白化する。
                            if (nestedTripleIsFString && nestedTripleHoleDepth >= 0)
                            {
                                // Inside the nested f-string's own hole: preserve chars.
                                // Skip nested single-line / triple-quoted strings and Python
                                // `#` comments so their braces do not mis-close the hole.
                                // ネスト f-string 内部のホール: 文字を残して call edge を
                                // 抽出に見せる。単行文字列・三重引用符文字列・`#` コメントは
                                // スキップし、内部の `{` / `}` がホール深度に影響しないようにする。
                                if (innerHoleTripleChar != '\0')
                                {
                                    // Currently inside a triple-quoted string that opened in
                                    // the inner hole. Blank its body so downstream extraction
                                    // does not see `}` / `'` / `"` as real tokens, and close
                                    // on the matching triple.
                                    // 内側ホール内で開いた三重引用符文字列を走査中。下流が
                                    // `}` / `'` / `"` を実トークンとして読まないよう本体を
                                    // 空白化し、閉じ三重で抜ける。
                                    if (!innerHoleTripleRaw && line[pos] == '\\' && pos + 1 < line.Length)
                                    {
                                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                        pos += 2;
                                        continue;
                                    }

                                    if (pos + 2 < line.Length
                                        && line[pos] == innerHoleTripleChar
                                        && line[pos + 1] == innerHoleTripleChar
                                        && line[pos + 2] == innerHoleTripleChar)
                                    {
                                        ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                        pos += 3;
                                        innerHoleTripleChar = '\0';
                                        innerHoleTripleRaw = false;
                                        continue;
                                    }

                                    GetMaskedLine()[pos] = ' ';
                                    pos++;
                                    continue;
                                }

                                if (TryOpenPythonTripleString(line, pos, out var innerPrefixLen, out var innerQuote, out var innerRawFlag, out _))
                                {
                                    // A triple-quoted string starts inside the inner hole.
                                    // Its body is opaque; the closing triple restores inner-
                                    // hole scanning. SkipPythonSingleLineString can only
                                    // find a same-line match, so detect triples up front.
                                    // 内側ホール内で三重引用符文字列が開始。本体は opaque と
                                    // 見なし、閉じ三重で内側ホール走査に戻る。
                                    // SkipPythonSingleLineString は同一行の対を探すだけなので、
                                    // 三重を先に検出する必要がある。
                                    ReplaceWithSpaces(GetMaskedLine(), pos, innerPrefixLen + 3);
                                    pos += innerPrefixLen + 3;
                                    innerHoleTripleChar = innerQuote;
                                    innerHoleTripleRaw = innerRawFlag;
                                    continue;
                                }

                                if (line[pos] == '\'' || line[pos] == '"')
                                {
                                    pos = SkipPythonSingleLineString(line, pos);
                                    continue;
                                }

                                if (line[pos] == '#')
                                    break;

                                if (line[pos] == '{')
                                {
                                    nestedTripleHoleDepth++;
                                    pos++;
                                    continue;
                                }

                                if (line[pos] == '}')
                                {
                                    if (nestedTripleHoleDepth == 0)
                                    {
                                        GetMaskedLine()[pos] = ' ';
                                        nestedTripleHoleDepth = -1;
                                        pos++;
                                        continue;
                                    }

                                    nestedTripleHoleDepth--;
                                    pos++;
                                    continue;
                                }

                                pos++;
                                continue;
                            }

                            if (!nestedTripleRaw && line[pos] == '\\' && pos + 1 < line.Length)
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (pos + 2 < line.Length
                                && line[pos] == nestedTripleChar
                                && line[pos + 1] == nestedTripleChar
                                && line[pos + 2] == nestedTripleChar)
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                pos += 3;
                                nestedTripleChar = '\0';
                                nestedTripleRaw = false;
                                nestedTripleIsFString = false;
                                nestedTripleHoleDepth = -1;
                                continue;
                            }

                            if (nestedTripleIsFString)
                            {
                                // `{{` / `}}` are escaped literal braces — blank both chars.
                                // `{{` / `}}` は literal brace のエスケープ。両方空白化。
                                if (pos + 1 < line.Length && line[pos] == '{' && line[pos + 1] == '{')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                    pos += 2;
                                    continue;
                                }

                                if (pos + 1 < line.Length && line[pos] == '}' && line[pos + 1] == '}')
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                    pos += 2;
                                    continue;
                                }

                                if (line[pos] == '{')
                                {
                                    GetMaskedLine()[pos] = ' ';
                                    nestedTripleHoleDepth = 0;
                                    pos++;
                                    continue;
                                }
                            }

                            GetMaskedLine()[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (nestedSingleFStringQuote != '\0')
                        {
                            // Inside the body of a nested single-line f-string that was
                            // opened earlier in the outer hole. This branch persists
                            // across lines so a multi-line triple-quoted string opened
                            // inside the inner `{expr}` hole does not leak its `}` back
                            // into the outer hole's brace depth and truncate the outer
                            // f-string early.
                            // 外側ホール内で開かれたネスト単行 f-string の本体を走査。
                            // 内側 `{expr}` ホールに複数行の三重引用符文字列が現れても、
                            // その `}` が外側ホールの brace 深度に漏れて外側 f-string を
                            // 早閉じしないよう、状態を行をまたいで保持する。
                            if (nestedSingleFStringInnerHoleDepth >= 0)
                            {
                                if (nestedSingleFStringInnerTripleChar != '\0')
                                {
                                    if (!nestedSingleFStringInnerTripleRaw && line[pos] == '\\' && pos + 1 < line.Length)
                                    {
                                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                        pos += 2;
                                        continue;
                                    }

                                    if (pos + 2 < line.Length
                                        && line[pos] == nestedSingleFStringInnerTripleChar
                                        && line[pos + 1] == nestedSingleFStringInnerTripleChar
                                        && line[pos + 2] == nestedSingleFStringInnerTripleChar)
                                    {
                                        ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                                        pos += 3;
                                        nestedSingleFStringInnerTripleChar = '\0';
                                        nestedSingleFStringInnerTripleRaw = false;
                                        continue;
                                    }

                                    GetMaskedLine()[pos] = ' ';
                                    pos++;
                                    continue;
                                }

                                if (TryOpenPythonTripleString(line, pos, out var nestedSingleInnerPrefixLen, out var nestedSingleInnerQuote, out var nestedSingleInnerRawFlag, out _))
                                {
                                    ReplaceWithSpaces(GetMaskedLine(), pos, nestedSingleInnerPrefixLen + 3);
                                    pos += nestedSingleInnerPrefixLen + 3;
                                    nestedSingleFStringInnerTripleChar = nestedSingleInnerQuote;
                                    nestedSingleFStringInnerTripleRaw = nestedSingleInnerRawFlag;
                                    continue;
                                }

                                if (line[pos] == '\'' || line[pos] == '"')
                                {
                                    pos = SkipPythonSingleLineString(line, pos);
                                    continue;
                                }

                                if (line[pos] == '#')
                                    break;

                                if (line[pos] == '{')
                                {
                                    nestedSingleFStringInnerHoleDepth++;
                                    pos++;
                                    continue;
                                }

                                if (line[pos] == '}')
                                {
                                    if (nestedSingleFStringInnerHoleDepth == 0)
                                    {
                                        GetMaskedLine()[pos] = ' ';
                                        nestedSingleFStringInnerHoleDepth = -1;
                                        pos++;
                                        continue;
                                    }

                                    nestedSingleFStringInnerHoleDepth--;
                                    pos++;
                                    continue;
                                }

                                pos++;
                                continue;
                            }

                            if (!nestedSingleFStringRaw && line[pos] == '\\' && pos + 1 < line.Length)
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (line[pos] == nestedSingleFStringQuote)
                            {
                                GetMaskedLine()[pos] = ' ';
                                nestedSingleFStringQuote = '\0';
                                nestedSingleFStringRaw = false;
                                pos++;
                                continue;
                            }

                            if (pos + 1 < line.Length && line[pos] == '{' && line[pos + 1] == '{')
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (pos + 1 < line.Length && line[pos] == '}' && line[pos + 1] == '}')
                            {
                                ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (line[pos] == '{')
                            {
                                GetMaskedLine()[pos] = ' ';
                                nestedSingleFStringInnerHoleDepth = 0;
                                pos++;
                                continue;
                            }

                            GetMaskedLine()[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (TryOpenPythonTripleString(line, pos, out var nestedPrefixLen, out var nestedQuote, out var nestedRawFlag, out var nestedFFlag))
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, nestedPrefixLen + 3);
                            pos += nestedPrefixLen + 3;
                            nestedTripleChar = nestedQuote;
                            nestedTripleRaw = nestedRawFlag;
                            nestedTripleIsFString = nestedFFlag;
                            nestedTripleHoleDepth = -1;
                            continue;
                        }

                        if (TryOpenPythonSingleLineString(line, pos, out var nestedSinglePrefixLen, out var nestedSingleQuote, out var nestedSingleRaw, out var nestedSingleFString)
                            && nestedSingleFString)
                        {
                            // Nested single-line f-string inside the outer hole. Mask the
                            // quote characters (and prefix) and stash the per-string state
                            // so ReferenceExtractor's StringLiteralRegex does not swallow
                            // the hole expression while still letting the inner `{expr}`
                            // (and any triple-quoted string opened inside it) straddle
                            // multiple source lines.
                            // 外側ホール内のネストした単行 f-string。PrepareLine の
                            // StringLiteralRegex に式本体ごと消されないよう quote と prefix を
                            // マスクし、内側 `{expr}`（および内側ホールで開いた三重引用符
                            // 文字列）が複数行にまたがっても追跡できるよう状態を保持する。
                            ReplaceWithSpaces(GetMaskedLine(), pos, nestedSinglePrefixLen + 1);
                            pos += nestedSinglePrefixLen + 1;
                            nestedSingleFStringQuote = nestedSingleQuote;
                            nestedSingleFStringRaw = nestedSingleRaw;
                            nestedSingleFStringInnerHoleDepth = -1;
                            nestedSingleFStringInnerTripleChar = '\0';
                            nestedSingleFStringInnerTripleRaw = false;
                            continue;
                        }

                        if (line[pos] == '\'' || line[pos] == '"')
                        {
                            pos = SkipPythonSingleLineString(line, pos);
                            continue;
                        }

                        if (line[pos] == '#')
                        {
                            // `#` starts a Python comment; skip the rest of the line.
                            // `#` から行末までは Python のコメント。
                            break;
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
                                // Mask the closing `}` so it still looks like string
                                // delimiter noise to regex extraction.
                                // 閉じ `}` は文字列境界としてマスクし、regex 抽出に
                                // ホール本体と混在させない。
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

                    if (!isRaw && line[pos] == '\\' && pos + 1 < line.Length)
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                        pos += 2;
                        continue;
                    }

                    if (pos + 2 < line.Length
                        && line[pos] == tripleChar
                        && line[pos + 1] == tripleChar
                        && line[pos + 2] == tripleChar)
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 3);
                        pos += 3;
                        tripleChar = '\0';
                        isRaw = false;
                        isFString = false;
                        continue;
                    }

                    if (isFString)
                    {
                        // `{{` / `}}` are escapes for literal braces — mask both as spaces.
                        // `{{` / `}}` は literal brace のエスケープ。両方マスク。
                        if (pos + 1 < line.Length && line[pos] == '{' && line[pos + 1] == '{')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            pos += 2;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '}' && line[pos + 1] == '}')
                        {
                            ReplaceWithSpaces(GetMaskedLine(), pos, 2);
                            pos += 2;
                            continue;
                        }

                        if (line[pos] == '{')
                        {
                            // Mask the opening `{` so brace balance matches the closing
                            // `}` we also mask; the expression contents are left alone.
                            // 開き `{` をマスクしつつ、式本体は残す。
                            GetMaskedLine()[pos] = ' ';
                            holeBraceDepth = 0;
                            pos++;
                            continue;
                        }
                    }

                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    continue;
                }

                // Outside any string; `#` starts a line comment (ignore rest of line).
                // 文字列外で `#` は行コメント開始。以降は走査しない。
                if (line[pos] == '#')
                    break;

                if (TryOpenPythonTripleString(line, pos, out var prefixLen, out var openingChar, out var rawFlag, out var fFlag))
                {
                    ReplaceWithSpaces(GetMaskedLine(), pos, prefixLen + 3);
                    pos += prefixLen + 3;
                    tripleChar = openingChar;
                    isRaw = rawFlag;
                    isFString = fFlag;
                    continue;
                }

                if (line[pos] == '"' || line[pos] == '\'')
                {
                    pos = SkipPythonSingleLineString(line, pos);
                    continue;
                }

                pos++;
            }

            if (masked is not null)
                lines[i] = new string(masked);
        }
    }


}
