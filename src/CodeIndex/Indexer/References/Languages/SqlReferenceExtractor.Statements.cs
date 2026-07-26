using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static void EmitStatementReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        var hasCallKeyword = statement.IndexOf("EXEC", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("CALL", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasFromKeyword = statement.IndexOf("FROM", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasJoinKeyword = statement.IndexOf("JOIN", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasApplyKeyword = statement.IndexOf("APPLY", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasDeleteKeyword = statement.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasIntoKeyword = statement.IndexOf("INTO", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasMergeKeyword = statement.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasOutputKeyword = statement.IndexOf("OUTPUT", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasSelectKeyword = statement.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasUsingKeyword = statement.IndexOf("USING", StringComparison.OrdinalIgnoreCase) >= 0;

        var cteBodySpans = PrepareStatementReferenceState(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            suppressedCallIndices);

        if (statement.IndexOf("OVER", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (hasCallKeyword)
        {
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
        }

        if (statement.IndexOf("@@", StringComparison.Ordinal) >= 0)
        {
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
        }

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

        if (hasFromKeyword)
        {
            EmitSourceCaptureReferences(
                BoundedRegex.EnumerateMatches(FromSourceListRegex, statement),
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
        }

        if (hasJoinKeyword || hasApplyKeyword)
        {
            EmitSourceCaptureReferences(
                BoundedRegex.EnumerateMatches(SourceReferenceRegex, statement),
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
        }

        if (hasMergeKeyword)
        {
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
        }

        if (hasDeleteKeyword)
        {
            if (hasUsingKeyword)
            {
                EmitSourceCaptureReferences(
                    BoundedRegex.EnumerateMatches(DeleteUsingSourceRegex, statement),
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

            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DeleteTargetWithoutFromRegex, statement),
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

        if (hasOutputKeyword && hasIntoKeyword)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(OutputIntoTargetRegex, statement),
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

        if (hasSelectKeyword && hasIntoKeyword)
        {
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
        }

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


}
