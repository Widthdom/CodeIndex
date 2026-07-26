using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static void EmitMaintenanceTargetReferences(
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
        if (statement.IndexOf("GRANT", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("DENY", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("REVOKE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(ObjectPermissionTargetRegex, statement),
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

        if (statement.IndexOf("AUTHORIZATION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterAuthorizationObjectTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterAuthorizationBareTargetRegex, statement),
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

        if (statement.IndexOf("STATISTICS", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(UpdateStatisticsTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(CreateStatisticsOnTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(DropStatisticsTargetRegex, statement),
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

        var mayContainTargetReference = statement.IndexOf("INSERT", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("UPDATE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("MERGE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase) >= 0
            || (statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0
                && statement.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0);
        if (mayContainTargetReference)
        {
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
        }

        if (statement.IndexOf("DROP", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropTableTargetRegex, statement),
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

        if (statement.IndexOf("TRUNCATE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(TruncateTargetRegex, statement),
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


}
