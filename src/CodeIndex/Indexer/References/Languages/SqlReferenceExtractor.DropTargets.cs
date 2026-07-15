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
        }

        if (statement.IndexOf("VIEW", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("PROCEDURE", StringComparison.OrdinalIgnoreCase) >= 0
            || statement.IndexOf("PROC", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("TRIGGER", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("SEQUENCE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("TYPE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("RULE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("DEFAULT", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("AGGREGATE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("SECURITY", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("POLICY", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("FULLTEXT", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("CATALOG", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("PARTITION", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("SCHEME", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("PARTITION", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("XML", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("SCHEMA", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("COLLECTION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
        }

        if (statement.IndexOf("ASSEMBLY", StringComparison.OrdinalIgnoreCase) >= 0)
        {
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
