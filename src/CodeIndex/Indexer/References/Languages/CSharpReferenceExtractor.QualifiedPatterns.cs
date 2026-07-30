using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void EmitCSharpQualifiedEnumMemberReferences(
        string preparedLine,
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> enumMemberLookup,
        IReadOnlyList<(int start, int end)>? csharpAttrRangesOnLine,
        IReadOnlyList<CSharpUsingAliasRecord> usingAliases,
        Func<IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames>> getValueReceiverNamesByContainingType,
        Func<IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>>> getValueReceiverNamesByFunctionStartLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var scan = 0;
        while (scan < preparedLine.Length)
        {
            if (!TryReadCSharpQualifiedAccess(preparedLine, scan, out var parsed))
            {
                scan++;
                continue;
            }

            scan = Math.Max(scan + 1, parsed.NextIndex);
            if (!parsed.LastSeparatorWasDot || parsed.Segments.Count < 2)
                continue;

            var member = parsed.Segments[^1];
            var memberName = preparedLine.Substring(member.Start, member.End - member.Start);
            if (!enumMemberLookup.TryGetValue(memberName, out var targets))
                continue;

            var callContainer = resolveContainerForCall(member.Start);
            var qualifier = TrimLeadingCSharpGlobalQualifier(NormalizeCSharpQualifiedSegments(preparedLine, parsed.Segments, parsed.Segments.Count - 1));
            var resolvedQualifier = parsed.HasLeadingGlobalQualifier
                ? qualifier
                : ResolveCSharpQualifiedAliasTarget(qualifier, lineNumber, usingAliases);
            if (!parsed.HasLeadingGlobalQualifier
                && HasCSharpValueReceiverConflict(
                    qualifier,
                    resolvedQualifier,
                    lineNumber,
                    member.Start,
                    callContainer,
                    getValueReceiverNamesByContainingType(),
                    getValueReceiverNamesByFunctionStartLine()))
            {
                continue;
            }
            if (!MatchesQualifiedConstantContainer(
                    resolvedQualifier,
                    targets,
                    allowShortNameFallback: !parsed.HasLeadingGlobalQualifier,
                    allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier))
                continue;

            var nextTokenIndex = SkipWhitespace(preparedLine, member.End);
            if (nextTokenIndex < preparedLine.Length && preparedLine[nextTokenIndex] == '(')
                continue;
            if (IsCSharpSimpleAssignmentTarget(preparedLine, nextTokenIndex))
                continue;

            var insideCSharpAttributeRange = csharpAttrRangesOnLine != null
                && IsInsideCSharpAttributeRange(csharpAttrRangesOnLine, member.Start);
            var referenceKind = TryClassifyMetadataReference("csharp", preparedLine, member.Start, insideCSharpAttributeRange) ?? "member_read";

            AddReference(
                references,
                seen,
                fileId,
                memberName,
                member.Start,
                referenceKind,
                context,
                lineNumber,
                callContainer);
        }
    }

    private static bool IsCSharpSimpleAssignmentTarget(string preparedLine, int nextTokenIndex)
        => nextTokenIndex < preparedLine.Length
            && preparedLine[nextTokenIndex] == '='
            && (nextTokenIndex + 1 >= preparedLine.Length
                || preparedLine[nextTokenIndex + 1] is not ('=' or '>'));

    private static bool IsCSharpQualifiedConstantPatternReferenceSite(
        string preparedLine,
        (IReadOnlyList<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed)
    {
        if (!parsed.LastSeparatorWasDot || parsed.Segments.Count < 2)
            return false;

        var headCursor = parsed.Segments[0].Start;
        if (parsed.HasLeadingGlobalQualifier
            && headCursor >= "global::".Length
            && preparedLine.AsSpan(headCursor - "global::".Length, "global::".Length).Equals("global::", StringComparison.Ordinal))
        {
            headCursor -= "global::".Length;
        }

        return IsCSharpConstantPatternAnchor(preparedLine, ref headCursor);
    }

    private static bool IsCSharpConstantPatternAnchor(string text, ref int cursor)
    {
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(text, cursor);

        while (true)
        {
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "case"))
                return true;

            if (TryConsumeTrailingCSharpToken(text, ref cursor, "is"))
                return false;

            if (!TryConsumeTrailingCSharpToken(text, ref cursor, "or")
                && !TryConsumeTrailingCSharpToken(text, ref cursor, "and"))
            {
                return false;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!SkipCSharpPatternHeadBackward(text, ref cursor))
                return false;
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
                cursor = SkipCSharpTriviaBackward(text, cursor);
        }
    }

    private static int SkipCSharpTriviaBackward(string text, int cursor)
    {
        while (cursor > 0)
        {
            if (char.IsWhiteSpace(text[cursor - 1]))
            {
                cursor--;
                continue;
            }

            if (cursor >= 2
                && text[cursor - 1] == '/'
                && text[cursor - 2] == '*')
            {
                var commentStart = text.LastIndexOf("/*", cursor - 2, StringComparison.Ordinal);
                if (commentStart >= 0)
                {
                    cursor = commentStart;
                    continue;
                }
            }

            break;
        }

        return cursor;
    }

    internal static bool IsCSharpPatternHeadCallSite(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
    {
        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(preparedLine);
        if (whenOffset >= 0 && nameIndex > whenOffset)
            return false;

        var cursor = nameIndex;
        if (IsCSharpConstantPatternAnchor(preparedLine, ref cursor))
            return true;

        cursor = nameIndex;
        cursor = SkipCSharpTriviaBackward(preparedLine, cursor);
        if (TryConsumeTrailingCSharpToken(preparedLine, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(preparedLine, cursor);

        if (TryConsumeTrailingCSharpToken(preparedLine, ref cursor, "is"))
            return true;

        for (var previous = lineIndex - 1; previous >= 0; previous--)
        {
            var previousLine = preparedLines[previous];
            if (string.IsNullOrWhiteSpace(previousLine))
                continue;

            if (LineEndsWithCSharpToken(previousLine, "case")
                || LineEndsWithCSharpToken(previousLine, "is")
                || LineEndsWithCSharpToken(previousLine, "not"))
            {
                return true;
            }

            break;
        }

        // Switch-expression arms (`Point(...) => ...`) do not have a `case` / `is` anchor,
        // so the same positional pattern suppression has to look for the trailing arrow.
        if (IsCSharpSwitchExpressionPatternHead(preparedLines, lineIndex, preparedLine, nameIndex))
            return true;

        return false;
    }

    private static bool IsCSharpSwitchExpressionPatternHead(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
    {
        var cursor = nameIndex;
        while (cursor < preparedLine.Length && IsCSharpIdentifierPart(preparedLine[cursor]))
            cursor++;

        cursor = SkipCSharpTriviaForward(preparedLine, cursor);

        var openParenIndex = preparedLine.IndexOf('(', cursor);
        if (openParenIndex < 0)
            return false;

        var parenDepth = 0;
        for (var i = openParenIndex; i < preparedLine.Length; i++)
        {
            switch (preparedLine[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        var afterClose = SkipCSharpTriviaForward(preparedLine, i + 1);
                        if (afterClose + 1 < preparedLine.Length
                            && preparedLine[afterClose] == '='
                            && preparedLine[afterClose + 1] == '>')
                        {
                            return true;
                        }

                        for (var next = lineIndex + 1; next < preparedLines.Length; next++)
                        {
                            var nextLine = preparedLines[next];
                            if (string.IsNullOrWhiteSpace(nextLine))
                                continue;

                            var nextCursor = SkipCSharpTriviaForward(nextLine, 0);
                            return nextCursor + 1 < nextLine.Length
                                && nextLine[nextCursor] == '='
                                && nextLine[nextCursor + 1] == '>';
                        }

                        return false;
                    }
                    break;
            }
        }

        return false;
    }

    private static bool LineEndsWithCSharpToken(string text, string token)
    {
        var cursor = text.Length;
        return TryConsumeTrailingCSharpToken(text, ref cursor, token);
    }

    private static bool TryConsumeTrailingCSharpToken(string text, ref int cursor, string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (cursor < token.Length)
            return false;

        var tokenStart = cursor - token.Length;
        if (!text.AsSpan(tokenStart, token.Length).Equals(token, StringComparison.Ordinal))
            return false;

        if ((tokenStart > 0 && IsCSharpIdentifierPart(text[tokenStart - 1]))
            || (cursor < text.Length && IsCSharpIdentifierPart(text[cursor])))
        {
            return false;
        }

        cursor = tokenStart;
        return true;
    }

    private static bool SkipCSharpPatternHeadBackward(string text, ref int cursor)
    {
        if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
            return false;

        while (true)
        {
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (cursor >= 2
                && text[cursor - 2] == ':'
                && text[cursor - 1] == ':')
            {
                cursor -= 2;
            }
            else if (cursor > 0 && text[cursor - 1] == '.')
            {
                cursor--;
            }
            else
            {
                break;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
                return false;
        }

        return true;
    }

    private static bool TryConsumeTrailingCSharpIdentifier(string text, ref int cursor)
    {
        var end = cursor;
        while (cursor > 0 && IsCSharpIdentifierPart(text[cursor - 1]))
            cursor--;

        if (cursor == end)
            return false;

        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;

        return true;
    }

    private static bool TryReadCSharpQualifiedAccess(
        string preparedLine,
        int start,
        out (IReadOnlyList<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed)
    {
        parsed = (Array.Empty<(int Start, int End)>(), start, false, false);

        if (start > 0 && IsCSharpIdentifierPart(preparedLine[start - 1]))
            return false;
        if (start >= preparedLine.Length || !IsCSharpIdentifierStart(preparedLine[start]))
            return false;

        var segments = new List<(int Start, int End)>();
        var cursor = start;
        var lastSeparatorWasDot = false;
        var hasLeadingGlobalQualifier = false;
        while (true)
        {
            if (!TryConsumeCSharpIdentifier(preparedLine, ref cursor, out var segmentStart, out var segmentEnd))
                return false;

            segments.Add((segmentStart, segmentEnd));

            var separatorStart = SkipWhitespace(preparedLine, cursor);
            if (separatorStart + 1 < preparedLine.Length
                && preparedLine[separatorStart] == ':'
                && preparedLine[separatorStart + 1] == ':')
            {
                if (segments.Count == 1
                    && segmentEnd - segmentStart == "global".Length
                    && string.CompareOrdinal(preparedLine, segmentStart, "global", 0, "global".Length) == 0)
                {
                    hasLeadingGlobalQualifier = true;
                }

                cursor = SkipWhitespace(preparedLine, separatorStart + 2);
                lastSeparatorWasDot = false;
                continue;
            }

            if (separatorStart < preparedLine.Length && preparedLine[separatorStart] == '.')
            {
                cursor = SkipWhitespace(preparedLine, separatorStart + 1);
                lastSeparatorWasDot = true;
                continue;
            }

            parsed = (segments, cursor, lastSeparatorWasDot, hasLeadingGlobalQualifier);
            return true;
        }
    }

    private static bool TryConsumeCSharpIdentifier(
        string preparedLine,
        ref int cursor,
        out int start,
        out int end)
    {
        start = cursor;
        if (cursor >= preparedLine.Length || !IsCSharpIdentifierStart(preparedLine[cursor]))
        {
            end = cursor;
            return false;
        }

        cursor++;
        while (cursor < preparedLine.Length && IsCSharpIdentifierPart(preparedLine[cursor]))
            cursor++;

        end = cursor;
        return true;
    }

    private static bool TryConsumeCSharpPatternKeyword(string preparedLine, ref int cursor, string keyword)
    {
        if (!preparedLine.AsSpan(cursor).StartsWith(keyword, StringComparison.Ordinal))
            return false;

        int afterKeyword = cursor + keyword.Length;
        if (afterKeyword < preparedLine.Length && !char.IsWhiteSpace(preparedLine[afterKeyword]))
            return false;

        cursor = afterKeyword;
        return true;
    }

    private static bool IsCSharpCaseTypePatternContinuation(
        string preparedLine,
        string typeExpression,
        int cursor,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        int lineNumber)
    {
        if (IsCSharpNonTypePatternExpression(typeExpression))
            return false;

        if (cursor >= preparedLine.Length)
            return false;

        return preparedLine[cursor] switch
        {
            ':' => !IsCSharpConstantPatternMemberHead(
                    typeExpression,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate),
            '{' or '(' or '[' => true,
            _ => IsCSharpCaseTypePatternIdentifier(
                preparedLine,
                typeExpression,
                cursor,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                lineNumber)
        };
    }

    private static bool IsCSharpCaseTypePatternIdentifier(
        string preparedLine,
        string typeExpression,
        int cursor,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        int lineNumber)
    {
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken.Length > 0 && rawToken[0] == '@')
            return true;

        return rawToken switch
        {
            "when" => !IsCSharpConstantPatternMemberHead(
                    typeExpression,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate),
            "or" or "and" => !IsCSharpLogicalConstantPatternHead(
                preparedLine,
                typeExpression,
                tokenCursor,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate),
            _ => true,
        };
    }

    private static bool TryEmitCSharpLogicalTypePatternHeads(
        string preparedLine,
        string initialTypeExpression,
        int initialTypeIndex,
        int continuationIndex,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        Action<string, int> emitTypeExpression)
    {
        var currentTypeExpression = initialTypeExpression;
        var currentTypeIndex = initialTypeIndex;
        var currentContinuationIndex = continuationIndex;
        var sawLogicalKeyword = false;
        var emittedAny = false;
        while (TryConsumeCSharpLogicalPatternKeyword(preparedLine, currentContinuationIndex, out var nextHeadCursor))
        {
            sawLogicalKeyword = true;
            if (!IsCSharpLogicalConstantPatternHead(
                    preparedLine,
                    currentTypeExpression,
                    nextHeadCursor,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                emitTypeExpression(currentTypeExpression, currentTypeIndex);
                emittedAny = true;
            }

            int nextTypeCursor = nextHeadCursor;
            if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextTypeCursor, "not"))
                nextTypeCursor = SkipWhitespace(preparedLine, nextTypeCursor);

            var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextTypeCursor);
            if (!nextMatch.Success)
                return false;

            var nextTypeGroup = nextMatch.Groups["type"];
            currentTypeExpression = nextTypeGroup.Value;
            currentTypeIndex = nextTypeGroup.Index;
            currentContinuationIndex = SkipWhitespace(preparedLine, nextTypeGroup.Index + nextTypeGroup.Length);
        }

        if (sawLogicalKeyword
            && !IsCSharpNonTypePatternExpression(currentTypeExpression)
            && !IsCSharpConstantPatternMemberHead(
                currentTypeExpression,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            emitTypeExpression(currentTypeExpression, currentTypeIndex);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static bool IsCSharpLogicalConstantPatternAtCursor(
        string preparedLine,
        string typeExpression,
        int cursor,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken is not ("or" or "and"))
            return false;

        return IsCSharpLogicalConstantPatternHead(
            preparedLine,
            typeExpression,
            tokenCursor,
            lineNumber,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate);
    }

    private static bool TryConsumeCSharpLogicalPatternKeyword(
        string preparedLine,
        int cursor,
        out int nextHeadCursor)
    {
        nextHeadCursor = cursor;
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken is not ("or" or "and"))
            return false;

        nextHeadCursor = SkipWhitespace(preparedLine, tokenCursor);
        return true;
    }

    private static bool IsCSharpLogicalConstantPatternHead(
        string preparedLine,
        string typeExpression,
        int cursor,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        if (IsCSharpConstantPatternMemberHead(
                typeExpression,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            return true;
        }

        if (IsCSharpQualifiedTypePatternHead(
                typeExpression,
                lineNumber,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases))
        {
            return false;
        }

        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var currentParsed)
            || !currentParsed.LastSeparatorWasDot
            || currentParsed.Segments.Count < 2)
        {
            return false;
        }

        var currentQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, currentParsed, lineNumber, csharpUsingAliases);
        if (string.IsNullOrWhiteSpace(currentQualifier))
            return false;

        int nextCursor = SkipWhitespace(preparedLine, cursor);
        if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextCursor, "not"))
            nextCursor = SkipWhitespace(preparedLine, nextCursor);

        var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextCursor);
        if (!nextMatch.Success)
            return false;

        var nextTypeExpression = nextMatch.Groups["type"].Value;
        if (IsCSharpQualifiedTypePatternHead(
                nextTypeExpression,
                lineNumber,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases))
        {
            return false;
        }

        if (!TryReadCSharpQualifiedAccess(nextTypeExpression, 0, out var nextParsed)
            || !nextParsed.LastSeparatorWasDot
            || nextParsed.Segments.Count < 2)
        {
            return false;
        }

        var nextQualifier = ResolveCSharpQualifiedConstantPatternQualifier(nextTypeExpression, nextParsed, lineNumber, csharpUsingAliases);
        return string.Equals(currentQualifier, nextQualifier, StringComparison.Ordinal);
    }

    private static bool IsCSharpQualifiedTypePatternHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var parsed)
            || !parsed.LastSeparatorWasDot
            || parsed.Segments.Count < 2)
        {
            return false;
        }

        var member = parsed.Segments[^1];
        var memberName = typeExpression.Substring(member.Start, member.End - member.Start);
        if (!csharpQualifiedTypePatternLookup.TryGetValue(memberName, out var targets))
            return false;

        var resolvedQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, parsed, lineNumber, csharpUsingAliases);
        bool qualifierHasMultipleSegments = resolvedQualifier.Contains('.') || resolvedQualifier.Contains("::", StringComparison.Ordinal);
        return MatchesQualifiedConstantContainer(
            resolvedQualifier,
            targets,
            allowShortNameFallback: !parsed.HasLeadingGlobalQualifier && !qualifierHasMultipleSegments,
            allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier);
    }

    private static string ResolveCSharpQualifiedConstantPatternQualifier(
        string typeExpression,
        (IReadOnlyList<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed,
        int lineNumber,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        var qualifier = TrimLeadingCSharpGlobalQualifier(NormalizeCSharpQualifiedSegments(typeExpression, parsed.Segments, parsed.Segments.Count - 1));
        return parsed.HasLeadingGlobalQualifier
            ? qualifier
            : ResolveCSharpQualifiedAliasTarget(qualifier, lineNumber, csharpUsingAliases);
    }

    private static bool IsCSharpQualifiedConstantPatternMemberHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var parsed)
            || !parsed.LastSeparatorWasDot
            || parsed.Segments.Count < 2)
        {
            return false;
        }

        var member = parsed.Segments[^1];
        var memberName = typeExpression.Substring(member.Start, member.End - member.Start);
        if (!csharpQualifiedConstantPatternMemberLookup.TryGetValue(memberName, out var targets))
            return false;

        var resolvedQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, parsed, lineNumber, csharpUsingAliases);
        bool qualifierHasMultipleSegments = resolvedQualifier.Contains('.') || resolvedQualifier.Contains("::", StringComparison.Ordinal);
        return MatchesQualifiedConstantContainer(
            resolvedQualifier,
            targets,
            allowShortNameFallback: !parsed.HasLeadingGlobalQualifier && !qualifierHasMultipleSegments,
            allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier);
    }

    private static bool IsCSharpConstantPatternMemberHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        return IsCSharpQualifiedConstantPatternMemberHead(
            typeExpression,
            lineNumber,
            csharpQualifiedConstantPatternMemberLookup,
            csharpUsingAliases);
    }

    private static bool IsCSharpNonTypePatternExpression(string typeExpression)
    {
        var trimmed = typeExpression.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '@')
            return false;

        return trimmed.IndexOf('.') < 0
            && trimmed.IndexOf(':') < 0
            && trimmed.IndexOf('<') < 0
            && trimmed.IndexOf('[') < 0
            && trimmed.IndexOf('?') < 0
            && trimmed.IndexOf(' ') < 0
            && CSharpNonTypePatternTokens.Contains(trimmed);
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static string NormalizeCSharpIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    private static string NormalizeAtPrefixedIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    private static string NormalizeCSharpQualifiedSegments(
        string preparedLine,
        IReadOnlyList<(int Start, int End)> segments,
        int count)
    {
        var capacity = Math.Max(0, count - 1);
        for (var i = 0; i < count; i++)
        {
            var (start, end) = segments[i];
            var length = end - start;
            if (length > 0 && preparedLine[start] == '@')
                length--;
            capacity += length;
        }

        var builder = new StringBuilder(capacity);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append('.');
            var (start, end) = segments[i];
            var length = end - start;
            if (length > 0 && preparedLine[start] == '@')
            {
                start++;
                length--;
            }

            builder.Append(preparedLine, start, length);
        }
        return builder.ToString();
    }

    private static string TrimLeadingCSharpGlobalQualifier(string qualifiedName) =>
        qualifiedName.StartsWith("global.", StringComparison.Ordinal)
            ? qualifiedName["global.".Length..]
            : qualifiedName;

    private static string? TryNormalizeCSharpQualifiedName(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;
        if (!TryReadCSharpQualifiedAccess(trimmed, 0, out var parsed))
            return null;
        if (SkipWhitespace(trimmed, parsed.NextIndex) != trimmed.Length)
            return null;
        return NormalizeCSharpQualifiedSegments(trimmed, parsed.Segments, parsed.Segments.Count);
    }

    private static string ResolveCSharpQualifiedAliasTarget(string qualifier, int lineNumber, IReadOnlyList<CSharpUsingAliasRecord> usingAliases)
    {
        if (string.IsNullOrWhiteSpace(qualifier) || usingAliases.Count == 0)
            return qualifier;

        var firstSegment = GetFirstQualifiedSegment(qualifier);
        string? aliasTarget = null;
        for (var i = usingAliases.Count - 1; i >= 0; i--)
        {
            var alias = usingAliases[i];
            if (alias.Line > lineNumber)
                continue;
            if (lineNumber < alias.ScopeStartLine || lineNumber > alias.ScopeEndLine)
                continue;
            if (!string.Equals(alias.AliasName, firstSegment, StringComparison.Ordinal))
                continue;

            aliasTarget = alias.TargetQualifiedName;
            break;
        }

        if (aliasTarget == null)
            return qualifier;

        return qualifier.Length == firstSegment.Length
            ? aliasTarget
            : aliasTarget + qualifier[firstSegment.Length..];
    }

}
