using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class ScientificNativeReferenceExtractor
{
    internal const string CurrentContainerReceiverMarker = "\u001fcurrent-container";
    private const string JuliaIdentifierPattern = @"[\p{L}_]\w*";
    private const string JuliaCallableIdentifierPattern = JuliaIdentifierPattern + @"!?";

    internal readonly record struct DTemplateArgumentCallSpan(int Start, int EndExclusive);
    private readonly record struct DTemplateInvocation(
        string Name,
        int NameIndex,
        int ArgumentStart,
        int EndExclusive);

    private static readonly HashSet<string> SupportedLanguages =
        new(StringComparer.Ordinal) { "ada", "cython", "d", "julia", "matlab", "nim", "objc" };

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
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaDerivedTypeRegex = new(
        @"^\s*type\s+[A-Za-z]\w*\s+is\s+new\s+(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdaBareCallRegex = new(
        @"(?:^|;|\b(?:begin|then|else|loop)\b|=>)\s*(?!(?:end|null|return|exit|raise|goto)\b)(?<name>[A-Za-z]\w*(?:\.[A-Za-z]\w*)*)\s*(?=;)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectiveCImportRegex = new(
        """^\s*#\s*(?:import|include)\s*[<"](?<name>[^>"]+)[>"]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectiveCImportDirectiveRegex = new(
        @"^\s*#\s*(?:import|include)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool Supports(string language) => SupportedLanguages.Contains(language);

    internal static string? GetParenthesizedCallTargetQualifier(
        string language,
        string preparedLine,
        int callIndex)
    {
        var separatorIndex = callIndex - 1;
        while (separatorIndex >= 0 && char.IsWhiteSpace(preparedLine[separatorIndex]))
            separatorIndex--;
        if (separatorIndex >= 0 && preparedLine[separatorIndex] == '@')
        {
            separatorIndex--;
            while (separatorIndex >= 0 && char.IsWhiteSpace(preparedLine[separatorIndex]))
                separatorIndex--;
        }
        if (separatorIndex < 0 || preparedLine[separatorIndex] != '.')
            return null;

        var segments = new List<string>();
        while (separatorIndex >= 0 && preparedLine[separatorIndex] == '.')
        {
            var segmentEnd = separatorIndex;
            var segmentStart = segmentEnd - 1;
            while (segmentStart >= 0 && char.IsWhiteSpace(preparedLine[segmentStart]))
                segmentStart--;
            segmentEnd = segmentStart + 1;
            while (segmentStart >= 0 && IsQualifierIdentifierPart(preparedLine[segmentStart]))
                segmentStart--;
            segmentStart++;
            if (segmentStart >= segmentEnd
                || !IsQualifierIdentifierStart(preparedLine[segmentStart]))
            {
                return null;
            }

            segments.Add(preparedLine[segmentStart..segmentEnd]);
            separatorIndex = segmentStart - 1;
            while (separatorIndex >= 0 && char.IsWhiteSpace(preparedLine[separatorIndex]))
                separatorIndex--;
        }

        segments.Reverse();
        if ((language == "cython" && segments[0] is "self" or "cls")
            || (language == "d" && segments[0] is "this" or "super"))
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

        while (spanIndex < spans.Count && callIndex >= spans[spanIndex].EndExclusive)
            spanIndex++;

        return spanIndex < spans.Count && callIndex >= spans[spanIndex].Start;
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
    {
        var dependencyLimitReported = false;
        List<DTemplateArgumentCallSpan>? dTemplateArgumentCallSpans = null;

        switch (language)
        {
            case "nim":
                EmitMatch(NimFromImportRegex, "import");
                EmitNimImportList();
                EmitMatches(NimBaseTypeRegex, "type_reference", normalizeQualifiedTypeName: true);
                EmitMatches(NimAnnotatedTypeRegex, "type_reference", normalizeQualifiedTypeName: true);
                break;
            case "matlab":
                EmitNameList(MatlabImportListRegex, "import", ',', splitOnWhitespace: true);
                EmitNameList(
                    MatlabBaseTypeListRegex,
                    "type_reference",
                    '&',
                    normalizeQualifiedTypeName: true);
                break;
            case "julia":
                EmitNameList(
                    JuliaImportListRegex,
                    "import",
                    ',',
                    stopAtColon: true,
                    stripLeadingRelativePrefix: true);
                EmitMatches(JuliaTypeRegex, "type_reference", normalizeQualifiedTypeName: true);
                foreach (Match match in JuliaMacroCallRegex.Matches(preparedLine))
                {
                    var group = match.Groups["name"];
                    addCallLikeReference(group.Value, group.Index);
                }
                foreach (Match match in JuliaBangCallRegex.Matches(preparedLine))
                {
                    var group = match.Groups["name"];
                    addCallLikeReference(group.Value, group.Index);
                }
                foreach (Match match in JuliaBroadcastCallRegex.Matches(preparedLine))
                {
                    var group = match.Groups["name"];
                    addCallLikeReference(group.Value, group.Index);
                }
                break;
            case "d":
                EmitNameList(DImportListRegex, "import", ',', stopAtColon: true, stripLeadingAlias: true);
                EmitNameList(
                    DBaseTypeListRegex,
                    "type_reference",
                    ',',
                    normalizeQualifiedTypeName: true);
                foreach (var invocation in FindDTemplateInvocations(preparedLine))
                {
                    addCallLikeReference(invocation.Name, invocation.NameIndex);
                    (dTemplateArgumentCallSpans ??= []).Add(
                        new DTemplateArgumentCallSpan(
                            invocation.ArgumentStart,
                            invocation.EndExclusive));
                }
                break;
            case "cython":
                EmitMatch(CythonFromImportRegex, "import", stripLeadingRelativePrefix: true);
                EmitNameList(CythonImportListRegex, "import", ',');
                EmitCythonStringDependency();
                EmitNameList(
                    CythonBaseTypeListRegex,
                    "type_reference",
                    ',',
                    normalizeQualifiedTypeName: true);
                break;
            case "ada":
                EmitNameList(AdaImportListRegex, "import", ',');
                EmitMatches(AdaDerivedTypeRegex, "type_reference", normalizeQualifiedTypeName: true);
                foreach (Match bareCall in AdaBareCallRegex.Matches(preparedLine))
                {
                    var group = bareCall.Groups["name"];
                    var separatorIndex = group.Value.LastIndexOf('.');
                    var leafOffset = separatorIndex + 1;
                    EmitName(
                        group.Value[leafOffset..],
                        group.Index + leafOffset,
                        "call",
                        separatorIndex >= 0 ? group.Value[..separatorIndex] : null);
                }
                break;
            case "objc":
                EmitObjectiveCImport();
                break;
        }

        return dTemplateArgumentCallSpans;

        void EmitMatch(
            Regex regex,
            string referenceKind,
            bool stripLeadingRelativePrefix = false)
        {
            var match = regex.Match(preparedLine);
            if (!match.Success)
                return;

            var group = match.Groups["name"];
            if (!stripLeadingRelativePrefix)
            {
                EmitGroup(group, referenceKind);
                return;
            }

            var nameStart = 0;
            while (nameStart < group.Length && group.Value[nameStart] == '.')
                nameStart++;
            if (nameStart < group.Length)
                EmitName(group.Value[nameStart..], group.Index + nameStart, referenceKind);
        }

        void EmitMatches(
            Regex regex,
            string referenceKind,
            bool normalizeQualifiedTypeName = false)
        {
            foreach (Match match in regex.Matches(preparedLine))
                EmitGroup(match.Groups["name"], referenceKind, normalizeQualifiedTypeName);
        }

        void EmitNameList(
            Regex regex,
            string referenceKind,
            char separator,
            bool splitOnWhitespace = false,
            bool stopAtColon = false,
            bool stripLeadingAlias = false,
            bool stripLeadingRelativePrefix = false,
            bool normalizeQualifiedTypeName = false)
        {
            var match = regex.Match(preparedLine);
            if (!match.Success)
                return;

            var group = match.Groups["names"];
            if (!group.Success || group.Length == 0)
                return;

            var names = group.Value;
            var namesEnd = names.Length;
            if (stopAtColon)
            {
                var colonIndex = names.IndexOf(':');
                if (colonIndex >= 0)
                    namesEnd = colonIndex;
            }

            var dependencyCount = 0;
            var segmentStart = 0;
            for (var index = 0; index <= namesEnd; index++)
            {
                var atEnd = index == namesEnd;
                var isSeparator = !atEnd
                    && (names[index] == separator || (splitOnWhitespace && char.IsWhiteSpace(names[index])));
                if (!atEnd && !isSeparator)
                    continue;

                var canEmit = dependencyCount < maxDependenciesPerDeclaration;
                if (TryEmitDependencySegment(
                    names,
                    segmentStart,
                    index,
                    group.Index,
                    referenceKind,
                    stripLeadingAlias,
                    stripLeadingRelativePrefix,
                    normalizeQualifiedTypeName,
                    emit: canEmit))
                {
                    if (!canEmit)
                    {
                        ReportDependencyLimit();
                        return;
                    }

                    dependencyCount++;
                }

                segmentStart = index + 1;
                while (segmentStart < namesEnd
                    && (names[segmentStart] == separator
                        || (splitOnWhitespace && char.IsWhiteSpace(names[segmentStart]))))
                {
                    segmentStart++;
                    index++;
                }
            }
        }

        bool TryEmitDependencySegment(
            string names,
            int segmentStart,
            int segmentEnd,
            int absoluteOffset,
            string referenceKind,
            bool stripLeadingAlias,
            bool stripLeadingRelativePrefix,
            bool normalizeQualifiedTypeName = false,
            bool emit = true)
        {
            while (segmentStart < segmentEnd && char.IsWhiteSpace(names[segmentStart]))
                segmentStart++;
            while (segmentEnd > segmentStart && char.IsWhiteSpace(names[segmentEnd - 1]))
                segmentEnd--;
            if (segmentStart >= segmentEnd)
                return false;

            if (stripLeadingAlias)
            {
                var equalsIndex = names.LastIndexOf('=', segmentEnd - 1, segmentEnd - segmentStart);
                if (equalsIndex >= segmentStart)
                {
                    segmentStart = equalsIndex + 1;
                    while (segmentStart < segmentEnd && char.IsWhiteSpace(names[segmentStart]))
                        segmentStart++;
                }
            }

            for (var index = segmentStart; index + 3 < segmentEnd; index++)
            {
                if (!char.IsWhiteSpace(names[index])
                    || !names.AsSpan(index + 1, 2).Equals("as", StringComparison.OrdinalIgnoreCase)
                    || !char.IsWhiteSpace(names[index + 3]))
                {
                    continue;
                }

                segmentEnd = index;
                break;
            }

            while (segmentEnd > segmentStart && char.IsWhiteSpace(names[segmentEnd - 1]))
                segmentEnd--;

            var nameEnd = segmentStart;
            while (nameEnd < segmentEnd && IsDependencyNameChar(names[nameEnd]))
                nameEnd++;
            while (nameEnd > segmentStart && names[nameEnd - 1] is '.' or '/')
                nameEnd--;

            var firstIdentifierIndex = segmentStart;
            while (firstIdentifierIndex < nameEnd && names[firstIdentifierIndex] == '.')
                firstIdentifierIndex++;
            if (firstIdentifierIndex >= nameEnd
                || !(char.IsLetter(names[firstIdentifierIndex]) || names[firstIdentifierIndex] == '_'))
            {
                return false;
            }

            var emittedNameStart = stripLeadingRelativePrefix
                ? firstIdentifierIndex
                : segmentStart;
            string? targetQualifier = null;
            if (normalizeQualifiedTypeName)
            {
                var lastDotIndex = names.LastIndexOf(
                    '.',
                    nameEnd - 1,
                    nameEnd - emittedNameStart);
                if (lastDotIndex >= emittedNameStart)
                {
                    targetQualifier = names[emittedNameStart..lastDotIndex];
                    emittedNameStart = lastDotIndex + 1;
                }
            }
            if (emit)
            {
                EmitName(
                    names[emittedNameStart..nameEnd],
                    absoluteOffset + emittedNameStart,
                    referenceKind,
                    targetQualifier);
            }

            return true;
        }

        void EmitNimImportList()
        {
            var match = NimImportListRegex.Match(preparedLine);
            if (!match.Success)
                return;

            var group = match.Groups["names"];
            if (!group.Success || group.Length == 0)
                return;

            var names = group.Value;
            var dependencyCount = 0;
            var segmentStart = 0;
            var bracketDepth = 0;
            for (var index = 0; index <= names.Length; index++)
            {
                if (index < names.Length)
                {
                    if (names[index] == '[')
                        bracketDepth++;
                    else if (names[index] == ']' && bracketDepth > 0)
                        bracketDepth--;
                }

                if (index < names.Length && (names[index] != ',' || bracketDepth != 0))
                    continue;

                var (emittedCount, truncated) = EmitNimImportSegment(
                    names,
                    segmentStart,
                    index,
                    group.Index,
                    Math.Max(0, maxDependenciesPerDeclaration - dependencyCount));
                dependencyCount += emittedCount;
                if (truncated)
                {
                    ReportDependencyLimit();
                    return;
                }

                segmentStart = index + 1;
            }
        }

        (int EmittedCount, bool Truncated) EmitNimImportSegment(
            string names,
            int segmentStart,
            int segmentEnd,
            int absoluteOffset,
            int remainingCapacity)
        {
            while (segmentStart < segmentEnd && char.IsWhiteSpace(names[segmentStart]))
                segmentStart++;
            while (segmentEnd > segmentStart && char.IsWhiteSpace(names[segmentEnd - 1]))
                segmentEnd--;
            if (segmentStart >= segmentEnd)
                return (0, false);

            var openingBracket = names.IndexOf('[', segmentStart, segmentEnd - segmentStart);
            var closingBracket = openingBracket >= 0
                ? names.IndexOf(']', openingBracket + 1, segmentEnd - openingBracket - 1)
                : -1;
            if (openingBracket < 0 || closingBracket < 0)
            {
                var canEmit = remainingCapacity > 0;
                var hasDependency = TryEmitDependencySegment(
                    names,
                    segmentStart,
                    segmentEnd,
                    absoluteOffset,
                    "import",
                    stripLeadingAlias: false,
                    stripLeadingRelativePrefix: false,
                    emit: canEmit);
                return hasDependency
                    ? (canEmit ? 1 : 0, !canEmit)
                    : (0, false);
            }

            var prefixStart = segmentStart;
            var prefixEnd = openingBracket;
            while (prefixEnd > prefixStart && char.IsWhiteSpace(names[prefixEnd - 1]))
                prefixEnd--;
            if (prefixEnd <= prefixStart || names[prefixEnd - 1] != '/')
                return (0, false);

            var prefix = names[prefixStart..prefixEnd];
            var emittedCount = 0;
            var itemStart = openingBracket + 1;
            for (var index = itemStart; index <= closingBracket; index++)
            {
                if (index < closingBracket && names[index] != ',')
                    continue;

                var itemEnd = index;
                while (itemStart < itemEnd && char.IsWhiteSpace(names[itemStart]))
                    itemStart++;
                while (itemEnd > itemStart && char.IsWhiteSpace(names[itemEnd - 1]))
                    itemEnd--;
                if (itemStart < itemEnd)
                {
                    var nameEnd = itemStart;
                    while (nameEnd < itemEnd && IsDependencyNameChar(names[nameEnd]))
                        nameEnd++;
                    while (nameEnd > itemStart && names[nameEnd - 1] is '.' or '/')
                        nameEnd--;
                    if (nameEnd > itemStart)
                    {
                        if (emittedCount >= remainingCapacity)
                            return (emittedCount, true);

                        EmitName(
                            prefix + names[itemStart..nameEnd],
                            absoluteOffset + itemStart,
                            "import");
                        emittedCount++;
                    }
                }

                itemStart = index + 1;
            }

            return (emittedCount, false);
        }

        void ReportDependencyLimit()
        {
            if (dependencyLimitReported)
                return;

            dependencyLimitReported = true;
            reportDiagnostic?.Invoke(new ReferenceExtractionDiagnostic(
                "reference_scientific_native_dependency_name_budget_exceeded",
                $"Scientific/native dependency extraction used the first {maxDependenciesPerDeclaration:N0} names on line {lineNumber:N0} and skipped additional names."));
        }

        void EmitGroup(
            Group group,
            string referenceKind,
            bool normalizeQualifiedTypeName = false)
        {
            if (!group.Success || group.Length == 0)
                return;

            if (!normalizeQualifiedTypeName)
            {
                EmitName(group.Value, group.Index, referenceKind);
                return;
            }

            var lastDotIndex = group.Value.LastIndexOf('.');
            EmitName(
                lastDotIndex >= 0 ? group.Value[(lastDotIndex + 1)..] : group.Value,
                group.Index + lastDotIndex + 1,
                referenceKind,
                lastDotIndex >= 0 ? group.Value[..lastDotIndex] : null);
        }

        void EmitName(
            string name,
            int index,
            string referenceKind,
            string? targetQualifier = null)
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                index,
                referenceKind,
                context,
                lineNumber,
                resolveContainerForColumn(index),
                language,
                targetQualifier);
        }

        void EmitObjectiveCImport()
        {
            var directiveLine = ObjectiveCImportRegex.IsMatch(preparedLine)
                ? preparedLine
                : ObjectiveCImportDirectiveRegex.IsMatch(preparedLine)
                    ? originalLine
                    : null;
            if (directiveLine == null)
                return;

            var match = ObjectiveCImportRegex.Match(directiveLine);
            if (match.Success)
                EmitGroup(match.Groups["name"], "import");
        }

        void EmitCythonStringDependency()
        {
            var directiveLine = CythonStringDependencyRegex.IsMatch(preparedLine)
                ? preparedLine
                : CythonStringDependencyDirectiveRegex.IsMatch(preparedLine)
                    ? originalLine
                    : null;
            if (directiveLine == null)
                return;

            var match = CythonStringDependencyRegex.Match(directiveLine);
            if (match.Success)
                EmitGroup(match.Groups["name"], "import");
        }
    }

    private static bool IsDependencyNameChar(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '.' or '/' or '*';

    private static IReadOnlyList<DTemplateInvocation> FindDTemplateInvocations(string line)
    {
        List<DTemplateInvocation>? invocations = null;
        var cursor = 0;
        while (cursor < line.Length)
        {
            if (!IsDIdentifierStart(line[cursor])
                || (cursor > 0 && IsDIdentifierPart(line[cursor - 1])))
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
                if (nextNameIndex >= line.Length || !IsDIdentifierStart(line[nextNameIndex]))
                    break;

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
                if (!TryScanBalancedDTemplateArguments(line, scan, out scan))
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

            (invocations ??= []).Add(new DTemplateInvocation(
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
        for (var cursor = openingParenthesis; cursor < line.Length; cursor++)
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
        while (cursor < line.Length && IsDIdentifierPart(line[cursor]))
            cursor++;

        return cursor;
    }

    private static void SkipWhitespace(string line, ref int cursor)
    {
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
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
