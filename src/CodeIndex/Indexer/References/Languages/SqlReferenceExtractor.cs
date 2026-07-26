using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private readonly record struct CteBodySpan(int StartIndex, int EndIndexExclusive);
    private readonly record struct DefinitionLeafPattern(string LeafName, string Pattern);
    public static State CreateState() => new();

    public static void AddDefinitionNameAliases(HashSet<string> names, SymbolRecord symbol)
    {
        var leafName = SqlNameResolver.GetLeafName(symbol.Name);
        if (!string.IsNullOrWhiteSpace(leafName))
            names.Add(leafName);
    }

    public static Dictionary<int, List<DefinitionLeafSpan>>? BuildDefinitionLeafSpansByLine(
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        Dictionary<int, List<DefinitionLeafSpan>>? spansByLine = null;
        Dictionary<string, DefinitionLeafPattern>? patternCache = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Line < 1 || symbol.Line > lines.Length)
                continue;
            patternCache ??= new Dictionary<string, DefinitionLeafPattern>(Math.Min(symbols.Count, 64), StringComparer.Ordinal);
            if (!TryFindDefinitionLeafSpan(lines[symbol.Line - 1], symbol.Name, patternCache, out var span))
                continue;

            spansByLine ??= new Dictionary<int, List<DefinitionLeafSpan>>(Math.Min(symbols.Count, 64));
            if (!spansByLine.TryGetValue(symbol.Line, out var spans))
            {
                spans = new List<DefinitionLeafSpan>(1);
                spansByLine[symbol.Line] = spans;
            }

            spans.Add(span);
        }

        return spansByLine;
    }

    public static HashSet<(int LineNumber, int ColumnIndex)>? BuildWindowFunctionCallSiteSuppressions(string[] lines)
    {
        if (lines.Length == 0)
            return null;

        var hasWindowClauseKeyword = false;
        long joinedLength = lines.Length - 1;
        foreach (var line in lines)
        {
            joinedLength = Math.Min((long)int.MaxValue, joinedLength + line.Length);
            if (line.IndexOf("OVER", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            hasWindowClauseKeyword = true;
            break;
        }

        if (!hasWindowClauseKeyword)
            return null;

        var lineStarts = new int[lines.Length];
        var textBuilder = new StringBuilder((int)joinedLength);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
                textBuilder.Append('\n');
            lineStarts[lineIndex] = textBuilder.Length;
            textBuilder.Append(lines[lineIndex]);
        }

        var text = textBuilder.ToString();
        HashSet<(int LineNumber, int ColumnIndex)>? suppressed = null;
        var searchStart = 0;
        while (TryFindNextWindowClause(
            text,
            searchStart,
            out var overKeywordIndex,
            out _,
            out var closeParenIndex))
        {
            if (TryFindWindowFunctionNameIndex(text, overKeywordIndex, out var functionNameIndex)
                && TryMapJoinedOffsetToLine(lineStarts, text, functionNameIndex, out var lineNumber, out var columnIndex))
            {
                (suppressed ??= []).Add((lineNumber, columnIndex));
            }

            searchStart = closeParenIndex + 1;
        }

        return suppressed;
    }

    public static bool ShouldSuppressDefinitionCall(
        IReadOnlyList<DefinitionLeafSpan>? definitionLeafSpans,
        string resolvedName,
        int callIndex)
    {
        if (definitionLeafSpans == null)
            return false;

        foreach (var span in definitionLeafSpans)
        {
            if (callIndex >= span.StartIndex
                && callIndex < span.EndIndexExclusive
                && string.Equals(span.LeafName, resolvedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static HashSet<int> Emit(
        string structuralLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        State state,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        var suppressedCallIndices = new HashSet<int>();
        var lineFragment = PrepareLineForIdentifierScan(
            structuralLine,
            state.IdentifierScanState,
            state.StatementPrefix,
            out var lineEndedByLineComment,
            out var nextIdentifierScanState);
        state.IdentifierScanState = nextIdentifierScanState;
        if (string.IsNullOrWhiteSpace(lineFragment))
            return suppressedCallIndices;

        if (ShouldFlushTempObjectPrefixAtLineBoundary(state.StatementPrefix, lineFragment))
        {
            CollectTempObjectNamesFromStatement(state.StatementPrefix, state.EstablishedTempObjectNames);
            state.StatementPrefix = string.Empty;
        }

        var combinedLine = CombineStatementPrefix(state.StatementPrefix, lineFragment, out var lineOffset);
        int statementStart = 0;

        while (true)
        {
            int terminatorIndex = FindStatementTerminator(combinedLine, statementStart);
            int statementEnd = terminatorIndex >= 0 ? terminatorIndex + 1 : combinedLine.Length;
            var statement = combinedLine[statementStart..statementEnd];
            int statementLineOffset = Math.Max(0, lineOffset - statementStart);

            if (!string.IsNullOrWhiteSpace(statement))
            {
                EmitStatementReferences(
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    state.EstablishedTempObjectNames,
                    suppressedCallIndices,
                    resolveContainerForCall,
                    shouldIgnoreName,
                    shouldSuppressDefinitionCall);
            }

            if (terminatorIndex < 0)
                break;

            CollectTempObjectNamesFromStatement(statement, state.EstablishedTempObjectNames);
            statementStart = terminatorIndex + 1;
            while (statementStart < combinedLine.Length && char.IsWhiteSpace(combinedLine[statementStart]))
                statementStart++;
        }

        state.StatementPrefix = AdvanceStatementPrefix(combinedLine, statementStart, lineEndedByLineComment);
        return suppressedCallIndices;
    }

    private static IReadOnlyList<CteBodySpan>? PrepareStatementReferenceState(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        HashSet<int> suppressedCallIndices)
    {
        var hasDeleteKeyword = statement.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasMergeKeyword = statement.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasUsingKeyword = statement.IndexOf("USING", StringComparison.OrdinalIgnoreCase) >= 0;
        var cteBodySpans = FindCteBodySpans(statement);
        HashSet<int>? usingSourceIndices = null;
        if (hasMergeKeyword && hasUsingKeyword)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(MergeUsingSourceRegex, statement))
            {
                if (IsInsideDoubleQuotedRegion(statement, match.Index))
                    continue;
                var nameGroup = match.Groups["name"];
                if (nameGroup.Index < statementLineOffset)
                    continue;

                (usingSourceIndices ??= []).Add(nameGroup.Index);
            }
        }

        if (hasDeleteKeyword && hasUsingKeyword)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(DeleteUsingSourceRegex, statement))
            {
                if (IsInsideDoubleQuotedRegion(statement, match.Index))
                    continue;

                foreach (Capture capture in match.Groups["name"].Captures)
                {
                    if (capture.Index < statementLineOffset)
                        continue;

                    (usingSourceIndices ??= []).Add(capture.Index);
                }
            }
        }

        if (statement.IndexOf("TOP", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(TopCallSuppressionRegex, statement))
            {
                var nameGroup = match.Groups["name"];
                if (nameGroup.Index < statementLineOffset)
                    continue;

                suppressedCallIndices.Add(nameGroup.Index + statementStart - lineOffset);
            }
        }

        if (statement.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("INDEX", StringComparison.OrdinalIgnoreCase) >= 0
            && hasUsingKeyword)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(AccessMethodCallSuppressionRegex, statement))
            {
                if (IsInsideDoubleQuotedRegion(statement, match.Index))
                    continue;
                var nameGroup = match.Groups["name"];
                if (nameGroup.Index < statementLineOffset)
                    continue;
                if (usingSourceIndices != null && usingSourceIndices.Contains(nameGroup.Index))
                    continue;

                suppressedCallIndices.Add(nameGroup.Index + statementStart - lineOffset);
            }
        }

        return cteBodySpans;
    }

    private static void EmitIndexTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        if (statement.IndexOf("INDEX", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        if (statement.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitCreateIndexTargetReferences(
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                suppressedCallIndices);
        }

        if (statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("DROP", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitAlterAndDropIndexTargetReferences(
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }
    }

    private static void EmitCreateIndexTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        if (statement.IndexOf("ON", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        EmitMultiTargetReferences(
            BoundedRegex.EnumerateMatches(CreateIndexOnTargetRegex, statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        if (statement.IndexOf("XML", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateSpecialXmlIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                suppressedCallIndices);
        }

        if (statement.IndexOf("COLUMNSTORE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateClusteredColumnstoreIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                suppressedCallIndices);
        }

        if (statement.IndexOf("HASH", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateHashIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                suppressedCallIndices);
        }

        if (statement.IndexOf("FULLTEXT", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateFullTextIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                suppressedCallIndices);
        }
    }

    private static void EmitAlterAndDropIndexTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        if (statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);

            if (statement.IndexOf("FULLTEXT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                EmitMultiTargetReferences(
                    BoundedRegex.EnumerateMatches(AlterFullTextIndexOnTargetRegex, statement),
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    resolveContainerForCall,
                    shouldIgnoreName);
            }
        }

        if (statement.IndexOf("DROP", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        if (statement.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        EmitMultiTargetReferences(
            BoundedRegex.EnumerateMatches(DropIndexLegacyTargetRegex, statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        if (statement.IndexOf("FULLTEXT", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropFullTextIndexOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }
    }

    private static void EmitObjectLifecycleTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        var hasCreateKeyword = statement.IndexOf("CREATE", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasAlterKeyword = statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasDropKeyword = statement.IndexOf("DROP", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasOnKeyword = statement.IndexOf("ON", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasTriggerKeyword = statement.IndexOf("TRIGGER", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasSecurityKeyword = statement.IndexOf("SECURITY", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasPolicyKeyword = statement.IndexOf("POLICY", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasPredicateKeyword = statement.IndexOf("PREDICATE", StringComparison.OrdinalIgnoreCase) >= 0;

        if (hasCreateKeyword && hasTriggerKeyword && hasOnKeyword)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateTriggerOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        if (hasCreateKeyword
            && hasSecurityKeyword
            && hasPolicyKeyword
            && hasPredicateKeyword
            && hasOnKeyword)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateSecurityPolicyPredicateTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        if (hasAlterKeyword
            && hasSecurityKeyword
            && hasPolicyKeyword
            && hasPredicateKeyword
            && hasOnKeyword)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterSecurityPolicyPredicateTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        if ((statement.IndexOf("ENABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || statement.IndexOf("DISABLE", StringComparison.OrdinalIgnoreCase) >= 0)
            && hasTriggerKeyword
            && hasOnKeyword)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(ToggleTriggerOnTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        if (statement.IndexOf("REFERENCES", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(ForeignKeyReferencesTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                suppressedCallIndices);
        }

        if (hasCreateKeyword
            && statement.IndexOf("SYNONYM", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("FOR", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(CreateSynonymForTargetRegex, statement),
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        if (hasDropKeyword)
        {
            EmitDropObjectTargetReferences(
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName);
        }
    }

    private static void EmitMergeOnColumnReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(MergeOnClauseRegex, statement, references))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var bodyGroup = match.Groups["body"];
            EmitQualifiedColumnReferences(
                bodyGroup.Value,
                bodyGroup.Index,
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                "join_condition_reference");
        }
    }

    private static void EmitMergeUpdateColumnReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(MergeUpdateSetActionRegex, statement, references))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var bodyGroup = match.Groups["body"];
            foreach (var segment in SplitTopLevelCommaSegments(bodyGroup.Value, bodyGroup.Index))
            {
                var equalsIndex = IndexOfTopLevelChar(segment.Text, '=');
                if (equalsIndex <= 0)
                    continue;

                EmitMergeColumnReference(
                    segment.Text[..equalsIndex],
                    segment.StartIndex,
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    resolveContainerForCall,
                    shouldIgnoreName,
                    "column_reference");
            }

            EmitQualifiedColumnReferences(
                bodyGroup.Value,
                bodyGroup.Index,
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                resolveContainerForCall,
                shouldIgnoreName,
                "column_reference");
        }
    }

    private static void EmitMergeInsertColumnReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(MergeInsertActionRegex, statement, references))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var columnsGroup = match.Groups["columns"];
            if (columnsGroup.Success)
            {
                var innerStart = columnsGroup.Index + 1;
                var inner = columnsGroup.Value.Length >= 2
                    ? columnsGroup.Value[1..^1]
                    : string.Empty;
                foreach (var segment in SplitTopLevelCommaSegments(inner, innerStart))
                {
                    EmitMergeColumnReference(
                        segment.Text,
                        segment.StartIndex,
                        statement,
                        statementStart,
                        statementLineOffset,
                        lineOffset,
                        context,
                        lineNumber,
                        references,
                        seen,
                        fileId,
                        resolveContainerForCall,
                        shouldIgnoreName,
                        "column_reference");
                }
            }

            var valuesGroup = match.Groups["values"];
            if (valuesGroup.Success)
            {
                EmitQualifiedColumnReferences(
                    valuesGroup.Value,
                    valuesGroup.Index,
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    resolveContainerForCall,
                    shouldIgnoreName,
                    "column_reference");
            }
        }
    }

}
