using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class ScientificNativeReferenceExtractor
{
    private sealed class ScientificNativeReferenceEmitter
    {
        private readonly string language;
        private readonly string preparedLine;
        private readonly string originalLine;
        private readonly List<ReferenceRecord> references;
        private readonly ReferenceDedupeSet seen;
        private readonly long fileId;
        private readonly string context;
        private readonly int lineNumber;
        private readonly Func<int, SymbolRecord?> resolveContainerForColumn;
        private readonly int maxDependenciesPerDeclaration;
        private readonly Action<ReferenceExtractionDiagnostic>? reportDiagnostic;
        private bool dependencyLimitReported;

        internal ScientificNativeReferenceEmitter(
            string language,
            string preparedLine,
            string originalLine,
            List<ReferenceRecord> references,
            ReferenceDedupeSet seen,
            long fileId,
            string context,
            int lineNumber,
            Func<int, SymbolRecord?> resolveContainerForColumn,
            int maxDependenciesPerDeclaration,
            Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
        {
            this.language = language;
            this.preparedLine = preparedLine;
            this.originalLine = originalLine;
            this.references = references;
            this.seen = seen;
            this.fileId = fileId;
            this.context = context;
            this.lineNumber = lineNumber;
            this.resolveContainerForColumn = resolveContainerForColumn;
            this.maxDependenciesPerDeclaration =
                maxDependenciesPerDeclaration;
            this.reportDiagnostic = reportDiagnostic;
        }

        internal IReadOnlyList<DTemplateArgumentCallSpan>? Emit(
            Action<string, int> addCallLikeReference)
        {
            List<DTemplateArgumentCallSpan>? dTemplateArgumentCallSpans = null;
            switch (language)
            {
                case "nim":
                    EmitMatch(NimFromImportRegex, "import");
                    EmitNimImportList();
                    EmitMatches(
                        NimBaseTypeRegex,
                        "type_reference",
                        normalizeQualifiedTypeName: true);
                    EmitMatches(
                        NimAnnotatedTypeRegex,
                        "type_reference",
                        normalizeQualifiedTypeName: true);
                    break;
                case "matlab":
                    EmitNameList(
                        MatlabImportListRegex,
                        "import",
                        ',',
                        splitOnWhitespace: true);
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
                    EmitMatches(
                        JuliaTypeRegex,
                        "type_reference",
                        normalizeQualifiedTypeName: true);
                    EmitCallMatches(
                        JuliaMacroCallRegex,
                        addCallLikeReference);
                    EmitCallMatches(
                        JuliaBangCallRegex,
                        addCallLikeReference);
                    EmitCallMatches(
                        JuliaBroadcastCallRegex,
                        addCallLikeReference);
                    break;
                case "d":
                    EmitNameList(
                        DImportListRegex,
                        "import",
                        ',',
                        stopAtColon: true,
                        stripLeadingAlias: true);
                    EmitNameList(
                        DBaseTypeListRegex,
                        "type_reference",
                        ',',
                        normalizeQualifiedTypeName: true);
                    foreach (var invocation in
                             FindDTemplateInvocations(preparedLine))
                    {
                        addCallLikeReference(
                            invocation.Name,
                            invocation.NameIndex);
                        (dTemplateArgumentCallSpans ??= []).Add(
                            new DTemplateArgumentCallSpan(
                                invocation.ArgumentStart,
                                invocation.EndExclusive));
                    }
                    break;
                case "cython":
                    EmitMatch(
                        CythonFromImportRegex,
                        "import",
                        stripLeadingRelativePrefix: true);
                    EmitNameList(
                        CythonImportListRegex,
                        "import",
                        ',');
                    EmitCythonStringDependency();
                    EmitNameList(
                        CythonBaseTypeListRegex,
                        "type_reference",
                        ',',
                        normalizeQualifiedTypeName: true);
                    break;
                case "ada":
                    EmitNameList(
                        AdaImportListRegex,
                        "import",
                        ',');
                    EmitMatches(
                        AdaDerivedTypeRegex,
                        "type_reference",
                        normalizeQualifiedTypeName: true);
                    EmitAdaBareCalls();
                    break;
                case "objc":
                    EmitObjectiveCImport();
                    break;
            }

            return dTemplateArgumentCallSpans;
        }

        private void EmitCallMatches(
            Regex regex,
            Action<string, int> addCallLikeReference)
        {
            foreach (Match match in regex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                addCallLikeReference(group.Value, group.Index);
            }
        }

        private void EmitAdaBareCalls()
        {
            foreach (Match bareCall in AdaBareCallRegex.Matches(preparedLine))
            {
                var group = bareCall.Groups["name"];
                var separatorIndex = group.Value.LastIndexOf('.');
                var leafOffset = separatorIndex + 1;
                EmitName(
                    group.Value[leafOffset..],
                    group.Index + leafOffset,
                    "call",
                    separatorIndex >= 0
                        ? group.Value[..separatorIndex]
                        : null);
            }
        }

        private void EmitMatch(
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
            while (nameStart < group.Length
                   && group.Value[nameStart] == '.')
            {
                nameStart++;
            }
            if (nameStart < group.Length)
            {
                EmitName(
                    group.Value[nameStart..],
                    group.Index + nameStart,
                    referenceKind);
            }
        }

        private void EmitMatches(
            Regex regex,
            string referenceKind,
            bool normalizeQualifiedTypeName = false)
        {
            foreach (Match match in regex.Matches(preparedLine))
            {
                EmitGroup(
                    match.Groups["name"],
                    referenceKind,
                    normalizeQualifiedTypeName);
            }
        }

        private void EmitNameList(
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
                    && (names[index] == separator
                        || (splitOnWhitespace
                            && char.IsWhiteSpace(names[index])));
                if (!atEnd && !isSeparator)
                    continue;

                var canEmit =
                    dependencyCount < maxDependenciesPerDeclaration;
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
                           || (splitOnWhitespace
                               && char.IsWhiteSpace(
                                   names[segmentStart]))))
                {
                    segmentStart++;
                    index++;
                }
            }
        }

        private bool TryEmitDependencySegment(
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
            TrimDependencySegment(
                names,
                ref segmentStart,
                ref segmentEnd);
            if (segmentStart >= segmentEnd)
                return false;

            if (stripLeadingAlias)
            {
                var equalsIndex = names.LastIndexOf(
                    '=',
                    segmentEnd - 1,
                    segmentEnd - segmentStart);
                if (equalsIndex >= segmentStart)
                {
                    segmentStart = equalsIndex + 1;
                    while (segmentStart < segmentEnd
                           && char.IsWhiteSpace(names[segmentStart]))
                    {
                        segmentStart++;
                    }
                }
            }

            segmentEnd = FindDependencyAliasEnd(
                names,
                segmentStart,
                segmentEnd);
            while (segmentEnd > segmentStart
                   && char.IsWhiteSpace(names[segmentEnd - 1]))
            {
                segmentEnd--;
            }

            var nameEnd = segmentStart;
            while (nameEnd < segmentEnd
                   && IsDependencyNameChar(names[nameEnd]))
            {
                nameEnd++;
            }
            while (nameEnd > segmentStart
                   && names[nameEnd - 1] is '.' or '/')
            {
                nameEnd--;
            }

            var firstIdentifierIndex = segmentStart;
            while (firstIdentifierIndex < nameEnd
                   && names[firstIdentifierIndex] == '.')
            {
                firstIdentifierIndex++;
            }
            if (firstIdentifierIndex >= nameEnd
                || !(char.IsLetter(names[firstIdentifierIndex])
                    || names[firstIdentifierIndex] == '_'))
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
                    targetQualifier =
                        names[emittedNameStart..lastDotIndex];
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

        private static void TrimDependencySegment(
            string names,
            ref int segmentStart,
            ref int segmentEnd)
        {
            while (segmentStart < segmentEnd
                   && char.IsWhiteSpace(names[segmentStart]))
            {
                segmentStart++;
            }
            while (segmentEnd > segmentStart
                   && char.IsWhiteSpace(names[segmentEnd - 1]))
            {
                segmentEnd--;
            }
        }

        private static int FindDependencyAliasEnd(
            string names,
            int segmentStart,
            int segmentEnd)
        {
            for (var index = segmentStart;
                 index + 3 < segmentEnd;
                 index++)
            {
                if (char.IsWhiteSpace(names[index])
                    && names.AsSpan(index + 1, 2)
                        .Equals(
                            "as",
                            StringComparison.OrdinalIgnoreCase)
                    && char.IsWhiteSpace(names[index + 3]))
                {
                    return index;
                }
            }

            return segmentEnd;
        }

        private void EmitNimImportList()
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

                if (index < names.Length
                    && (names[index] != ',' || bracketDepth != 0))
                {
                    continue;
                }

                var (emittedCount, truncated) = EmitNimImportSegment(
                    names,
                    segmentStart,
                    index,
                    group.Index,
                    Math.Max(
                        0,
                        maxDependenciesPerDeclaration
                        - dependencyCount));
                dependencyCount += emittedCount;
                if (truncated)
                {
                    ReportDependencyLimit();
                    return;
                }

                segmentStart = index + 1;
            }
        }

        private (int EmittedCount, bool Truncated) EmitNimImportSegment(
            string names,
            int segmentStart,
            int segmentEnd,
            int absoluteOffset,
            int remainingCapacity)
        {
            TrimDependencySegment(
                names,
                ref segmentStart,
                ref segmentEnd);
            if (segmentStart >= segmentEnd)
                return (0, false);

            var openingBracket = names.IndexOf(
                '[',
                segmentStart,
                segmentEnd - segmentStart);
            var closingBracket = openingBracket >= 0
                ? names.IndexOf(
                    ']',
                    openingBracket + 1,
                    segmentEnd - openingBracket - 1)
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
            while (prefixEnd > prefixStart
                   && char.IsWhiteSpace(names[prefixEnd - 1]))
            {
                prefixEnd--;
            }
            if (prefixEnd <= prefixStart
                || names[prefixEnd - 1] != '/')
            {
                return (0, false);
            }

            var prefix = names[prefixStart..prefixEnd];
            var emittedCount = 0;
            var itemStart = openingBracket + 1;
            for (var index = itemStart;
                 index <= closingBracket;
                 index++)
            {
                if (index < closingBracket && names[index] != ',')
                    continue;

                var itemEnd = index;
                TrimDependencySegment(
                    names,
                    ref itemStart,
                    ref itemEnd);
                if (itemStart < itemEnd)
                {
                    var nameEnd = itemStart;
                    while (nameEnd < itemEnd
                           && IsDependencyNameChar(names[nameEnd]))
                    {
                        nameEnd++;
                    }
                    while (nameEnd > itemStart
                           && names[nameEnd - 1] is '.' or '/')
                    {
                        nameEnd--;
                    }
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

        private void ReportDependencyLimit()
        {
            if (dependencyLimitReported)
                return;

            dependencyLimitReported = true;
            reportDiagnostic?.Invoke(
                new ReferenceExtractionDiagnostic(
                    "reference_scientific_native_dependency_name_budget_exceeded",
                    $"Scientific/native dependency extraction used the first {maxDependenciesPerDeclaration:N0} names on line {lineNumber:N0} and skipped additional names."));
        }

        private void EmitGroup(
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
                lastDotIndex >= 0
                    ? group.Value[(lastDotIndex + 1)..]
                    : group.Value,
                group.Index + lastDotIndex + 1,
                referenceKind,
                lastDotIndex >= 0
                    ? group.Value[..lastDotIndex]
                    : null);
        }

        private void EmitName(
            string name,
            int index,
            string referenceKind,
            string? targetQualifier = null)
            => ReferenceExtractor.AddReference(
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

        private void EmitObjectiveCImport()
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

        private void EmitCythonStringDependency()
        {
            var directiveLine =
                CythonStringDependencyRegex.IsMatch(preparedLine)
                    ? preparedLine
                    : CythonStringDependencyDirectiveRegex.IsMatch(
                        preparedLine)
                        ? originalLine
                        : null;
            if (directiveLine == null)
                return;

            var match = CythonStringDependencyRegex.Match(directiveLine);
            if (match.Success)
                EmitGroup(match.Groups["name"], "import");
        }
    }
}
