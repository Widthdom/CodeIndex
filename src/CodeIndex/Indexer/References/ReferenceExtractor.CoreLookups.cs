using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private sealed class CoreExtractionLookups
    {
        private readonly ReferenceExtractionContext request;
        private readonly string language;
        private readonly IReadOnlyList<SymbolRecord> symbols;
        private readonly IReadOnlyList<SymbolRecord> containerCandidates;
        private readonly bool[]? csharpLinesInsideMultilineStringContent;
        private readonly string[] preparedLines;
        private readonly string[] structuralLines;
        private readonly string[] lines;
        private readonly IReadOnlySet<string> csharpKnownTypeNames;
        private readonly IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases;
        private readonly IReadOnlyList<CSharpUsingNamespaceRecord> csharpUsingNamespaces;

        private Dictionary<int, List<SymbolRecord>>? csharpSameLineContainerCandidatesByLine;
        private bool csharpSameLineContainerCandidatesResolved;
        private IReadOnlyList<SymbolRecord>? csharpXmlDocAttachmentScopeCandidates;
        private bool csharpXmlDocAttachmentScopeCandidatesResolved;
        private IReadOnlyList<SymbolRecord>? enclosingTypeCandidates;
        private bool enclosingTypeCandidatesResolved;
        private IReadOnlyList<SymbolRecord>? rustEnumCandidates;
        private bool rustEnumCandidatesResolved;
        private (
            IReadOnlyDictionary<(int Line, string Kind), SymbolRecord>? DefinitionContainersByLineAndKind,
            IReadOnlyDictionary<int, SymbolRecord>? HeaderSymbolsByLine) pythonSymbolLookups;
        private bool pythonSymbolLookupsResolved;
        private HashSet<string>? pythonClassNames;
        private bool pythonClassNamesResolved;
        private PythonImportBindingResolver.ImportedTypeCallLookup? pythonImportedTypeCallLookup;
        private HashSet<(string Container, string Name)>? csharpFieldOrPropertyMembers;
        private bool csharpFieldOrPropertyMembersResolved;
        private Dictionary<string, List<SymbolRecord>>? csharpContainerCandidatesByName;
        private List<(int StartLine, int StartColumn, int EndLine, int EndColumn, SymbolRecord Container, SymbolRecord Owner)>? recordPrimaryCtorRanges;
        private bool recordPrimaryCtorRangesResolved;
        private (
            IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> ByContainingType,
            IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> ByFunctionStartLine)? csharpValueReceiverLookups;
        private bool csharpValueReceiverLookupsResolved;
        private IReadOnlyDictionary<string, List<PowerShellReferenceExtractor.SplatAssignment>>? powershellSplatAssignments;
        private bool powershellSplatAssignmentsResolved;

        internal CoreExtractionLookups(
            ReferenceExtractionContext request,
            string language,
            IReadOnlyList<SymbolRecord> symbols,
            IReadOnlyList<SymbolRecord> containerCandidates,
            bool[]? csharpLinesInsideMultilineStringContent,
            string[] preparedLines,
            string[] structuralLines,
            string[] lines,
            IReadOnlySet<string> csharpKnownTypeNames,
            IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
            IReadOnlyList<CSharpUsingNamespaceRecord> csharpUsingNamespaces)
        {
            this.request = request;
            this.language = language;
            this.symbols = symbols;
            this.containerCandidates = containerCandidates;
            this.csharpLinesInsideMultilineStringContent = csharpLinesInsideMultilineStringContent;
            this.preparedLines = preparedLines;
            this.structuralLines = structuralLines;
            this.lines = lines;
            this.csharpKnownTypeNames = csharpKnownTypeNames;
            this.csharpUsingAliases = csharpUsingAliases;
            this.csharpUsingNamespaces = csharpUsingNamespaces;
        }

        internal bool HasSameFilePythonClass(string candidate, string leaf)
        {
            if (!pythonClassNamesResolved)
            {
                foreach (var symbol in symbols)
                {
                    if (symbol.Kind == "class")
                        (pythonClassNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(symbol.Name);
                }
                pythonClassNamesResolved = true;
            }

            return pythonClassNames != null
                && (pythonClassNames.Contains(candidate) || pythonClassNames.Contains(leaf));
        }

        internal PythonImportBindingResolver.ImportedTypeCallLookup GetPythonImportedTypeCallLookup()
            => pythonImportedTypeCallLookup ??= PythonImportBindingResolver.BuildImportedTypeCallLookup(symbols);

        internal bool HasCSharpFieldOrPropertyMember(string containingType, string memberName)
        {
            if (!csharpFieldOrPropertyMembersResolved)
            {
                foreach (var symbol in symbols)
                {
                    if (symbol.Kind is "field" or "property"
                        && symbol.ContainerQualifiedName != null)
                    {
                        (csharpFieldOrPropertyMembers ??= []).Add((symbol.ContainerQualifiedName, symbol.Name));
                    }
                }
                csharpFieldOrPropertyMembersResolved = true;
            }

            return csharpFieldOrPropertyMembers?.Contains((containingType, memberName)) == true;
        }

        internal SymbolRecord? FindCSharpContainerCandidate(string? containerName, int lineNumber)
        {
            if (containerName == null)
                return null;

            if (csharpContainerCandidatesByName == null)
            {
                csharpContainerCandidatesByName = new Dictionary<string, List<SymbolRecord>>(StringComparer.Ordinal);
                foreach (var candidate in containerCandidates)
                {
                    if (!csharpContainerCandidatesByName.TryGetValue(candidate.Name, out var candidates))
                    {
                        candidates = [];
                        csharpContainerCandidatesByName.Add(candidate.Name, candidates);
                    }
                    candidates.Add(candidate);
                }
            }

            if (!csharpContainerCandidatesByName.TryGetValue(containerName, out var namedCandidates))
                return null;

            foreach (var candidate in namedCandidates)
            {
                if (candidate.BodyStartLine <= lineNumber && candidate.BodyEndLine >= lineNumber)
                    return candidate;
            }

            return null;
        }

        // Workspace-wide same-name type rescue needs cross-file visibility, so the
        // extractor leaves ambiguous unqualified using-static pattern heads for the
        // read path to disambiguate.
        // ワークスペース全体の同名型 rescue には cross-file 可視性が必要なため、
        // extractor は曖昧な unqualified using-static pattern head を残し、
        // read path 側で判定させる。
        internal bool HasActiveSameFileCSharpTypeCandidate(string typeExpression, int lineNumber)
        {
            var normalized = NormalizeCSharpAliasTargetForTypeLookup(typeExpression);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            normalized = TrimLeadingCSharpGlobalQualifier(normalized);
            if (csharpKnownTypeNames.Contains(normalized))
                return true;

            var shortName = GetLastQualifiedSegment(normalized);
            for (var aliasIndex = csharpUsingAliases.Count - 1; aliasIndex >= 0; aliasIndex--)
            {
                var alias = csharpUsingAliases[aliasIndex];
                if (alias.TargetsType
                    && alias.Line <= lineNumber
                    && lineNumber >= alias.ScopeStartLine
                    && lineNumber <= alias.ScopeEndLine
                    && string.Equals(alias.AliasName, shortName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal Dictionary<int, List<SymbolRecord>>? GetCSharpSameLineContainerCandidatesByLine()
        {
            if (!csharpSameLineContainerCandidatesResolved)
            {
                csharpSameLineContainerCandidatesByLine = BuildCSharpSameLineContainerCandidatesByLine(language, containerCandidates);
                csharpSameLineContainerCandidatesResolved = true;
            }

            return csharpSameLineContainerCandidatesByLine;
        }

        internal IReadOnlyList<SymbolRecord>? GetCSharpXmlDocAttachmentScopeCandidates()
        {
            if (!csharpXmlDocAttachmentScopeCandidatesResolved)
            {
                csharpXmlDocAttachmentScopeCandidates = csharpLinesInsideMultilineStringContent != null
                    ? BuildCSharpXmlDocAttachmentScopeCandidates(language, symbols, request.ReportDiagnostic)
                    : null;
                csharpXmlDocAttachmentScopeCandidatesResolved = true;
            }

            return csharpXmlDocAttachmentScopeCandidates;
        }

        internal IReadOnlyList<SymbolRecord> GetEnclosingTypeCandidates()
        {
            if (!enclosingTypeCandidatesResolved)
            {
                enclosingTypeCandidates = language is "csharp" or "java" or "kotlin"
                    ? BuildEnclosingTypeCandidates(symbols, request.ReportDiagnostic)
                    : [];
                enclosingTypeCandidatesResolved = true;
            }

            return enclosingTypeCandidates!;
        }

        internal IReadOnlyList<SymbolRecord>? GetRustEnumCandidates()
        {
            if (!rustEnumCandidatesResolved)
            {
                rustEnumCandidates = language == "rust"
                    ? BuildRustEnumCandidates(symbols)
                    : null;
                rustEnumCandidatesResolved = true;
            }

            return rustEnumCandidates;
        }

        private (
            IReadOnlyDictionary<(int Line, string Kind), SymbolRecord>? DefinitionContainersByLineAndKind,
            IReadOnlyDictionary<int, SymbolRecord>? HeaderSymbolsByLine) GetPythonSymbolLookups()
        {
            if (!pythonSymbolLookupsResolved)
            {
                pythonSymbolLookups = language == "python"
                    ? BuildPythonSymbolLookups(symbols)
                    : default;
                pythonSymbolLookupsResolved = true;
            }

            return pythonSymbolLookups;
        }

        internal IReadOnlyDictionary<(int Line, string Kind), SymbolRecord>? GetPythonDefinitionContainersByLineAndKind() =>
            GetPythonSymbolLookups().DefinitionContainersByLineAndKind;

        internal IReadOnlyDictionary<int, SymbolRecord>? GetPythonHeaderSymbolsByLine() =>
            GetPythonSymbolLookups().HeaderSymbolsByLine;

        internal IReadOnlyDictionary<string, List<PowerShellReferenceExtractor.SplatAssignment>> GetPowerShellSplatAssignments()
        {
            if (!powershellSplatAssignmentsResolved)
            {
                powershellSplatAssignments = PowerShellReferenceExtractor.BuildSplatAssignments(preparedLines);
                powershellSplatAssignmentsResolved = true;
            }

            return powershellSplatAssignments!;
        }

        internal List<(int StartLine, int StartColumn, int EndLine, int EndColumn, SymbolRecord Container, SymbolRecord Owner)> GetRecordPrimaryCtorRanges()
        {
            if (!recordPrimaryCtorRangesResolved)
            {
                recordPrimaryCtorRanges = BuildCSharpPrimaryCtorContainers(language, symbols, structuralLines);
                recordPrimaryCtorRangesResolved = true;
            }

            return recordPrimaryCtorRanges!;
        }

        internal (
            IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> ByContainingType,
            IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> ByFunctionStartLine) GetCSharpValueReceiverLookups()
        {
            if (!csharpValueReceiverLookupsResolved)
            {
                csharpValueReceiverLookups = BuildCSharpValueReceiverNameLookups(
                    language,
                    symbols,
                    structuralLines,
                    csharpKnownTypeNames,
                    csharpUsingAliases);
                csharpValueReceiverLookupsResolved = true;
            }

            return csharpValueReceiverLookups!.Value;
        }

        internal IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> GetCSharpValueReceiverNames() =>
            GetCSharpValueReceiverLookups().ByContainingType;

        internal IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> GetCSharpFunctionValueReceiverNames() =>
            GetCSharpValueReceiverLookups().ByFunctionStartLine;

        internal string ResolveCSharpUsingAliasReferenceName(string referenceName, int lineNumber)
        {
            if (language != "csharp")
                return referenceName;

            var alias = FindActiveCSharpUsingAlias(referenceName, lineNumber);
            if (alias == null)
                return referenceName;

            var targetName = GetLastQualifiedSegment(
                TrimLeadingCSharpGlobalQualifier(alias.TargetQualifiedName));
            return string.IsNullOrWhiteSpace(targetName) ? referenceName : targetName;
        }

        internal void ApplyCSharpUsingAliasReferenceNames(List<ReferenceRecord> references)
        {
            if (language != "csharp")
                return;

            var aliasNameChanged = false;
            foreach (var reference in references)
            {
                if (reference.ReferenceKind is not ("instantiate" or "attribute"))
                    continue;
                if (reference.Line <= 0 || reference.Line > lines.Length || reference.Column <= 0)
                    continue;
                if (!IsUnqualifiedCSharpTokenAtColumn(reference.Line, reference.Column, reference.SymbolName))
                    continue;

                var alias = FindActiveCSharpUsingAlias(reference.SymbolName, reference.Line);
                if (alias == null)
                    continue;

                var lexicalName = reference.SymbolName;
                var normalizedTarget = TrimLeadingCSharpGlobalQualifier(alias.TargetQualifiedName);
                var resolvedName = GetLastQualifiedSegment(normalizedTarget);
                if (string.IsNullOrWhiteSpace(resolvedName))
                    continue;

                var nameChanged = !string.Equals(
                    resolvedName,
                    reference.SymbolName,
                    StringComparison.Ordinal);
                if (!nameChanged && reference.ReferenceKind != "instantiate")
                    continue;

                if (reference.ReferenceKind == "instantiate")
                {
                    reference.TargetQualifier = GetCSharpUsingAliasTargetQualifier(
                        normalizedTarget,
                        resolvedName);
                    reference.Context = BuildCSharpUsingAliasInvocationContext(
                        reference,
                        lexicalName);
                }
                if (nameChanged)
                {
                    reference.SymbolName = resolvedName;
                    reference.IsSelfReference = IsSameReferenceName(reference.ContainerName, resolvedName);
                    aliasNameChanged = true;
                }
            }

            if (aliasNameChanged)
                CompactCSharpUsingAliasReferences(references, language);
        }

        string BuildCSharpUsingAliasInvocationContext(
            ReferenceRecord reference,
            string lexicalName)
        {
            const int maxLineCount = 32;
            const int maxContextLength = 4096;
            var firstLine = lines[reference.Line - 1];
            if (firstLine.Length > maxContextLength)
                return reference.Context;

            var context = new System.Text.StringBuilder(firstLine);
            for (var lineOffset = 0;
                 lineOffset < maxLineCount && reference.Line - 1 + lineOffset < lines.Length;
                 lineOffset++)
            {
                if (lineOffset > 0)
                {
                    var nextLine = lines[reference.Line - 1 + lineOffset];
                    if (context.Length + 1 + nextLine.Length > maxContextLength)
                        break;
                    context.Append('\n');
                    context.Append(nextLine);
                }

                var candidate = context.ToString();
                if (candidate.Contains("\"\"\"", StringComparison.Ordinal)
                    || CSharpTypeReferenceArity.HasCompleteInvocationArgumentList(
                        candidate,
                        lexicalName,
                        reference.Column,
                        reference.SpanLength))
                {
                    return candidate;
                }
            }

            return firstLine;
        }

        static string? GetCSharpUsingAliasTargetQualifier(
            string normalizedTarget,
            string targetName)
        {
            if (string.IsNullOrWhiteSpace(normalizedTarget)
                || string.IsNullOrWhiteSpace(targetName)
                || normalizedTarget.Length <= targetName.Length)
            {
                return null;
            }

            var separatorIndex = normalizedTarget.Length - targetName.Length - 1;
            return separatorIndex >= 0 && normalizedTarget[separatorIndex] == '.'
                ? normalizedTarget[..separatorIndex]
                : null;
        }

        bool IsUnqualifiedCSharpTokenAtColumn(int lineNumber, int column, string symbolName)
        {
            if (lineNumber <= 0
                || lineNumber > lines.Length
                || column <= 0
                || string.IsNullOrWhiteSpace(symbolName))
                return false;

            var line = lines[lineNumber - 1];
            var tokenStart = column - 1;
            if (tokenStart >= line.Length)
                return false;

            var tokenNameStart = tokenStart;
            if (line[tokenNameStart] == '@')
                tokenNameStart++;

            if (tokenNameStart + symbolName.Length > line.Length)
                return false;
            if (!line.AsSpan(tokenNameStart, symbolName.Length).Equals(symbolName, StringComparison.Ordinal))
                return false;

            var previousIndex = tokenStart - 1;
            var nextIndex = tokenNameStart + symbolName.Length;
            var hasQualifiedPrefix = HasCSharpQualifiedSeparatorBeforeToken(line, tokenStart)
                || (previousIndex >= 0 && IsCSharpIdentifierPart(line[previousIndex]));
            var hasIdentifierSuffix = nextIndex < line.Length && IsCSharpIdentifierPart(line[nextIndex]);
            return !hasQualifiedPrefix && !hasIdentifierSuffix;
        }

        bool HasActiveCSharpUsingNamespace(string targetQualifiedName, int lineNumber)
        {
            var normalizedTarget = NormalizeCSharpBclRegexQualifiedName(targetQualifiedName);
            for (var importIndex = csharpUsingNamespaces.Count - 1; importIndex >= 0; importIndex--)
            {
                var import = csharpUsingNamespaces[importIndex];
                if (import.Line > lineNumber
                    || lineNumber < import.ScopeStartLine
                    || lineNumber > import.ScopeEndLine)
                {
                    continue;
                }

                if (string.Equals(NormalizeCSharpBclRegexQualifiedName(import.TargetQualifiedName), normalizedTarget, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        CSharpUsingAliasRecord? FindActiveCSharpUsingAlias(string aliasName, int lineNumber)
        {
            for (var aliasIndex = csharpUsingAliases.Count - 1; aliasIndex >= 0; aliasIndex--)
            {
                var alias = csharpUsingAliases[aliasIndex];
                if (alias.Line > lineNumber
                    || lineNumber < alias.ScopeStartLine
                    || lineNumber > alias.ScopeEndLine
                    || !string.Equals(alias.AliasName, aliasName, StringComparison.Ordinal))
                {
                    continue;
                }

                return alias;
            }

            return null;
        }

        internal void EmitCSharpBclRegexWithoutTimeoutReferences(List<ReferenceRecord> references, ReferenceDedupeSet seen)
        {
            if (language != "csharp")
                return;

            var referenceCount = references.Count;
            for (var referenceIndex = 0; referenceIndex < referenceCount; referenceIndex++)
            {
                var reference = references[referenceIndex];
                if (reference.ReferenceKind != "instantiate"
                    || !string.Equals(reference.SymbolName, "Regex", StringComparison.Ordinal)
                    || reference.Line <= 0
                    || reference.Line > lines.Length
                    || reference.Column <= 0
                    || !IsCSharpBclRegexInstantiateReference(reference)
                    || !IsCSharpRegexConstructorWithoutTimeout(reference.Line, reference.Column, reference.SymbolName))
                {
                    continue;
                }

                var dedupeKey = CreateReferenceDedupeKey(
                    reference.FileId,
                    language,
                    reference.Line,
                    reference.Column,
                    "bcl_regex_without_timeout",
                    reference.SymbolName,
                    reference.ContainerKind,
                    reference.ContainerName);
                if (!seen.Add(dedupeKey))
                    continue;

                if (!TryAddReference(references, new ReferenceRecord
                {
                    FileId = reference.FileId,
                    SymbolName = reference.SymbolName,
                    ReferenceKind = "bcl_regex_without_timeout",
                    Line = reference.Line,
                    Column = reference.Column,
                    SpanLength = reference.SpanLength,
                    Context = reference.Context,
                    ContainerKind = reference.ContainerKind,
                    ContainerName = reference.ContainerName,
                    IsSelfReference = reference.IsSelfReference,
                }))
                {
                    return;
                }
            }
        }

        bool IsCSharpBclRegexInstantiateReference(ReferenceRecord reference)
        {
            var line = lines[reference.Line - 1];
            if (!TryGetCSharpIdentifierAtColumn(line, reference.Column, out _, out _, out var tokenName))
                return false;

            if (TryGetCSharpQualifiedPrefixAtColumn(line, reference.Column, tokenName, out var prefix)
                && string.Equals(NormalizeCSharpBclRegexQualifiedName($"{prefix}.{tokenName}"), "System.Text.RegularExpressions.Regex", StringComparison.Ordinal))
            {
                return true;
            }

            var alias = FindActiveCSharpUsingAlias(tokenName, reference.Line);
            if (alias != null)
            {
                return string.Equals(
                    NormalizeCSharpBclRegexQualifiedName(alias.TargetQualifiedName),
                    "System.Text.RegularExpressions.Regex",
                    StringComparison.Ordinal);
            }

            return string.Equals(tokenName, "Regex", StringComparison.Ordinal)
                && !HasActiveSameFileCSharpTypeCandidate(tokenName, reference.Line)
                && HasActiveCSharpUsingNamespace("System.Text.RegularExpressions", reference.Line);
        }

        bool IsCSharpRegexConstructorWithoutTimeout(int lineNumber, int column, string symbolName)
        {
            var line = lines[lineNumber - 1];
            _ = symbolName;
            if (!TryGetCSharpIdentifierAtColumn(line, column, out _, out var tokenNameStart, out var tokenName))
                return false;

            var cursor = tokenNameStart + tokenName.Length;
            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;
            if (cursor >= line.Length || line[cursor] != '(')
                return false;

            if (!TryCollectCSharpInvocationArguments(lines, lineNumber - 1, cursor, out var args))
                return false;

            var argCount = CountTopLevelCSharpArguments(args.AsSpan(), out var hasNamedMatchTimeout);
            return argCount is 1 or 2 && !hasNamedMatchTimeout;
        }
    }
}
