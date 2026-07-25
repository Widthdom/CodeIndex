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
internal static partial class StructuralLineMasker
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

    private static readonly BlockCommentFrame SharedBlockCommentFrame = new();
    private static readonly CharLiteralFrame SharedCharLiteralFrame = new();

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
        => MaskLines(lang, originalLines, collectJsTaggedTemplateHits: false, out _);

    internal static string[] MaskLines(string? lang, string[] originalLines, out List<JsTaggedTemplateHit>? jsTaggedTemplateHits)
        => MaskLines(lang, originalLines, collectJsTaggedTemplateHits: true, out jsTaggedTemplateHits);

    private static string[] MaskLines(
        string? lang,
        string[] originalLines,
        bool collectJsTaggedTemplateHits,
        out List<JsTaggedTemplateHit>? jsTaggedTemplateHits)
    {
        jsTaggedTemplateHits = null;
        if (!RequiresStructuralMasking(lang))
            return originalLines;
        if (!MayContainStructuralMaskingDelimiter(lang, originalLines))
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
                MaskJsTsTemplateLiteralContents(maskedLines, collectJsTaggedTemplateHits, ref jsTaggedTemplateHits, lang);
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

    private static bool MayContainStructuralMaskingDelimiter(string? lang, string[] lines)
    {
        return lang switch
        {
            "csharp" => MayContainCSharpStructuralDelimiter(lines),
            "python" => MayContainPythonTripleQuoteDelimiter(lines),
            "rust" => MayContainRustStructuralDelimiter(lines),
            "javascript" or "typescript" => MayContainJsTsStructuralDelimiter(lines),
            "kotlin" or "scala" => MayContainTripleDoubleQuoteOrBlockComment(lines),
            "swift" => MayContainSwiftStructuralDelimiter(lines),
            "perl" => MayContainPerlPodDelimiter(lines),
            _ => false,
        };
    }

    private static bool MayContainCSharpStructuralDelimiter(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var span = line.AsSpan();
            if (span.IndexOf('"') >= 0 || ContainsBlockCommentStart(span))
                return true;
        }

        return false;
    }

    private static bool MayContainPythonTripleQuoteDelimiter(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var span = line.AsSpan();
            if (ContainsQuoteRun(span, '\'', 3) || ContainsQuoteRun(span, '"', 3))
                return true;
        }

        return false;
    }

    private static bool MayContainRustStructuralDelimiter(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            for (var i = 0; i < line.Length; i++)
            {
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                    return true;
                if (line[i] is 'b' or 'c' or 'r' && TryOpenRustRawString(line, i, out _, out _))
                    return true;
            }
        }

        return false;
    }

    private static bool MayContainJsTsStructuralDelimiter(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var span = line.AsSpan();
            if (span.IndexOfAny('"', '\'', '/') >= 0 || span.IndexOf('`') >= 0)
                return true;
        }

        return false;
    }

    private static bool MayContainTripleDoubleQuoteOrBlockComment(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var span = line.AsSpan();
            if (ContainsQuoteRun(span, '"', 3) || ContainsBlockCommentStart(span))
                return true;
        }

        return false;
    }

    private static bool MayContainSwiftStructuralDelimiter(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var span = line.AsSpan();
            if (ContainsBlockCommentStart(span)
                || ContainsQuoteRun(span, '"', 3)
                || ContainsHashPrefixedQuote(span))
                return true;
        }

        return false;
    }

    private static bool MayContainPerlPodDelimiter(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var trimmed = line.AsSpan().TrimStart();
            if (!trimmed.IsEmpty && trimmed[0] == '=')
                return true;
        }

        return false;
    }

    private static bool ContainsQuoteRun(ReadOnlySpan<char> span, char quote, int runLength)
    {
        for (var i = 0; i <= span.Length - runLength; i++)
        {
            if (span[i] != quote)
                continue;

            var matched = true;
            for (var j = 1; j < runLength; j++)
            {
                if (span[i + j] != quote)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }

    private static bool ContainsBlockCommentStart(ReadOnlySpan<char> span)
    {
        for (var i = 0; i + 1 < span.Length; i++)
        {
            if (span[i] == '/' && span[i + 1] == '*')
                return true;
        }

        return false;
    }

    private static bool ContainsHashPrefixedQuote(ReadOnlySpan<char> span)
    {
        for (var i = 0; i + 1 < span.Length; i++)
        {
            if (span[i] != '#')
                continue;

            var hashEnd = i + 1;
            while (hashEnd < span.Length && span[hashEnd] == '#')
                hashEnd++;

            if (hashEnd < span.Length && span[hashEnd] == '"')
                return true;
        }

        return false;
    }

    private static void MaskPerlPodSections(string[] lines)
    {
        var inPod = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var firstNonWhitespace = GetFirstNonWhitespaceIndex(line);
            if (firstNonWhitespace == line.Length)
            {
                if (inPod)
                    lines[i] = new string(' ', line.Length);
                continue;
            }

            var trimmed = line.AsSpan(firstNonWhitespace);
            if (IsPerlPodDirectiveLine(trimmed))
            {
                lines[i] = new string(' ', line.Length);
                inPod = !trimmed.StartsWith("=cut", StringComparison.Ordinal);
                continue;
            }

            if (inPod)
                lines[i] = new string(' ', line.Length);
        }
    }

    private static int GetFirstNonWhitespaceIndex(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]))
                return i;
        }

        return line.Length;
    }

    private static bool IsPerlPodDirectiveLine(ReadOnlySpan<char> trimmedLine)
    {
        return trimmedLine.StartsWith("=", StringComparison.Ordinal)
            && trimmedLine.Length > 1
            && (trimmedLine[1] == 'c' && trimmedLine.Length >= 4 && trimmedLine.StartsWith("=cut", StringComparison.Ordinal)
                || char.IsLetter(trimmedLine[1]));
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

            char[]? masked = null;
            char[] GetMaskedLine() => masked ??= line.ToCharArray();
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
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
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
                        ReplaceWithSpaces(GetMaskedLine(), pos, 2);
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
                    GetMaskedLine()[pos] = ' ';
                    pos++;
                    continue;
                }

                if (hashCount >= 0)
                {
                    if (line[pos] == '"' && HasHashRun(line, pos + 1, hashCount))
                    {
                        ReplaceWithSpaces(GetMaskedLine(), pos, 1 + hashCount);
                        pos += 1 + hashCount;
                        hashCount = -1;
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
                    // Must enter block-comment state before `TryOpenRustRawString`, otherwise
                    // `/* r#" */` would be mis-parsed as a real raw string opener and swallow
                    // subsequent source until the next `"#`. Blank the opener so nested-comment
                    // body blanking stays contiguous.
                    // `TryOpenRustRawString` の前に block comment 状態へ入る。そうしないと
                    // `/* r#" */` が本物の raw string 開始と誤認され、次の `"#` まで
                    // 以降のソースを丸ごとマスクしてしまう。ネストコメント本文の空白化と
                    // 連続させるため `/*` 自体も空白化する。
                    ReplaceWithSpaces(GetMaskedLine(), pos, 2);
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
                    ReplaceWithSpaces(GetMaskedLine(), pos, openingLength);
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

            if (masked is not null)
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
}
