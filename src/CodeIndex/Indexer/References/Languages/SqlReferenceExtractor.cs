using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private readonly record struct CteBodySpan(int StartIndex, int EndIndexExclusive);
    public static State CreateState() => new();

    public static void AddDefinitionNameAliases(HashSet<string> names, SymbolRecord symbol)
    {
        var leafName = SqlNameResolver.GetLeafName(symbol.Name);
        if (!string.IsNullOrWhiteSpace(leafName))
            names.Add(leafName);
    }

    public static Dictionary<int, List<DefinitionLeafSpan>> BuildDefinitionLeafSpansByLine(
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var spansByLine = new Dictionary<int, List<DefinitionLeafSpan>>();
        foreach (var symbol in symbols)
        {
            if (symbol.Line < 1 || symbol.Line > lines.Length)
                continue;
            if (!TryFindDefinitionLeafSpan(lines[symbol.Line - 1], symbol.Name, out var span))
                continue;

            if (!spansByLine.TryGetValue(symbol.Line, out var spans))
            {
                spans = [];
                spansByLine[symbol.Line] = spans;
            }

            spans.Add(span);
        }

        return spansByLine;
    }

    public static HashSet<(int LineNumber, int ColumnIndex)> BuildWindowFunctionCallSiteSuppressions(string[] lines)
    {
        var suppressed = new HashSet<(int LineNumber, int ColumnIndex)>();
        if (lines.Length == 0)
            return suppressed;

        var lineStarts = new int[lines.Length];
        var textBuilder = new StringBuilder();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
                textBuilder.Append('\n');
            lineStarts[lineIndex] = textBuilder.Length;
            textBuilder.Append(lines[lineIndex]);
        }

        var text = textBuilder.ToString();
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
                suppressed.Add((lineNumber, columnIndex));
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
        HashSet<string> seen,
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

    private static void EmitStatementReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        var cteBodySpans = PrepareStatementReferenceState(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            suppressedCallIndices);

        EmitWindowClauseReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitProcedureCalls(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            resolveContainerForCall,
            shouldIgnoreName,
            shouldSuppressDefinitionCall);

        EmitSystemVariableReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall);

        EmitGeneratedColumnDependencyReferences(
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

        EmitSourceCaptureReferences(
            FromSourceListRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName,
            cteBodySpans);

        EmitSourceCaptureReferences(
            SourceReferenceRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName,
            cteBodySpans);

        EmitMergeUsingReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitSourceCaptureReferences(
            DeleteUsingSourceRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DeleteTargetWithoutFromRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            OutputIntoTargetRegex.Matches(statement),
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

        EmitSelectIntoTargetReferences(
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

        EmitIndexTargetReferences(
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

        EmitObjectLifecycleTargetReferences(
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

        EmitAlterAndMaintenanceTargetReferences(
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

    private static IReadOnlyList<CteBodySpan>? PrepareStatementReferenceState(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        HashSet<int> suppressedCallIndices)
    {
        var cteBodySpans = FindCteBodySpans(statement);
        HashSet<int>? usingSourceIndices = null;
        foreach (Match match in MergeUsingSourceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;

            (usingSourceIndices ??= []).Add(nameGroup.Index);
        }

        foreach (Match match in DeleteUsingSourceRegex.Matches(statement))
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

        foreach (Match match in TopCallSuppressionRegex.Matches(statement))
        {
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;

            suppressedCallIndices.Add(nameGroup.Index + statementStart - lineOffset);
        }

        foreach (Match match in AccessMethodCallSuppressionRegex.Matches(statement))
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
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
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

    private static void EmitCreateIndexTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        EmitMultiTargetReferences(
            CreateIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateSpecialXmlIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateClusteredColumnstoreIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateHashIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateFullTextIndexOnTargetRegex.Matches(statement),
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

    private static void EmitAlterAndDropIndexTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        EmitMultiTargetReferences(
            AlterIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterFullTextIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropIndexOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropIndexLegacyTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropFullTextIndexOnTargetRegex.Matches(statement),
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

    private static void EmitObjectLifecycleTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        EmitMultiTargetReferences(
            CreateTriggerOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateSecurityPolicyPredicateTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterSecurityPolicyPredicateTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            ToggleTriggerOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            ForeignKeyReferencesTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateSynonymForTargetRegex.Matches(statement),
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

    private static void EmitDropObjectTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        EmitMultiTargetReferences(
            DropSynonymTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropViewTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropProcedureTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropFunctionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropTriggerTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropSequenceTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropTypeTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropRuleTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropDefaultTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropAggregateTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropSecurityPolicyTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropFullTextCatalogTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropPartitionSchemeTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropPartitionFunctionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropXmlSchemaCollectionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropAssemblyTargetRegex.Matches(statement),
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

    private static void EmitAlterAndMaintenanceTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        EmitAlterObjectTargetReferences(
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

        EmitMaintenanceTargetReferences(
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

    private static void EmitMaintenanceTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int> suppressedCallIndices)
    {
        EmitMultiTargetReferences(
            ObjectPermissionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterAuthorizationObjectTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterAuthorizationBareTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            UpdateStatisticsTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            CreateStatisticsOnTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            DropStatisticsTargetRegex.Matches(statement),
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

        EmitTargetReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropTableTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            TruncateTargetRegex.Matches(statement),
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

    private static void EmitAlterObjectTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        EmitMultiTargetReferences(
            AlterViewTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterProcedureTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterFunctionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterTriggerTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterSequenceTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterSecurityPolicyTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterFullTextCatalogTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterPartitionFunctionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterPartitionSchemeTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterXmlSchemaCollectionTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterAssemblyTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterSchemaTransferTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterTableSwitchTargetRegex.Matches(statement),
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

        EmitMultiTargetReferences(
            AlterTableSystemVersioningHistoryTargetRegex.Matches(statement),
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

    private static void EmitWindowClauseReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        int searchStart = 0;
        while (TryFindNextWindowClause(
            statement,
            searchStart,
            out var overKeywordIndex,
            out var openParenIndex,
            out var closeParenIndex))
        {
            if (TryFindWindowFunctionNameIndex(statement, overKeywordIndex, out var functionNameIndex)
                && functionNameIndex >= statementLineOffset)
            {
                suppressedCallIndices.Add(functionNameIndex + statementStart - lineOffset);
            }

            var bodyStart = openParenIndex + 1;
            if (closeParenIndex > statementLineOffset)
            {
                EmitWindowClauseColumnReferences(
                    statement,
                    bodyStart,
                    closeParenIndex,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    suppressedCallIndices,
                    resolveContainerForCall,
                    shouldIgnoreName);
            }

            searchStart = closeParenIndex + 1;
        }
    }

    private static bool TryFindNextWindowClause(
        string statement,
        int searchStart,
        out int overKeywordIndex,
        out int openParenIndex,
        out int closeParenIndex)
    {
        overKeywordIndex = -1;
        openParenIndex = -1;
        closeParenIndex = -1;

        for (int i = searchStart; i < statement.Length;)
        {
            if (!IsKeywordAt(statement, i, "OVER"))
            {
                i++;
                continue;
            }

            if (IsInsideDoubleQuotedRegion(statement, i))
            {
                i += "OVER".Length;
                continue;
            }

            int probe = SkipWhitespaceAhead(statement, i + "OVER".Length);
            if (probe >= statement.Length || statement[probe] != '(')
            {
                i += "OVER".Length;
                continue;
            }

            int close = FindMatchingParen(statement, probe);
            if (close < 0)
            {
                i += "OVER".Length;
                continue;
            }

            overKeywordIndex = i;
            openParenIndex = probe;
            closeParenIndex = close;
            return true;
        }

        return false;
    }

    private static bool TryFindWindowFunctionNameIndex(string statement, int overKeywordIndex, out int functionNameIndex)
    {
        functionNameIndex = -1;
        int probe = overKeywordIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(statement[probe]))
            probe--;
        if (probe < 0 || statement[probe] != ')')
            return false;

        int openParen = FindMatchingOpenParen(statement, probe);
        if (openParen <= 0)
            return false;

        probe = openParen - 1;
        while (probe >= 0 && char.IsWhiteSpace(statement[probe]))
            probe--;
        int nameEnd = probe;
        while (probe >= 0 && IsSqlIdentifierPart(statement[probe]))
            probe--;
        int nameStart = probe + 1;
        if (nameStart > nameEnd)
            return false;

        functionNameIndex = nameStart;
        return true;
    }

    private static bool TryMapJoinedOffsetToLine(int[] lineStarts, string text, int offset, out int lineNumber, out int columnIndex)
    {
        lineNumber = 0;
        columnIndex = 0;
        if (offset < 0 || offset >= text.Length)
            return false;

        var lineIndex = Array.BinarySearch(lineStarts, offset);
        if (lineIndex < 0)
            lineIndex = ~lineIndex - 1;
        if (lineIndex < 0 || lineIndex >= lineStarts.Length)
            return false;

        lineNumber = lineIndex + 1;
        columnIndex = offset - lineStarts[lineIndex];
        return true;
    }

    private static void EmitWindowClauseColumnReferences(
        string statement,
        int bodyStart,
        int bodyEnd,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match keywordMatch in WindowFrameKeywordRegex.Matches(statement))
        {
            if (keywordMatch.Index >= bodyStart && keywordMatch.Index < bodyEnd && keywordMatch.Index >= statementLineOffset)
                suppressedCallIndices.Add(keywordMatch.Index + statementStart - lineOffset);
        }

        foreach (var (start, end) in EnumerateWindowColumnListSpans(statement, bodyStart, bodyEnd))
        {
            foreach (Match match in WindowClauseColumnRegex.Matches(statement))
            {
                var nameGroup = match.Groups["name"];
                if (!nameGroup.Success || nameGroup.Index < start || nameGroup.Index >= end || nameGroup.Index < statementLineOffset)
                    continue;
                if (IsSqlWindowKeyword(nameGroup.Value))
                    continue;
                if (IsImmediatelyFollowedByOpenParen(statement, nameGroup.Index + nameGroup.Length))
                    continue;

                NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                if (!wasQuoted && shouldIgnoreName(resolvedName))
                    continue;

                int nameColumn = nameIndex + statementStart - lineOffset;
                var container = resolveContainerForCall(nameGroup.Index);
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    resolvedName,
                    nameColumn,
                    "column_reference",
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    private static IEnumerable<(int Start, int End)> EnumerateWindowColumnListSpans(string statement, int bodyStart, int bodyEnd)
    {
        int position = bodyStart;
        while (position < bodyEnd)
        {
            if (IsKeywordAt(statement, position, "PARTITION"))
            {
                int byIndex = SkipWhitespaceAhead(statement, position + "PARTITION".Length);
                if (IsKeywordAt(statement, byIndex, "BY"))
                {
                    int start = SkipWhitespaceAhead(statement, byIndex + "BY".Length);
                    int end = FindWindowListEnd(statement, start, bodyEnd);
                    yield return (start, end);
                    position = end;
                    continue;
                }
            }

            if (IsKeywordAt(statement, position, "ORDER"))
            {
                int byIndex = SkipWhitespaceAhead(statement, position + "ORDER".Length);
                if (IsKeywordAt(statement, byIndex, "BY"))
                {
                    int start = SkipWhitespaceAhead(statement, byIndex + "BY".Length);
                    int end = FindWindowListEnd(statement, start, bodyEnd);
                    yield return (start, end);
                    position = end;
                    continue;
                }
            }

            position++;
        }
    }

    private static int FindWindowListEnd(string statement, int start, int bodyEnd)
    {
        for (int i = start; i < bodyEnd; i++)
        {
            if (IsKeywordAt(statement, i, "PARTITION")
                || IsKeywordAt(statement, i, "ORDER")
                || IsKeywordAt(statement, i, "ROWS")
                || IsKeywordAt(statement, i, "RANGE")
                || IsKeywordAt(statement, i, "GROUPS"))
            {
                return i;
            }
        }

        return bodyEnd;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;
        if (string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (index > 0 && IsSqlIdentifierPart(text[index - 1]))
            return false;
        var after = index + keyword.Length;
        return after >= text.Length || !IsSqlIdentifierPart(text[after]);
    }

    private static bool IsSqlIdentifierPart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#';
    }

    private static bool IsSqlWindowKeyword(string value)
    {
        return string.Equals(value, "PARTITION", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ORDER", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "BY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ASC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "DESC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "NULLS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "FIRST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "LAST", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImmediatelyFollowedByOpenParen(string text, int index)
    {
        int probe = SkipWhitespaceAhead(text, index);
        return probe < text.Length && text[probe] == '(';
    }

    private static void EmitProcedureCalls(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        foreach (Match match in ProcCallRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;

            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;
            if (shouldSuppressDefinitionCall(resolvedName, nameIndex))
                continue;
            if (!wasQuoted
                && resolvedName.StartsWith("#", StringComparison.Ordinal)
                && !establishedTempObjectNames.Contains(resolvedName))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "call", context, lineNumber, container);
        }
    }

    private static void EmitSystemVariableReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in SystemVariableReferenceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;

            var resolvedName = SqlSymbolNameNormalizer.Normalize(nameGroup.Value);
            var nameColumn = nameGroup.Index + statementStart - lineOffset;
            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "system_variable", context, lineNumber, container);
        }
    }

    private static void EmitSourceCaptureReferences(
        MatchCollection matches,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        IReadOnlyList<CteBodySpan>? cteBodySpans = null)
    {
        if (RevokePermissionStatementRegex.IsMatch(statement))
            return;

        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            foreach (Capture capture in match.Groups["name"].Captures)
            {
                EmitSourceReference(
                    capture.Value,
                    capture.Index,
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    establishedTempObjectNames,
                    suppressedCallIndices,
                    resolveContainerForCall,
                    shouldIgnoreName,
                    GetSourceReferenceKind(capture.Index, cteBodySpans));
            }
        }
    }

    private static void EmitMergeUsingReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in MergeUsingSourceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            EmitSourceReference(
                nameGroup.Value,
                nameGroup.Index,
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                establishedTempObjectNames,
                suppressedCallIndices,
                resolveContainerForCall,
                shouldIgnoreName);
        }

        EmitMergeActionColumnReferences(
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

    private static void EmitMergeActionColumnReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        EmitMergeOnColumnReferences(
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

        EmitMergeUpdateColumnReferences(
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

        EmitMergeInsertColumnReferences(
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

    private static void EmitMergeOnColumnReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in MergeOnClauseRegex.Matches(statement))
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
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in MergeUpdateSetActionRegex.Matches(statement))
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
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in MergeInsertActionRegex.Matches(statement))
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

    private readonly record struct TextSegment(string Text, int StartIndex);

    private static void EmitQualifiedColumnReferences(
        string text,
        int textStart,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        string referenceKind)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(QualifiedColumnReferenceRegex, text))
        {
            if (IsInsideDoubleQuotedRegion(text, match.Index))
                continue;

            var nameGroup = match.Groups["name"];
            EmitMergeColumnReference(
                nameGroup.Value,
                textStart + nameGroup.Index,
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
                referenceKind);
        }
    }

    private static void EmitMergeColumnReference(
        string rawName,
        int rawIndex,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        string referenceKind)
    {
        var trimmedStart = 0;
        while (trimmedStart < rawName.Length && char.IsWhiteSpace(rawName[trimmedStart]))
            trimmedStart++;
        var trimmedEnd = rawName.Length;
        while (trimmedEnd > trimmedStart && char.IsWhiteSpace(rawName[trimmedEnd - 1]))
            trimmedEnd--;
        if (trimmedStart >= trimmedEnd)
            return;

        rawName = rawName[trimmedStart..trimmedEnd];
        rawIndex += trimmedStart;
        var leafIndex = FindQualifiedIdentifierLeafIndex(rawName);
        rawIndex += leafIndex;
        rawName = rawName[leafIndex..].TrimStart();

        var match = BoundedRegex.Match(
            rawName,
            $"^(?<name>{QuotedIdentifierPattern}|{BareIdentifierPattern})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        var absoluteNameIndex = rawIndex + nameGroup.Index;
        if (absoluteNameIndex < statementLineOffset)
            return;

        NormalizeIdentifier(nameGroup.Value, absoluteNameIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
        if (!wasQuoted && shouldIgnoreName(resolvedName))
            return;

        var nameColumn = nameIndex + statementStart - lineOffset;
        var container = resolveContainerForCall(absoluteNameIndex);
        ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, referenceKind, context, lineNumber, container);
    }

    private static int FindQualifiedIdentifierLeafIndex(string rawName)
    {
        var leafStart = 0;
        var quote = '\0';
        for (var i = 0; i < rawName.Length; i++)
        {
            var ch = rawName[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < rawName.Length && rawName[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }
                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < rawName.Length && rawName[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (ch is '[' or '"' or '`')
            {
                quote = ch;
                continue;
            }

            if (ch != '.')
                continue;

            leafStart = i + 1;
            while (leafStart < rawName.Length && char.IsWhiteSpace(rawName[leafStart]))
                leafStart++;
        }

        return leafStart;
    }

    private static List<TextSegment> SplitTopLevelCommaSegments(string text, int textStart)
    {
        var segments = new List<TextSegment>();
        var segmentStart = 0;
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }
                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (ch is '[' or '"' or '`' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }
            if (ch == ')' && depth > 0)
            {
                depth--;
                continue;
            }
            if (ch != ',' || depth != 0)
                continue;

            segments.Add(new TextSegment(text[segmentStart..i], textStart + segmentStart));
            segmentStart = i + 1;
        }

        segments.Add(new TextSegment(text[segmentStart..], textStart + segmentStart));
        return segments;
    }

    private static int IndexOfTopLevelChar(string text, char value)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']')
                            i++;
                        else
                            quote = '\0';
                    }
                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                        i++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (ch is '[' or '"' or '`' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                continue;
            }
            if (ch == ')' && depth > 0)
            {
                depth--;
                continue;
            }
            if (ch == value && depth == 0)
                return i;
        }

        return -1;
    }

    private static void EmitGeneratedColumnDependencyReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        if (!GeneratedColumnMarkerRegex.IsMatch(statement))
            return;

        foreach (Match match in GeneratedColumnExpressionStartRegex.Matches(statement))
        {
            if (match.Index < statementLineOffset || IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            if (match.Value.TrimStart().StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                && !IsLikelyComputedColumnAsExpression(statement, match.Index))
            {
                continue;
            }

            var openParenIndex = statement.IndexOf('(', match.Index + match.Length - 1);
            if (openParenIndex < 0)
                continue;

            var closeParenIndex = FindMatchingParen(statement, openParenIndex);
            if (closeParenIndex <= openParenIndex)
                continue;

            EmitSqlExpressionIdentifierDependencies(
                statement,
                openParenIndex + 1,
                closeParenIndex,
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

        foreach (Match match in DefaultNextValueForExpressionRegex.Matches(statement))
        {
            if (match.Index < statementLineOffset || IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var sequence = match.Groups["name"];
            EmitSqlExpressionIdentifierDependencies(
                statement,
                sequence.Index,
                sequence.Index + sequence.Length,
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

    private static void EmitSqlExpressionIdentifierDependencies(
        string statement,
        int startIndex,
        int endIndexExclusive,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        var expression = statement[startIndex..endIndexExclusive];
        foreach (Match match in SqlExpressionIdentifierRegex.Matches(expression))
        {
            var rawIndex = startIndex + match.Index;
            if (rawIndex < statementLineOffset || IsInsideDoubleQuotedRegion(statement, rawIndex))
                continue;

            var rawName = match.Value;
            NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
            if (!wasQuoted && (shouldIgnoreName(resolvedName) || IsGeneratedColumnDependencyKeyword(resolvedName)))
                continue;

            var nameColumn = nameIndex + statementStart - lineOffset;
            var container = resolveContainerForCall(rawIndex);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "generated_column_dependency", context, lineNumber, container);
        }
    }

    private static bool IsGeneratedColumnDependencyKeyword(string name)
        => name.Equals("GENERATED", StringComparison.OrdinalIgnoreCase)
           || name.Equals("ALWAYS", StringComparison.OrdinalIgnoreCase)
           || name.Equals("AS", StringComparison.OrdinalIgnoreCase)
           || name.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
           || name.Equals("NEXT", StringComparison.OrdinalIgnoreCase)
           || name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
           || name.Equals("FOR", StringComparison.OrdinalIgnoreCase)
           || name.Equals("STORED", StringComparison.OrdinalIgnoreCase)
           || name.Equals("VIRTUAL", StringComparison.OrdinalIgnoreCase)
           || name.Equals("PERSISTED", StringComparison.OrdinalIgnoreCase)
           || name.Equals("NULL", StringComparison.OrdinalIgnoreCase)
           || name.Equals("NOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyComputedColumnAsExpression(string statement, int asIndex)
    {
        var prefix = statement[..asIndex];
        return Regex.IsMatch(prefix, @"(?<![\w$])ALTER\s+TABLE\b[\s\S]*\bADD\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(prefix, @"(?<![\w$])CREATE\s+(?:OR\s+(?:REPLACE|ALTER)\s+)?(?:(?:(?:GLOBAL|LOCAL)\s+)?(?:TEMP|TEMPORARY)\s+|UNLOGGED\s+)?TABLE\b", RegexOptions.IgnoreCase);
    }

    private static void EmitSourceReference(
        string rawName,
        int rawIndex,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        string referenceKind = "reference")
    {
        if (rawIndex < statementLineOffset)
            return;

        var followedByOpenParen = IsFollowedByOpenParen(statement, rawIndex + rawName.Length);
        NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
        int nameColumn = nameIndex + statementStart - lineOffset;
        if (!wasQuoted && shouldIgnoreName(resolvedName))
            return;
        if (followedByOpenParen)
        {
            var container = resolveContainerForCall(rawIndex);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "call", context, lineNumber, container);
            if (!wasQuoted)
                suppressedCallIndices.Add(GetCallLikeSuppressionIndex(statement, rawIndex) + statementStart - lineOffset);
            return;
        }
        if (resolvedName.StartsWith("#", StringComparison.Ordinal)
            && !establishedTempObjectNames.Contains(resolvedName))
            return;

        var referenceContainer = resolveContainerForCall(rawIndex);
        ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, referenceKind, context, lineNumber, referenceContainer);
    }

    private static string GetSourceReferenceKind(int index, IReadOnlyList<CteBodySpan>? cteBodySpans)
    {
        if (cteBodySpans == null)
            return "reference";

        foreach (var span in cteBodySpans)
        {
            if (index >= span.StartIndex && index < span.EndIndexExclusive)
                return "cte_body_reference";
        }

        return "reference";
    }

    private static List<CteBodySpan>? FindCteBodySpans(string statement)
    {
        if (statement.IndexOf("WITH", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        List<CteBodySpan>? spans = null;
        foreach (Match match in CteDefinitionRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var openParenIndex = match.Index + match.Length - 1;
            var closeParenIndex = FindMatchingParen(statement, openParenIndex);
            (spans ??= []).Add(new CteBodySpan(openParenIndex + 1, closeParenIndex < 0 ? statement.Length : closeParenIndex));
        }

        return spans;
    }

    private static int FindMatchingParen(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
                continue;
            }

            if (text[i] != ')')
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static int FindMatchingOpenParen(string text, int closeParenIndex)
    {
        var depth = 0;
        for (var i = closeParenIndex; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
                continue;
            }

            if (text[i] != '(')
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static void EmitSelectIntoTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int>? suppressedCallIndices = null)
    {
        foreach (Match match in SelectIntoTargetStatementRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;
            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
        }
    }

    private static void EmitTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in TargetReferenceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;
            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;
            if (!wasQuoted
                && string.Equals(resolvedName, "SET", StringComparison.OrdinalIgnoreCase)
                && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!wasQuoted
                && string.Equals(resolvedName, "STATISTICS", StringComparison.OrdinalIgnoreCase)
                && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
            if (IsFollowedByOpenParen(statement, nameGroup.Index + nameGroup.Length))
                AddCallLikeSuppressionIndices(suppressedCallIndices, statement, nameGroup.Index, statementStart, lineOffset);
        }
    }

    private static void EmitMultiTargetReferences(
        MatchCollection matches,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int>? suppressedCallIndices = null)
    {
        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (capture.Index < statementLineOffset)
                    continue;
                NormalizeIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                int nameColumn = nameIndex + statementStart - lineOffset;
                if (!wasQuoted && shouldIgnoreName(resolvedName))
                    continue;

                var container = resolveContainerForCall(capture.Index);
                ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
                if (suppressedCallIndices != null && IsFollowedByOpenParen(statement, capture.Index + capture.Length))
                    AddCallLikeSuppressionIndices(suppressedCallIndices, statement, capture.Index, statementStart, lineOffset);
            }
        }
    }

    private static void NormalizeIdentifier(
        string rawName,
        int rawIndex,
        out string resolvedName,
        out int resolvedIndex,
        out bool wasQuoted)
    {
        if (rawName.Length >= 2
            && ((rawName[0] == '[' && rawName[^1] == ']')
                || (rawName[0] == '`' && rawName[^1] == '`')
                || (rawName[0] == '"' && rawName[^1] == '"')))
        {
            resolvedName = rawName.Substring(1, rawName.Length - 2);
            if (rawName[0] == '"')
                resolvedName = resolvedName.Replace("\"\"", "\"", StringComparison.Ordinal);
            else if (rawName[0] == '[')
                resolvedName = resolvedName.Replace("]]", "]", StringComparison.Ordinal);
            resolvedIndex = rawIndex + 1;
            wasQuoted = true;
            return;
        }

        resolvedName = rawName;
        resolvedIndex = rawIndex;
        wasQuoted = false;
    }

    private static bool IsFollowedByOpenParen(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '(';
    }

    private static int GetCallLikeSuppressionIndex(string line, int index)
    {
        while (index < line.Length && line[index] == '#')
            index++;

        return index;
    }

    private static void AddCallLikeSuppressionIndices(
        HashSet<int> suppressedCallIndices,
        string line,
        int leafIndex,
        int statementStart,
        int lineOffset)
    {
        var leafSuppressionIndex = GetCallLikeSuppressionIndex(line, leafIndex) + statementStart - lineOffset;
        suppressedCallIndices.Add(leafSuppressionIndex);

        var qualifiedStart = FindQualifiedIdentifierStart(line, leafIndex);
        if (qualifiedStart == leafIndex)
            return;

        suppressedCallIndices.Add(GetCallLikeSuppressionIndex(line, qualifiedStart) + statementStart - lineOffset);
    }

    private static int FindQualifiedIdentifierStart(string line, int leafIndex)
    {
        var start = leafIndex;
        while (start > 0)
        {
            var scan = start - 1;
            while (scan >= 0 && char.IsWhiteSpace(line[scan]))
                scan--;
            if (scan < 0 || line[scan] != '.')
                break;

            scan--;
            while (scan >= 0 && char.IsWhiteSpace(line[scan]))
                scan--;
            if (scan < 0)
                break;

            start = ScanIdentifierSegmentStart(line, scan);
        }

        return start;
    }

    private static int ScanIdentifierSegmentStart(string line, int index)
    {
        if (line[index] == ']')
        {
            index--;
            while (index >= 0)
            {
                if (line[index] == '[')
                    return index;
                index--;
            }

            return 0;
        }

        if (line[index] is '"' or '`')
        {
            var quote = line[index--];
            while (index >= 0)
            {
                if (line[index] == quote)
                    return index;
                index--;
            }

            return 0;
        }

        while (index >= 0 && IsIdentifierContinuationForReverseScan(line[index]))
            index--;

        return index + 1;
    }

    private static bool IsIdentifierContinuationForReverseScan(char ch)
        => ch is '_' or '$' or '#'
           || char.IsLetterOrDigit(ch)
           || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
               or System.Globalization.UnicodeCategory.SpacingCombiningMark
               or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    private static string CombineStatementPrefix(string prefix, string line, out int lineOffset)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            lineOffset = 0;
            return line;
        }

        lineOffset = prefix.Length + 1;
        return prefix + "\n" + line;
    }

    private static string AdvanceStatementPrefix(
        string combined,
        int statementStart,
        bool lineEndedByLineComment)
    {
        var remaining = statementStart == 0 ? combined : combined[statementStart..];
        if (!lineEndedByLineComment)
            return remaining;

        return CanStatementRequireLineCommentCarry(remaining) ? remaining : string.Empty;
    }

    private static bool ShouldFlushTempObjectPrefixAtLineBoundary(
        string prefix,
        string nextLine)
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(nextLine))
            return false;
        if (!CanStatementEstablishTempObject(prefix))
            return false;

        return StartsTopLevelStatement(nextLine);
    }

    private static bool CanStatementEstablishTempObject(string statement)
    {
        if (statement.IndexOf('#') < 0)
            return false;

        return TargetReferenceRegex.IsMatch(statement)
            || TruncateTargetRegex.IsMatch(statement)
            || SelectIntoTargetStatementRegex.IsMatch(statement)
            || CreateTempTableRegex.IsMatch(statement)
            || CreateTempRoutineRegex.IsMatch(statement);
    }

    private static bool CanStatementRequireLineCommentCarry(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        return CanStatementEstablishTempObject(statement)
            || TargetReferencePrefixRegex.IsMatch(statement)
            || FromListContinuationPrefixRegex.IsMatch(statement)
            || SelectIntoTargetPrefixRegex.IsMatch(statement)
            || DeleteUsingPrefixRegex.IsMatch(statement)
            || DeleteUsingListContinuationPrefixRegex.IsMatch(statement)
            || MergeUsingPrefixRegex.IsMatch(statement)
            || MergeTargetHintContinuationPrefixRegex.IsMatch(statement);
    }

    private static bool StartsTopLevelStatement(string line)
    {
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || !char.IsLetter(line[index]))
            return false;

        int start = index;
        while (index < line.Length && char.IsLetter(line[index]))
            index++;

        var keyword = line[start..index].ToUpperInvariant();
        if (keyword == "WITH")
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            return index >= line.Length || line[index] != '(';
        }

        return keyword switch
        {
            "SELECT" => true,
            "INSERT" => true,
            "UPDATE" => true,
            "DELETE" => true,
            "MERGE" => true,
            "CREATE" => true,
            "ALTER" => true,
            "DROP" => true,
            "TRUNCATE" => true,
            "SET" => true,
            "DECLARE" => true,
            "IF" => true,
            "WHILE" => true,
            "DO" => true,
            "BEGIN" => true,
            "EXEC" => true,
            "EXECUTE" => true,
            "CALL" => true,
            _ => false,
        };
    }

    private static int FindStatementTerminator(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ';')
                return i;
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(text, i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
            }
        }

        return -1;
    }

    private static int FindClosingDoubleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static int FindClosingSingleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }
            if (text[i] != '\'')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool IsInsideDoubleQuotedRegion(string text, int index)
    {
        if (index <= 0)
            return false;

        bool inside = false;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (inside && i + 1 < index && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            inside = !inside;
        }

        return inside;
    }

    private static bool TryReadDollarQuoteDelimiter(
        string line,
        int index,
        out string delimiter)
    {
        delimiter = string.Empty;
        if (index < 0 || index >= line.Length || line[index] != '$')
            return false;
        if (index > 0 && (char.IsLetterOrDigit(line[index - 1]) || line[index - 1] == '_'))
            return false;
        if (index + 1 >= line.Length)
            return false;
        if (line[index + 1] == '$')
        {
            delimiter = "$$";
            return true;
        }
        if (!(char.IsLetter(line[index + 1]) || line[index + 1] == '_'))
            return false;

        int probe = index + 2;
        while (probe < line.Length && (char.IsLetterOrDigit(line[probe]) || line[probe] == '_'))
            probe++;
        if (probe >= line.Length || line[probe] != '$')
            return false;

        delimiter = line[index..(probe + 1)];
        return true;
    }

    private static int SkipWhitespaceAhead(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static void CollectTempObjectNamesFromStatement(
        string statement,
        HashSet<string> names)
    {
        CollectTempObjectNamesFromMatches(TargetReferenceRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(TruncateTargetRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(SelectIntoTargetStatementRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(CreateTempTableRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(CreateTempRoutineRegex.Matches(statement), statement, names);
    }

    private static void CollectTempObjectNamesFromMatches(MatchCollection matches, string statement, HashSet<string> names)
    {
        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Captures.Count == 0)
                continue;

            foreach (Capture capture in nameGroup.Captures)
            {
                NormalizeIdentifier(capture.Value, capture.Index, out var resolvedName, out _, out _);
                if (resolvedName.StartsWith("#", StringComparison.Ordinal))
                    names.Add(resolvedName);
            }
        }
    }

    private static bool TryFindDefinitionLeafSpan(string line, string qualifiedName, out DefinitionLeafSpan span)
    {
        span = default;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(qualifiedName))
            return false;

        var leafName = SqlNameResolver.GetLeafName(qualifiedName);
        if (string.IsNullOrWhiteSpace(leafName))
            return false;

        var rawSegments = SplitQualifiedNameSourceSegments(qualifiedName);
        if (rawSegments.Count == 0)
            return false;

        var pattern = new StringBuilder();
        for (var i = 0; i < rawSegments.Count; i++)
        {
            if (i > 0)
                pattern.Append(@"\s*\.\s*");

            var escaped = Regex.Escape(rawSegments[i]);
            if (i == rawSegments.Count - 1)
                pattern.Append("(?<leaf>").Append(escaped).Append(')');
            else
                pattern.Append(escaped);
        }

        var match = BoundedRegex.Match(line, pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var leafGroup = match.Groups["leaf"];
        if (!leafGroup.Success)
            return false;

        span = new DefinitionLeafSpan(leafName, leafGroup.Index, leafGroup.Index + leafGroup.Length);
        return true;
    }

    private static List<string> SplitQualifiedNameSourceSegments(string qualifiedName)
    {
        var trimmed = qualifiedName.Trim();
        var segments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (quote != '\0')
            {
                current.Append(ch);
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                        {
                            current.Append(trimmed[i + 1]);
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }

                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == quote)
                    {
                        current.Append(trimmed[i + 1]);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (ch is '[' or '"' or '`')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (ch == '.')
            {
                AppendQualifiedNameSourceSegment(segments, current);
                continue;
            }

            current.Append(ch);
        }

        AppendQualifiedNameSourceSegment(segments, current);
        return segments;
    }

    private static void AppendQualifiedNameSourceSegment(List<string> segments, StringBuilder current)
    {
        var value = current.ToString().Trim();
        if (value.Length > 0)
            segments.Add(value);
        current.Clear();
    }

    private static string PrepareLineForIdentifierScan(
        string line,
        IdentifierScanState state,
        string? statementPrefix,
        out bool lineEndedByLineComment,
        out IdentifierScanState nextState)
    {
        lineEndedByLineComment = false;
        if (string.IsNullOrEmpty(line))
        {
            nextState = state;
            return line;
        }

        var sanitized = line.ToCharArray();
        bool inBlockComment = state.InBlockComment;
        string? dollarQuoteDelimiter = state.DollarQuoteDelimiter;
        bool inSingleQuotedString = state.InSingleQuotedString;

        void BlankRange(int start, int endExclusive)
        {
            start = Math.Max(0, start);
            endExclusive = Math.Min(sanitized.Length, endExclusive);
            for (int blankIndex = start; blankIndex < endExclusive; blankIndex++)
                sanitized[blankIndex] = ' ';
        }

        for (int i = 0; i < line.Length;)
        {
            if (inBlockComment)
            {
                int closing = line.IndexOf("*/", i, StringComparison.Ordinal);
                int end = closing >= 0 ? closing + 2 : line.Length;
                BlankRange(i, end);
                if (closing < 0)
                    break;
                i = end;
                inBlockComment = false;
                continue;
            }
            if (!string.IsNullOrEmpty(dollarQuoteDelimiter))
            {
                int closing = line.IndexOf(dollarQuoteDelimiter, i, StringComparison.Ordinal);
                if (closing < 0)
                {
                    BlankRange(i, line.Length);
                    break;
                }

                int nextContent = SkipWhitespaceAhead(line, closing + dollarQuoteDelimiter.Length);
                if (nextContent < line.Length
                    && line[nextContent] != ';'
                    && line[nextContent] != ','
                    && line[nextContent] != ')'
                    && line[nextContent] != ']')
                {
                    int nestedClosing = line.IndexOf(
                        dollarQuoteDelimiter,
                        closing + dollarQuoteDelimiter.Length,
                        StringComparison.Ordinal);
                    if (nestedClosing >= 0)
                    {
                        int end = nestedClosing + dollarQuoteDelimiter.Length;
                        BlankRange(i, end);
                        i = end;
                        continue;
                    }
                }

                int closingEnd = closing + dollarQuoteDelimiter.Length;
                BlankRange(i, closingEnd);
                i = closingEnd;
                dollarQuoteDelimiter = null;
                continue;
            }
            if (inSingleQuotedString)
            {
                int closing = FindClosingSingleQuote(line, i);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing >= 0)
                {
                    inSingleQuotedString = false;
                    continue;
                }

                break;
            }

            char c = line[i];
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(line, i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '`')
            {
                int closing = line.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '[')
            {
                int closing = line.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '\'')
            {
                int closing = FindClosingSingleQuote(line, i + 1);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing < 0)
                    inSingleQuotedString = true;
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                BlankRange(i, i + 2);
                i += 2;
                inBlockComment = true;
                continue;
            }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                lineEndedByLineComment = true;
                BlankRange(i, line.Length);
                break;
            }
            if (c == '#')
            {
                if (ShouldTreatHashAsComment(line, i, statementPrefix))
                {
                    lineEndedByLineComment = true;
                    BlankRange(i, line.Length);
                    break;
                }
            }
            if (c == '$' && TryReadDollarQuoteDelimiter(line, i, out var delimiter))
            {
                BlankRange(i, i + delimiter.Length);
                i += delimiter.Length;
                dollarQuoteDelimiter = delimiter;
                continue;
            }

            i++;
        }

        nextState = new IdentifierScanState(inBlockComment, dollarQuoteDelimiter, inSingleQuotedString);
        return new string(sanitized);
    }

    private static bool ShouldTreatHashAsComment(string line, int hashIndex, string? statementPrefix)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        if (probe < 0 && !string.IsNullOrWhiteSpace(statementPrefix))
        {
            var combined = statementPrefix + "\n" + line;
            return ShouldTreatHashAsCommentCore(combined, statementPrefix.Length + 1 + hashIndex);
        }

        return ShouldTreatHashAsCommentCore(line, hashIndex);
    }

    private static bool ShouldTreatHashAsCommentCore(string line, int hashIndex)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int next = hashIndex + 1;
        if (hashIndex > 0
            && line[hashIndex - 1] == '#'
            && next < line.Length
            && (char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return false;
        if (next + 1 < line.Length
            && line[next] == '#'
            && (char.IsLetterOrDigit(line[next + 1]) || line[next + 1] == '_'))
            return false;
        if (next >= line.Length || !(char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return true;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        while (probe >= 0 && line[probe] == ',')
        {
            var priorListItem = line[..probe];
            int sourceStart = FindLastCommaOutsideQuotedIdentifiers(priorListItem);
            if (sourceStart >= 0)
                sourceStart++;
            else
            {
                var usingMatches = UsingKeywordRegex.Matches(priorListItem);
                if (usingMatches.Count > 0)
                    sourceStart = usingMatches[^1].Index + usingMatches[^1].Length;
                else
                {
                    sourceStart = priorListItem.LastIndexOf('#');
                    if (sourceStart < 0)
                        return true;
                }
            }
            while (sourceStart < priorListItem.Length && char.IsWhiteSpace(priorListItem[sourceStart]))
                sourceStart++;

            var listMatch = TrailingTempIdentifierRegex.Match(priorListItem[sourceStart..]);
            if (!listMatch.Success)
                return true;

            probe = sourceStart - 1;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }
        if (probe < 0)
            return true;
        if (line[probe] == '.')
            return false;
        if (line[probe] == ')')
        {
            int depth = 1;
            probe--;
            while (probe >= 0 && depth > 0)
            {
                if (line[probe] == ')')
                    depth++;
                else if (line[probe] == '(')
                    depth--;
                probe--;
            }
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
            if (probe < 0)
                return true;

            int modifierEnd = probe;
            while (probe >= 0 && char.IsLetter(line[probe]))
                probe--;
            int modifierStart = probe + 1;
            if (modifierStart <= modifierEnd
                && string.Equals(line[modifierStart..(modifierEnd + 1)], "TOP", StringComparison.OrdinalIgnoreCase))
            {
                while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                    probe--;
                if (probe < 0)
                    return true;
            }
        }

        int tokenEnd = probe;
        while (probe >= 0 && char.IsLetter(line[probe]))
            probe--;
        int tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return true;

        var token = line[tokenStart..(tokenEnd + 1)];
        return !string.Equals(token, "FROM", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "JOIN", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "MERGE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "USING", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "INTO", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "UPDATE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "TABLE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXEC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXECUTE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "CALL", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROCEDURE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "FUNCTION", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindLastCommaOutsideQuotedIdentifiers(string text)
    {
        int lastComma = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(text, i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == ',')
                lastComma = i;
        }

        return lastComma;
    }
}
