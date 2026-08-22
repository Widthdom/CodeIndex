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
        foreach (Match match in BoundedRegex.EnumerateMatches(introRegex, text))
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("typeof", StringComparison.Ordinal) < 0
            && preparedLine.IndexOf("keyof", StringComparison.Ordinal) < 0)
        {
            return;
        }

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
        ReferenceDedupeSet seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in EnumerateReferenceMatches(CSharpDocCrefRegex, originalLine, references))
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
        ReferenceDedupeSet seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in EnumerateReferenceMatches(JvmDocInlineLinkRegex, docText, references))
        {
            EmitJvmDocTargetReference(language, match.Groups["target"], references, seen, fileId, columnOffset, context, lineNumber, container);
        }

        foreach (Match match in EnumerateReferenceMatches(JvmDocSeeReferenceRegex, docText, references))
        {
            EmitJvmDocTargetReference(language, match.Groups["target"], references, seen, fileId, columnOffset, context, lineNumber, container);
        }

        if (language == "kotlin")
        {
            foreach (Match match in EnumerateReferenceMatches(KDocBracketLinkRegex, docText, references))
            {
                EmitJvmDocTargetReference(language, match.Groups["target"], references, seen, fileId, columnOffset, context, lineNumber, container);
            }
        }
    }

    private static void EmitJvmDocTargetReference(
        string language,
        Group targetGroup,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (line.IndexOf(':') < 0)
            return;

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
            var segmentLeading = CountLeadingWhitespace(baseList, segmentStart, segmentLength);
            var rawSegmentLength = segmentLength - segmentLeading;
            while (rawSegmentLength > 0 && char.IsWhiteSpace(baseList[segmentStart + segmentLeading + rawSegmentLength - 1]))
                rawSegmentLength--;
            if (rawSegmentLength == 0)
                continue;

            var rawSegment = baseList.AsSpan(segmentStart + segmentLeading, rawSegmentLength);
            if (rawSegment.Contains('('))
                continue;

            var absoluteStart = colonIndex + 1 + segmentStart + segmentLeading;
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment.ToString(),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "csharp",
                ignoredSegments: ignoredSegments);
        }
    }

    private static readonly IReadOnlySet<string>
        EmptyCSharpGenericParameterNames =
            new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string>
        CollectCSharpGenericParameterNamesForDeclaration(string line)
    {
        // A declaration cannot introduce generic parameters without an angle
        // bracket. Most source lines take this path, so share the immutable-by-
        // contract empty set instead of tokenizing and allocating a new set.
        if (line.IndexOf('<') < 0)
            return EmptyCSharpGenericParameterNames;

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
            return EmptyCSharpGenericParameterNames;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = line.Substring(tokens[i].Start, tokens[i].Length);
            if (token is not ("class" or "struct" or "interface" or "record" or "delegate"))
                continue;
            var nameIndex = i + 1;
            if (nameIndex >= tokens.Count)
                return EmptyCSharpGenericParameterNames;
            var nameToken = line.Substring(tokens[nameIndex].Start, tokens[nameIndex].Length);
            var genericOpen = nameToken.IndexOf('<');
            if (genericOpen < 0)
                return EmptyCSharpGenericParameterNames;
            return CollectCSharpGenericParameterNamesFromClause(line, tokens[nameIndex].Start + genericOpen);
        }

        return EmptyCSharpGenericParameterNames;
    }

    private static HashSet<string> CollectCSharpGenericParameterNamesFromClause(string line, int genericOpen)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var genericClose = FindMatchingChar(line, genericOpen, '<', '>');
        if (genericClose <= genericOpen)
            return names;

        var clause = line.AsSpan(genericOpen + 1, genericClose - genericOpen - 1);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Slice(segmentStart, segmentLength);
            if (TryReadCSharpGenericParameterName(fragment, out var name))
                names.Add(name);
        }

        return names;
    }

    private static bool TryReadCSharpGenericParameterName(ReadOnlySpan<char> fragment, out string name)
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

            var token = NormalizeCSharpIdentifier(fragment.Slice(tokenStart, index - tokenStart).ToString());
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string> declarationGenericParameterNames,
        CSharpWhereConstraintState pendingWhereConstraint)
    {
        UpdateCSharpWhereHeaderGenericParameterNames(line, declarationGenericParameterNames, pendingWhereConstraint);

        if (!pendingWhereConstraint.Active
            && line.IndexOf("where", StringComparison.Ordinal) < 0)
        {
            if (FindTypeListTerminator(line, allowArrow: true) >= 0)
                pendingWhereConstraint.HeaderGenericParameterNames.Clear();
            return;
        }

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

        // Both passes need the same match objects; keep this intentional materialization
        // instead of rescanning every `where` clause.
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
            var remaining = line.AsSpan(listStart);
            var nextWhereMatch = CSharpWhereClauseRegex.Match(line, listStart);
            int nextWhere = nextWhereMatch.Success ? nextWhereMatch.Index - listStart : -1;
            int terminator = FindTypeListTerminator(remaining, allowArrow: true);
            int end = terminator;
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

            if (listStart + end >= line.Length && nextWhere < 0 && terminator < 0)
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string> ignoredSegments)
    {
        if (listLength <= 0)
            return;

        var constraintList = line.AsSpan(listStart, listLength);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(constraintList))
        {
            var segmentLeading = CountLeadingWhitespace(constraintList, segmentStart, segmentLength);
            var rawSegmentLength = segmentLength - segmentLeading;
            while (rawSegmentLength > 0 && char.IsWhiteSpace(constraintList[segmentStart + segmentLeading + rawSegmentLength - 1]))
                rawSegmentLength--;
            if (rawSegmentLength == 0)
                continue;

            var rawSegment = constraintList.Slice(segmentStart + segmentLeading, rawSegmentLength);
            if (rawSegment.Contains('('))
                continue;

            var absoluteStart = listStart + segmentStart + segmentLeading;
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                rawSegment.ToString(),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                "csharp",
                ignoredSegments);
        }
    }

}
