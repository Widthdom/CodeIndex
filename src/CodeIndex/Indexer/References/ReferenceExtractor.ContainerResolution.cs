using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void AddTypeReferenceSegment(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string segment,
        int startInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        bool isEscapedCSharpIdentifier = false,
        IReadOnlySet<string>? ignoredSegments = null,
        string referenceKind = "type_reference")
    {
        if (segment.Length == 0 || IsIgnoredTypeReferenceSegment(language, segment, isEscapedCSharpIdentifier, ignoredSegments))
            return;

        int column = startInLine + 1; // 1-based / 1始まり
        var dedupeKey = CreateReferenceDedupeKey(fileId, language, lineNumber, column, referenceKind, segment, container);
        if (!seen.Add(dedupeKey))
            return;

        TryAddReference(references, new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = segment,
            ReferenceKind = referenceKind,
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
        });
    }

    private static SymbolRecord? FindInnermostContainer(IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine!.Value <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    internal sealed class InnermostContainerResolver
    {
        private readonly IReadOnlyList<SymbolRecord> candidates;
        private readonly List<(SymbolRecord Symbol, int OriginalIndex)>? candidatesByStart;
        private SortedSet<ActiveContainer>? activeContainers;
        private int nextCandidateIndex;
        private int currentLine;
        private int? cachedLine;
        private SymbolRecord? cachedContainer;
        private readonly bool preferCallable;

        internal InnermostContainerResolver(
            IReadOnlyList<SymbolRecord> candidates,
            bool preferCallable = false)
        {
            this.candidates = candidates;
            this.preferCallable = preferCallable;
            if (candidates.Count == 0)
                return;

            candidatesByStart = new List<(SymbolRecord Symbol, int OriginalIndex)>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                var symbol = candidates[index];
                candidatesByStart.Add((symbol, index));
            }

            candidatesByStart.Sort(CompareCandidatesByStart);
        }

        internal SymbolRecord? Find(int lineNumber)
        {
            if (cachedLine == lineNumber)
                return cachedContainer;

            if (candidatesByStart == null)
                return Cache(lineNumber, null);

            if (lineNumber < currentLine)
                return Cache(lineNumber, FindInnermostContainer(candidates, lineNumber));

            AdvanceTo(lineNumber);
            return Cache(lineNumber, activeContainers is not { Count: > 0 } ? null : activeContainers.Min.Symbol);
        }

        private void AdvanceTo(int lineNumber)
        {
            if (candidatesByStart == null)
            {
                currentLine = lineNumber;
                return;
            }

            while (nextCandidateIndex < candidatesByStart.Count
                   && candidatesByStart[nextCandidateIndex].Symbol.BodyStartLine!.Value <= lineNumber)
            {
                var candidate = candidatesByStart[nextCandidateIndex];
                (activeContainers ??= []).Add(new ActiveContainer(
                    candidate.Symbol,
                    candidate.OriginalIndex,
                    preferCallable));
                nextCandidateIndex++;
            }

            activeContainers?.RemoveWhere(active => active.Symbol.BodyEndLine!.Value < lineNumber);
            currentLine = lineNumber;
        }

        private SymbolRecord? Cache(int lineNumber, SymbolRecord? container)
        {
            cachedLine = lineNumber;
            cachedContainer = container;
            return container;
        }

        private static int CompareCandidatesByStart(
            (SymbolRecord Symbol, int OriginalIndex) left,
            (SymbolRecord Symbol, int OriginalIndex) right)
        {
            var compare = left.Symbol.BodyStartLine!.Value.CompareTo(right.Symbol.BodyStartLine!.Value);
            if (compare != 0)
                return compare;

            compare = left.Symbol.BodyEndLine!.Value.CompareTo(right.Symbol.BodyEndLine!.Value);
            if (compare != 0)
                return compare;

            return left.OriginalIndex.CompareTo(right.OriginalIndex);
        }

        private readonly record struct ActiveContainer(
            SymbolRecord Symbol,
            int OriginalIndex,
            bool PreferCallable) : IComparable<ActiveContainer>
        {
            public int CompareTo(ActiveContainer other)
                => CallableContainerSelection.CompareInnermost(
                    Symbol,
                    OriginalIndex,
                    other.Symbol,
                    other.OriginalIndex,
                    PreferCallable);
        }
    }

    private static bool CanAttachCSharpXmlDocCommentToNextDeclaration(
        SymbolRecord? innermostContainer,
        IReadOnlyList<SymbolRecord>? scopeCandidates,
        IReadOnlyList<List<(int start, int end)>?>? csharpAttrRanges,
        string[] preparedLines,
        int lineNumber,
        SymbolRecord documentedContainer)
    {
        if (!HasOnlyCSharpWhitespaceOrAttributesBetweenCommentAndDeclaration(
                csharpAttrRanges,
                preparedLines,
                lineNumber,
                documentedContainer.StartLine))
        {
            return false;
        }

        if (innermostContainer != null
            && innermostContainer.Kind is not "class" or "struct" or "interface" or "enum" or "namespace")
        {
            return false;
        }

        var enclosingScope = scopeCandidates == null
            ? null
            : FindInnermostContainer(scopeCandidates, lineNumber);
        if (enclosingScope?.BodyStartLine == null)
            return true;

        return IsAtCSharpXmlDocAttachmentDepth(enclosingScope, preparedLines, lineNumber);
    }

    private static bool HasOnlyCSharpWhitespaceOrAttributesBetweenCommentAndDeclaration(
        IReadOnlyList<List<(int start, int end)>?>? csharpAttrRanges,
        string[] preparedLines,
        int commentLineNumber,
        int declarationLineNumber)
    {
        var startLineIndex = Math.Max(commentLineNumber, 0);
        var endLineIndex = Math.Min(declarationLineNumber - 1, preparedLines.Length);
        for (var lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
        {
            var line = preparedLines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsCSharpAttributeOnlyLine(line, csharpAttrRanges?[lineIndex]))
                continue;

            return false;
        }

        return true;
    }

    private static bool IsCSharpAttributeOnlyLine(string preparedLine, IReadOnlyList<(int start, int end)>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
            return false;

        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[i]))
                continue;

            var covered = false;
            foreach (var (start, end) in ranges)
            {
                if (i >= start && i < end)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
                return false;
        }

        return true;
    }

    private static bool IsAtCSharpXmlDocAttachmentDepth(
        SymbolRecord enclosingScope,
        string[] preparedLines,
        int lineNumber)
    {
        var scopeBodyStartIndex = enclosingScope.BodyStartLine!.Value - 1;
        var commentLineIndex = lineNumber - 1;
        if (scopeBodyStartIndex < 0
            || scopeBodyStartIndex >= preparedLines.Length
            || scopeBodyStartIndex >= commentLineIndex)
        {
            return true;
        }

        var sawScopeOpenBrace = false;
        var nestedBraceDepth = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var topLevelExecutableContinuation = false;
        var topLevelArrowExpressionContinuation = false;

        for (var i = scopeBodyStartIndex; i < commentLineIndex && i < preparedLines.Length; i++)
        {
            var line = preparedLines[i];
            for (var j = 0; j < line.Length; j++)
            {
                var ch = line[j];
                if (!sawScopeOpenBrace)
                {
                    if (ch == '{')
                        sawScopeOpenBrace = true;

                    continue;
                }

                if (nestedBraceDepth == 0)
                {
                    if (ch == '<')
                    {
                        angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }

                    if (IsCSharpTopLevelArrowToken(line, j))
                    {
                        topLevelExecutableContinuation = true;
                        topLevelArrowExpressionContinuation = !IsCSharpArrowBlockStart(line, j + 2);
                        j++;
                        continue;
                    }

                    if (IsCSharpTopLevelAssignmentOperator(line, j))
                    {
                        topLevelExecutableContinuation = true;
                    }
                }

                if (ch == '{')
                {
                    nestedBraceDepth++;
                }
                else if (ch == '}')
                {
                    if (nestedBraceDepth == 0)
                        return false;

                    nestedBraceDepth--;
                }
                else if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                }
                else if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (nestedBraceDepth == 0
                         && ch == ';'
                         && parenDepth == 0
                         && bracketDepth == 0)
                {
                    topLevelExecutableContinuation = false;
                    topLevelArrowExpressionContinuation = false;
                }
            }
        }

        return !sawScopeOpenBrace
            || (nestedBraceDepth == 0
                && angleDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && !topLevelExecutableContinuation
                && !topLevelArrowExpressionContinuation);
    }

    private static (bool[] MultilineStringContent, bool[] BlockComment) BuildCSharpLineStateMasks(string[] lines)
    {
        var insideStringContent = new bool[lines.Length];
        var insideBlockComment = new bool[lines.Length];
        var inBlockComment = false;
        var inVerbatimString = false;
        var rawStringDelimiterLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            insideStringContent[i] = inVerbatimString || rawStringDelimiterLength > 0;
            insideBlockComment[i] = inBlockComment;

            var index = 0;
            while (index < line.Length)
            {
                if (inBlockComment)
                {
                    var closeIndex = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (closeIndex < 0)
                        break;

                    index = closeIndex + 2;
                    inBlockComment = false;
                    continue;
                }

                if (rawStringDelimiterLength > 0)
                {
                    var closeCandidateIndex = index;
                    while (closeCandidateIndex < line.Length && char.IsWhiteSpace(line[closeCandidateIndex]))
                        closeCandidateIndex++;

                    var closeLength = CountCharacterRun(line, closeCandidateIndex, '"');
                    if (closeLength >= rawStringDelimiterLength
                        && closeLength > 0)
                    {
                        rawStringDelimiterLength = 0;
                        index = closeCandidateIndex + closeLength;
                        continue;
                    }

                    break;
                }

                if (inVerbatimString)
                {
                    if (line[index] == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    if (line[index] == '"')
                    {
                        index++;
                        inVerbatimString = false;
                        continue;
                    }

                    index++;
                    continue;
                }

                if (StartsWithOrdinal(line, index, "//"))
                    break;

                if (StartsWithOrdinal(line, index, "/*"))
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }

                if (TryStartCSharpRawString(line, index, out var rawOpeningLength, out var rawDelimiterLength))
                {
                    rawStringDelimiterLength = rawDelimiterLength;
                    index += rawOpeningLength;
                    continue;
                }

                if (TryStartCSharpVerbatimString(line, index, out var verbatimOpeningLength))
                {
                    inVerbatimString = true;
                    index += verbatimOpeningLength;
                    continue;
                }

                if (TryStartCSharpRegularString(line, index, out var regularOpeningLength))
                {
                    index += regularOpeningLength;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '"')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if (line[index] == '\'')
                {
                    index++;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '\'')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                index++;
            }
        }

        return (insideStringContent, insideBlockComment);
    }

    private static bool IsCSharpTopLevelAssignmentOperator(string line, int index)
    {
        if (index < 0 || index >= line.Length || line[index] != '=')
            return false;

        var previous = index > 0 ? line[index - 1] : '\0';
        var next = index + 1 < line.Length ? line[index + 1] : '\0';
        return previous is not ('=' or '!' or '<' or '>')
            && next is not ('=' or '>');
    }

    private static bool IsCSharpTopLevelArrowToken(string line, int index) =>
        index >= 0
        && index + 1 < line.Length
        && line[index] == '='
        && line[index + 1] == '>';

    private static bool IsCSharpArrowBlockStart(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '{';
    }

    private static int GetCSharpSameLineDocumentedDeclarationStartColumn(
        string originalLine,
        int commentEndExclusive,
        bool nextDelimitedDocComment)
    {
        if (nextDelimitedDocComment
            || commentEndExclusive < 0
            || commentEndExclusive + 1 >= originalLine.Length
            || originalLine[commentEndExclusive] != '*'
            || originalLine[commentEndExclusive + 1] != '/')
        {
            return -1;
        }

        var column = commentEndExclusive + 2;
        while (column < originalLine.Length && char.IsWhiteSpace(originalLine[column]))
            column++;

        return column < originalLine.Length ? column : -1;
    }

    private static bool HasOnlyCSharpWhitespaceOrAttributesAfterColumn(
        string preparedLine,
        IReadOnlyList<(int start, int end)>? ranges,
        int startColumn)
    {
        if (startColumn < 0 || startColumn >= preparedLine.Length)
            return true;

        for (var i = startColumn; i < preparedLine.Length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[i]))
                continue;

            if (ranges != null)
            {
                var covered = false;
                foreach (var (start, end) in ranges)
                {
                    if (i >= start && i < end)
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                    continue;
            }

            return false;
        }

        return true;
    }

}
