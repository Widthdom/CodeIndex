using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static readonly HashSet<string> TypeScriptTypeExpressionIgnoredSegments = new(StringComparer.Ordinal)
    {
        "as",
        "extends",
        "in",
        "infer",
        "keyof",
        "readonly",
    };

    internal static bool HasTrailingCSharpTypePatternIntro(string text, Regex introRegex)
    {
        foreach (Match match in introRegex.Matches(text))
        {
            if (HasOnlyTrailingCSharpTrivia(text, match.Index + match.Length))
                return true;
        }

        return false;
    }

    internal static void EmitCSharpSwitchExpressionTypePatternReferences(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> containerCandidates,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId)
    {
        if (preparedLines.Count == 0)
            return;

        var preparedContent = string.Join("\n", preparedLines);
        for (var searchIndex = 0; searchIndex < preparedContent.Length;)
        {
            var arrowIndex = preparedContent.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                break;

            searchIndex = arrowIndex + 2;
            var looksLikeLambda = IsPotentialCSharpLambdaArrow(preparedContent, arrowIndex);

            if (!TryGetCSharpSwitchExpressionArmTypePatternRange(
                    preparedContent,
                    arrowIndex,
                    out var bodyStartOffset,
                    out var armStartOffset,
                    out var armPatternEndOffset)
                || armStartOffset >= armPatternEndOffset)
            {
                continue;
            }

            var armText = preparedContent[armStartOffset..armPatternEndOffset];
            var cursor = SkipWhitespaceForward(armText, 0);
            if (TryConsumeCSharpPatternKeyword(armText, ref cursor, "not"))
                cursor = SkipWhitespace(armText, cursor);

            string currentTypeExpression;
            int currentTypeIndex;
            int currentContinuationIndex;
            var declarationPatternMatch = CSharpSwitchExpressionDeclarationPatternValueNameRegex.Match(armText);
            if (declarationPatternMatch.Success)
            {
                var declarationTypeGroup = declarationPatternMatch.Groups["type"];
                currentTypeExpression = declarationTypeGroup.Value;
                currentTypeIndex = declarationTypeGroup.Index;
                currentContinuationIndex = SkipWhitespace(armText, declarationTypeGroup.Index + declarationTypeGroup.Length);
            }
            else
            {
                var typeMatch = CSharpTypeExpressionAtCursorRegex.Match(armText, cursor);
                if (!typeMatch.Success)
                    continue;

                var typeGroup = typeMatch.Groups["type"];
                currentTypeExpression = typeGroup.Value;
                currentTypeIndex = typeGroup.Index;
                currentContinuationIndex = SkipWhitespace(armText, typeGroup.Index + typeGroup.Length);
            }

            var currentTypeLineNumber = GetLineNumberFromOffset(preparedContent, armStartOffset + currentTypeIndex, 1);
            if (looksLikeLambda
                && !HasStrongCSharpSwitchExpressionTypeSignal(
                    currentTypeExpression,
                    currentTypeLineNumber,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                continue;
            }

            while (TryConsumeCSharpLogicalPatternKeyword(armText, currentContinuationIndex, out var nextHeadCursor))
            {
                if (!IsCSharpLogicalConstantPatternHead(
                        armText,
                        currentTypeExpression,
                        nextHeadCursor,
                        currentTypeLineNumber,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpQualifiedTypePatternLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        hasActiveSameFileCSharpTypeCandidate))
                {
                    EmitCSharpSwitchExpressionArmTypePatternReference(
                        lines,
                        preparedLines,
                        preparedContent,
                        containerCandidates,
                        references,
                        seen,
                        fileId,
                        currentTypeExpression,
                        bodyStartOffset,
                        armStartOffset + currentTypeIndex);
                }

                var nextTypeCursor = nextHeadCursor;
                if (TryConsumeCSharpPatternKeyword(armText, ref nextTypeCursor, "not"))
                    nextTypeCursor = SkipWhitespace(armText, nextTypeCursor);

                var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(armText, nextTypeCursor);
                if (!nextMatch.Success)
                {
                    currentTypeExpression = string.Empty;
                    break;
                }

                var nextTypeGroup = nextMatch.Groups["type"];
                currentTypeExpression = nextTypeGroup.Value;
                currentTypeIndex = nextTypeGroup.Index;
                currentContinuationIndex = SkipWhitespace(armText, nextTypeGroup.Index + nextTypeGroup.Length);
                currentTypeLineNumber = GetLineNumberFromOffset(preparedContent, armStartOffset + currentTypeIndex, 1);
            }

            if (currentTypeExpression.Length == 0)
                continue;

            if (IsCSharpNonTypePatternExpression(currentTypeExpression)
                || IsCSharpConstantPatternMemberHead(
                    currentTypeExpression,
                    currentTypeLineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                continue;
            }

            EmitCSharpSwitchExpressionArmTypePatternReference(
                lines,
                preparedLines,
                preparedContent,
                containerCandidates,
                references,
                seen,
                fileId,
                currentTypeExpression,
                bodyStartOffset,
                armStartOffset + currentTypeIndex);
        }
    }

    private static bool HasStrongCSharpSwitchExpressionTypeSignal(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        return IsCSharpQualifiedTypePatternHead(
                   typeExpression,
                   lineNumber,
                   csharpQualifiedTypePatternLookup,
                   csharpUsingAliases)
               || hasActiveSameFileCSharpTypeCandidate(typeExpression, lineNumber);
    }

    private static void EmitCSharpSwitchExpressionArmTypePatternReference(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> preparedLines,
        string preparedContent,
        IReadOnlyList<SymbolRecord> containerCandidates,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string typeExpression,
        int containerAnchorOffset,
        int absoluteTypeOffset)
    {
        var position = GetLineColumnFromOffset(preparedContent, absoluteTypeOffset, 1);
        var lineIndex = position.Line - 1;
        if (lineIndex < 0 || lineIndex >= lines.Count)
            return;

        var context = lines[lineIndex];
        if (context.Length == 0)
            return;

        var containerAnchorPosition = GetLineColumnFromOffset(preparedContent, containerAnchorOffset, 1);
        var containerAnchorLineIndex = containerAnchorPosition.Line - 1;
        var container = FindInnermostSameLineCSharpContainer(
                            containerCandidates,
                            containerAnchorLineIndex >= 0 && containerAnchorLineIndex < preparedLines.Count
                                ? preparedLines[containerAnchorLineIndex]
                                : preparedLines[lineIndex],
                            containerAnchorPosition.Line,
                            containerAnchorPosition.Column)
                        ?? FindInnermostContainer(containerCandidates, containerAnchorPosition.Line);

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            typeExpression,
            position.Column,
            context,
            position.Line,
            container,
            "csharp");
    }

    internal static void EmitTypeScriptTypePositionReferences(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var tokens = GetTopLevelTokenSpans(preparedLine);
        if (tokens.Count == 0)
            return;

        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            var token = preparedLine.Substring(tokens[tokenIndex].Start, tokens[tokenIndex].Length);
            if (token is not "typeof" and not "keyof")
                continue;

            if (!IsTypeScriptTypeQueryContext(preparedLines, lineIndex, preparedLine, tokens, tokenIndex))
                continue;

            if (!TryExtractTypeScriptTypeQueryTarget(
                    rawLine,
                    tokens[tokenIndex].Start + tokens[tokenIndex].Length,
                    out var expressionStart,
                    out var expressionLength,
                    out var literalTarget))
                continue;

            if (literalTarget != null)
            {
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    literalTarget,
                    expressionStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(expressionStart),
                    "typescript");
                continue;
            }

            if (expressionStart < 0 || expressionStart >= rawLine.Length)
                continue;

            var expressionLengthSafe = Math.Min(expressionLength, rawLine.Length - expressionStart);
            AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                rawLine.Substring(expressionStart, expressionLengthSafe),
                expressionStart,
                context,
                lineNumber,
                resolveContainerForColumn(expressionStart),
                "typescript");
        }
    }

    internal static void EmitCSharpDocCrefReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in CSharpDocCrefRegex.Matches(originalLine))
        {
            var crefGroup = match.Groups["cref"];
            var normalized = NormalizeCSharpDocCref(crefGroup.Value);
            if (normalized.Length == 0)
                continue;
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                normalized,
                columnOffset + crefGroup.Index,
                context,
                lineNumber,
                container,
                "csharp");
        }
    }

    internal static void EmitJvmDocLinkReferences(
        string language,
        string docText,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in JvmDocInlineLinkRegex.Matches(docText))
            EmitJvmDocTargetReference(language, match.Groups["target"], references, seen, fileId, columnOffset, context, lineNumber, container);

        foreach (Match match in JvmDocSeeReferenceRegex.Matches(docText))
            EmitJvmDocTargetReference(language, match.Groups["target"], references, seen, fileId, columnOffset, context, lineNumber, container);

        if (language == "kotlin")
        {
            foreach (Match match in KDocBracketLinkRegex.Matches(docText))
                EmitJvmDocTargetReference(language, match.Groups["target"], references, seen, fileId, columnOffset, context, lineNumber, container);
        }
    }

    private static void EmitJvmDocTargetReference(
        string language,
        Group targetGroup,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var normalized = NormalizeJvmDocLinkTarget(targetGroup.Value);
        if (normalized.Length == 0)
            return;

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            normalized,
            columnOffset + targetGroup.Index + CountLeadingTrimmedJvmDocTargetChars(targetGroup.Value),
            context,
            lineNumber,
            container,
            language);
    }

    private static string NormalizeJvmDocLinkTarget(string target)
    {
        var text = target.Trim();
        if (text.Length == 0)
            return string.Empty;
        if (text[0] is '<' or '"' or '\'' || text.Contains("://", StringComparison.Ordinal))
            return string.Empty;

        var paren = text.IndexOf('(');
        if (paren >= 0)
            text = text.Substring(0, paren);

        var label = text.IndexOf('|');
        if (label >= 0)
            text = text.Substring(0, label);

        return text.Trim().Replace('#', '.');
    }

    private static int CountLeadingTrimmedJvmDocTargetChars(string target)
    {
        var count = 0;
        while (count < target.Length && char.IsWhiteSpace(target[count]))
            count++;
        return count;
    }

    private static void TryEmitCSharpBaseListReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        var trimmed = line.TrimStart();
        if (!(trimmed.Contains(" class ", StringComparison.Ordinal)
              || trimmed.Contains(" struct ", StringComparison.Ordinal)
              || trimmed.Contains(" interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("struct ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.Contains(" record ", StringComparison.Ordinal)
              || trimmed.StartsWith("record ", StringComparison.Ordinal)))
        {
            return;
        }

        var colonIndex = FindSignatureColonIndex(line);
        if (colonIndex < 0)
            return;

        var baseList = line.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);
        baseList = TrimTrailingTypeListTerminator(baseList);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(baseList))
        {
            var rawSegment = baseList.Substring(segmentStart, segmentLength).Trim();
            if (rawSegment.Length == 0 || rawSegment.Contains('('))
                continue;
            var absoluteStart = colonIndex + 1 + segmentStart + CountLeadingWhitespace(baseList, segmentStart, segmentLength);
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment,
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "csharp",
                ignoredSegments: ignoredSegments);
        }
    }

    private static HashSet<string> CollectCSharpGenericParameterNamesForDeclaration(string line)
    {
        if (TryFindCallableParameterList(line, "csharp", out var callableNameStart, out var paramStart, out _))
        {
            var nameEnd = callableNameStart;
            while (nameEnd < line.Length && IsTypeExpressionIdentifierPart("csharp", line[nameEnd]))
                nameEnd++;

            var genericOpen = SkipWhitespace(line, nameEnd);
            if (genericOpen < paramStart && genericOpen < line.Length && line[genericOpen] == '<')
                return CollectCSharpGenericParameterNamesFromClause(line, genericOpen);
        }

        var tokens = GetTopLevelTokenSpans(line);
        if (tokens.Count < 2)
            return [];

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = line.Substring(tokens[i].Start, tokens[i].Length);
            if (token is not ("class" or "struct" or "interface" or "record" or "delegate"))
                continue;
            var nameIndex = i + 1;
            if (nameIndex >= tokens.Count)
                return [];
            var nameToken = line.Substring(tokens[nameIndex].Start, tokens[nameIndex].Length);
            var genericOpen = nameToken.IndexOf('<');
            if (genericOpen < 0)
                return [];
            return CollectCSharpGenericParameterNamesFromClause(line, tokens[nameIndex].Start + genericOpen);
        }

        return [];
    }

    private static HashSet<string> CollectCSharpGenericParameterNamesFromClause(string line, int genericOpen)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var genericClose = FindMatchingChar(line, genericOpen, '<', '>');
        if (genericClose <= genericOpen)
            return names;

        var clause = line.Substring(genericOpen + 1, genericClose - genericOpen - 1);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            if (TryReadCSharpGenericParameterName(fragment, out var name))
                names.Add(name);
        }

        return names;
    }

    private static bool TryReadCSharpGenericParameterName(string fragment, out string name)
    {
        name = string.Empty;
        var index = 0;
        while (index < fragment.Length)
        {
            while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
                index++;

            if (index >= fragment.Length)
                return false;

            if (fragment[index] == '[')
            {
                var close = FindMatchingChar(fragment, index, '[', ']');
                if (close < 0)
                    return false;
                index = close + 1;
                continue;
            }

            var tokenStart = index;
            if (!IsTypeExpressionIdentifierStart("csharp", fragment[index]))
                return false;

            index++;
            while (index < fragment.Length && IsTypeExpressionIdentifierPart("csharp", fragment[index]))
                index++;

            var token = NormalizeCSharpIdentifier(fragment.Substring(tokenStart, index - tokenStart));
            if (token is "in" or "out")
                continue;

            name = token;
            return true;
        }

        return false;
    }

    private static void EmitCSharpWhereConstraintReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string> declarationGenericParameterNames,
        CSharpWhereConstraintState pendingWhereConstraint)
    {
        UpdateCSharpWhereHeaderGenericParameterNames(line, declarationGenericParameterNames, pendingWhereConstraint);

        var searchStart = 0;
        if (pendingWhereConstraint.Active)
        {
            var nextWhereMatch = CSharpWhereClauseRegex.Match(line);
            var nextWhere = nextWhereMatch.Success ? nextWhereMatch.Index : -1;
            var pendingEnd = FindTypeListTerminator(line, allowArrow: true);
            if (nextWhere >= 0 && (pendingEnd < 0 || nextWhere < pendingEnd))
                pendingEnd = nextWhere;
            if (pendingEnd < 0)
                pendingEnd = line.Length;

            EmitCSharpWhereConstraintSegments(
                line,
                0,
                pendingEnd,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                pendingWhereConstraint.IgnoredSegments);

            if (pendingEnd < line.Length && nextWhere >= 0 && nextWhere == pendingEnd)
            {
                searchStart = nextWhere;
            }
            else if (pendingEnd < line.Length)
            {
                pendingWhereConstraint.Active = false;
                pendingWhereConstraint.HeaderGenericParameterNames.Clear();
                pendingWhereConstraint.IgnoredSegments.Clear();
                return;
            }
            else
            {
                return;
            }

            pendingWhereConstraint.Active = false;
            pendingWhereConstraint.IgnoredSegments.Clear();
        }

        var lineWhereMatches = CSharpWhereClauseRegex.Matches(line);
        var sawWhereMatch = false;
        var lineWhereNames = new HashSet<string>(pendingWhereConstraint.HeaderGenericParameterNames, StringComparer.Ordinal);
        lineWhereNames.UnionWith(declarationGenericParameterNames);
        foreach (Match match in lineWhereMatches)
        {
            if (match.Index < searchStart)
                continue;
            sawWhereMatch = true;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Success && nameGroup.Value.Length > 0)
                lineWhereNames.Add(nameGroup.Value);
        }
        lineWhereNames.UnionWith(CSharpWhereConstraintIgnoredSegments);

        foreach (Match match in lineWhereMatches)
        {
            if (match.Index < searchStart)
                continue;
            int listStart = match.Index + match.Length;
            var remaining = line.Substring(listStart);
            var nextWhereMatch = CSharpWhereClauseRegex.Match(remaining);
            int nextWhere = nextWhereMatch.Success ? nextWhereMatch.Index : -1;
            int end = FindTypeListTerminator(remaining, allowArrow: true);
            if (nextWhere >= 0 && (end < 0 || nextWhere < end))
                end = nextWhere;
            if (end < 0)
                end = remaining.Length;
            EmitCSharpWhereConstraintSegments(
                line,
                listStart,
                end,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                lineWhereNames);

            if (listStart + end >= line.Length && nextWhere < 0 && FindTypeListTerminator(remaining, allowArrow: true) < 0)
            {
                pendingWhereConstraint.Active = true;
                pendingWhereConstraint.IgnoredSegments.Clear();
                pendingWhereConstraint.IgnoredSegments.UnionWith(lineWhereNames);
            }
            else
            {
                pendingWhereConstraint.HeaderGenericParameterNames.Clear();
            }
        }

        if (!sawWhereMatch && FindTypeListTerminator(line, allowArrow: true) >= 0)
        {
            pendingWhereConstraint.HeaderGenericParameterNames.Clear();
        }
    }

    private static void UpdateCSharpWhereHeaderGenericParameterNames(
        string line,
        IReadOnlySet<string> declarationGenericParameterNames,
        CSharpWhereConstraintState pendingWhereConstraint)
    {
        if (declarationGenericParameterNames.Count > 0)
        {
            pendingWhereConstraint.CollectingHeaderGenericParameters = false;
            pendingWhereConstraint.HeaderGenericParameterDepth = 0;
            pendingWhereConstraint.HeaderGenericParameterText = string.Empty;
            pendingWhereConstraint.HeaderGenericParameterNames.Clear();
            pendingWhereConstraint.HeaderGenericParameterNames.UnionWith(declarationGenericParameterNames);
            return;
        }

        if (pendingWhereConstraint.Active)
            return;

        if (pendingWhereConstraint.CollectingHeaderGenericParameters)
        {
            pendingWhereConstraint.HeaderGenericParameterText += " " + line.Trim();
            pendingWhereConstraint.HeaderGenericParameterDepth += CountCSharpGenericParameterAngleDelta(line);
            if (pendingWhereConstraint.HeaderGenericParameterDepth <= 0)
            {
                pendingWhereConstraint.HeaderGenericParameterNames.Clear();
                pendingWhereConstraint.HeaderGenericParameterNames.UnionWith(
                    CollectCSharpGenericParameterNamesFromClause(pendingWhereConstraint.HeaderGenericParameterText, 0));
                pendingWhereConstraint.CollectingHeaderGenericParameters = false;
                pendingWhereConstraint.HeaderGenericParameterDepth = 0;
                pendingWhereConstraint.HeaderGenericParameterText = string.Empty;
            }

            return;
        }

        var genericOpen = FindPotentialCSharpGenericDeclarationOpen(line);
        if (genericOpen < 0)
            return;

        var genericClose = FindMatchingChar(line, genericOpen, '<', '>');
        if (genericClose > genericOpen)
        {
            pendingWhereConstraint.HeaderGenericParameterNames.Clear();
            pendingWhereConstraint.HeaderGenericParameterNames.UnionWith(
                CollectCSharpGenericParameterNamesFromClause(line, genericOpen));
            return;
        }

        pendingWhereConstraint.CollectingHeaderGenericParameters = true;
        pendingWhereConstraint.HeaderGenericParameterDepth = CountCSharpGenericParameterAngleDelta(line[genericOpen..]);
        pendingWhereConstraint.HeaderGenericParameterText = line[genericOpen..].Trim();
    }

    private static int FindPotentialCSharpGenericDeclarationOpen(string line)
    {
        var whereIndex = line.IndexOf(" where ", StringComparison.Ordinal);
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] != '<')
                continue;
            if (whereIndex >= 0 && index > whereIndex)
                return -1;

            var before = line[..index].TrimEnd();
            if (before.Length == 0)
                continue;
            if (!IsTypeExpressionIdentifierPart("csharp", before[^1]))
                continue;

            return index;
        }

        return -1;
    }

    private static int CountCSharpGenericParameterAngleDelta(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '<')
                depth++;
            else if (c == '>')
                depth--;
        }

        return depth;
    }

    private static void EmitCSharpWhereConstraintSegments(
        string line,
        int listStart,
        int listLength,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string> ignoredSegments)
    {
        if (listLength <= 0)
            return;

        var constraintList = line.Substring(listStart, listLength);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(constraintList))
        {
            var rawSegment = constraintList.Substring(segmentStart, segmentLength).Trim();
            if (rawSegment.Length == 0 || rawSegment.Contains('('))
                continue;
            var absoluteStart = listStart + segmentStart + CountLeadingWhitespace(constraintList, segmentStart, segmentLength);
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment,
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "csharp",
                ignoredSegments);
        }
    }

    internal static void EmitDeclarationTypeReferences(
        string language,
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (TryFindCallableParameterList(line, language, out var callableNameStart, out var paramStart, out var paramEnd))
        {
            if (TryGetCallableReturnTypeSpan(line, callableNameStart, language, out var typeStart, out var typeLength))
            {
                AddTypeExpressionSegmentsForLanguage(
                    language,
                    references,
                    seen,
                    fileId,
                    line.Substring(typeStart, typeLength),
                    typeStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(typeStart),
                    ignoredSegments);
            }

            EmitParameterTypeReferences(
                language,
                line,
                paramStart,
                paramEnd,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                ignoredSegments);
        }

        if (TryGetSimpleDeclarationTypeSpan(line, language, out var declarationTypeStart, out var declarationTypeLength))
        {
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                line.Substring(declarationTypeStart, declarationTypeLength),
                declarationTypeStart,
                context,
                lineNumber,
                resolveContainerForColumn(declarationTypeStart),
                ignoredSegments);
        }
    }

    internal static void EmitTypeScriptDeclarationTypeReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int equalsIndex = FindTopLevelAssignmentIndex(line);
        if (equalsIndex < 0)
            return;

        var head = line.Substring(0, equalsIndex);
        var tokens = GetTopLevelTokenSpans(head);
        if (tokens.Count < 2)
            return;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = head.Substring(tokens[first].Start, tokens[first].Length);
            if (token is "export" or "declare")
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return;

        var keyword = head.Substring(tokens[first].Start, tokens[first].Length);
        if (!string.Equals(keyword, "type", StringComparison.Ordinal))
            return;

        int typeStart = SkipWhitespace(line, equalsIndex + 1);
        if (typeStart >= line.Length)
            return;

        int typeEnd = FindTypeScriptTypeExpressionTerminator(line, typeStart);
        if (typeEnd < 0)
            typeEnd = line.Length;

        AddTypeScriptTypeExpressionSegments(
            references,
            seen,
            fileId,
            line.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static int FindTypeScriptTypeExpressionTerminator(string line, int startIndex)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;

        for (int i = startIndex; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(line, i) - 1;
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    line,
                    i,
                    0,
                    string.Empty,
                    0,
                    [],
                    [],
                    0,
                    null,
                    null) - 1;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    return i;
                if (line[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockCommentForTypeExpression(line, i + 2);
                    continue;
                }
            }

            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ';' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return line.Length;
    }

    internal static bool TryFindCallableParameterList(
        string line,
        string language,
        out int callableNameStart,
        out int paramStart,
        out int paramEnd)
    {
        callableNameStart = -1;
        paramStart = -1;
        paramEnd = -1;

        if (IsDefinitelyNotTypeDeclarationLine(line, language))
            return false;

        int openParen = FindFirstTopLevelChar(line, '(');
        if (openParen <= 0)
            return false;
        if (!TryFindCallableName(line, openParen, language, out callableNameStart))
            return false;

        int closeParen = FindMatchingChar(line, openParen, '(', ')');
        if (closeParen < 0)
            return false;

        paramStart = openParen + 1;
        paramEnd = closeParen;
        return true;
    }

    private static bool TryFindCallableName(string line, int openParen, string language, out int nameStart)
    {
        nameStart = -1;
        int i = openParen - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i]))
            i--;
        if (i < 0)
            return false;

        if (line[i] == '>')
        {
            int depth = 1;
            i--;
            while (i >= 0 && depth > 0)
            {
                if (line[i] == '>')
                    depth++;
                else if (line[i] == '<')
                    depth--;
                i--;
            }
            while (i >= 0 && char.IsWhiteSpace(line[i]))
                i--;
        }

        if (i < 0 || !IsTypeExpressionIdentifierPart(language, line[i]))
            return false;
        int end = i + 1;
        while (i >= 0 && IsTypeExpressionIdentifierPart(language, line[i]))
            i--;
        nameStart = i + 1;

        var name = line.Substring(nameStart, end - nameStart);
        if (IsIgnoredCallName(language, name))
            return false;
        return true;
    }

    internal static bool TryGetCallableReturnTypeSpan(string line, int callableNameStart, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;
        var prefix = line.Substring(0, callableNameStart);
        if (prefix.IndexOf('=') >= 0 || prefix.Contains("=>", StringComparison.Ordinal))
            return false;

        var tokens = GetTopLevelTokenSpans(prefix);
        if (tokens.Count == 0)
            return false;

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = prefix.Substring(tokens[i].Start, tokens[i].Length);
            if (IsCallablePrefixModifier(language, token) || token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal))
                continue;
            if (!HasWhitespaceGap(prefix, tokens[i].Start + tokens[i].Length))
                return false;
            typeStart = tokens[i].Start;
            typeLength = tokens[i].Length;
            return true;
        }

        return false;
    }

    private static void EmitParameterTypeReferences(
        string language,
        string line,
        int paramStart,
        int paramEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (paramEnd <= paramStart)
            return;

        var parameterList = line.Substring(paramStart, paramEnd - paramStart);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(parameterList))
        {
            var fragment = parameterList.Substring(segmentStart, segmentLength);
            if (!TryGetParameterTypeRelativeSpan(fragment, language, out var typeRelativeStart, out var typeRelativeLength))
                continue;

            int absoluteStart = paramStart + segmentStart + typeRelativeStart;
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                fragment.Substring(typeRelativeStart, typeRelativeLength),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                ignoredSegments);
        }
    }

    private static void AddTypeExpressionSegmentsForLanguage(
        string language,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (language == "typescript")
        {
            AddTypeScriptTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                expressionStartInLine,
                context,
                lineNumber,
                container);
            return;
        }

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            expression,
            expressionStartInLine,
            context,
            lineNumber,
            container,
            language,
            ignoredSegments: ignoredSegments);
    }

    internal static void AddTypeScriptTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (TypedLanguageReferenceExtractor.TryEmitTypeScriptFunctionTypeExpressionReferences(
                expression,
                expressionStartInLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container))
        {
            return;
        }

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];

            if (c is '\'' or '"')
            {
                i = SkipTypeScriptQuotedString(expression, i);
                continue;
            }

            if (c == '`')
            {
                i = SkipTypeScriptTemplateLiteral(expression, i, references, seen, fileId, expressionStartInLine, context, lineNumber, container);
                continue;
            }

            if (c == '/' && i + 1 < expression.Length)
            {
                if (expression[i + 1] == '/')
                {
                    i = SkipTypeScriptLineComment(expression, i + 2);
                    continue;
                }

                if (expression[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockCommentForTypeExpression(expression, i + 2);
                    continue;
                }
            }

            if (!IsTypeExpressionIdentifierStart("typescript", c))
                continue;

            int segmentStart = i;
            while (i < expression.Length && IsTypeExpressionIdentifierPart("typescript", expression[i]))
                i++;

            var segment = expression.Substring(segmentStart, i - segmentStart);
            if (TypeScriptTypeExpressionIgnoredSegments.Contains(segment))
            {
                i--;
                continue;
            }

            AddTypeReferenceSegment(references, seen, fileId, segment, expressionStartInLine + segmentStart, context, lineNumber, container, "typescript");
            i--;
        }
    }

    private static int SkipTypeScriptQuotedString(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
                return i;

            i++;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptLineComment(string text, int start)
    {
        int i = start;
        while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            i++;
        return Math.Max(start - 1, i - 1);
    }

    private static int SkipTypeScriptBlockComment(string text, int start)
    {
        for (int i = start; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptTemplateLiteral(
        string text,
        int start,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (c == '`')
                return i;

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int holeStart = i + 2;
                int holeEnd = FindMatchingTypeScriptTemplateHoleEnd(text, holeStart);
                if (holeEnd < 0)
                    return text.Length - 1;

                var hole = text.Substring(holeStart, holeEnd - holeStart);
                AddTypeScriptTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    hole,
                    expressionStartInLine + holeStart,
                    context,
                    lineNumber,
                    container);
                i = holeEnd + 1;
                continue;
            }

            i++;
        }

        return text.Length - 1;
    }

    private static int FindMatchingTypeScriptTemplateHoleEnd(string text, int start)
    {
        int braceDepth = 1;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '\'' or '"')
            {
                i = SkipTypeScriptQuotedString(text, i);
                continue;
            }

            if (c == '`')
            {
                i = SkipTypeScriptTemplateLiteral(text, i, new List<ReferenceRecord>(), new HashSet<string>(), 0, 0, string.Empty, 0, null);
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i = SkipTypeScriptLineComment(text, i + 2);
                    continue;
                }

                if (text[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockComment(text, i + 2);
                    continue;
                }
            }

            if (c == '{')
            {
                braceDepth++;
                continue;
            }

            if (c == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool TryGetParameterTypeRelativeSpan(string parameterFragment, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;

        int end = FindTopLevelAssignmentIndex(parameterFragment);
        if (end < 0)
            end = parameterFragment.Length;
        var candidate = parameterFragment.Substring(0, end);
        var tokens = GetTopLevelTokenSpans(candidate);
        if (tokens.Count < 2)
            return false;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = candidate.Substring(tokens[first].Start, tokens[first].Length);
            if (token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal) || IsParameterModifier(language, token))
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return false;

        typeStart = tokens[first].Start;
        int lastTypeToken = tokens.Count - 2;
        while (lastTypeToken >= first)
        {
            var token = candidate.Substring(tokens[lastTypeToken].Start, tokens[lastTypeToken].Length);
            if (IsParameterModifier(language, token))
            {
                lastTypeToken--;
                continue;
            }

            break;
        }

        if (lastTypeToken < first)
            return false;
        typeLength = tokens[lastTypeToken].Start + tokens[lastTypeToken].Length - typeStart;
        return true;
    }

    private static bool TryGetSimpleDeclarationTypeSpan(string line, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;

        if (IsDefinitelyNotTypeDeclarationLine(line, language))
            return false;

        int firstParen = FindFirstTopLevelChar(line, '(');
        int firstTerminator = FindFirstTopLevelChar(line, ';');
        int firstBrace = FindFirstTopLevelChar(line, '{');
        int firstEquals = FindFirstTopLevelChar(line, '=');
        int firstComma = FindFirstTopLevelChar(line, ',');
        int boundary = int.MaxValue;
        if (firstTerminator >= 0) boundary = Math.Min(boundary, firstTerminator);
        if (firstBrace >= 0) boundary = Math.Min(boundary, firstBrace);
        if (firstEquals >= 0) boundary = Math.Min(boundary, firstEquals);
        if (firstComma >= 0) boundary = Math.Min(boundary, firstComma);
        if (boundary == int.MaxValue)
            return false;
        if (firstParen >= 0 && firstParen < boundary)
            return false;

        var head = line.Substring(0, boundary);
        var tokens = GetTopLevelTokenSpans(head);
        if (tokens.Count < 2)
            return false;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = head.Substring(tokens[first].Start, tokens[first].Length);
            if (token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal) || IsDeclarationModifier(language, token))
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return false;

        var declaredNameToken = head.Substring(tokens[^1].Start, tokens[^1].Length);
        if (!IsSimpleDeclarationIdentifier(language, declaredNameToken))
            return false;

        typeStart = tokens[first].Start;
        int lastTypeToken = tokens.Count - 2;
        typeLength = tokens[lastTypeToken].Start + tokens[lastTypeToken].Length - typeStart;
        return true;
    }

    private static bool IsDefinitelyNotTypeDeclarationLine(string line, string language)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return true;
        if (language == "csharp"
            && TryFindFirstTopLevelCSharpArrow(line, out var arrowIndex))
        {
            var commaIndex = FindFirstTopLevelChar(line, ',');
            var semicolonIndex = FindFirstTopLevelChar(line, ';');
            if (commaIndex > arrowIndex && (semicolonIndex < 0 || commaIndex < semicolonIndex))
                return true;
        }

        if (trimmed.StartsWith("using ", StringComparison.Ordinal)
            || trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("package ", StringComparison.Ordinal)
            || trimmed.StartsWith("import ", StringComparison.Ordinal)
            || trimmed.StartsWith("return ", StringComparison.Ordinal)
            || trimmed.StartsWith("throw ", StringComparison.Ordinal)
            || trimmed.StartsWith("if ", StringComparison.Ordinal)
            || trimmed.StartsWith("if(", StringComparison.Ordinal)
            || trimmed.StartsWith("switch ", StringComparison.Ordinal)
            || trimmed.StartsWith("switch(", StringComparison.Ordinal)
            || trimmed.StartsWith("while ", StringComparison.Ordinal)
            || trimmed.StartsWith("while(", StringComparison.Ordinal)
            || trimmed.StartsWith("for ", StringComparison.Ordinal)
            || trimmed.StartsWith("for(", StringComparison.Ordinal)
            || trimmed.StartsWith("foreach ", StringComparison.Ordinal)
            || trimmed.StartsWith("foreach(", StringComparison.Ordinal)
            || trimmed.StartsWith("catch ", StringComparison.Ordinal)
            || trimmed.StartsWith("catch(", StringComparison.Ordinal)
            || trimmed.StartsWith("lock ", StringComparison.Ordinal)
            || trimmed.StartsWith("lock(", StringComparison.Ordinal)
            || trimmed.StartsWith("case ", StringComparison.Ordinal)
            || trimmed.StartsWith("else", StringComparison.Ordinal)
            || trimmed.StartsWith("do", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.StartsWith("class ", StringComparison.Ordinal)
            || trimmed.StartsWith("struct ", StringComparison.Ordinal)
            || trimmed.StartsWith("interface ", StringComparison.Ordinal)
            || trimmed.StartsWith("record ", StringComparison.Ordinal)
            || (language == "java" && trimmed.StartsWith("enum ", StringComparison.Ordinal));
    }

    internal static void EmitCatchTypeReferences(
        string language,
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (language is not ("csharp" or "java" or "kotlin"))
            return;

        var catchIndex = FindTopLevelKeyword(line, "catch");
        if (catchIndex < 0)
            return;

        var openParen = line.IndexOf('(', catchIndex + "catch".Length);
        if (openParen < 0)
            return;

        var closeParen = FindMatchingChar(line, openParen, '(', ')');
        if (closeParen < 0 || closeParen <= openParen + 1)
            return;

        var clauseStart = openParen + 1;
        var clause = line.Substring(clauseStart, closeParen - clauseStart);
        if (language == "kotlin")
        {
            EmitKotlinCatchTypeReference(
                clause,
                clauseStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            return;
        }

        EmitCStyleCatchTypeReferences(
            language,
            clause,
            clauseStart,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitKotlinCatchTypeReference(
        string clause,
        int clauseStartInLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(clause, ':');
        if (colonIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(clause, colonIndex + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(clause, typeStart);
        if (typeEnd <= typeStart)
            return;

        var absoluteStart = clauseStartInLine + typeStart;
        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            clause.Substring(typeStart, typeEnd - typeStart),
            absoluteStart,
            context,
            lineNumber,
            resolveContainerForColumn(absoluteStart),
            "kotlin");
    }

    private static void EmitCStyleCatchTypeReferences(
        string language,
        string clause,
        int clauseStartInLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var start = SkipCatchParameterPrefix(language, clause, 0);
        var end = clause.Length;
        while (end > start && char.IsWhiteSpace(clause[end - 1]))
            end--;
        if (end <= start)
            return;

        var typeEnd = FindCatchTypeEndBeforeVariable(language, clause, start, end);
        if (typeEnd <= start)
            return;

        var typeExpression = clause.Substring(start, typeEnd - start);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelPipeSpans(typeExpression))
        {
            var leading = CountLeadingWhitespace(typeExpression, segmentStart, segmentLength);
            var trimmedLength = segmentLength - leading;
            while (trimmedLength > 0 && char.IsWhiteSpace(typeExpression[segmentStart + leading + trimmedLength - 1]))
                trimmedLength--;
            if (trimmedLength <= 0)
                continue;

            var absoluteStart = clauseStartInLine + start + segmentStart + leading;
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeExpression.Substring(segmentStart + leading, trimmedLength),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                language);
        }
    }

    private static int SkipCatchParameterPrefix(string language, string clause, int start)
    {
        var i = start;
        while (i < clause.Length)
        {
            while (i < clause.Length && char.IsWhiteSpace(clause[i]))
                i++;

            if (language == "java" && i < clause.Length && clause[i] == '@')
            {
                i = SkipJavaAnnotation(clause, i) + 1;
                continue;
            }

            if (language == "java" && IsWordAt(clause, i, "final"))
            {
                i += "final".Length;
                continue;
            }

            break;
        }

        return i;
    }

    private static int FindCatchTypeEndBeforeVariable(string language, string clause, int start, int end)
    {
        if (!TryFindLastIdentifier(clause, start, end, out var lastStart, out _))
            return end;

        var before = clause.Substring(start, lastStart - start).TrimEnd();
        if (language == "csharp" && before.EndsWith("@", StringComparison.Ordinal))
        {
            var prefix = before.Substring(0, before.Length - 1).TrimEnd();
            if (prefix.Length == 0
                || prefix.EndsWith(".", StringComparison.Ordinal)
                || prefix.EndsWith("::", StringComparison.Ordinal))
            {
                return end;
            }

            return lastStart - 1;
        }

        if (before.Length == 0
            || before.EndsWith(".", StringComparison.Ordinal)
            || before.EndsWith("::", StringComparison.Ordinal))
        {
            return end;
        }

        return lastStart;
    }

    private static bool TryFindLastIdentifier(string text, int start, int end, out int identifierStart, out int identifierEnd)
    {
        identifierStart = -1;
        identifierEnd = -1;
        var i = end - 1;
        while (i >= start && char.IsWhiteSpace(text[i]))
            i--;
        if (i < start || !IsJavaIdentifierPart(text[i]))
            return false;

        identifierEnd = i + 1;
        while (i >= start && IsJavaIdentifierPart(text[i]))
            i--;
        identifierStart = i + 1;
        return identifierStart < identifierEnd;
    }

    private static List<(int Start, int Length)> SplitTopLevelPipeSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '|' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static bool IsWordAt(string text, int index, string word)
    {
        if (index + word.Length > text.Length)
            return false;
        if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0)
            return false;
        if (index > 0 && IsJavaIdentifierPart(text[index - 1]))
            return false;
        var after = index + word.Length;
        return after >= text.Length || !IsJavaIdentifierPart(text[after]);
    }

    private static bool TryFindFirstTopLevelCSharpArrow(string text, out int arrowIndex)
    {
        arrowIndex = -1;
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case '=':
                    if (text[i + 1] == '>'
                        && angleDepth == 0
                        && parenDepth == 0
                        && squareDepth == 0
                        && braceDepth == 0)
                    {
                        arrowIndex = i;
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    internal static void AddTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (language == "typescript")
        {
            AddTypeScriptTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                expressionStartInLine,
                context,
                lineNumber,
                container,
                ignoredSegments);
            return;
        }

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (language == "rust"
                && c == '\''
                && i + 1 < expression.Length
                && IsRustLifetimeStart(expression[i + 1]))
            {
                var lifetimeStart = i;
                i += 2;
                while (i < expression.Length && IsRustLifetimePart(expression[i]))
                    i++;

                var lifetime = expression.Substring(lifetimeStart, i - lifetimeStart);
                AddReference(references, seen, fileId, lifetime, expressionStartInLine + lifetimeStart, "lifetime_reference", context, lineNumber, container);
                i--;
                continue;
            }

            if (language == "rust"
                && c == 'r'
                && i + 2 < expression.Length
                && expression[i + 1] == '#'
                && IsJavaIdentifierStart(expression[i + 2]))
            {
                var rustSegmentStart = i;
                i += 2;
                var rawNameStart = i;
                i++;
                while (i < expression.Length && IsJavaIdentifierPart(expression[i]))
                    i++;

                var rustSegment = expression.Substring(rawNameStart, i - rawNameStart);
                if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
                {
                    i++;
                    continue;
                }

                AddReference(references, seen, fileId, rustSegment, expressionStartInLine + rustSegmentStart, "type_reference", context, lineNumber, container);
                i--;
                continue;
            }

            if (language is "java" or "kotlin" or "swift" && c == '@')
            {
                i = SkipJavaAnnotation(expression, i);
                continue;
            }

            if (language == "kotlin" && c == '`')
            {
                var closeIndex = expression.IndexOf('`', i + 1);
                if (closeIndex < 0)
                    continue;

                var backtickSegment = expression.Substring(i + 1, closeIndex - i - 1);
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    backtickSegment,
                    expressionStartInLine + i,
                    context,
                    lineNumber,
                    container,
                    language,
                    ignoredSegments: ignoredSegments);
                i = closeIndex;
                continue;
            }

            if (language == "vb" && c == '[')
            {
                var closeIndex = expression.IndexOf(']', i + 1);
                if (closeIndex < 0)
                    continue;

                var escapedSegment = expression.Substring(i + 1, closeIndex - i - 1);
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    escapedSegment,
                    expressionStartInLine + i,
                    context,
                    lineNumber,
                    container,
                    language,
                    ignoredSegments: ignoredSegments);
                i = closeIndex;
                continue;
            }

            if (!IsTypeExpressionIdentifierStart(language, c))
                continue;

            int segmentStart = i;
            if (language == "csharp" && expression[i] == '@')
                i++;
            while (i < expression.Length && IsTypeExpressionIdentifierPart(language, expression[i]))
                i++;

            if (language == "rust" && segmentStart > 0 && expression[segmentStart - 1] == '\'')
            {
                i--;
                continue;
            }

            var rawSegment = expression.Substring(segmentStart, i - segmentStart);
            var isEscapedCSharpIdentifier = language == "csharp" && rawSegment.Length > 0 && rawSegment[0] == '@';
            var segment = rawSegment;
            if (language == "csharp")
                segment = NormalizeCSharpIdentifier(rawSegment);

            if (language == "kotlin" && KotlinTypeProjectionModifierNames.Contains(segment))
            {
                i--;
                continue;
            }

            if (language == "swift" && IsSwiftTupleElementLabelSegment(expression, segmentStart, i))
            {
                i--;
                continue;
            }

            if (language == "swift" && IsSwiftMetatypeSuffixSegment(expression, segmentStart, segment))
            {
                i--;
                continue;
            }

            if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
            {
                i++;
                continue;
            }

            AddTypeReferenceSegment(references, seen, fileId, segment, expressionStartInLine + segmentStart, context, lineNumber, container, language, isEscapedCSharpIdentifier, ignoredSegments);
            i--;
        }
    }

    private static bool IsRustLifetimeStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsRustLifetimePart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static bool IsSwiftTupleElementLabelSegment(string expression, int segmentStart, int segmentEnd)
    {
        var next = segmentEnd;
        while (next < expression.Length && char.IsWhiteSpace(expression[next]))
            next++;
        if (next >= expression.Length || expression[next] != ':')
            return false;
        if (next + 1 < expression.Length && expression[next + 1] == ':')
            return false;

        var previous = segmentStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
            previous--;

        return previous >= 0 && expression[previous] is '(' or ',';
    }

    private static bool IsSwiftMetatypeSuffixSegment(string expression, int segmentStart, string segment)
    {
        if (segment is not ("Type" or "Protocol"))
            return false;

        var previous = segmentStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
            previous--;

        return previous >= 0 && expression[previous] == '.';
    }

    private static void AddTypeScriptTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        ignoredSegments ??= TypeScriptTypeExpressionIgnoredSegments;

        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(expression, i);
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    expression,
                    i,
                    expressionStartInLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container,
                    ignoredSegments);
                continue;
            }

            if (!IsJavaIdentifierStart(c))
            {
                i++;
                continue;
            }

            int segmentStart = i;
            i++;
            while (i < expression.Length && IsJavaIdentifierPart(expression[i]))
                i++;

            var segment = expression.Substring(segmentStart, i - segmentStart);
            if (TypeScriptTypeExpressionIgnoredSegments.Contains(segment)
                || ignoredSegments != null && ignoredSegments.Contains(segment))
            {
                continue;
            }
            if (IsTypeScriptTypeLabelSegment(expression, i))
                continue;

            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                segment,
                expressionStartInLine + segmentStart,
                context,
                lineNumber,
                container,
                "typescript",
                ignoredSegments: ignoredSegments);
        }
    }

    private static bool IsTypeScriptTypeLabelSegment(string expression, int segmentEnd)
    {
        var next = segmentEnd;
        while (next < expression.Length && char.IsWhiteSpace(expression[next]))
            next++;

        return next < expression.Length
            && expression[next] == ':'
            && (next + 1 >= expression.Length || expression[next + 1] != ':');
    }

    private static int ScanTypeScriptTemplateLiteralForTypeExpression(
        string expression,
        int startIndex,
        int expressionStartInLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments)
    {
        int i = startIndex + 1;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c == '\\')
            {
                i += Math.Min(2, expression.Length - i);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(expression, i);
                continue;
            }

            if (c == '$' && i + 1 < expression.Length && expression[i + 1] == '{')
            {
                int holeStart = i + 2;
                int holeEnd = FindMatchingTypeScriptHoleEndForTypeExpression(expression, holeStart);
                if (holeEnd < 0)
                    return expression.Length;

                AddTypeScriptTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    expression.Substring(holeStart, holeEnd - holeStart),
                    expressionStartInLine + holeStart,
                    context,
                    lineNumber,
                    container,
                    ignoredSegments);
                i = holeEnd + 1;
                continue;
            }

            if (c == '`')
                return i + 1;

            i++;
        }

        return expression.Length;
    }

    private static int FindMatchingTypeScriptHoleEndForTypeExpression(string text, int startIndex)
    {
        int braceDepth = 0;
        int i = startIndex;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(text, i);
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    text,
                    i,
                    0,
                    string.Empty,
                    0,
                    [],
                    [],
                    0,
                    null,
                    null);
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (braceDepth == 0)
                    return i;
                braceDepth--;
                i++;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static int SkipTypeScriptStringLiteral(string text, int startIndex)
    {
        char quote = text[startIndex];
        int i = startIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (text[i] == quote)
                return i + 1;

            i++;
        }

        return text.Length;
    }

    private static int SkipTypeScriptBlockCommentForTypeExpression(string text, int startIndex)
    {
        for (int i = startIndex; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static int SkipBalanced(string line, int start, char open, char close)
    {
        int depth = 0;
        int i = start;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == open)
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth <= 0)
                    return i + 1;
            }
            i++;
        }
        return i;
    }

    internal static int SkipJavaAnnotation(string text, int start)
    {
        int i = start + 1;
        var annotationStart = i;
        while (i < text.Length && IsJavaIdentifierPart(text[i]))
            i++;
        if (i < text.Length && text[i] == ':')
        {
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
        }
        else
        {
            i = annotationStart;
        }

        if (i < text.Length && text[i] == '`')
        {
            var closeIndex = text.IndexOf('`', i + 1);
            if (closeIndex < 0)
                return start;
            i = closeIndex + 1;
        }
        else
        {
            while (i < text.Length && (IsJavaIdentifierPart(text[i]) || text[i] == '.'))
                i++;
        }

        if (i < text.Length && text[i] == '(')
        {
            int close = FindMatchingChar(text, i, '(', ')');
            if (close >= 0)
                return close;
        }

        return i - 1;
    }

    internal static int FindMatchingChar(string text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
                depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int FindFirstTopLevelChar(string text, char target)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == target && angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0)
                return i;

            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }
        }

        return -1;
    }

    private static int FindTopLevelAssignmentIndex(string text)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case '=' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    if (i + 1 >= text.Length || text[i + 1] != '>')
                        return i;
                    break;
            }
        }

        return -1;
    }

    internal static List<(int Start, int Length)> GetTopLevelTokenSpans(string text)
    {
        var tokens = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int tokenStart = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }

            bool topLevelWhitespace = char.IsWhiteSpace(c) && angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0;
            if (topLevelWhitespace)
            {
                if (tokenStart >= 0)
                {
                    tokens.Add((tokenStart, i - tokenStart));
                    tokenStart = -1;
                }
                continue;
            }

            if (tokenStart < 0)
                tokenStart = i;
        }

        if (tokenStart >= 0)
            tokens.Add((tokenStart, text.Length - tokenStart));
        return tokens;
    }

    internal static List<(int Start, int Length)> SplitTopLevelCommaSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    internal static List<(int Start, int Length)> SplitTopLevelAmpersandSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case '&' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    internal static int CountLeadingWhitespace(string text, int start, int length)
    {
        int count = 0;
        while (count < length && char.IsWhiteSpace(text[start + count]))
            count++;
        return count;
    }

    internal static int FindTypeListTerminator(string text, bool allowArrow)
    {
        int brace = FindFirstTopLevelChar(text, '{');
        int semi = FindFirstTopLevelChar(text, ';');
        int end = -1;
        if (brace >= 0) end = brace;
        if (semi >= 0 && (end < 0 || semi < end)) end = semi;
        if (allowArrow)
        {
            int arrow = text.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0 && (end < 0 || arrow < end))
                end = arrow;
        }
        return end;
    }

    private static string TrimTrailingTypeListTerminator(string text)
    {
        int end = FindTypeListTerminator(text, allowArrow: true);
        return end >= 0 ? text.Substring(0, end) : text;
    }

    internal static int FindJavaTypeListTerminator(string text, int start)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
                angleDepth++;
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
                parenDepth++;
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0)
            {
                if (c == '{' || c == ';')
                    return i;
                if (IsJavaBaseListTerminatorKeyword(text, i, start, "implements")
                    || IsJavaBaseListTerminatorKeyword(text, i, start, "permits")
                    || IsJavaBaseListTerminatorKeyword(text, i, start, "throws"))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    internal static int FindTopLevelKeyword(string text, string keyword)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }

            if (angleDepth != 0 || parenDepth != 0 || squareDepth != 0 || braceDepth != 0)
                continue;
            if (i > 0 && IsJavaIdentifierPart(text[i - 1]))
                continue;
            if (i + keyword.Length > text.Length || string.CompareOrdinal(text, i, keyword, 0, keyword.Length) != 0)
                continue;
            int after = i + keyword.Length;
            if (after < text.Length && IsJavaIdentifierPart(text[after]))
                continue;
            return i;
        }

        return -1;
    }

    private static bool IsCallablePrefixModifier(string language, string token) =>
        language == "csharp"
            ? token is "public" or "private" or "protected" or "internal" or "file" or "static" or "readonly" or "required" or "volatile" or "const"
                or "unsafe" or "new" or "sealed" or "abstract" or "virtual" or "override" or "extern" or "partial" or "async" or "ref" or "scoped"
            : token is "public" or "private" or "protected" or "static" or "final" or "abstract" or "synchronized" or "native" or "strictfp" or "default";

    private static bool IsParameterModifier(string language, string token) =>
        language == "csharp"
            ? token is "ref" or "out" or "in" or "params" or "this" or "scoped" or "readonly"
            : token is "final";

    private static bool IsDeclarationModifier(string language, string token) =>
        language == "csharp"
            ? token is "public" or "private" or "protected" or "internal" or "file" or "static" or "readonly" or "required" or "volatile" or "const"
                or "unsafe" or "new" or "sealed" or "abstract" or "virtual" or "override" or "extern" or "partial" or "async" or "ref" or "scoped" or "event"
            : token is "public" or "private" or "protected" or "static" or "final" or "abstract" or "volatile" or "transient" or "synchronized" or "native" or "strictfp";

    private static bool IsSimpleDeclarationIdentifier(string language, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        if (!IsTypeExpressionIdentifierStart(language, token[0]))
            return false;
        for (int i = 1; i < token.Length; i++)
        {
            if (!IsTypeExpressionIdentifierPart(language, token[i]))
                return false;
        }

        return true;
    }

    private static bool HasWhitespaceGap(string text, int start)
    {
        if (start >= text.Length)
            return false;
        for (int i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return true;
    }

    private static string NormalizeCSharpDocCref(string cref)
    {
        var text = cref.Trim();
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
            text = text.Substring(2);
        int paren = text.IndexOf('(');
        if (paren >= 0)
            text = text.Substring(0, paren);
        int brace = text.IndexOf('{');
        if (brace >= 0)
            text = text.Substring(0, brace);
        return text.Trim();
    }

    private static bool IsCSharpIdentifierStart(char c) =>
        c == '_' || c == '@' || char.IsLetter(c);

    private static bool IsJavaIdentifierStart(char c) =>
        c == '_' || c == '$' || char.IsLetter(c);

    private static bool IsTypeExpressionIdentifierStart(string language, char c) =>
        language == "csharp" ? IsCSharpIdentifierStart(c) : IsJavaIdentifierStart(c);

    private static bool IsTypeExpressionIdentifierPart(string language, char c) =>
        language == "csharp" ? IsCSharpIdentifierPart(c) : IsJavaIdentifierPart(c);

    private static bool IsTypeScriptTypeQueryContext(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string line,
        List<(int Start, int Length)> tokens,
        int keywordIndex)
    {
        for (int i = 0; i < keywordIndex; i++)
        {
            var token = line.Substring(tokens[i].Start, tokens[i].Length);
            if (TypeScriptTypeQueryDisqualifyingTokens.Contains(token))
                return false;

            if (TypeScriptTypeQueryContextTokens.Contains(token))
                return true;
        }

        if (keywordIndex == 0)
            return HasTypeScriptTypeQueryLeadingContext(preparedLines, lineIndex);

        var previousToken = line.Substring(tokens[keywordIndex - 1].Start, tokens[keywordIndex - 1].Length);
        return previousToken.EndsWith(':');
    }

    private static bool HasTypeScriptTypeQueryLeadingContext(IReadOnlyList<string> preparedLines, int lineIndex)
    {
        for (int previousIndex = lineIndex - 1; previousIndex >= 0; previousIndex--)
        {
            var previousLine = preparedLines[previousIndex];
            if (string.IsNullOrWhiteSpace(previousLine))
                continue;

            if (IsTypeScriptTypeQueryLineContext(previousLine))
                return true;

            if (!IsTypeScriptTypeQueryContinuationLine(previousLine))
                return false;
        }

        return false;
    }

    private static bool IsTypeScriptTypeQueryLineContext(string line)
    {
        var tokens = GetTopLevelTokenSpans(line);
        foreach (var token in tokens)
        {
            var text = line.Substring(token.Start, token.Length);
            if (TypeScriptTypeQueryDisqualifyingTokens.Contains(text))
                return false;
            if (TypeScriptTypeQueryContextTokens.Contains(text))
                return true;
        }

        return false;
    }

    private static bool IsTypeScriptTypeQueryContinuationLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        return trimmed[^1] is '<' or '(' or '[' or ',' or '|' or '&' or ':';
    }

    private static bool TryExtractTypeScriptTypeQueryTarget(
        string line,
        int startIndex,
        out int targetStart,
        out int targetLength,
        out string? literalTarget)
    {
        targetStart = 0;
        targetLength = 0;
        literalTarget = null;

        var cursor = startIndex;
        while (cursor < line.Length)
        {
            cursor = SkipWhitespace(line, cursor);
            if (cursor >= line.Length)
                return false;

            if (TryConsumeTypeScriptTypeQueryWrapper(line, cursor, "typeof", out cursor))
                continue;

            if (TryConsumeTypeScriptImportTypeWrapper(line, cursor, out cursor, out var importModuleStart, out var importModuleLength))
            {
                if (cursor >= line.Length || line[cursor] != '.')
                {
                    targetStart = importModuleStart;
                    targetLength = importModuleLength;
                    literalTarget = line.Substring(importModuleStart, importModuleLength);
                    return targetLength > 0;
                }

                continue;
            }

            if (line[cursor] == '(' || line[cursor] == '[')
            {
                cursor++;
                continue;
            }

            break;
        }

        cursor = SkipWhitespace(line, cursor);
        while (cursor < line.Length && line[cursor] == '.')
        {
            cursor++;
            cursor = SkipWhitespace(line, cursor);
        }

        if (cursor >= line.Length || !IsJavaIdentifierStart(line[cursor]))
            return false;

        var end = cursor + 1;
        while (end < line.Length && (IsJavaIdentifierPart(line[end]) || line[end] == '.'))
            end++;

        targetStart = cursor;
        targetLength = end - cursor;
        return targetLength > 0;
    }

    private static bool TryConsumeTypeScriptTypeQueryWrapper(
        string line,
        int cursor,
        string keyword,
        out int nextCursor)
    {
        nextCursor = cursor;
        if (cursor + keyword.Length > line.Length
            || !line.AsSpan(cursor, keyword.Length).Equals(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + keyword.Length;
        if (nextIndex < line.Length && (char.IsLetterOrDigit(line[nextIndex]) || line[nextIndex] == '_'))
            return false;

        nextCursor = nextIndex;
        return true;
    }

    private static bool TryConsumeTypeScriptImportTypeWrapper(
        string line,
        int cursor,
        out int nextCursor,
        out int moduleStart,
        out int moduleLength)
    {
        nextCursor = cursor;
        moduleStart = -1;
        moduleLength = 0;
        if (cursor + "import".Length > line.Length
            || !line.AsSpan(cursor, "import".Length).Equals("import", StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + "import".Length;
        if (nextIndex < line.Length && (char.IsLetterOrDigit(line[nextIndex]) || line[nextIndex] == '_'))
            return false;

        nextIndex = SkipWhitespace(line, nextIndex);
        if (nextIndex >= line.Length || line[nextIndex] != '(')
            return false;

        var moduleQuoteIndex = SkipWhitespace(line, nextIndex + 1);
        if (moduleQuoteIndex >= line.Length || line[moduleQuoteIndex] is not '\'' and not '"')
            return false;

        var moduleLiteralStart = moduleQuoteIndex + 1;
        var moduleLiteralEnd = SkipTypeScriptStringLiteral(line, moduleQuoteIndex) - 1;
        if (moduleLiteralEnd < moduleLiteralStart)
            return false;

        var closeIndex = SkipBalanced(line, nextIndex, '(', ')');
        if (closeIndex <= nextIndex)
            return false;

        moduleStart = moduleLiteralStart;
        moduleLength = moduleLiteralEnd - moduleLiteralStart;
        nextCursor = closeIndex;
        return true;
    }
}
