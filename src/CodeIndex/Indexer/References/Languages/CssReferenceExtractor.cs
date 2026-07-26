using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class CssReferenceExtractor
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
        ReferenceDedupeSet seen,
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
            foreach (Match match in BoundedRegex.EnumerateMatches(CssAnimationNameValueRegex, preparedLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;
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

            foreach (Match match in BoundedRegex.EnumerateMatches(CssAnimationShorthandValueRegex, preparedLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;
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
            foreach (Match match in BoundedRegex.EnumerateMatches(CssImportReferenceRegex, importScanLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        IReadOnlySet<string>? definitionNames,
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

    internal static HashSet<string>? BuildStylusVariableDefinitionNames(string[] lines)
    {
        HashSet<string>? names = null;
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
                (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(match.Groups["name"].Value);
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
        ReferenceDedupeSet seen,
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
        foreach (Match match in BoundedRegex.EnumerateMatches(importRegex, importScanLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;
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

}
