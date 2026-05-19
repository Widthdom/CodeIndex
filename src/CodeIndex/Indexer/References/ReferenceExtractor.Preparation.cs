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
        IReadOnlyDictionary<int, List<JsTaggedTemplateHit>>? JsTaggedTemplatesByLine);

    private static bool TryPrepareReferenceLines(
        string language,
        string content,
        bool isRazorFile,
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
        if (ChunkSplitter.HasOversizeLine(content))
            return false;

        if (FileIndexer.HasConflictMarkers(content))
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
        if (content.Contains('\r'))
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
        content = FileIndexer.StripLineLeadingInvisibles(content);

        var maskedContent = string.Equals(language, "java", StringComparison.OrdinalIgnoreCase)
            ? MaskJavaTextBlocks(content)
            : content;
        var lines = maskedContent.Split('\n');
        var structuralLines = StructuralLineMasker.MaskLines(language, lines, out var jsTaggedTemplateHits);
        var csharpLinesInsideMultilineStringContent = language == "csharp"
            ? BuildCSharpMultilineStringContentLines(lines)
            : null;
        var csharpLinesInsideBlockComment = language == "csharp"
            ? BuildCSharpBlockCommentLines(lines)
            : null;
        var referenceStructuralLines = language == "pascal"
            ? MaskPascalBlockCommentLines(structuralLines)
            : language == "haskell"
                ? MaskHaskellBlockCommentLines(structuralLines)
                : UsesCStyleBlockComments(language)
                    ? MaskCStyleBlockCommentLines(language, structuralLines)
                    : structuralLines;
        var preparedLines = new string[lines.Length];
        for (var pi = 0; pi < lines.Length; pi++)
            preparedLines[pi] = PrepareLine(language, referenceStructuralLines[pi]);
        var goImportBlockLines = language == "go"
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
                luaPreparedLines[pi] = PrepareLine(language, luaReferenceLines[pi]);
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

    private static IReadOnlyDictionary<int, List<JsTaggedTemplateHit>>? GroupJsTaggedTemplatesByLine(
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
        var hitsByLine = new Dictionary<int, List<JsTaggedTemplateHit>>();
        foreach (var hit in jsTaggedTemplateHits)
        {
            if (!hitsByLine.TryGetValue(hit.Line, out var bucket))
            {
                bucket = new List<JsTaggedTemplateHit>();
                hitsByLine[hit.Line] = bucket;
            }
            bucket.Add(hit);
        }

        return hitsByLine;
    }
}
