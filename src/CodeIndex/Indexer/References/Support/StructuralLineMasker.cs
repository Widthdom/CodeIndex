namespace CodeIndex.Indexer;

/// <summary>
/// JavaScript / TypeScript tagged template literal call site captured while masking.
/// Line and Column are 1-based; Column points to the tag identifier's starting column.
/// マスク走査中に検出した JS/TS タグ付きテンプレート呼び出し。Line/Column は 1 始まり。
/// </summary>
internal readonly record struct JsTaggedTemplateHit(int Line, int Column, string Name, bool IsMemberAccess);

/// <summary>
/// Masks non-code regions that would otherwise confuse line-based structural regexes.
/// 行ベースの構造 regex を誤誘導する非コード領域をマスクする。
/// </summary>
internal static class StructuralLineMasker
{
    private enum StringKind
    {
        Regular,
        Verbatim,
        Raw,
    }

    private abstract class ScannerFrame;

    private sealed class BlockCommentFrame : ScannerFrame;

    private sealed class CharLiteralFrame : ScannerFrame;

    private sealed class StringFrame : ScannerFrame
    {
        public required StringKind Kind { get; init; }
        public required int DelimiterLength { get; init; }
        public required int InterpolationBraceCount { get; init; }
    }

    private sealed class InterpolationFrame : ScannerFrame
    {
        public required int CloseBraceCount { get; init; }
        public int NestedBraceDepth { get; set; }
    }

    // Frames used by the JS/TS template literal scanner only. C# uses the frames above.
    // The frame snapshots the enclosing `JsLexState` on push so the closing backtick can
    // restore the paren stack, class-header hint, and case-label hint that would otherwise
    // be lost by resetting `lexState` for the template body. Without this, patterns like
    // `if (\`${x}\`) /regex/` lose the statement-head `(` context: `)` after the template
    // becomes `CloseParen` instead of `StatementHeadCloseParen`, so the next `/` falls to
    // division and the regex body's backtick is misread as a phantom template opener.
    // JS/TS テンプレートリテラル scanner 専用のフレーム。C# は上のフレームを使う。
    // push 時に外側の `JsLexState` を退避し、閉じ backtick で復元する。これがないと
    // `if (\`${x}\`) /regex/` のように、テンプレート直前に積んだ statement-head `(` の
    // コンテキストがテンプレート本体の Reset で失われ、`)` 後の `/` が division に落ちて
    // regex 本文の backtick を phantom template と誤認してしまう。
    private sealed class JsTemplateLiteralFrame : ScannerFrame
    {
        public JsLexState SavedLexState;
    }

    private sealed class JsTemplateHoleFrame : ScannerFrame
    {
        public int NestedBraceDepth { get; set; }
        // Stack entry `true` = nested `{` opened an expression brace (object literal /
        // arrow-body-with-parens): the matching `}` behaves like `)`/`]` and the next
        // `/` is division. Entry `false` = nested `{` opened a statement block (e.g.
        // `if (x) {}`, `() => { ... }`): the matching `}` keeps regex legal for the
        // next `/`. Using a stack lets us mix both kinds within a single hole.
        // スタック値 `true` は expression brace (object literal / `() => ({})`)で、
        // `}` のあとの `/` を division として扱う。`false` は statement block で、
        // `}` のあとの `/` は regex として扱う。ホール内で両者が混在しても追える。
        public Stack<bool> InnerBraceIsExpression { get; } = new();
    }

    internal static string[] MaskLines(string? lang, string[] originalLines)
        => MaskLines(lang, originalLines, out _);

    internal static string[] MaskLines(string? lang, string[] originalLines, out List<JsTaggedTemplateHit>? jsTaggedTemplateHits)
    {
        jsTaggedTemplateHits = null;
        if (!RequiresStructuralMasking(lang))
            return originalLines;

        var maskedLines = (string[])originalLines.Clone();

        switch (lang)
        {
            case "csharp":
                MaskCSharpRawStringContents(maskedLines);
                break;
            case "python":
                MaskPythonTripleStringContents(maskedLines);
                break;
            case "rust":
                MaskRustRawStringContents(maskedLines);
                break;
            case "javascript":
            case "typescript":
                jsTaggedTemplateHits = new List<JsTaggedTemplateHit>();
                MaskJsTsTemplateLiteralContents(maskedLines, jsTaggedTemplateHits, lang);
                break;
            case "kotlin":
                MaskKotlinTripleStringContents(maskedLines);
                break;
            case "swift":
                MaskSwiftMultilineStringContents(maskedLines);
                break;
            case "scala":
                MaskScalaTripleStringContents(maskedLines);
                break;
            case "perl":
                MaskPerlPodSections(maskedLines);
                break;
        }

        return maskedLines;
    }

    private static bool RequiresStructuralMasking(string? lang) => lang is
        "csharp"
        or "python"
        or "rust"
        or "javascript"
        or "typescript"
        or "kotlin"
        or "swift"
        or "scala"
        or "perl";

    private static void MaskPerlPodSections(string[] lines)
    {
        var inPod = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                if (inPod)
                    lines[i] = new string(' ', line.Length);
                continue;
            }

            if (IsPerlPodDirectiveLine(trimmed))
            {
                lines[i] = new string(' ', line.Length);
                inPod = !string.Equals(trimmed, "=cut", StringComparison.Ordinal);
                continue;
            }

