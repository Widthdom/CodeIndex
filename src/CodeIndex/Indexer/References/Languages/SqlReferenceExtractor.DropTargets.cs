using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static void EmitDropObjectTargetReferences(
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
        if (statement.IndexOf("SYNONYM", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropSynonymTargetRegex, statement),
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

        if (statement.IndexOf("VIEW", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropViewTargetRegex, statement),
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

        if (statement.IndexOf("PROCEDURE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("PROC", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropProcedureTargetRegex, statement),
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

        if (statement.IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropFunctionTargetRegex, statement),
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

        if (statement.IndexOf("TRIGGER", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropTriggerTargetRegex, statement),
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

        if (statement.IndexOf("SEQUENCE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropSequenceTargetRegex, statement),
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

        if (statement.IndexOf("TYPE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropTypeTargetRegex, statement),
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

        if (statement.IndexOf("RULE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropRuleTargetRegex, statement),
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

        if (statement.IndexOf("DEFAULT", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropDefaultTargetRegex, statement),
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

        if (statement.IndexOf("AGGREGATE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropAggregateTargetRegex, statement),
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

        if (statement.IndexOf("SECURITY", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("POLICY", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropSecurityPolicyTargetRegex, statement),
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

        if (statement.IndexOf("FULLTEXT", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("CATALOG", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropFullTextCatalogTargetRegex, statement),
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

        if (statement.IndexOf("PARTITION", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("SCHEME", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropPartitionSchemeTargetRegex, statement),
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

        if (statement.IndexOf("PARTITION", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropPartitionFunctionTargetRegex, statement),
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

        if (statement.IndexOf("XML", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("SCHEMA", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("COLLECTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropXmlSchemaCollectionTargetRegex, statement),
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

        if (statement.IndexOf("ASSEMBLY", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(DropAssemblyTargetRegex, statement),
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

    private static void EmitAlterAndMaintenanceTargetReferences(
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
        if (statement.IndexOf("ALTER", StringComparison.OrdinalIgnoreCase) >= 0)
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
        }

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


}
