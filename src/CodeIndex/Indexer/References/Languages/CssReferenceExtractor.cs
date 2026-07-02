using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class CssReferenceExtractor
{
    private readonly record struct ReferencePattern(Regex Regex, string Kind, bool SkipVariableDeclarations = false);

    private static readonly Regex ScssVariableReferenceRegex = new(
        @"(?<![\w$])\$(?<name>[A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex ScssExtendReferenceRegex = new(
        @"@extend\s+(?<name>[%.][A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex ScssIncludeReferenceRegex = new(
        @"@include\s+(?<name>[A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex SassIndentedMixinReferenceRegex = new(
        @"(?<![\w-])\+(?<name>[A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex SassBareFunctionReferenceRegex = new(
        @"(?<![:@+\w.-])(?<name>[A-Za-z_][\w-]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex StylusVariableReferenceRegex = new(
        @"(?<![\w$])\$(?<name>[A-Za-z_][\w-]*)",
        RegexOptions.Compiled);

    private static readonly Regex StylusBareVariableReferenceRegex = new(
        @"(?<![$\w.-])(?<name>[A-Za-z_][\w-]*)(?![\w-])",
        RegexOptions.Compiled);

    private static readonly Regex StylusBareFunctionReferenceRegex = new(
        @"(?<![:@\w.-])(?<name>[A-Za-z_][\w-]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex StylusVariableDefinitionRegex = new(
        @"^\s*\$?(?<name>[A-Za-z_][\w-]*)\s*(?:=|:=)\s*",
        RegexOptions.Compiled);

    private static readonly Regex SassImportReferenceRegex = new(
        @"^\s*@(?>import|use|forward)\s+(?:url\(\s*)?(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[^\s)""';]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StylusImportReferenceRegex = new(
        @"^\s*@(?>import|require|use)\s+(?:url\(\s*)?(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[^\s)""';]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CssCustomPropertyReferenceRegex = new(@"\bvar\(\s*--(?<name>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssAnimationNameValueRegex = new(@"\banimation-name\s*:\s*(?<value>[^;{}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssAnimationShorthandValueRegex = new(@"\banimation\s*:\s*(?<value>[^;{}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssClassSelectorReferenceRegex = new(@"\.(?<name>[\w-]+)", RegexOptions.Compiled);
    // First char restricted to letter/`_`/`-` so numeric hex colors like `#336699`
    // do not match. Letter-only hex colors (`#fff`) are still ambiguous; the
    // emission site additionally requires a selector-position context to skip them.
    // 数値開始の hex color (`#336699`) を弾くため最初の文字を letter / `_` / `-` に限定する。
    // `#fff` のような文字だけの hex color は曖昧なので、呼び出し側でセレクタ位置の
    // コンテキストをさらに要求して除外する。
    private static readonly Regex CssIdSelectorReferenceRegex = new(@"#(?<name>[A-Za-z_-][\w-]*)", RegexOptions.Compiled);
    private static readonly Regex CssImportReferenceRegex = new(
        @"@import\s+(?:url\(\s*)?(?:""(?<name>[^""]+)""|'(?<name>[^']+)'|(?<name>[^\s)""';]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssInlineBlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Compiled);

    private static readonly ReferencePattern[] CssReferencePatterns =
    [
        new(CssCustomPropertyReferenceRegex, "reference"),
    ];

    private static readonly ReferencePattern ScssVariableReferencePattern = new(ScssVariableReferenceRegex, "call", SkipVariableDeclarations: true);
    private static readonly ReferencePattern ScssExtendReferencePattern = new(ScssExtendReferenceRegex, "call");
    private static readonly ReferencePattern ScssIncludeReferencePattern = new(ScssIncludeReferenceRegex, "call");

    private static readonly HashSet<string> CssAnimationShorthandIgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ease", "ease-in", "ease-out", "ease-in-out", "linear",
        "step-start", "step-end", "cubic-bezier", "steps",
        "infinite", "normal", "reverse", "alternate", "alternate-reverse",
        "none", "forwards", "backwards", "both", "running", "paused",
        "initial", "inherit", "unset", "revert", "revert-layer",
    };

    private static readonly HashSet<string> CssBuiltInFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "var", "calc", "rgb", "rgba", "hsl", "hsla",
    };

    public static void EmitCss(
        string preparedLine,
        string originalLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("var", StringComparison.OrdinalIgnoreCase) >= 0
            && preparedLine.IndexOf("--", StringComparison.Ordinal) >= 0)
        {
            foreach (var pattern in CssReferencePatterns)
                EmitMatches(pattern, preparedLine, context, lineNumber, references, seen, fileId, definitionNames, container);
        }

        if (preparedLine.IndexOf("animation", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in CssAnimationNameValueRegex.Matches(preparedLine))
            {
                EmitCssAnimationNameReferences(
                    match.Groups["value"].Value,
                    match.Groups["value"].Index,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    definitionNames,
                    container);
            }

            foreach (Match match in CssAnimationShorthandValueRegex.Matches(preparedLine))
            {
                EmitCssAnimationShorthandReferences(
                    match.Groups["value"].Value,
                    match.Groups["value"].Index,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    definitionNames,
                    container);
            }
        }

        EmitCssClassSelectorReferences(
            preparedLine,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            definitionNames,
            container);

        // `@import "theme.css";` paths are stripped by the shared string-literal masker, so the
        // regex must scan the original (comment-stripped) line. Comment-strip locally to avoid
        // false positives on `/* @import "fake.css"; */`.
        // 共有の文字列リテラルマスカーが `@import "theme.css";` のパスを潰すため、@import の
        // パターンは元行（ブロックコメント除去後）に対して走らせる。`/* @import "fake.css"; */`
        // のような偽陽性を避けるため、ここでローカルに `/* */` を除去する。
        if (HasCssImportMarker(originalLine))
        {
            var importScanLine = CssInlineBlockCommentRegex.Replace(originalLine, " ");
            foreach (Match match in CssImportReferenceRegex.Matches(importScanLine))
            {
                var nameGroup = match.Groups["name"];
                if (!nameGroup.Success || nameGroup.Value.Length == 0)
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    nameGroup.Value,
                    nameGroup.Index,
                    "import",
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitScss(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('$') >= 0)
            EmitMatches(ScssVariableReferencePattern, preparedLine, context, lineNumber, references, seen, fileId, definitionNames: null, container);

        if (preparedLine.IndexOf("@extend", StringComparison.Ordinal) >= 0)
            EmitMatches(ScssExtendReferencePattern, preparedLine, context, lineNumber, references, seen, fileId, definitionNames: null, container);

        if (preparedLine.IndexOf("@include", StringComparison.Ordinal) >= 0)
            EmitMatches(ScssIncludeReferencePattern, preparedLine, context, lineNumber, references, seen, fileId, definitionNames: null, container);
    }

    public static void EmitSass(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        EmitPreprocessorImportReferences(SassImportReferenceRegex, originalLine, references, seen, fileId, context, lineNumber, container);

        if (originalLine.IndexOf('$') < 0
            && originalLine.IndexOf('@') < 0
            && originalLine.IndexOf('+') < 0
            && originalLine.IndexOf('(') < 0)
        {
            return;
        }

        var sassReferenceLine = PrepareSassStylusReferenceLine(originalLine);
        if (ShouldSkipSassIndentedDeclarationReferences(sassReferenceLine))
            sassReferenceLine = "";

        EmitScss(sassReferenceLine, references, seen, fileId, context, lineNumber, container);

        var sassMixinReferenceLine = ShouldSkipSassIndentedDeclarationReferences(sassReferenceLine)
            ? ""
            : sassReferenceLine;

        if (sassMixinReferenceLine.IndexOf('+') >= 0)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(SassIndentedMixinReferenceRegex, sassMixinReferenceLine))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    match,
                    "call",
                    context,
                    lineNumber,
                    container);
            }
        }

        if (sassReferenceLine.IndexOf('(') >= 0)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(SassBareFunctionReferenceRegex, sassReferenceLine))
            {
                var name = match.Groups["name"].Value;
                if (CssBuiltInFunctionNames.Contains(name))
                    continue;
                if (ShouldSkipSassBareFunctionReference(sassReferenceLine, match.Groups["name"].Index))
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    match,
                    "call",
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitStylus(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        HashSet<string>? definitionNames,
        HashSet<string>? variableDefinitionNames,
        SymbolRecord? container)
    {
        EmitPreprocessorImportReferences(StylusImportReferenceRegex, originalLine, references, seen, fileId, context, lineNumber, container);

        if (originalLine.IndexOf('$') < 0
            && originalLine.IndexOf('(') < 0
            && (variableDefinitionNames == null || variableDefinitionNames.Count == 0))
        {
            return;
        }

        var stylusReferenceLine = PrepareSassStylusReferenceLine(originalLine);
        if (stylusReferenceLine.IndexOf('$') >= 0)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(StylusVariableReferenceRegex, stylusReferenceLine))
            {
                if (ShouldSkipStylusVariableReference(stylusReferenceLine, match.Groups["name"].Index))
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    match,
                    "call",
                    context,
                    lineNumber,
                    container);
            }
        }

        if (variableDefinitionNames is { Count: > 0 })
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(StylusBareVariableReferenceRegex, stylusReferenceLine))
            {
                var nameGroup = match.Groups["name"];
                if (!variableDefinitionNames.Contains(nameGroup.Value))
                    continue;
                if (ShouldSkipStylusBareVariableReference(stylusReferenceLine, nameGroup.Index))
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    nameGroup.Value,
                    nameGroup.Index,
                    "call",
                    context,
                    lineNumber,
                    container);
            }
        }

        if (stylusReferenceLine.IndexOf('(') >= 0)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(StylusBareFunctionReferenceRegex, stylusReferenceLine))
            {
                var name = match.Groups["name"].Value;
                if (CssBuiltInFunctionNames.Contains(name))
                    continue;
                if (definitionNames != null && definitionNames.Contains(name) && match.Groups["name"].Index == 0)
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    match,
                    "call",
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    internal static HashSet<string> BuildStylusVariableDefinitionNames(string[] lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var inBlockComment = false;

        foreach (var line in lines)
        {
            var mayAffectBlockComment = inBlockComment
                || line.IndexOf("/*", StringComparison.Ordinal) >= 0
                || line.IndexOf("*/", StringComparison.Ordinal) >= 0;
            if (!mayAffectBlockComment && line.IndexOf('=') < 0)
                continue;

            var blockMaskedLine = MaskSassStylusBlockCommentLine(line, ref inBlockComment);
            if (blockMaskedLine.IndexOf('=') < 0)
                continue;

            var referenceLine = PrepareSassStylusReferenceLine(blockMaskedLine);
            var match = StylusVariableDefinitionRegex.Match(referenceLine);
            if (match.Success)
                names.Add(match.Groups["name"].Value);
        }

        return names;
    }

    internal sealed class SassLoudCommentState
    {
        public bool Active { get; set; }
        public int Indent { get; set; }
        public bool SilentActive { get; set; }
        public int SilentIndent { get; set; }
    }

    internal static string MaskSassBlockCommentLine(string line, SassLoudCommentState state)
    {
        char[]? chars = null;

        void MaskRange(int start, int endExclusive)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < endExclusive; index++)
                masked[index] = ' ';
        }

        var lineIndent = CountLeadingWhitespace(line);
        var isBlank = lineIndent == line.Length;
        var cursor = 0;

        if (state.SilentActive)
        {
            if (isBlank || lineIndent > state.SilentIndent)
            {
                return new string(' ', line.Length);
            }

            state.SilentActive = false;
        }

        if (state.Active)
        {
            var trimmed = isBlank ? "" : line[lineIndent..];
            if (!isBlank
                && lineIndent <= state.Indent
                && !trimmed.StartsWith("*/", StringComparison.Ordinal))
            {
                state.Active = false;
            }
            else
            {
                var commentEnd = line.IndexOf("*/", StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    return new string(' ', line.Length);
                }

                var stop = commentEnd + 2;
                MaskRange(0, stop);
                cursor = stop;
                state.Active = false;
            }
        }

        var quote = '\0';
        var parenDepth = 0;
        for (var i = cursor; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var commentEnd = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = commentEnd >= 0 ? commentEnd + 2 : line.Length;
                MaskRange(i, stop);

                if (commentEnd < 0)
                {
                    state.Active = true;
                    state.Indent = lineIndent;
                    break;
                }

                i = stop - 1;
                continue;
            }

            if (parenDepth == 0 && ch == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                MaskRange(i, line.Length);
                if (i == lineIndent)
                {
                    state.SilentActive = true;
                    state.SilentIndent = lineIndent;
                }
                break;
            }
        }

        return chars is null ? line : new string(chars);
    }

    internal static string MaskSassStylusBlockCommentLine(string line, ref bool inBlockComment)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        var quote = '\0';

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (inBlockComment)
            {
                MaskAt(i);
                if (ch == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    MaskAt(i + 1);
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                MaskAt(i);
                MaskAt(i + 1);
                inBlockComment = true;
                i++;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private static int CountLeadingWhitespace(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        return index;
    }

    private static void EmitPreprocessorImportReferences(
        Regex importRegex,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!HasPreprocessorImportMarker(originalLine))
            return;

        if (originalLine.TrimStart().StartsWith("//", StringComparison.Ordinal))
            return;

        var importScanLine = CssInlineBlockCommentRegex.Replace(originalLine, " ");
        foreach (Match match in importRegex.Matches(importScanLine))
        {
            var nameGroup = match.Groups["name"];
            if (!nameGroup.Success || nameGroup.Value.Length == 0)
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "import",
                context,
                lineNumber,
                container);
        }
    }

    private static bool HasCssImportMarker(string line) =>
        line.IndexOf('@') >= 0
        && line.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool HasPreprocessorImportMarker(string line) =>
        line.IndexOf('@') >= 0
        && (line.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("use", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("forward", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("require", StringComparison.OrdinalIgnoreCase) >= 0);

    private static void EmitMatches(
        ReferencePattern pattern,
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(pattern.Regex, preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            if (pattern.SkipVariableDeclarations && ShouldSkipScssVariableReference(preparedLine, nameGroup.Index))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                pattern.Kind,
                context,
                lineNumber,
                container);
        }
    }

    private static void EmitCssAnimationNameReferences(
        string value,
        int valueIndex,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var segmentStart = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != ',')
                continue;

            EmitCssAnimationNameSegmentReference(
                value,
                valueIndex,
                segmentStart,
                i,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                definitionNames,
                container);
            segmentStart = i + 1;
        }
    }

    private static void EmitCssAnimationNameSegmentReference(
        string value,
        int valueIndex,
        int segmentStart,
        int segmentEnd,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var cursor = segmentStart;
        while (cursor < segmentEnd && char.IsWhiteSpace(value[cursor]))
            cursor++;
        if (cursor >= segmentEnd)
            return;

        var tokenStart = cursor;
        while (cursor < segmentEnd && !char.IsWhiteSpace(value[cursor]))
            cursor++;

        var token = value[tokenStart..cursor];
        if (!IsCssAnimationNameToken(token))
            return;
        if (definitionNames != null && definitionNames.Contains(token))
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            token,
            valueIndex + tokenStart,
            "reference",
            context,
            lineNumber,
            container);
    }

    private static void EmitCssAnimationShorthandReferences(
        string value,
        int valueIndex,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length)
            {
                var ch = value[i];
                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch != ',' || parenDepth > 0)
                    continue;
            }

            EmitCssAnimationShorthandSegmentReference(
                value,
                valueIndex,
                segmentStart,
                i,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                definitionNames,
                container);
            segmentStart = i + 1;
        }
    }

    private static void EmitCssAnimationShorthandSegmentReference(
        string value,
        int valueIndex,
        int segmentStart,
        int segmentEnd,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        var cursor = segmentStart;
        while (cursor < segmentEnd)
        {
            while (cursor < segmentEnd && char.IsWhiteSpace(value[cursor]))
                cursor++;
            if (cursor >= segmentEnd)
                break;

            var tokenStart = cursor;
            while (cursor < segmentEnd && !char.IsWhiteSpace(value[cursor]))
                cursor++;

            var token = value[tokenStart..cursor];
            if (!IsCssAnimationNameToken(token))
                continue;
            if (definitionNames != null && definitionNames.Contains(token))
                return;

            ReferenceExtractor.AddReference(references, seen, fileId, token, valueIndex + tokenStart, "reference", context, lineNumber, container);
            return;
        }
    }

    private static void EmitCssClassSelectorReferences(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        // ID selectors (`#name`) are emitted only in selector-position segments
        // because `#fff` / `#abc123` color literals also match the regex. A
        // segment is treated as selector position when it terminates at `{`
        // on the current line (clear selector → block opener) or when the
        // entire line is a selector-list continuation (trimmed line ends with `,`).
        // ID セレクタ (`#name`) は `#fff` 等の color literal とパターンが衝突するため、
        // セレクタ位置のセグメントでのみ参照を発行する。セグメントが本行内で `{` で
        // 終わる場合、または行末カンマで selector list が継続する場合をセレクタ位置とみなす。
        var isSelectorContinuationLine = preparedLine.TrimEnd().EndsWith(',');
        var segmentStart = 0;
        while (segmentStart < preparedLine.Length)
        {
            var braceIndex = preparedLine.IndexOf('{', segmentStart);
            var segmentEnd = braceIndex >= 0 ? braceIndex : preparedLine.Length;
            var trimmedStart = segmentStart;
            while (trimmedStart < segmentEnd && char.IsWhiteSpace(preparedLine[trimmedStart]))
                trimmedStart++;

            if (trimmedStart < segmentEnd && preparedLine[trimmedStart] != '@')
            {
                var selectorSegment = preparedLine[trimmedStart..segmentEnd];
                var isIdSelectorContext = braceIndex >= 0
                    || (segmentStart == 0 && isSelectorContinuationLine);
                foreach (var (partStart, partEnd) in EnumerateCssSelectorListSegments(selectorSegment))
                {
                    var selectorPart = selectorSegment[partStart..partEnd];
                    var hasClassCandidate = ContainsCssClassSelectorReferenceCandidate(selectorPart);
                    var hasIdCandidate = isIdSelectorContext
                        && ContainsCssIdSelectorReferenceCandidate(selectorPart);
                    if (!hasClassCandidate && !hasIdCandidate)
                        continue;

                    var selectorPartTrimStart = 0;
                    while (selectorPartTrimStart < selectorPart.Length && char.IsWhiteSpace(selectorPart[selectorPartTrimStart]))
                        selectorPartTrimStart++;

                    var selectorPartBody = selectorPart[selectorPartTrimStart..];

                    if (hasClassCandidate)
                    {
                        EmitCssSelectorMatches(
                            CssClassSelectorReferenceRegex,
                            selectorPartBody,
                            ".",
                            trimmedStart + partStart + selectorPartTrimStart,
                            context,
                            lineNumber,
                            references,
                            seen,
                            fileId,
                            definitionNames,
                            container);
                    }

                    if (hasIdCandidate)
                    {
                        EmitCssSelectorMatches(
                            CssIdSelectorReferenceRegex,
                            selectorPartBody,
                            "#",
                            trimmedStart + partStart + selectorPartTrimStart,
                            context,
                            lineNumber,
                            references,
                            seen,
                            fileId,
                            definitionNames,
                            container);
                    }
                }
            }

            if (braceIndex < 0)
                break;

            segmentStart = braceIndex + 1;
        }
    }

    private static void EmitCssSelectorMatches(
        Regex regex,
        string selectorPartBody,
        string prefix,
        int baseColumn,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(regex, selectorPartBody))
        {
            var nameGroup = match.Groups["name"];
            var prefixIndex = nameGroup.Index - 1;
            if (!IsCssSelectorPrefixOutsideAttributeValue(selectorPartBody, prefixIndex))
                continue;

            var name = prefix + nameGroup.Value;
            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                baseColumn + nameGroup.Index - 1,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static bool IsCssSelectorPrefixOutsideAttributeValue(string selectorPartBody, int prefixIndex)
    {
        var bracketDepth = 0;
        char quote = '\0';
        for (var index = 0; index <= prefixIndex && index < selectorPartBody.Length; index++)
        {
            var ch = selectorPartBody[index];
            if (quote != '\0')
            {
                if (ch == quote && (index == 0 || selectorPartBody[index - 1] != '\\'))
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }
        }

        return bracketDepth == 0 && quote == '\0';
    }

    private static IEnumerable<(int Start, int End)> EnumerateCssSelectorListSegments(string selectorSegment)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = 0; index < selectorSegment.Length; index++)
        {
            var ch = selectorSegment[index];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (ch == ',' && parenDepth == 0 && bracketDepth == 0)
            {
                yield return (segmentStart, index);
                segmentStart = index + 1;
            }
        }

        yield return (segmentStart, selectorSegment.Length);
    }

    private static bool ContainsCssClassSelectorReferenceCandidate(string selectorPart)
        => ContainsCssSelectorReferenceCandidate(selectorPart, '.');

    private static bool ContainsCssIdSelectorReferenceCandidate(string selectorPart)
        => ContainsCssSelectorReferenceCandidate(selectorPart, '#');

    private static bool ContainsCssSelectorReferenceCandidate(string selectorPart, char prefix)
    {
        var bracketDepth = 0;
        char quote = '\0';
        for (var index = 0; index < selectorPart.Length; index++)
        {
            var ch = selectorPart[index];
            if (quote != '\0')
            {
                if (ch == quote && (index == 0 || selectorPart[index - 1] != '\\'))
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (bracketDepth == 0 && ch == prefix)
                return true;
        }

        return false;
    }

    private static bool IsCssAnimationNameToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (CssAnimationShorthandIgnoredTokens.Contains(token))
            return false;
        if (token.IndexOf('(') >= 0 || token.IndexOf(')') >= 0 || token.IndexOf(',') >= 0
            || token.IndexOf('/') >= 0 || token.IndexOf(':') >= 0 || token.IndexOf(';') >= 0)
            return false;
        if (IsCssAnimationTimeToken(token) || IsCssAnimationNumberToken(token))
            return false;
        if (token.StartsWith("--", StringComparison.Ordinal))
            return false;
        if (!(char.IsLetter(token[0]) || token[0] == '_' || token[0] == '-'))
            return false;
        if (token[0] == '-' && token.Length > 1 && (token[1] == '-' || char.IsDigit(token[1])))
            return false;

        for (var i = 1; i < token.Length; i++)
        {
            if (char.IsLetterOrDigit(token[i]) || token[i] == '_' || token[i] == '-')
                continue;
            return false;
        }

        return true;
    }

    private static bool IsCssAnimationTimeToken(string token)
    {
        if (token.Length < 2)
            return false;

        var unitLength = token.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            ? 2
            : token.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        if (unitLength == 0 || token.Length == unitLength)
            return false;

        var numberPart = token[..^unitLength];
        var sawDigit = false;
        var sawDot = false;
        foreach (var ch in numberPart)
        {
            if (char.IsDigit(ch))
            {
                sawDigit = true;
                continue;
            }

            if (ch == '.' && !sawDot)
            {
                sawDot = true;
                continue;
            }

            return false;
        }

        return sawDigit;
    }

    private static bool IsCssAnimationNumberToken(string token)
    {
        if (token.Length == 0 || token.IndexOfAny(['(', ')', ',', '/', ':', ';']) >= 0)
            return false;
        if (!(char.IsDigit(token[0]) || token[0] == '.'))
            return false;

        var sawDigit = false;
        var sawDot = false;
        foreach (var ch in token)
        {
            if (char.IsDigit(ch))
            {
                sawDigit = true;
                continue;
            }

            if (ch == '.' && !sawDot)
            {
                sawDot = true;
                continue;
            }

            return false;
        }

        return sawDigit;
    }

    private static bool ShouldSkipScssVariableReference(string preparedLine, int variableIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        var lineTail = preparedLine.AsSpan(firstNonWhitespace);
        if (lineTail.StartsWith("$", StringComparison.Ordinal))
        {
            var declarationColonIndex = preparedLine.IndexOf(':', variableIndex);
            if (declarationColonIndex >= 0)
                return true;
        }

        if (lineTail.StartsWith("@mixin", StringComparison.Ordinal)
            || lineTail.StartsWith("@function", StringComparison.Ordinal))
        {
            var braceIndex = preparedLine.IndexOf('{');
            if (braceIndex < 0)
                return true;
            if (variableIndex < braceIndex)
                return true;
        }

        return false;
    }

    private static bool ShouldSkipSassIndentedDeclarationReferences(string preparedLine)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        return firstNonWhitespace < preparedLine.Length && preparedLine[firstNonWhitespace] == '=';
    }

    private static bool ShouldSkipSassBareFunctionReference(string preparedLine, int functionIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        var lineTail = preparedLine.AsSpan(firstNonWhitespace);
        if (!lineTail.StartsWith("@function", StringComparison.Ordinal)
            && !lineTail.StartsWith("@mixin", StringComparison.Ordinal))
        {
            return false;
        }

        return functionIndex >= firstNonWhitespace;
    }

    private static string PrepareSassStylusReferenceLine(string originalLine)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= originalLine.ToCharArray())[index] = ' ';

        void MaskRange(int start, int endExclusive)
        {
            var masked = chars ??= originalLine.ToCharArray();
            for (var index = start; index < endExclusive; index++)
                masked[index] = ' ';
        }

        char quote = '\0';
        var parenDepth = 0;
        for (var i = 0; i < originalLine.Length; i++)
        {
            var ch = originalLine[i];
            if (quote != '\0')
            {
                MaskAt(i);
                if (ch == quote && (i == 0 || originalLine[i - 1] != '\\'))
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                MaskAt(i);
                quote = ch;
                continue;
            }

            if (ch == '/' && i + 1 < originalLine.Length && originalLine[i + 1] == '*')
            {
                var commentEnd = originalLine.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = commentEnd >= 0 ? commentEnd + 2 : originalLine.Length;
                MaskRange(i, stop);
                i = stop - 1;
                continue;
            }

            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (parenDepth == 0 && ch == '/' && i + 1 < originalLine.Length && originalLine[i + 1] == '/')
            {
                MaskRange(i, originalLine.Length);
                break;
            }
        }

        return chars is null ? originalLine : new string(chars);
    }

    private static bool ShouldSkipStylusVariableReference(string preparedLine, int variableIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        var dollarIndex = variableIndex - 1;
        if (dollarIndex != firstNonWhitespace || dollarIndex < 0 || preparedLine[dollarIndex] != '$')
            return false;

        var cursor = variableIndex;
        while (cursor < preparedLine.Length && (char.IsLetterOrDigit(preparedLine[cursor]) || preparedLine[cursor] is '_' or '-'))
            cursor++;
        while (cursor < preparedLine.Length && char.IsWhiteSpace(preparedLine[cursor]))
            cursor++;

        return cursor < preparedLine.Length
            && (preparedLine[cursor] == '='
                || (preparedLine[cursor] == ':' && cursor + 1 < preparedLine.Length && preparedLine[cursor + 1] == '='));
    }

    private static bool ShouldSkipStylusBareVariableReference(string preparedLine, int variableIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;
        if (variableIndex == firstNonWhitespace)
            return true;

        var cursor = variableIndex;
        while (cursor < preparedLine.Length && (char.IsLetterOrDigit(preparedLine[cursor]) || preparedLine[cursor] is '_' or '-'))
            cursor++;
        while (cursor < preparedLine.Length && char.IsWhiteSpace(preparedLine[cursor]))
            cursor++;

        if (cursor < preparedLine.Length && preparedLine[cursor] == '(')
            return true;
        return cursor < preparedLine.Length
            && (preparedLine[cursor] == '='
                || (preparedLine[cursor] == ':' && cursor + 1 < preparedLine.Length && preparedLine[cursor + 1] == '='));
    }
}