            if (inPod)
                lines[i] = new string(' ', line.Length);
        }
    }

    private static bool IsPerlPodDirectiveLine(string trimmedLine)
    {
        return trimmedLine.StartsWith("=", StringComparison.Ordinal)
            && trimmedLine.Length > 1
            && (trimmedLine[1] == 'c' && trimmedLine.Length >= 4 && string.Equals(trimmedLine[..4], "=cut", StringComparison.Ordinal)
                || char.IsLetter(trimmedLine[1]));
    }

    private static void MaskCSharpRawStringContents(string[] lines)
    {
        var frames = new Stack<ScannerFrame>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            var masked = line.ToCharArray();
            var searchStart = 0;

            while (searchStart < line.Length)
            {
                if (frames.TryPeek(out var activeFrame))
                {
                    if (activeFrame is BlockCommentFrame)
                    {
                        if (StartsWith(line, searchStart, "*/"))
                        {
                            ReplaceWithSpaces(masked, searchStart, 2);
                            searchStart += 2;
                            frames.Pop();
                            continue;
                        }

                        masked[searchStart] = ' ';
                        searchStart++;
                        continue;
                    }

                    if (activeFrame is CharLiteralFrame)
                    {
                        if (line[searchStart] == '\\')
                        {
                            searchStart += Math.Min(2, line.Length - searchStart);
                            continue;
                        }

                        if (line[searchStart] == '\'')
                            frames.Pop();

                        searchStart++;
                        continue;
                    }

                    if (activeFrame is StringFrame stringFrame)
                    {
                        if (stringFrame.InterpolationBraceCount == 1 && stringFrame.Kind != StringKind.Raw)
                        {
                            if (StartsWith(line, searchStart, "{{") || StartsWith(line, searchStart, "}}"))
                            {
                                ReplaceWithSpaces(masked, searchStart, 2);
                                searchStart += 2;
                                continue;
                            }
                        }

                        if (stringFrame.InterpolationBraceCount > 0)
                        {
                            var openBraceRun = CountRun(line, searchStart, '{');
                            if (openBraceRun >= stringFrame.InterpolationBraceCount)
                            {
                                ReplaceWithSpaces(masked, searchStart, stringFrame.InterpolationBraceCount);
                                searchStart += stringFrame.InterpolationBraceCount;
                                frames.Push(new InterpolationFrame { CloseBraceCount = stringFrame.InterpolationBraceCount });
                                continue;
                            }
                        }

                        if (stringFrame.Kind == StringKind.Raw)
                        {
                            var closeLength = CountQuoteRun(line, searchStart);
                            if (closeLength == stringFrame.DelimiterLength)
                            {
                                ReplaceWithSpaces(masked, searchStart, closeLength);
                                searchStart += closeLength;
                                frames.Pop();
                                continue;
                            }

                            if (closeLength > 0)
                            {
                                ReplaceWithSpaces(masked, searchStart, closeLength);
                                searchStart += closeLength;
                                continue;
                            }

                            masked[searchStart++] = ' ';
                            continue;
                        }

                        if (stringFrame.Kind == StringKind.Verbatim)
                        {
                            if (line[searchStart] == '"' && searchStart + 1 < line.Length && line[searchStart + 1] == '"')
                            {
                                ReplaceWithSpaces(masked, searchStart, 2);
                                searchStart += 2;
                                continue;
                            }

                            masked[searchStart] = ' ';
                            if (line[searchStart] == '"')
                                frames.Pop();

                            searchStart++;
                            continue;
                        }

                        masked[searchStart] = ' ';
                        if (line[searchStart] == '\\')
                        {
                            if (searchStart + 1 < line.Length)
                                masked[searchStart + 1] = ' ';

                            searchStart += Math.Min(2, line.Length - searchStart);
                            continue;
                        }

                        if (line[searchStart] == '"')
                            frames.Pop();

                        searchStart++;
                        continue;
                    }

                    if (activeFrame is InterpolationFrame interpolationFrame)
                    {
                        if (StartsWith(line, searchStart, "//"))
                            break;

                        if (StartsWith(line, searchStart, "/*"))
                        {
                            ReplaceWithSpaces(masked, searchStart, 2);
                            frames.Push(new BlockCommentFrame());
                            searchStart += 2;
                            continue;
                        }

                        if (TryStartString(line, searchStart, out var nestedStringLength, out var nestedStringFrame))
                        {
                            ReplaceWithSpaces(masked, searchStart, nestedStringLength);
                            searchStart += nestedStringLength;
                            frames.Push(nestedStringFrame);
                            continue;
                        }

                        if (line[searchStart] == '\'')
                        {
                            frames.Push(new CharLiteralFrame());
                            searchStart++;
                            continue;
                        }

                        var closeBraceRun = CountRun(line, searchStart, '}');
                        if (interpolationFrame.NestedBraceDepth == 0 && closeBraceRun >= interpolationFrame.CloseBraceCount)
                        {
                            ReplaceWithSpaces(masked, searchStart, interpolationFrame.CloseBraceCount);
                            searchStart += interpolationFrame.CloseBraceCount;
                            frames.Pop();
                            continue;
                        }

                        if (line[searchStart] == '{')
                        {
                            interpolationFrame.NestedBraceDepth++;
                            searchStart++;
                            continue;
                        }

                        if (line[searchStart] == '}' && interpolationFrame.NestedBraceDepth > 0)
                        {
                            interpolationFrame.NestedBraceDepth--;
                            searchStart++;
                            continue;
                        }

                        searchStart++;
                        continue;
                    }
                }

                if (StartsWith(line, searchStart, "//"))
                    break;

                if (StartsWith(line, searchStart, "/*"))
                {
                    ReplaceWithSpaces(masked, searchStart, 2);
                    frames.Push(new BlockCommentFrame());
                    searchStart += 2;
                    continue;
                }

                if (TryStartString(line, searchStart, out var openingLength, out var openingFrame))
                {
                    ReplaceWithSpaces(masked, searchStart, openingLength);
                    searchStart += openingLength;
                    frames.Push(openingFrame);
                    continue;
                }

                if (line[searchStart] == '\'')
                {
                    frames.Push(new CharLiteralFrame());
                    searchStart++;
                    continue;
                }

                searchStart++;
            }

            lines[i] = new string(masked);
        }
    }

    private static int CountQuoteRun(string line, int startIndex)
    {
        return CountRun(line, startIndex, '"');
    }

    private static bool StartsWith(string line, int startIndex, string value)
    {
        if (startIndex + value.Length > line.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (line[startIndex + i] != value[i])
                return false;
        }

        return true;
    }

    private static bool IsInterpolatedVerbatimStringStart(string line, int startIndex) =>
        StartsWith(line, startIndex, "$@\"") || StartsWith(line, startIndex, "@$\"");

    private static bool TryStartString(string line, int startIndex, out int openingLength, out StringFrame frame)
    {
        if (IsInterpolatedVerbatimStringStart(line, startIndex))
        {
            openingLength = 3;
            frame = new StringFrame
            {
                Kind = StringKind.Verbatim,
                DelimiterLength = 1,
                InterpolationBraceCount = 1,
            };
            return true;
        }

        if (StartsWith(line, startIndex, "@\""))
        {
            openingLength = 2;
            frame = new StringFrame
            {
                Kind = StringKind.Verbatim,
                DelimiterLength = 1,
                InterpolationBraceCount = 0,
            };
            return true;
        }

        var dollarCount = CountRun(line, startIndex, '$');
        var rawDelimiterLength = CountQuoteRun(line, startIndex + dollarCount);
        if (dollarCount > 0 && rawDelimiterLength >= 3)
        {
            openingLength = dollarCount + rawDelimiterLength;
            frame = new StringFrame
            {
                Kind = StringKind.Raw,
                DelimiterLength = rawDelimiterLength,
                InterpolationBraceCount = dollarCount,
            };
            return true;
        }

        var rawOpenLength = CountQuoteRun(line, startIndex);
        if (rawOpenLength >= 3)
        {
            openingLength = rawOpenLength;
            frame = new StringFrame
            {
                Kind = StringKind.Raw,
                DelimiterLength = rawOpenLength,
                InterpolationBraceCount = 0,
            };
            return true;
        }

        if (StartsWith(line, startIndex, "$\""))
        {
            openingLength = 2;
            frame = new StringFrame
            {
                Kind = StringKind.Regular,
                DelimiterLength = 1,
                InterpolationBraceCount = 1,
            };
            return true;
        }

        if (line[startIndex] == '"')
        {
            openingLength = 1;
            frame = new StringFrame
            {
                Kind = StringKind.Regular,
                DelimiterLength = 1,
                InterpolationBraceCount = 0,
            };
            return true;
        }

        openingLength = 0;
        frame = null!;
        return false;
    }

    private static int CountRun(string line, int startIndex, char value)
    {
        if (startIndex >= line.Length || line[startIndex] != value)
            return 0;

        var length = 1;
        while (startIndex + length < line.Length && line[startIndex + length] == value)
            length++;

        return length;
    }

    private static void ReplaceWithSpaces(char[] buffer, int start, int length)
    {
        for (int i = start; i < start + length; i++)
            buffer[i] = ' ';
    }

    private static bool LooksLikeDeepTripleOpenerContext(string[] lines, int lineIndex, int pos, int delimiterLength)
    {
        if (LooksLikeDeepTripleCloserTail(lines[lineIndex], pos + delimiterLength))
            return false;

        if (!TryGetPreviousNonWhitespacePosition(lines, lineIndex, pos, out var prevLine, out var prevPos))
            return true;

        var prev = lines[prevLine][prevPos];
        if (prev is ')' or ']' or '}' or '.' or '"' or '\'')
            return false;

        if (IsIdentifierPart(prev))
        {
            var identLine = prevLine;
            var identPos = prevPos;
            while (TryGetPreviousNonWhitespacePosition(lines, identLine, identPos, out var runLine, out var runPos)
                && IsIdentifierPart(lines[runLine][runPos]))
            {
                identLine = runLine;
                identPos = runPos;
            }

            if (!TryGetPreviousNonWhitespacePosition(lines, identLine, identPos, out var beforeLine, out var beforePos))
                return true;

            prev = lines[beforeLine][beforePos];
        }

        return prev is '(' or '[' or '{' or '=' or ',' or ';' or '?' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '!' or '<' or '>' or '#';
    }

    private static bool LooksLikeDeepTripleCloserTail(string line, int startIndex)
    {
        for (int i = startIndex; i < line.Length; i++)
        {
            var ch = line[i];
            if (IsDeepTripleWhitespace(ch))
                continue;

            return ch is ')' or ']' or '}' or ',' or ';' or '.';
        }

        return false;
    }

    private static bool TryGetPreviousNonWhitespacePosition(string[] lines, int lineIndex, int pos, out int previousLineIndex, out int previousColumn)
    {
        previousLineIndex = lineIndex;
        previousColumn = pos - 1;

        while (previousLineIndex >= 0)
        {
            var line = lines[previousLineIndex];
            while (previousColumn >= 0)
            {
                var ch = line[previousColumn];
                if (!IsDeepTripleWhitespace(ch))
                    return true;

                previousColumn--;
            }

            previousLineIndex--;
            if (previousLineIndex < 0)
                break;

            previousColumn = lines[previousLineIndex].Length - 1;
        }

        return false;
    }

    private static bool IsDeepTripleWhitespace(char ch) =>
        char.IsWhiteSpace(ch) || ch == '\uFEFF';

    // Python triple-quoted strings: """...""" and '''...''' (with optional r/b/u/f prefixes).
    // f-string interpolation holes `{expr}` preserve expression contents so downstream
    // reference extraction still sees real call edges; `{{` / `}}` are escape sequences.
    // Python の三重引用符文字列: """...""" と '''...'''（r/b/u/f 接頭辞対応）。
    // f-string の補間ホール `{expr}` は内容を残し、real call を参照抽出に見せる。
    // `{{` / `}}` は literal 用のエスケープ。
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

            var masked = line.ToCharArray();
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
                                        ReplaceWithSpaces(masked, pos, 2);
                                        pos += 2;
                                        continue;
                                    }

                                    if (pos + 2 < line.Length
                                        && line[pos] == innerHoleTripleChar
                                        && line[pos + 1] == innerHoleTripleChar
                                        && line[pos + 2] == innerHoleTripleChar)
                                    {
                                        ReplaceWithSpaces(masked, pos, 3);
                                        pos += 3;
                                        innerHoleTripleChar = '\0';
                                        innerHoleTripleRaw = false;
                                        continue;
                                    }

                                    masked[pos] = ' ';
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
                                    ReplaceWithSpaces(masked, pos, innerPrefixLen + 3);
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
                                        masked[pos] = ' ';
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
                                ReplaceWithSpaces(masked, pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (pos + 2 < line.Length
                                && line[pos] == nestedTripleChar
                                && line[pos + 1] == nestedTripleChar
                                && line[pos + 2] == nestedTripleChar)
                            {
                                ReplaceWithSpaces(masked, pos, 3);
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
                                    ReplaceWithSpaces(masked, pos, 2);
                                    pos += 2;
                                    continue;
                                }

                                if (pos + 1 < line.Length && line[pos] == '}' && line[pos + 1] == '}')
                                {
                                    ReplaceWithSpaces(masked, pos, 2);
                                    pos += 2;
                                    continue;
                                }

                                if (line[pos] == '{')
                                {
                                    masked[pos] = ' ';
                                    nestedTripleHoleDepth = 0;
                                    pos++;
                                    continue;
                                }
                            }

                            masked[pos] = ' ';
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
                                        ReplaceWithSpaces(masked, pos, 2);
                                        pos += 2;
                                        continue;
                                    }

                                    if (pos + 2 < line.Length
                                        && line[pos] == nestedSingleFStringInnerTripleChar
                                        && line[pos + 1] == nestedSingleFStringInnerTripleChar
                                        && line[pos + 2] == nestedSingleFStringInnerTripleChar)
                                    {
                                        ReplaceWithSpaces(masked, pos, 3);
                                        pos += 3;
                                        nestedSingleFStringInnerTripleChar = '\0';
                                        nestedSingleFStringInnerTripleRaw = false;
                                        continue;
                                    }

                                    masked[pos] = ' ';
                                    pos++;
                                    continue;
                                }

                                if (TryOpenPythonTripleString(line, pos, out var nestedSingleInnerPrefixLen, out var nestedSingleInnerQuote, out var nestedSingleInnerRawFlag, out _))
                                {
                                    ReplaceWithSpaces(masked, pos, nestedSingleInnerPrefixLen + 3);
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
                                        masked[pos] = ' ';
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
                                ReplaceWithSpaces(masked, pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (line[pos] == nestedSingleFStringQuote)
                            {
                                masked[pos] = ' ';
                                nestedSingleFStringQuote = '\0';
                                nestedSingleFStringRaw = false;
                                pos++;
                                continue;
                            }

                            if (pos + 1 < line.Length && line[pos] == '{' && line[pos + 1] == '{')
                            {
                                ReplaceWithSpaces(masked, pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (pos + 1 < line.Length && line[pos] == '}' && line[pos + 1] == '}')
                            {
                                ReplaceWithSpaces(masked, pos, 2);
                                pos += 2;
                                continue;
                            }

                            if (line[pos] == '{')
                            {
                                masked[pos] = ' ';
                                nestedSingleFStringInnerHoleDepth = 0;
                                pos++;
                                continue;
                            }

                            masked[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (TryOpenPythonTripleString(line, pos, out var nestedPrefixLen, out var nestedQuote, out var nestedRawFlag, out var nestedFFlag))
                        {
                            ReplaceWithSpaces(masked, pos, nestedPrefixLen + 3);
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
                            ReplaceWithSpaces(masked, pos, nestedSinglePrefixLen + 1);
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
                                masked[pos] = ' ';
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
                        ReplaceWithSpaces(masked, pos, 2);
                        pos += 2;
                        continue;
                    }

                    if (pos + 2 < line.Length
                        && line[pos] == tripleChar
                        && line[pos + 1] == tripleChar
                        && line[pos + 2] == tripleChar)
                    {
                        ReplaceWithSpaces(masked, pos, 3);
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
                            ReplaceWithSpaces(masked, pos, 2);
                            pos += 2;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '}' && line[pos + 1] == '}')
                        {
                            ReplaceWithSpaces(masked, pos, 2);
                            pos += 2;
                            continue;
                        }

                        if (line[pos] == '{')
                        {
                            // Mask the opening `{` so brace balance matches the closing
                            // `}` we also mask; the expression contents are left alone.
                            // 開き `{` をマスクしつつ、式本体は残す。
                            masked[pos] = ' ';
                            holeBraceDepth = 0;
                            pos++;
                            continue;
                        }
                    }

                    masked[pos] = ' ';
                    pos++;
                    continue;
                }

                // Outside any string; `#` starts a line comment (ignore rest of line).
                // 文字列外で `#` は行コメント開始。以降は走査しない。
                if (line[pos] == '#')
                    break;

                if (TryOpenPythonTripleString(line, pos, out var prefixLen, out var openingChar, out var rawFlag, out var fFlag))
                {
                    ReplaceWithSpaces(masked, pos, prefixLen + 3);
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

            lines[i] = new string(masked);
        }
    }

    private static bool TryOpenPythonTripleString(string line, int startIndex, out int prefixLength, out char tripleChar, out bool isRaw, out bool isFString)
    {
        prefixLength = 0;
        tripleChar = '\0';
        isRaw = false;
        isFString = false;

        if (startIndex > 0 && IsIdentifierPart(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var seenRaw = false;
        var seenF = false;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] == 'r' || line[p] == 'R')
                seenRaw = true;
            else if (line[p] == 'f' || line[p] == 'F')
                seenF = true;
            p++;
            prefixChars++;
        }

        if (p + 2 < line.Length && (line[p] == '"' || line[p] == '\'') && line[p] == line[p + 1] && line[p] == line[p + 2])
        {
            prefixLength = p - startIndex;
            tripleChar = line[p];
            isRaw = seenRaw;
            isFString = seenF;
            return true;
        }

        return false;
    }

    private static bool IsPythonStringPrefixChar(char c) =>
        c is 'r' or 'R' or 'b' or 'B' or 'u' or 'U' or 'f' or 'F';

    private static int SkipPythonSingleLineString(string line, int startIndex)
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

    private static bool TryOpenPythonSingleLineString(string line, int startIndex, out int prefixLength, out char quoteChar, out bool isRaw, out bool isFString)
    {
        prefixLength = 0;
        quoteChar = '\0';
        isRaw = false;
        isFString = false;

        if (startIndex > 0 && IsIdentifierPart(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var seenRaw = false;
        var seenF = false;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] == 'r' || line[p] == 'R')
                seenRaw = true;
            else if (line[p] == 'f' || line[p] == 'F')
                seenF = true;
            p++;
            prefixChars++;
        }

        if (p >= line.Length)
            return false;

        if (line[p] != '"' && line[p] != '\'')
            return false;

        // Triple-quoted strings are handled by the dedicated triple scanner; skip here.
        // 三重引用符は別の scanner が扱うのでここでは対象外。
        if (p + 2 < line.Length && line[p] == line[p + 1] && line[p] == line[p + 2])
            return false;

        prefixLength = p - startIndex;
        quoteChar = line[p];
        isRaw = seenRaw;
        isFString = seenF;
        return true;
    }

    // Rust raw string literals: r"...", r#"..."#, r##"..."##, ... (also with b/c byte/C-string prefix).
    // Rust の raw string リテラル: r"..." や r#"..."#、r##"..."## など（b/c 接頭辞も）。
    private static void MaskRustRawStringContents(string[] lines)
    {
        var hashCount = -1;
        // Rust supports nested `/* ... */` block comments; track depth across lines.
        // Rust の `/* ... */` ブロックコメントはネスト可能で、行をまたぐため深度で管理する。
        var blockCommentDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            var masked = line.ToCharArray();
            var pos = 0;

            while (pos < line.Length)
            {
                if (blockCommentDepth > 0)
                {
                    if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                    {
                        // Blank the nested `/*` opener so downstream reference
                        // extraction cannot mistake it for real tokens.
                        // ネストされた `/*` 自体も空白化し、後段の参照抽出が
                        // 実トークンと誤認しないようにする。
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth++;
                        pos += 2;
                        continue;
                    }

                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                    {
                        // Blank the `*/` closer alongside the body so pseudo
                        // references inside nested comments never reach the
                        // downstream simple comment stripper.
                        // `*/` の閉じも本文と同様に空白化し、ネストされた
                        // コメント内の疑似参照が下流の単純な comment stripper
                        // をすり抜けないようにする。
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth--;
                        pos += 2;
                        continue;
                    }

                    // Rust block comments nest, but downstream comment stripping
                    // is non-nesting. Blank the body so identifiers inside an
                    // outer-closed comment do not leak as phantom references.
                    // Rust の block comment はネスト可能だが下流の comment
                    // 除去はネスト非対応。本文を空白化し、外側閉じに
                    // 巻き込まれた識別子が疑似参照として残らないようにする。
                    masked[pos] = ' ';
                    pos++;
                    continue;
                }

                if (hashCount >= 0)
                {
                    if (line[pos] == '"' && HasHashRun(line, pos + 1, hashCount))
                    {
                        ReplaceWithSpaces(masked, pos, 1 + hashCount);
                        pos += 1 + hashCount;
                        hashCount = -1;
                        continue;
                    }

                    masked[pos] = ' ';
                    pos++;
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                    break;

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    // Must enter block-comment state before `TryOpenRustRawString`, otherwise
                    // `/* r#" */` would be mis-parsed as a real raw string opener and swallow
                    // subsequent source until the next `"#`. Blank the opener so nested-comment
                    // body blanking stays contiguous.
                    // `TryOpenRustRawString` の前に block comment 状態へ入る。そうしないと
                    // `/* r#" */` が本物の raw string 開始と誤認され、次の `"#` まで
                    // 以降のソースを丸ごとマスクしてしまう。ネストコメント本文の空白化と
                    // 連続させるため `/*` 自体も空白化する。
                    ReplaceWithSpaces(masked, pos, 2);
                    blockCommentDepth = 1;
                    pos += 2;
                    continue;
                }

                if (line[pos] == '\'')
                {
                    // `'X'` / `'\n'` char literals may contain `"`, `r#`, or other sequences
                    // that would otherwise trip the raw-string / string scanners. Lifetimes
                    // (`'a`, `'static`) are advanced one char at a time so their trailing
                    // identifier is scanned normally.
                    // `'X'` / `'\n'` の char literal 内には `"` や `r#` が入りうる。それらを
                    // 丸ごと読み飛ばす。lifetime (`'a`, `'static`) は 1 文字だけ進めて後続の
                    // 識別子を通常走査へ渡す。
                    pos = SkipRustCharLiteralOrLifetime(line, pos);
                    continue;
                }

                if (TryOpenRustRawString(line, pos, out var openingLength, out var hashes))
                {
                    ReplaceWithSpaces(masked, pos, openingLength);
                    pos += openingLength;
                    hashCount = hashes;
                    continue;
                }

                if (line[pos] == '"')
                {
                    // Ordinary non-raw string; Rust permits newlines inside but the common case is single-line.
                    // 通常の非 raw 文字列。Rust は改行を許すが、実運用では単一行がほとんど。
                    pos = SkipRustSingleLineString(line, pos);
                    continue;
                }

                pos++;
            }

            lines[i] = new string(masked);
        }
    }

    private static int SkipRustCharLiteralOrLifetime(string line, int startIndex)
    {
        if (startIndex + 1 >= line.Length)
            return startIndex + 1;

        // Escape-form char literal `'\X'` — scan to the next `'` on the line.
        // エスケープ付き char literal `'\X'` は次の `'` まで読み飛ばす。
        if (line[startIndex + 1] == '\\')
        {
            var p = startIndex + 2;
            while (p < line.Length && line[p] != '\'')
                p++;
            if (p < line.Length)
                p++;
            return p;
        }

        // Simple `'X'`: the character at startIndex+2 must be the closing quote.
        // 単純な `'X'` は startIndex+2 が閉じクォート。
        if (startIndex + 2 < line.Length && line[startIndex + 2] == '\'')
            return startIndex + 3;

        // Lifetime or stray apostrophe: advance one char so the following identifier
        // (or other token) is scanned normally.
        // lifetime やぶら下がり `'` は 1 文字だけ進める。
        return startIndex + 1;
    }

    private static bool HasHashRun(string line, int startIndex, int count)
    {
        if (count == 0)
            return true;
        if (startIndex + count > line.Length)
            return false;
        for (int j = 0; j < count; j++)
        {
            if (line[startIndex + j] != '#')
                return false;
        }
        return true;
    }

    private static bool TryOpenRustRawString(string line, int startIndex, out int openingLength, out int hashCount)
    {
        openingLength = 0;
        hashCount = 0;

        if (startIndex > 0 && IsIdentifierPart(line[startIndex - 1]))
            return false;

        var p = startIndex;
        // Optional byte (b) or C-string (c) prefix: br#"..."#, cr#"..."#
        // 任意の byte (b) / C-string (c) 接頭辞: br#"..."#, cr#"..."#
        if (p < line.Length && (line[p] == 'b' || line[p] == 'c'))
            p++;

        if (p >= line.Length || line[p] != 'r')
            return false;
        p++;

        var hashes = 0;
        while (p < line.Length && line[p] == '#')
        {
            hashes++;
            p++;
        }

        if (p >= line.Length || line[p] != '"')
            return false;

        p++;
        openingLength = p - startIndex;
        hashCount = hashes;
        return true;
    }

    private static int SkipRustSingleLineString(string line, int startIndex)
    {
        var p = startIndex + 1;
        while (p < line.Length && line[p] != '"')
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

    // Token-aware state for JS/TS regex-vs-division disambiguation within a line.
    // Carries the last identifier word so keywords like `return` / `throw` / `typeof`
    // flip the following `/` from division to regex literal.
    // 1 行内の JS/TS regex 判定用 state。直前の識別子語も保持し、`return` / `throw` /
    // `typeof` など regex-prefix keyword の後の `/` を division ではなく regex として扱う。
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
    private static void MaskJsTsTemplateLiteralContents(string[] lines, List<JsTaggedTemplateHit>? taggedTemplateHits = null, string? lang = null)
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

            var masked = line.ToCharArray();
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
                            ReplaceWithSpaces(masked, pos, 2);
                            frames.Pop();
                            pos += 2;
                            continue;
                        }

                        // Blank the body so identifiers inside a template-hole
                        // block comment do not survive into reference extraction.
                        // ホール内 block comment 本文は空白化し、内部の識別子が
                        // 参照抽出まで残らないようにする。
                        masked[pos] = ' ';
                        pos++;
                        continue;
                    }

                    if (active is JsTemplateLiteralFrame tplFrame)
                    {
                        if (line[pos] == '\\')
                        {
                            ReplaceWithSpaces(masked, pos, Math.Min(2, line.Length - pos));
                            pos += Math.Min(2, line.Length - pos);
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '$' && line[pos + 1] == '{')
                        {
                            ReplaceWithSpaces(masked, pos, 2);
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
                            masked[pos] = ' ';
                            pos++;
                            lexState = tplFrame.SavedLexState;
                            lexState.SetKind(JsPrevTokenKind.Literal);
                            frames.Pop();
                            continue;
                        }

                        masked[pos] = ' ';
                        pos++;
                        continue;
                    }

                    if (active is JsTemplateHoleFrame holeFrame)
                    {
                        if (activeJsHoleStringQuote != '\0')
                        {
                            pos = MaskJsTemplateHoleString(line, pos, masked, activeJsHoleStringQuote, startsInsideString: true, out var continuesOnNextLine);
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
                            ReplaceWithSpaces(masked, pos, masked.Length - pos);
                            break;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            // Blank the `/*` opener so the hole's block comment
                            // span is fully whitespace for downstream extraction.
                            // ホールの block comment 開始 `/*` を空白化し、
                            // 下流抽出から見えるスパン全体を空白化する。
                            ReplaceWithSpaces(masked, pos, 2);
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
                            if (taggedTemplateHits != null)
                                TryRecordJsTaggedTemplateHit(lines, masked, i, pos, taggedTemplateHits, allowGenericTag);
                            pos++;
                            frames.Push(new JsTemplateLiteralFrame { SavedLexState = lexState });
                            lexState = default;
                            lexState.Reset();
                            continue;
                        }

                        if (line[pos] == '"' || line[pos] == '\'')
                        {
                            var quote = line[pos];
                            pos = MaskJsTemplateHoleString(line, pos, masked, quote, startsInsideString: false, out var continuesOnNextLine);
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
                                masked[pos] = ' ';
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
                    pos = MaskJsTemplateHoleString(line, pos, masked, activeJsTopLevelStringQuote, startsInsideString: true, out var continuesOnNextLine);
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
                    ReplaceWithSpaces(masked, pos, masked.Length - pos);
                    break;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    // Blank the top-level `/*` opener to match the hole-side
                    // behavior and keep downstream extraction consistent.
                    // 先頭レベルでも `/*` 開始を空白化し、ホール側と挙動を揃える。
                    ReplaceWithSpaces(masked, pos, 2);
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
                    if (taggedTemplateHits != null)
                        TryRecordJsTaggedTemplateHit(lines, masked, i, pos, taggedTemplateHits, allowGenericTag);
                    masked[pos] = ' ';
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
                        ReplaceWithSpaces(masked, start, pos - start);
                        activeJsTopLevelStringQuote = quote;
                        break;
                    }

                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }

                pos = AdvanceJsToken(line, pos, ref lexState);
            }

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
        if (taggedTemplateHits != null && taggedTemplateHits.Count > 0)
            FilterJsForOfHeaderHits(lines, taggedTemplateHits);
    }

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

    // Returns a copy of the masker output where single/double-quoted string spans,
    // regex literals, and `//` line-comment tails are blanked out. Template literal
    // bodies and block comments are already blanked by the outer masker, so we only
    // need to handle the three remaining kinds. The returned buffer keeps identical
    // column offsets so hit coordinates (Line, Column) remain valid.
    // 外側の masker の出力を複製し、文字列リテラル・regex リテラル・`//` 行コメント末尾を
    // 追加で空白化したバッファを返す。template 本体と block コメントは既に空白化済みなの
    // で、残る 3 種類だけを処理する。列オフセットは元の buffer と一致するため Hit 座標は
    // そのまま利用できる。
    private static string[] BuildJsForOfScanBuffer(string[] lines)
    {
        var result = new string[lines.Length];
        var lexState = default(JsLexState);
        var activeJsStringQuote = '\0';
        lexState.Reset();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                result[i] = line;
                continue;
            }
            var buf = line.ToCharArray();
            int pos = 0;
            while (pos < line.Length)
            {
                if (activeJsStringQuote != '\0')
                {
                    pos = MaskJsTemplateHoleString(line, pos, buf, activeJsStringQuote, startsInsideString: true, out var continuesOnNextLine);
                    if (continuesOnNextLine)
                        break;

                    activeJsStringQuote = '\0';
                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                {
                    for (int k = pos; k < line.Length; k++)
                        buf[k] = ' ';
                    pos = line.Length;
                    break;
                }
                char ch = line[pos];
                if (ch == '"' || ch == '\'')
                {
                    var quote = ch;
                    pos = MaskJsTemplateHoleString(line, pos, buf, quote, startsInsideString: false, out var continuesOnNextLine);
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
                    for (int k = pos; k < end; k++)
                        buf[k] = ' ';
                    pos = end;
                    lexState.SetKind(JsPrevTokenKind.Literal);
                    continue;
                }
                pos = AdvanceJsToken(line, pos, ref lexState);
            }
            result[i] = new string(buf);
        }
        return result;
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
    private static void TryRecordJsTaggedTemplateHit(
        string[] lines, char[] masked, int lineIndex, int backtickPos, List<JsTaggedTemplateHit> hits, bool allowGenericTag)
    {
        // Skip inter-token whitespace backward, crossing line boundaries when the tag
        // identifier lives on a prior line (multi-line forms like `tag\n\`hello\``).
        // Prior lines are already fully masked by the outer loop, so we can safely read
        // `lines[i]` for `i < lineIndex`.
        // トークン間空白を後方に辿る。`tag\n\`hello\`` のようにタグが前行にある形も扱うため、
        // 行境界を越えて走査する。先行行は外側ループで既にマスク済みなので `lines[i]` を
        // そのまま参照できる。
        int curLine = lineIndex;
        int k = backtickPos - 1;
        while (true)
        {
            if (curLine == lineIndex)
            {
                while (k >= 0 && IsJsInterTokenWhitespace(masked[k]))
                    k--;
                if (k >= 0) break;
            }
            else
            {
                var l = lines[curLine];
                while (k >= 0 && IsJsInterTokenWhitespace(l[k]))
                    k--;
                if (k >= 0) break;
            }
            curLine--;
            if (curLine < 0) return;
            k = (curLine == lineIndex ? masked.Length : lines[curLine].Length) - 1;
        }

        char CharAt(int li, int col)
            => li == lineIndex ? masked[col] : lines[li][col];
        int LineLen(int li)
            => li == lineIndex ? masked.Length : lines[li].Length;

        // Skip a balanced `<...>` (TypeScript generics) so `html<T>\`...\`` still sees `html`.
        // The generic-strip is TypeScript-only (`allowGenericTag`) because plain JavaScript has
        // no generics: `foo<bar>\`x\`` is always the chained comparison `(foo<bar)>\`x\``. Even
        // inside TypeScript we still require the `<` to directly abut an identifier so
        // whitespace-bearing comparison expressions like `foo < bar > \`plain\`` are rejected,
        // and we ignore `>` from `=>` (arrow-function type inside the generic range). The
        // generic-strip is same-line only; a generic argument list spanning line breaks is
        // extremely rare in practice.
        // `html<T>\`...\`` のジェネリクスを読み飛ばすため、同一行内で `<...>` が釣り合っている
        // 場合のみ括弧を剥がす。ジェネリクスは TypeScript 限定（`allowGenericTag`）。JavaScript
        // では `foo<bar>\`x\`` は常に連鎖比較式なので generic とは扱わない。TypeScript 側でも
        // `foo < bar > \`plain\`` のような比較式と区別するため `<` が識別子に隣接していることを
        // 要求し、`=>` 由来の `>` は関数型なので閉じ記号として数えない。ジェネリクス走査は
        // 同一行限定。行をまたぐジェネリクス引数リストは実運用で極めて稀。
        if (CharAt(curLine, k) == '>' && allowGenericTag)
        {
            int probe = k - 1;
            int depth = 1;
            while (probe >= 0 && depth > 0)
            {
                var ch = CharAt(curLine, probe);
                if (ch == '>' && probe > 0 && CharAt(curLine, probe - 1) == '=')
                {
                    probe -= 2;
                    continue;
                }
                if (ch == '>') depth++;
                else if (ch == '<') depth--;
                probe--;
            }
            if (depth != 0)
                return;
            if (probe < 0 || !IsJsIdentifierPart(CharAt(curLine, probe)))
                return;
            k = probe;
        }

        if (!IsJsIdentifierPart(CharAt(curLine, k)))
            return;

        // Identifier read stays within the current line — JS identifiers do not cross lines.
        // 識別子は行をまたがないため同一行内で読み切る。
        int end = k + 1;
        while (k >= 0 && IsJsIdentifierPart(CharAt(curLine, k)))
            k--;
        int start = k + 1;

        if (!IsJsIdentifierStart(CharAt(curLine, start)))
            return;

        string name = curLine == lineIndex
            ? new string(masked, start, end - start)
            : lines[curLine].Substring(start, end - start);

        // Member-access detection: look for a `.` (possibly after inter-token whitespace,
        // possibly across line breaks like `obj\n.default\`x\``) before the tag identifier.
        // Member-access tags bypass the keyword denylist downstream because any reserved
        // word — including `default`, `finally`, `in`, `instanceof`, `delete`, `void`,
        // `case` — is a legal property name in JavaScript/TypeScript.
        // メンバーアクセス判定: タグ識別子の前に空白（行境界含む）を挟んで `.` があれば
        // メンバーアクセス。JS/TS ではすべての予約語が property 名になりうるので、
        // メンバーアクセス扱いのタグは下流のキーワード除外リスト（`default` / `finally` /
        // `in` / `instanceof` / `delete` / `void` / `case`）の対象外にする。
        bool isMemberAccess = false;
        int mLine = curLine;
        int mk = start - 1;
        while (true)
        {
            if (mk < 0)
            {
                mLine--;
                if (mLine < 0) break;
                mk = LineLen(mLine) - 1;
                continue;
            }
            char pc = CharAt(mLine, mk);
            if (IsJsInterTokenWhitespace(pc))
            {
                mk--;
                continue;
            }
            if (pc == '.') isMemberAccess = true;
            break;
        }

        hits.Add(new JsTaggedTemplateHit(curLine + 1, start + 1, name, isMemberAccess));
    }

    // Decide whether `/` at the current scan position starts a regex literal rather
    // than a division operator. Division follows numeric / string / regex / template literals,
    // `)`, `]`, and non-keyword identifiers. Everything else (operators, `{`, `(`, `[`, `,`,
    // `;`, `=`, `?`, `:`, leading None) puts us in an expression-prefix context where `/`
    // begins a regex. Regex-prefix keywords such as `return`, `throw`, `typeof` re-enable
    // regex mode even though they are identifier-shaped.
    // `/` が division ではなく regex literal の開始かを判定する。数値 / 文字列 / regex /
    // template 等のリテラル、`)`、`]`、および非 keyword な識別子の後は division。
    // それ以外（演算子、`(` / `[` / `=` / `?` / `:` / `,` / `;` や行頭 None）は式の
    // 先頭コンテキストで `/` は regex。`return` / `throw` / `typeof` など regex-prefix
    // keyword は識別子形でも regex を許す。
    private static bool CanStartJsRegexLiteral(JsLexState lexState)
    {
        switch (lexState.PrevTokenKind)
        {
            case JsPrevTokenKind.None:
                return true;
            case JsPrevTokenKind.CloseParen:
            case JsPrevTokenKind.CloseBracket:
            case JsPrevTokenKind.CloseBrace:
            case JsPrevTokenKind.Numeric:
            case JsPrevTokenKind.Literal:
                return false;
            case JsPrevTokenKind.Identifier:
                return IsJsRegexPrefixKeyword(lexState.PrevIdentifier);
            case JsPrevTokenKind.StatementHeadCloseParen:
            case JsPrevTokenKind.Arrow:
            case JsPrevTokenKind.Other:
            default:
                return true;
        }
    }

    // Classify a nested `{` opened inside a template-literal hole as an expression
    // brace (object literal or `() => ({})` body, follows `=`, `(`, `[`, `,`, `:`,
    // `?`, operator, regex-prefix keyword) vs. a statement block (arrow-function
    // body, `if`/`while`/`for`/`function` block body — typically follows `)` or
    // `=>`). Expression braces classify the matching `}` as division-context; block
    // braces keep regex-legal classification so `{} /regex/` still parses.
    // テンプレートホール内でネストした `{` が expression brace（object literal /
    // `() => ({})` 本体）か statement block（arrow body / `if/while/for/function`
    // ブロック）かを判定する。expression は `=`、`(`、`[`、`,`、`:`、`?`、演算子、
    // regex-prefix keyword の直後。block は `)` や `=>` の直後。
    private static bool IsJsExpressionBraceContext(JsLexState lexState)
    {
        switch (lexState.PrevTokenKind)
        {
            case JsPrevTokenKind.CloseParen:
            case JsPrevTokenKind.StatementHeadCloseParen:
            case JsPrevTokenKind.Arrow:
                return false;
            case JsPrevTokenKind.Identifier:
                // Keywords that open a statement block follow the same rule as `)`.
                // `else { ... }`, `do { ... }`, `try { ... }`, `finally { ... }`,
                // and the optional-binding `catch { ... }` (ES2019).
                // block を開く keyword は `)` と同じ扱い。ES2019 の optional
                // binding 付き `catch { ... }` も block として扱う。
                return lexState.PrevIdentifier is not ("else" or "do" or "try" or "finally" or "catch");
            default:
                return true;
        }
    }

    private static bool IsJsRegexPrefixKeyword(string word) =>
        word is "return" or "throw" or "case" or "delete" or "typeof" or "void"
            or "new" or "in" or "of" or "instanceof" or "yield" or "await"
            or "else" or "do" or "finally";

    private static int SkipJsRegexLiteral(string line, int startIndex)
    {
        var p = startIndex + 1;
        var inCharClass = false;

        while (p < line.Length)
        {
            var ch = line[p];
            if (ch == '\\')
            {
                if (p + 1 < line.Length)
                {
                    p += 2;
                    continue;
                }

                return line.Length;
            }

            if (ch == '[')
            {
                inCharClass = true;
                p++;
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                p++;
                continue;
            }

            if (ch == '/' && !inCharClass)
            {
                p++;
                while (p < line.Length && char.IsLetter(line[p]))
                    p++;
                return p;
            }

            p++;
        }

        return line.Length;
    }

    private static int MaskJsTemplateHoleString(string line, int startIndex, char[] masked, char quote, bool startsInsideString, out bool continuesOnNextLine)
    {
        var p = startIndex;
        if (!startsInsideString)
        {
            masked[p] = ' ';
            p++;
        }

        while (p < line.Length)
        {
            var ch = line[p];
            masked[p] = ' ';

            if (ch == '\\')
            {
                if (p + 1 == line.Length)
                {
                    continuesOnNextLine = true;
                    return p + 1;
                }

                if (p + 2 == line.Length && line[p + 1] == '\r')
                {
                    masked[p + 1] = ' ';
                    continuesOnNextLine = true;
                    return line.Length;
                }

                if (p + 1 < line.Length)
                {
                    masked[p + 1] = ' ';
                    p += 2;
                    continue;
                }
            }

            p++;
            if (ch == quote)
            {
                continuesOnNextLine = false;
                return p;
            }
        }

        continuesOnNextLine = false;
        return p;
    }

    // Mask a single-line Swift extended raw string `<N>#"..."<N>#` while preserving
    // any matching `\<N>#(...)` interpolation hole bodies so real call edges inside
    // the holes still reach the reference graph. Returns the position immediately
    // after the closing delimiter (or end of line if the source is malformed).
    // Callers must have already verified that `line[startIndex .. startIndex + hashCount]`
    // is `<N>#"`. Closes #1001.
    // Swift の単行 `<N>#"..."<N>#` 拡張 raw 文字列をマスクしつつ、内側の hash 数一致 `\<N>#(...)`
    // 補間ホール本文だけは残し、ホール内の本物の call が reference graph に届くようにする。
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

            var masked = line.ToCharArray();
            var pos = 0;

            while (pos < line.Length)
            {
                if (blockCommentDepth > 0)
                {
                    if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                    {
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth++;
                        pos += 2;
                        continue;
                    }
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                    {
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth--;
                        pos += 2;
                        continue;
                    }
                    masked[pos] = ' ';
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
                                            ReplaceWithSpaces(masked, pos, 3);
                                            pos += 3;
                                            deepNestedTripleDepth++;
                                            deepNestedTripleHashCounts.Push(0);
                                            continue;
                                        }

                                        ReplaceWithSpaces(masked, pos, 3);
                                        pos += 3;
                                        deepNestedTripleDepth--;
                                        if (deepNestedTripleHashCounts.Count > 0)
                                            deepNestedTripleHashCounts.Pop();
                                        continue;
                                    }
                                    masked[pos] = ' ';
                                    pos++;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                                {
                                    ReplaceWithSpaces(masked, pos, line.Length - pos);
                                    pos = line.Length;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                                {
                                    ReplaceWithSpaces(masked, pos, 2);
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
                                    ReplaceWithSpaces(masked, pos, 3);
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
                                        masked[pos] = ' ';
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
                                ReplaceWithSpaces(masked, pos, 3);
                                pos += 3;
                                nestedTripleOpen = false;
                                nestedHoleBraceDepth = -1;
                                deepNestedTripleDepth = 0;
                                deepNestedTripleHashCounts.Clear();
                                continue;
                            }
                            if (pos + 1 < line.Length && line[pos] == '$' && line[pos + 1] == '{')
                            {
                                ReplaceWithSpaces(masked, pos, 2);
                                nestedHoleBraceDepth = 0;
                                pos += 2;
                                continue;
                            }
                            masked[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                        {
                            ReplaceWithSpaces(masked, pos, line.Length - pos);
                            pos = line.Length;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            ReplaceWithSpaces(masked, pos, 2);
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
                            ReplaceWithSpaces(masked, pos, 3);
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
                                masked[pos] = ' ';
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
                        ReplaceWithSpaces(masked, pos, 3);
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
                        ReplaceWithSpaces(masked, pos, 2);
                        holeBraceDepth = 0;
                        pos += 2;
                        continue;
                    }

                    masked[pos] = ' ';
                    pos++;
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                    break;

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    ReplaceWithSpaces(masked, pos, 2);
                    blockCommentDepth = 1;
                    pos += 2;
                    continue;
                }

                if (pos + 2 < line.Length
                    && line[pos] == '"' && line[pos + 1] == '"' && line[pos + 2] == '"')
                {
                    ReplaceWithSpaces(masked, pos, 3);
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

            var masked = line.ToCharArray();
            var pos = 0;

            while (pos < line.Length)
            {
                if (blockCommentDepth > 0)
                {
                    if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                    {
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth++;
                        pos += 2;
                        continue;
                    }
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                    {
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth--;
                        pos += 2;
                        continue;
                    }
                    masked[pos] = ' ';
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
                                            ReplaceWithSpaces(masked, pos, 3 + closeHashCount);
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
                                            ReplaceWithSpaces(masked, pos, 3);
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
                                        ReplaceWithSpaces(masked, pos, 3);
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
                                            ReplaceWithSpaces(masked, pos, deepBodyHashes + 3);
                                            pos += deepBodyHashes + 3;
                                            deepNestedTripleDepth++;
                                            deepNestedTripleHashCounts.Push(deepBodyHashes);
                                            continue;
                                        }

                                    }

                                    masked[pos] = ' ';
                                    pos++;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                                {
                                    ReplaceWithSpaces(masked, pos, line.Length - pos);
                                    pos = line.Length;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                                {
                                    ReplaceWithSpaces(masked, pos, 2);
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
                                    ReplaceWithSpaces(masked, pos, deepHashes + 3);
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
                                    pos = MaskSwiftSingleLineRawString(line, pos, deepHashes, masked);
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
                                        masked[pos] = ' ';
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
                                ReplaceWithSpaces(masked, pos, 3 + nestedTripleHashCount);
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
                                ReplaceWithSpaces(masked, pos, 2 + nestedTripleHashCount);
                                pos += 2 + nestedTripleHashCount;
                                nestedHoleParenDepth = 0;
                                continue;
                            }
                            // Plain (non-raw) nested triple: `\\` is a literal backslash.
                            // 通常 nested triple 内: `\\` は literal backslash。
                            if (nestedTripleHashCount == 0 && line[pos] == '\\' && pos + 1 < line.Length)
                            {
                                ReplaceWithSpaces(masked, pos, 2);
                                pos += 2;
                                continue;
                            }
                            masked[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                        {
                            ReplaceWithSpaces(masked, pos, line.Length - pos);
                            pos = line.Length;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            ReplaceWithSpaces(masked, pos, 2);
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
                            ReplaceWithSpaces(masked, pos, holeNestedHashes + 3);
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
                            pos = MaskSwiftSingleLineRawString(line, pos, holeNestedHashes, masked);
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
                                masked[pos] = ' ';
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
                        ReplaceWithSpaces(masked, pos, 3 + tripleHashCount);
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
                            ReplaceWithSpaces(masked, pos, 2 + tripleHashCount);
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
                            ReplaceWithSpaces(masked, pos, 2);
                            pos += 2;
                            continue;
                        }

                        // Extended form `#"""..."""#` (or more hashes): without a
                        // matching `\#` run the backslash is literal; advance one char.
                        // 拡張形 `#"""..."""#` など: hash 数が一致しない `\` は literal。
                        masked[pos] = ' ';
                        pos++;
                        continue;
                    }

                    masked[pos] = ' ';
                    pos++;
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                    break;

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    ReplaceWithSpaces(masked, pos, 2);
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
                    ReplaceWithSpaces(masked, pos, leadingHashes + 3);
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
                    pos = MaskSwiftSingleLineRawString(line, pos, leadingHashes, masked);
                    continue;
                }

                if (line[pos] == '"')
                {
                    pos = SkipJsSingleLineString(line, pos);
                    continue;
                }

                pos++;
            }

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

            var masked = line.ToCharArray();
            var pos = 0;

            while (pos < line.Length)
            {
                if (blockCommentDepth > 0)
                {
                    if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                    {
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth++;
                        pos += 2;
                        continue;
                    }
                    if (pos + 1 < line.Length && line[pos] == '*' && line[pos + 1] == '/')
                    {
                        ReplaceWithSpaces(masked, pos, 2);
                        blockCommentDepth--;
                        pos += 2;
                        continue;
                    }
                    masked[pos] = ' ';
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
                                            ReplaceWithSpaces(masked, pos, deepHashes + 3);
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
                                            ReplaceWithSpaces(masked, pos, 3 + deepHashes);
                                            pos += 3 + deepHashes;
                                            deepNestedTripleDepth--;
                                            if (deepNestedTripleHashCounts.Count > 0)
                                                deepNestedTripleHashCounts.Pop();
                                            continue;
                                        }
                                    }

                                    masked[pos] = ' ';
                                    pos++;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                                {
                                    ReplaceWithSpaces(masked, pos, line.Length - pos);
                                    pos = line.Length;
                                    continue;
                                }
                                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                                {
                                    ReplaceWithSpaces(masked, pos, 2);
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
                                    ReplaceWithSpaces(masked, pos, 3);
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
                                        masked[pos] = ' ';
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
                                ReplaceWithSpaces(masked, pos, 3);
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
                                ReplaceWithSpaces(masked, pos, 2);
                                nestedHoleBraceDepth = 0;
                                pos += 2;
                                continue;
                            }
                            masked[pos] = ' ';
                            pos++;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                        {
                            ReplaceWithSpaces(masked, pos, line.Length - pos);
                            pos = line.Length;
                            continue;
                        }

                        if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                        {
                            ReplaceWithSpaces(masked, pos, 2);
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
                            ReplaceWithSpaces(masked, pos, 3);
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
                                masked[pos] = ' ';
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
                        ReplaceWithSpaces(masked, pos, 3);
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
                        ReplaceWithSpaces(masked, pos, 2);
                        holeBraceDepth = 0;
                        pos += 2;
                        continue;
                    }

                    masked[pos] = ' ';
                    pos++;
                    continue;
                }

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '/')
                    break;

                if (pos + 1 < line.Length && line[pos] == '/' && line[pos + 1] == '*')
                {
                    ReplaceWithSpaces(masked, pos, 2);
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
                    ReplaceWithSpaces(masked, pos, 3);
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

            lines[i] = new string(masked);
        }
    }

    private static bool IsIdentifierPart(char c) =>
        c == '_' || char.IsLetterOrDigit(c);
}
