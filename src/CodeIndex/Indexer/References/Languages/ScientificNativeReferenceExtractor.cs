using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class ScientificNativeReferenceExtractor
{
    internal const string CurrentContainerReceiverMarker =
        "\u001fcurrent-container";
    private const string JuliaIdentifierPattern = @"[\p{L}_]\w*";
    private const string JuliaCallableIdentifierPattern =
        JuliaIdentifierPattern + @"!?";

    internal readonly record struct DTemplateArgumentCallSpan(
        int Start,
        int EndExclusive);

    private readonly record struct DTemplateInvocation(
        string Name,
        int NameIndex,
        int ArgumentStart,
        int EndExclusive);

    private static readonly HashSet<string> SupportedLanguages =
        new(StringComparer.Ordinal)
        {
            "ada",
            "cython",
            "d",
            "julia",
            "matlab",
            "nim",
            "objc",
        };

    private static readonly Regex NimFromImportRegex = new(
        @"^\s*from\s+(?<name>[A-Za-z_][\w./]*)\s+import\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimImportListRegex = new(
        @"^\s*(?:import|include)\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimBaseTypeRegex = new(
        @"\bobject\s+of\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NimAnnotatedTypeRegex = new(
        @":\s*(?:(?:var|lent|sink)\s+)?(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MatlabImportListRegex = new(
        @"^\s*import\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MatlabBaseTypeListRegex = new(
        @"^\s*classdef\b[^<\r\n]*<\s*(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JuliaImportListRegex = new(
        @"^\s*(?:using|import)\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaTypeRegex = new(
        @"(?:<:|::)\s*(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaMacroCallRegex = new(
        $@"(?<![\w@])@(?<name>{JuliaIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaBangCallRegex = new(
        $@"(?<![\w$])(?<name>{JuliaIdentifierPattern}!)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JuliaBroadcastCallRegex = new(
        $@"(?<![\w$])(?<name>{JuliaCallableIdentifierPattern})\s*\.\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DImportListRegex = new(
        @"^\s*(?:(?:public|private|protected|package|static|export)\s+)*import\s+(?<names>[^;\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DBaseTypeListRegex = new(
        @"^\s*(?:(?:public|private|protected|package|static|abstract|final|extern)\s+)*(?:class|interface)\s+[A-Za-z_]\w*(?:\s*\([^)]*\))?\s*:\s*(?<names>[^{\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonFromImportRegex = new(
        @"^\s*from\s+(?<name>\.*[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s+(?:cimport|import)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonImportListRegex = new(
        @"^\s*(?:cimport|import)\s+(?<names>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonStringDependencyRegex = new(
        """^\s*(?:include\s+|cdef\s+extern\s+from\s+)(?:'(?<name>[^']+)'|"(?<name>[^"]+)")""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonStringDependencyDirectiveRegex = new(
        @"^\s*(?:include\b|cdef\s+extern\s+from\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CythonBaseTypeListRegex = new(
        @"^\s*(?:cdef\s+)?class\s+[A-Za-z_]\w*\s*\(\s*(?<names>[^)\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AdaImportListRegex = new(
        @"^\s*(?:(?:limited|private)\s+)*with\s+(?<names>[^;\r\n]+)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);
    private static readonly Regex AdaDerivedTypeRegex = new(
        @"^\s*type\s+[A-Za-z]\w*\s+is\s+new\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);
    private static readonly Regex AdaBareCallRegex = new(
        @"(?:^|;|\b(?:begin|then|else|loop)\b|=>)\s*(?!(?:end|null|return|exit|raise|goto)\b)(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)\s*(?=;)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectiveCImportRegex = new(
        """^\s*#\s*(?:import|include)\s*[<"](?<name>[^>"]+)[>"]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectiveCImportDirectiveRegex = new(
        @"^\s*#\s*(?:import|include)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool Supports(string language) =>
        SupportedLanguages.Contains(language);

    internal static string? GetParenthesizedCallTargetQualifier(
        string language,
        string preparedLine,
        int callIndex)
    {
        var separatorIndex = callIndex - 1;
        while (separatorIndex >= 0
               && char.IsWhiteSpace(preparedLine[separatorIndex]))
        {
            separatorIndex--;
        }
        if (separatorIndex >= 0 && preparedLine[separatorIndex] == '@')
        {
            separatorIndex--;
            while (separatorIndex >= 0
                   && char.IsWhiteSpace(preparedLine[separatorIndex]))
            {
                separatorIndex--;
            }
        }
        if (separatorIndex < 0 || preparedLine[separatorIndex] != '.')
            return null;

        var segments = new List<string>();
        while (separatorIndex >= 0 && preparedLine[separatorIndex] == '.')
        {
            var segmentEnd = separatorIndex;
            var segmentStart = segmentEnd - 1;
            while (segmentStart >= 0
                   && char.IsWhiteSpace(preparedLine[segmentStart]))
            {
                segmentStart--;
            }
            segmentEnd = segmentStart + 1;
            while (segmentStart >= 0
                   && IsQualifierIdentifierPart(
                       preparedLine[segmentStart]))
            {
                segmentStart--;
            }
            segmentStart++;
            if (segmentStart >= segmentEnd
                || !IsQualifierIdentifierStart(
                    preparedLine[segmentStart]))
            {
                return null;
            }

            segments.Add(preparedLine[segmentStart..segmentEnd]);
            separatorIndex = segmentStart - 1;
            while (separatorIndex >= 0
                   && char.IsWhiteSpace(preparedLine[separatorIndex]))
            {
                separatorIndex--;
            }
        }

        segments.Reverse();
        if ((language == "cython" && segments[0] is "self" or "cls")
            || (language == "d" && segments[0] == "this"))
        {
            return CurrentContainerReceiverMarker;
        }

        return string.Join('.', segments);
    }

    internal static bool IsDTemplateArgumentCall(
        IReadOnlyList<DTemplateArgumentCallSpan>? spans,
        ref int spanIndex,
        int callIndex)
    {
        if (spans == null)
            return false;

        while (spanIndex < spans.Count
               && callIndex >= spans[spanIndex].EndExclusive)
        {
            spanIndex++;
        }

        return spanIndex < spans.Count
            && callIndex >= spans[spanIndex].Start;
    }

    internal static IReadOnlyList<DTemplateArgumentCallSpan>? EmitReferences(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        Action<string, int> addCallLikeReference,
        int maxDependenciesPerDeclaration,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
        => new ScientificNativeReferenceEmitter(
                language,
                preparedLine,
                originalLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                maxDependenciesPerDeclaration,
                reportDiagnostic)
            .Emit(addCallLikeReference);

    private static bool IsDependencyNameChar(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '.' or '/' or '*';

    private static IReadOnlyList<DTemplateInvocation>
        FindDTemplateInvocations(string line)
    {
        List<DTemplateInvocation>? invocations = null;
        var cursor = 0;
        while (cursor < line.Length)
        {
            if (!IsDIdentifierStart(line[cursor])
                || (cursor > 0
                    && IsDIdentifierPart(line[cursor - 1])))
            {
                cursor++;
                continue;
            }

            var nameIndex = cursor;
            cursor = ScanDIdentifier(line, cursor);
            var nameEnd = cursor;
            var scan = cursor;
            while (true)
            {
                SkipWhitespace(line, ref scan);
                if (scan >= line.Length || line[scan] != '.')
                    break;

                var nextNameIndex = scan + 1;
                SkipWhitespace(line, ref nextNameIndex);
                if (nextNameIndex >= line.Length
                    || !IsDIdentifierStart(line[nextNameIndex]))
                {
                    break;
                }

                nameIndex = nextNameIndex;
                nameEnd = ScanDIdentifier(line, nextNameIndex);
                scan = nameEnd;
            }

            SkipWhitespace(line, ref scan);
            if (scan >= line.Length || line[scan] != '!')
            {
                cursor = scan;
                continue;
            }

            if (scan + 1 < line.Length && line[scan + 1] == '=')
            {
                cursor = scan + 2;
                continue;
            }

            scan++;
            SkipWhitespace(line, ref scan);
            var argumentStart = nameEnd;
            if (scan < line.Length && line[scan] == '(')
            {
                if (!TryScanBalancedDTemplateArguments(
                        line,
                        scan,
                        out scan))
                {
                    cursor = line.Length;
                    continue;
                }
            }
            else
            {
                var tokenStart = scan;
                while (scan < line.Length
                       && !char.IsWhiteSpace(line[scan])
                       && line[scan] is not ('(' or ';' or ','))
                {
                    scan++;
                }

                if (scan == tokenStart)
                    continue;
            }

            SkipWhitespace(line, ref scan);
            if (scan >= line.Length || line[scan] != '(')
            {
                cursor = scan;
                continue;
            }

            (invocations ??= []).Add(
                new DTemplateInvocation(
                    line[nameIndex..nameEnd],
                    nameIndex,
                    argumentStart,
                    scan + 1));
            cursor = scan + 1;
        }

        return invocations ?? [];
    }

    private static bool TryScanBalancedDTemplateArguments(
        string line,
        int openingParenthesis,
        out int endExclusive)
    {
        var depth = 0;
        for (var cursor = openingParenthesis;
             cursor < line.Length;
             cursor++)
        {
            if (line[cursor] == '(')
            {
                depth++;
                continue;
            }

            if (line[cursor] != ')')
                continue;

            depth--;
            if (depth == 0)
            {
                endExclusive = cursor + 1;
                return true;
            }
        }

        endExclusive = line.Length;
        return false;
    }

    private static int ScanDIdentifier(string line, int start)
    {
        var cursor = start + 1;
        while (cursor < line.Length
               && IsDIdentifierPart(line[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static void SkipWhitespace(string line, ref int cursor)
    {
        while (cursor < line.Length
               && char.IsWhiteSpace(line[cursor]))
        {
            cursor++;
        }
    }

    private static bool IsDIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_';

    private static bool IsDIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static bool IsQualifierIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_';

    private static bool IsQualifierIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';
}
