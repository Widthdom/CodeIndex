using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
    private static void EmitAlterObjectTargetReferences(
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
        if (statement.IndexOf("VIEW", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterViewTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterProcedureTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterFunctionTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterTriggerTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterSequenceTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterSecurityPolicyTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterFullTextCatalogTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterPartitionFunctionTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterPartitionSchemeTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterXmlSchemaCollectionTargetRegex, statement),
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
                BoundedRegex.EnumerateMatches(AlterAssemblyTargetRegex, statement),
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

        if (statement.IndexOf("SCHEMA", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("TRANSFER", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterSchemaTransferTargetRegex, statement),
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

        if (statement.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("SWITCH", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterTableSwitchTargetRegex, statement),
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

        if (statement.IndexOf("TABLE", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("SYSTEM_VERSIONING", StringComparison.OrdinalIgnoreCase) >= 0
            && statement.IndexOf("HISTORY_TABLE", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            EmitMultiTargetReferences(
                BoundedRegex.EnumerateMatches(AlterTableSystemVersioningHistoryTargetRegex, statement),
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

    private static void EmitWindowClauseReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match keywordMatch in BoundedRegex.EnumerateMatches(WindowFrameKeywordRegex, statement))
        {
            if (keywordMatch.Index >= bodyStart && keywordMatch.Index < bodyEnd && keywordMatch.Index >= statementLineOffset)
                suppressedCallIndices.Add(keywordMatch.Index + statementStart - lineOffset);
        }

        foreach (var (start, end) in EnumerateWindowColumnListSpans(statement, bodyStart, bodyEnd))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;
            foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(WindowClauseColumnRegex, statement, references))
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
        ReferenceDedupeSet seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(ProcCallRegex, statement, references))
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
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(SystemVariableReferenceRegex, statement, references))
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
        BoundedRegex.MatchEnumerable matches,
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
        IReadOnlyList<CteBodySpan>? cteBodySpans = null)
    {
        if (statement.IndexOf("REVOKE", StringComparison.OrdinalIgnoreCase) >= 0
            && RevokePermissionStatementRegex.IsMatch(statement))
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(matches, references))
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
        ReferenceDedupeSet seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(MergeUsingSourceRegex, statement, references))
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
        ReferenceDedupeSet seen,
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


}
