using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private sealed record ReferenceLinePreparation(
        string Content,
        string[] Lines,
        string[] StructuralLines,
        bool[]? CSharpLinesInsideMultilineStringContent,
        bool[]? CSharpLinesInsideBlockComment,
        string[] ReferenceStructuralLines,
        string[] PreparedLines,
        bool[]? GoImportBlockLines,
        string[]? LuaReferenceLines,
        string[]? LuaPreparedLines,
        string[]? LispReferenceLines,
        string[]? RazorReferenceLines,
        IReadOnlyList<string>? RazorImplementedTypeNames,
        IReadOnlyList<TypeScriptReferenceExtractor.NamespaceAliasBinding> TypeScriptNamespaceAliases,
        IReadOnlyDictionary<int, IReadOnlyList<JsTaggedTemplateHit>>? JsTaggedTemplatesByLine);

    private static bool TryPrepareReferenceLines(
        string language,
        string content,
        bool isRazorFile,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        out ReferenceLinePreparation preparedInput)
    {
        preparedInput = null!;

        // Null / empty fast path — keep the direct-call null-safe contract that
        // FileIndexer.StripLineLeadingInvisibles' IsNullOrEmpty check used to provide
        // before the CRLF normalization step was added in front of it. Closes #183.
        // null / 空入力は早期 return。CRLF 正規化を StripLineLeadingInvisibles の前に
        // 入れたことで helper 側の IsNullOrEmpty による null 許容が効かなくなる
        // ため、direct call の null セーフ契約をここで復元する。Closes #183.
        if (string.IsNullOrEmpty(content))
            return false;

        // Oversize-line skip: bail out for files that pack a multi-MB payload
        // into a single physical line (minified bundles, base64 blobs). The
        // matching guard in ChunkSplitter / SymbolExtractor / ValidateContent
        // keeps the indexer from stalling on regex backtracking and surfaces
        // the skip as a `line_too_long` FileIssue. Closes #1542.
        // 1 行に複数 MB のペイロードを詰めたファイル (minified bundle や base64
        // ペイロード等) は早期に抜ける。ChunkSplitter / SymbolExtractor /
        // ValidateContent の同等ガードと合わせて、正規表現のバックトラックで
        // インデクサが止まることを防ぎ、スキップは `line_too_long` FileIssue
        // として表面化させる。Closes #1542.
        if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
            return false;

        if ((conflictMarkerLine ?? FileIndexer.GetConflictMarkerLine(content)) > 0)
            return false;

        // Normalize CRLF / CR to LF first so direct callers that bypass FileIndexer
        // still present a `\n`-only content stream, and then strip line-leading
        // UTF-8 BOM (U+FEFF) and zero-width space (U+200B) defensively so
        // `^\s*`-anchored patterns match on line 1 and on any mid-file line that
        // begins with such a marker (e.g. from file concatenation or tool insertion).
        // StripLineLeadingInvisibles assumes `\n` is the sole line separator, so the
        // CRLF pass must come first. Non-line-leading markers are preserved. Closes #183/#2117.
        // まず CRLF / CR を LF に正規化する。StripLineLeadingInvisibles は `\n` を唯一の
        // 行区切りとして行頭判定するので、FileIndexer を経由しない direct call
        // でも CRLF 正規化を済ませてから呼ばないと mid-file の行頭 marker を剥がし
        // 損なう。続いて行頭 U+FEFF/U+200B のみ剥がし、1 行目と mid-file の行頭
        // marker 両方で `^\s*` 固定パターンを成立させる。行頭以外の marker は
        // そのまま保持する。Closes #183/#2117.
        if (!contentIsNormalized)
        {
            content = FileIndexer.NormalizeContentForPrepass(content);
        }

        var maskedContent = string.Equals(language, "java", StringComparison.OrdinalIgnoreCase)
            ? MaskJavaTextBlocks(content)
            : content;
        var lines = SplitContentLines(maskedContent);
        var structuralLines = StructuralLineMasker.MaskLines(language, lines, out var jsTaggedTemplateHits);
        var csharpLineState = language == "csharp" && MightContainCSharpXmlDocComment(content)
            ? BuildCSharpLineStateMasks(lines)
            : (MultilineStringContent: null, BlockComment: null);
        var csharpLinesInsideMultilineStringContent = csharpLineState.MultilineStringContent;
        var csharpLinesInsideBlockComment = csharpLineState.BlockComment;
        var referenceStructuralLines = language == "cpp"
            ? SplitContentLines(MaskCppLexicalRanges(
                maskedContent,
                [new CppLexicalRange(0, maskedContent.Length)],
                maskPreprocessorPayloads: false,
                collapseLineSplices: false)[0])
            : language == "pascal"
            ? MaskPascalBlockCommentLines(structuralLines)
            : language == "haskell"
                ? MaskHaskellBlockCommentLines(structuralLines)
                : UsesCStyleBlockComments(language)
                    ? MaskCStyleBlockCommentLines(language, structuralLines)
                    : structuralLines;
        referenceStructuralLines = DynamicDeclarativeReferenceExtractor.MaskNonCodeLines(
            language,
            referenceStructuralLines);
        if (language == "python")
            referenceStructuralLines = MaskPythonFStrings(referenceStructuralLines);

        var linePrepareOptions = CreateReferenceLinePrepareOptions(language);
        var preparedLines = PrepareReferenceLines(language, referenceStructuralLines, linePrepareOptions);
        var goImportBlockLines = language == "go" && content.Contains("import", StringComparison.Ordinal)
            ? GoReferenceExtractor.BuildImportBlockLineMap(lines)
            : null;
        var luaReferenceLines = language == "lua"
            ? LuaReferenceExtractor.MaskLongCommentAndStringLines(lines)
            : null;
        var lispReferenceLines = language is "commonlisp" or "racket"
            ? SymbolExtractor.MaskLispCodeLines(lines)
            : null;
        string[]? luaPreparedLines = null;
        if (luaReferenceLines != null)
        {
            luaPreparedLines = new string[luaReferenceLines.Length];
            for (var pi = 0; pi < luaReferenceLines.Length; pi++)
                luaPreparedLines[pi] = PrepareLine(luaReferenceLines[pi], linePrepareOptions);
        }
        var razorReferenceLines = isRazorFile
            ? RazorReferenceExtractor.MaskCommentLines(lines)
            : null;
        var razorImplementedTypeNames = isRazorFile
            ? LanguageReferenceExtractionSupport.ExtractRazorImplementedTypeNames(lines)
            : null;
        var typeScriptNamespaceAliases = language == "typescript"
            ? TypeScriptReferenceExtractor.BuildNamespaceAliasBindings(lines, preparedLines)
            : [];

        preparedInput = new ReferenceLinePreparation(
            content,
            lines,
            structuralLines,
            csharpLinesInsideMultilineStringContent,
            csharpLinesInsideBlockComment,
            referenceStructuralLines,
            preparedLines,
            goImportBlockLines,
            luaReferenceLines,
            luaPreparedLines,
            lispReferenceLines,
            razorReferenceLines,
            razorImplementedTypeNames,
            typeScriptNamespaceAliases,
            GroupJsTaggedTemplatesByLine(jsTaggedTemplateHits));
        return true;
    }

    private static bool MightContainCSharpXmlDocComment(string content)
    {
        for (var index = 0; index + 2 < content.Length; index++)
        {
            if (content[index] != '/')
                continue;

            var second = content[index + 1];
            if ((second == '/' || second == '*') && content[index + 2] == second)
                return true;
        }

        return false;
    }

    private static string[] PrepareReferenceLines(
        string language,
        string[] referenceStructuralLines,
        ReferenceLinePrepareOptions linePrepareOptions)
    {
        string[]? preparedLines = null;
        for (var index = 0; index < referenceStructuralLines.Length; index++)
        {
            var structuralLine = referenceStructuralLines[index];
            var preparedLine = PrepareLine(structuralLine, linePrepareOptions);
            if (preparedLines == null)
            {
                if (string.Equals(preparedLine, structuralLine, StringComparison.Ordinal))
                    continue;

                preparedLines = new string[referenceStructuralLines.Length];
                Array.Copy(referenceStructuralLines, preparedLines, index);
            }

            preparedLines[index] = preparedLine;
        }

        return preparedLines ?? referenceStructuralLines;
    }

    internal readonly record struct CppLexicalRange(int Start, int End);

    internal static string[] MaskCppLexicalRanges(
        string content,
        IReadOnlyList<CppLexicalRange> ranges,
        bool maskPreprocessorPayloads,
        bool collapseLineSplices)
    {
        if (ranges.Count == 0)
            return [];

        var builders = new StringBuilder[ranges.Count];
        var previousEnd = 0;
        for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
        {
            var range = ranges[rangeIndex];
            if (range.Start < previousEnd || range.Start < 0 || range.End < range.Start || range.End > content.Length)
                throw new ArgumentOutOfRangeException(nameof(ranges), "C++ lexical ranges must be ordered, non-overlapping, and within content.");

            builders[rangeIndex] = new StringBuilder(range.End - range.Start);
            previousEnd = range.End;
        }

        var activeRangeIndex = 0;
        var inBlockComment = false;
        var inLineComment = false;
        var inMaskedDirective = false;
        var quote = '\0';
        string? rawStringTerminator = null;
        var onlyWhitespaceOnLine = true;

        void WriteAt(int contentIndex, bool masked)
        {
            while (activeRangeIndex < ranges.Count && contentIndex >= ranges[activeRangeIndex].End)
                activeRangeIndex++;
            if (activeRangeIndex >= ranges.Count || contentIndex < ranges[activeRangeIndex].Start)
                return;

            var ch = content[contentIndex];
            builders[activeRangeIndex].Append(masked && ch is not ('\r' or '\n') ? ' ' : ch);
        }

        void WriteRange(int start, int length, bool masked)
        {
            var end = start + length;
            for (var cursor = start; cursor < end; cursor++)
            {
                if (collapseLineSplices && TryGetCppLineSpliceLength(content, cursor, out var spliceLength))
                {
                    cursor += spliceLength - 1;
                    continue;
                }

                WriteAt(cursor, masked);
            }
        }

        var index = 0;
        while (index < content.Length && activeRangeIndex < ranges.Count)
        {
            var ch = content[index];
            var isLineBreak = IsCppPhysicalLineBreak(content, index);
            var isSplicedLineBreak = isLineBreak && IsCppSplicedLineBreak(content, index);

            if (TryGetCppLineSpliceLength(content, index, out var spliceLength))
            {
                if (!collapseLineSplices)
                    WriteRange(index, spliceLength, masked: true);
                index += spliceLength;
                continue;
            }

            if (rawStringTerminator != null)
            {
                if (TryMatchCppLogicalSequence(content, index, rawStringTerminator, out var rawTerminatorEnd))
                {
                    WriteRange(index, rawTerminatorEnd - index, masked: true);
                    index = rawTerminatorEnd;
                    rawStringTerminator = null;
                    continue;
                }

                WriteAt(index, masked: true);
                index++;
                continue;
            }

            if (inBlockComment)
            {
                if (TryMatchCppLogicalSequence(content, index, "*/", out var blockCommentEnd))
                {
                    WriteRange(index, blockCommentEnd - index, masked: true);
                    index = blockCommentEnd;
                    inBlockComment = false;
                    continue;
                }

                WriteAt(index, masked: true);
                if (isLineBreak)
                    onlyWhitespaceOnLine = true;
                index++;
                continue;
            }

            if (inLineComment)
            {
                WriteAt(index, masked: true);
                if (isLineBreak && !isSplicedLineBreak)
                {
                    inLineComment = false;
                    onlyWhitespaceOnLine = true;
                }
                index++;
                continue;
            }

            if (quote != '\0')
            {
                if (ch == '\\' && index + 1 < content.Length)
                {
                    WriteRange(index, 2, masked: true);
                    index += 2;
                    continue;
                }

                WriteAt(index, masked: true);
                if (ch == quote)
                    quote = '\0';
                else if (isLineBreak && !isSplicedLineBreak)
                {
                    quote = '\0';
                    onlyWhitespaceOnLine = true;
                }
                index++;
                continue;
            }

            if (inMaskedDirective)
            {
                WriteAt(index, masked: true);
                if (isLineBreak && !isSplicedLineBreak)
                {
                    inMaskedDirective = false;
                    onlyWhitespaceOnLine = true;
                }
                index++;
                continue;
            }

            if (isLineBreak)
            {
                WriteAt(index, masked: false);
                if (!isSplicedLineBreak)
                    onlyWhitespaceOnLine = true;
                index++;
                continue;
            }

            if (onlyWhitespaceOnLine && ch is ' ' or '\t' or '\f' or '\v')
            {
                WriteAt(index, masked: false);
                index++;
                continue;
            }

            if (onlyWhitespaceOnLine && ch == '#' && maskPreprocessorPayloads)
            {
                inMaskedDirective = true;
                WriteAt(index, masked: true);
                index++;
                continue;
            }

            if (ch == '/')
            {
                if (TryMatchCppLogicalSequence(content, index, "//", out var lineCommentEnd))
                {
                    WriteRange(index, lineCommentEnd - index, masked: true);
                    index = lineCommentEnd;
                    inLineComment = true;
                    continue;
                }
                if (TryMatchCppLogicalSequence(content, index, "/*", out var blockCommentStartEnd))
                {
                    WriteRange(index, blockCommentStartEnd - index, masked: true);
                    index = blockCommentStartEnd;
                    inBlockComment = true;
                    continue;
                }
            }

            if (TryGetCppRawStringStart(content, index, out var rawTerminator, out var rawOpeningLength))
            {
                WriteRange(index, rawOpeningLength, masked: true);
                index += rawOpeningLength;
                rawStringTerminator = rawTerminator;
                onlyWhitespaceOnLine = false;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                onlyWhitespaceOnLine = false;
                WriteAt(index, masked: true);
                index++;
                continue;
            }

            WriteAt(index, masked: false);
            onlyWhitespaceOnLine = false;
            index++;
        }

        var result = new string[builders.Length];
        for (var rangeIndex = 0; rangeIndex < builders.Length; rangeIndex++)
            result[rangeIndex] = builders[rangeIndex].ToString();
        return result;
    }

    private static bool IsCppPhysicalLineBreak(string content, int index)
        => content[index] == '\n' || (content[index] == '\r' && (index + 1 >= content.Length || content[index + 1] != '\n'));

    private static bool IsCppSplicedLineBreak(string content, int index)
    {
        var precedingIndex = index - 1;
        if (content[index] == '\n' && precedingIndex >= 0 && content[precedingIndex] == '\r')
            precedingIndex--;
        return precedingIndex >= 0 && content[precedingIndex] == '\\';
    }

    private static bool TryGetCppLineSpliceLength(string content, int index, out int length)
    {
        length = 0;
        if (content[index] != '\\' || index + 1 >= content.Length)
            return false;

        if (content[index + 1] == '\n' || content[index + 1] == '\r')
        {
            length = content[index + 1] == '\r'
                && index + 2 < content.Length
                && content[index + 2] == '\n'
                    ? 3
                    : 2;
            return true;
        }

        return false;
    }

    private static bool TryMatchCppLogicalSequence(
        string content,
        int index,
        ReadOnlySpan<char> sequence,
        out int physicalEnd)
    {
        var cursor = index;
        foreach (var expected in sequence)
        {
            while (cursor < content.Length && TryGetCppLineSpliceLength(content, cursor, out var spliceLength))
                cursor += spliceLength;

            if (cursor >= content.Length || content[cursor] != expected)
            {
                physicalEnd = index;
                return false;
            }

            cursor++;
        }

        physicalEnd = cursor;
        return true;
    }

    private static bool TryGetCppRawStringStart(
        string content,
        int index,
        out string terminator,
        out int openingLength)
    {
        terminator = string.Empty;
        openingLength = 0;
        if (!TryMatchCppLogicalSequence(content, index, "R\"", out var cursor))
            return false;

        const int maxDelimiterLength = 16;
        var delimiter = new StringBuilder(maxDelimiterLength);
        while (delimiter.Length <= maxDelimiterLength)
        {
            while (cursor < content.Length && TryGetCppLineSpliceLength(content, cursor, out var spliceLength))
                cursor += spliceLength;
            if (cursor >= content.Length)
                return false;

            var ch = content[cursor];
            if (ch == '(')
            {
                terminator = ")" + delimiter + "\"";
                openingLength = cursor - index + 1;
                return true;
            }

            if (char.IsWhiteSpace(ch) || ch is '\\' or ')')
                return false;

            delimiter.Append(ch);
            cursor++;
        }

        return false;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<JsTaggedTemplateHit>>? GroupJsTaggedTemplatesByLine(
        IReadOnlyList<JsTaggedTemplateHit>? jsTaggedTemplateHits)
    {
        if (jsTaggedTemplateHits == null || jsTaggedTemplateHits.Count == 0)
            return null;

        // Group JS/TS tagged template call sites by line for O(1) lookup in the per-line loop.
        // Tagged templates like `gql\`...\`` / `styled.div\`...\`` / `sql\`...${x}...\`` have no
        // trailing `(`, so CallRegex cannot see them. The structural masker already identifies
        // template openers while walking JS/TS token state, and emits one hit per opener with
        // the preceding tag identifier.
        // JS/TS のタグ付きテンプレート呼び出し位置を行番号でグループ化し、ループ中の参照追加で即座に拾えるようにする。
        // `gql\`...\`` / `styled.div\`...\`` / `sql\`...${x}...\`` は末尾 `(` がなく CallRegex で取れないが、
        // 構造マスカーがテンプレート opener 検出時に先行する tag 識別子を併せて記録する。
        var hitsByLine = new Dictionary<int, IReadOnlyList<JsTaggedTemplateHit>>(jsTaggedTemplateHits.Count);
        foreach (var hit in jsTaggedTemplateHits)
        {
            if (!hitsByLine.TryGetValue(hit.Line, out var bucket))
            {
                hitsByLine[hit.Line] = new[] { hit };
                continue;
            }

            if (bucket is List<JsTaggedTemplateHit> mutableBucket)
            {
                mutableBucket.Add(hit);
                continue;
            }

            hitsByLine[hit.Line] = new List<JsTaggedTemplateHit>(2) { bucket[0], hit };
        }

        return hitsByLine;
    }
}
