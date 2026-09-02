using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryGetJvmDocCommentSpan(
        string originalLine,
        bool inDelimitedDocComment,
        out int commentStart,
        out int commentEndExclusive,
        out int sameLineDeclarationStartColumn,
        out bool nextDelimitedDocComment)
    {
        commentStart = -1;
        commentEndExclusive = -1;
        sameLineDeclarationStartColumn = -1;
        nextDelimitedDocComment = inDelimitedDocComment;

        var lineStart = 0;
        while (lineStart < originalLine.Length && char.IsWhiteSpace(originalLine[lineStart]))
            lineStart++;

        if (!inDelimitedDocComment)
        {
            if (lineStart + 3 > originalLine.Length
                || originalLine[lineStart] != '/'
                || originalLine[lineStart + 1] != '*'
                || originalLine[lineStart + 2] != '*')
            {
                return false;
            }

            commentStart = lineStart + 3;
        }
        else
        {
            commentStart = lineStart;
            if (commentStart < originalLine.Length && originalLine[commentStart] == '*')
            {
                if (commentStart + 1 < originalLine.Length && originalLine[commentStart + 1] == '/')
                {
                    commentEndExclusive = commentStart;
                    nextDelimitedDocComment = false;
                    sameLineDeclarationStartColumn = GetJvmSameLineDeclarationStartColumn(originalLine, commentStart);
                    return true;
                }

                commentStart++;
                if (commentStart < originalLine.Length && originalLine[commentStart] == ' ')
                    commentStart++;
            }
        }

        var closeIndex = originalLine.IndexOf("*/", commentStart, StringComparison.Ordinal);
        if (closeIndex >= 0)
        {
            commentEndExclusive = closeIndex;
            nextDelimitedDocComment = false;
            sameLineDeclarationStartColumn = GetJvmSameLineDeclarationStartColumn(originalLine, closeIndex);
        }
        else
        {
            commentEndExclusive = originalLine.Length;
            nextDelimitedDocComment = true;
        }

        return true;
    }

    private static int GetJvmSameLineDeclarationStartColumn(string originalLine, int commentEndExclusive)
    {
        if (commentEndExclusive + 1 >= originalLine.Length
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

    private static SymbolRecord? FindJvmDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        IReadOnlyList<string> originalLines,
        string structuralLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        var innermostContainer = FindInnermostContainer(candidates, lineNumber);
        if (innermostContainer?.Kind is "function" or "property")
            return null;

        var sameLineCandidate = FindSameLineDocumentedContainer(
            candidates,
            structuralLine,
            lineNumber,
            sameLineDeclarationStartColumn);
        if (sameLineCandidate != null)
            return sameLineCandidate;

        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine <= lineNumber)
                continue;
            if (!HasOnlyJvmDocTriviaBeforeDeclaration(originalLines, lineNumber, candidate.StartLine))
                continue;

            if (best == null
                || candidate.StartLine < best.StartLine
                || (candidate.StartLine == best.StartLine
                    && ((candidate.BodyEndLine ?? candidate.EndLine) - (candidate.BodyStartLine ?? candidate.StartLine))
                       < ((best.BodyEndLine ?? best.EndLine) - (best.BodyStartLine ?? best.StartLine))))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool HasOnlyJvmDocTriviaBeforeDeclaration(
        IReadOnlyList<string> originalLines,
        int docLineNumber,
        int declarationLineNumber)
    {
        for (var lineIndex = docLineNumber; lineIndex < declarationLineNumber - 1 && lineIndex < originalLines.Count; lineIndex++)
        {
            var trimmed = originalLines[lineIndex].TrimStart();
            if (trimmed.Length == 0
                || trimmed.StartsWith("/**", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static SymbolRecord? FindDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        string preparedLine,
        IReadOnlyList<(int start, int end)>? csharpAttrRangesOnLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        var sameLineCandidate = FindSameLineDocumentedContainer(
            candidates,
            structuralLine,
            lineNumber,
            sameLineDeclarationStartColumn);
        if (sameLineCandidate != null)
            return sameLineCandidate;
        if (sameLineDeclarationStartColumn >= 0
            && !HasOnlyCSharpWhitespaceOrAttributesAfterColumn(
                preparedLine,
                csharpAttrRangesOnLine,
                sameLineDeclarationStartColumn))
        {
            return null;
        }

        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine <= lineNumber)
                continue;

            if (best == null
                || candidate.StartLine < best.StartLine
                || (candidate.StartLine == best.StartLine
                    && ((candidate.BodyEndLine ?? candidate.EndLine) - (candidate.BodyStartLine ?? candidate.StartLine))
                       < ((best.BodyEndLine ?? best.EndLine) - (best.BodyStartLine ?? best.StartLine))))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static SymbolRecord? FindSameLineDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        if (sameLineDeclarationStartColumn < 0)
            return null;

        SymbolRecord? best = null;
        var bestStartColumn = int.MaxValue;
        var bestSpanLength = int.MaxValue;
        var bestKindRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.StartLine != lineNumber
                || candidate.EndLine != lineNumber
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            if (!TryGetSameLineSignatureSpan(candidate, structuralLine, out var startColumn, out var endColumn)
                || startColumn < sameLineDeclarationStartColumn)
            {
                continue;
            }

            var spanLength = endColumn - startColumn;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || startColumn < bestStartColumn
                || (startColumn == bestStartColumn && spanLength < bestSpanLength)
                || (startColumn == bestStartColumn && spanLength == bestSpanLength && kindRank < bestKindRank))
            {
                best = candidate;
                bestStartColumn = startColumn;
                bestSpanLength = spanLength;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static SymbolRecord? FindInnermostSameLineCSharpContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int column,
        SymbolRecord? excludedCandidate = null)
    {
        SymbolRecord? best = null;
        var bestStartColumn = -1;
        var bestSpanLength = int.MaxValue;
        var bestKindRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, excludedCandidate)
                || candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.BodyStartLine.Value > lineNumber
                || candidate.BodyEndLine.Value < lineNumber
                || candidate.StartLine != lineNumber
                || candidate.EndLine != lineNumber
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            if (!TryGetSameLineSignatureSpan(candidate, structuralLine, out var startColumn, out var endColumn))
                continue;

            if (column < startColumn || column >= endColumn)
                continue;

            if (candidate.Kind == "function"
                && (!TryFindCSharpFunctionNameColumn(structuralLine, candidate.Name, out var nameColumn)
                    || column < nameColumn))
            {
                continue;
            }

            var spanLength = endColumn - startColumn;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || startColumn > bestStartColumn
                || (startColumn == bestStartColumn && spanLength < bestSpanLength)
                || (startColumn == bestStartColumn && spanLength == bestSpanLength && kindRank < bestKindRank))
            {
                best = candidate;
                bestStartColumn = startColumn;
                bestSpanLength = spanLength;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static Dictionary<int, List<SymbolRecord>>? BuildCSharpSameLineContainerCandidatesByLine(
        string language,
        IReadOnlyList<SymbolRecord> candidates)
    {
        if (language != "csharp")
            return null;

        Dictionary<int, List<SymbolRecord>>? candidatesByLine = null;
        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.StartLine != candidate.EndLine
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            candidatesByLine ??= new Dictionary<int, List<SymbolRecord>>();
            if (!candidatesByLine.TryGetValue(candidate.StartLine, out var lineCandidates))
            {
                lineCandidates = [];
                candidatesByLine.Add(candidate.StartLine, lineCandidates);
            }

            lineCandidates.Add(candidate);
        }

        return candidatesByLine;
    }

    private static Dictionary<int, List<SymbolRecord>>? BuildCSharpMultilineContainerCandidatesByStartLine(
        string language,
        IReadOnlyList<SymbolRecord> candidates)
    {
        if (language != "csharp")
            return null;

        Dictionary<int, List<SymbolRecord>>? candidatesByLine = null;
        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.StartLine == candidate.EndLine
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            candidatesByLine ??= new Dictionary<int, List<SymbolRecord>>();
            if (!candidatesByLine.TryGetValue(candidate.StartLine, out var lineCandidates))
            {
                lineCandidates = [];
                candidatesByLine.Add(candidate.StartLine, lineCandidates);
            }

            lineCandidates.Add(candidate);
        }

        return candidatesByLine;
    }

    private static SymbolRecord? FindInnermostSameLineCSharpContainer(
        IReadOnlyDictionary<int, List<SymbolRecord>>? candidatesByLine,
        string structuralLine,
        int lineNumber,
        int column,
        SymbolRecord? excludedCandidate = null)
        => candidatesByLine != null && candidatesByLine.TryGetValue(lineNumber, out var candidates)
            ? FindInnermostSameLineCSharpContainer(
                candidates,
                structuralLine,
                lineNumber,
                column,
                excludedCandidate)
            : null;

    private static SymbolRecord? FindInnermostFollowingSameLineCSharpMultilineContainer(
        IReadOnlyDictionary<int, List<SymbolRecord>>? candidatesByStartLine,
        string structuralLine,
        int lineNumber,
        int column,
        SymbolRecord excludedCandidate)
    {
        if (candidatesByStartLine == null
            || !candidatesByStartLine.TryGetValue(lineNumber, out var candidates))
        {
            return null;
        }

        SymbolRecord? best = null;
        var bestBodyOpenColumn = -1;
        var bestRange = int.MaxValue;
        var bestKindRank = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, excludedCandidate)
                || candidate.BodyStartLine is not int bodyStartLine
                || candidate.BodyEndLine is not int bodyEndLine
                || bodyStartLine > lineNumber
                || bodyEndLine < lineNumber)
            {
                continue;
            }

            var searchStart = Math.Clamp(candidate.StartColumn ?? 0, 0, structuralLine.Length);
            if (!TryFindCSharpSameLineDeclarationBodyOpenColumn(
                    structuralLine,
                    searchStart,
                    out var bodyOpenColumn)
                || column <= bodyOpenColumn)
            {
                continue;
            }

            var range = bodyEndLine - bodyStartLine;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || bodyOpenColumn > bestBodyOpenColumn
                || (bodyOpenColumn == bestBodyOpenColumn && range < bestRange)
                || (bodyOpenColumn == bestBodyOpenColumn && range == bestRange && kindRank < bestKindRank))
            {
                best = candidate;
                bestBodyOpenColumn = bodyOpenColumn;
                bestRange = range;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static bool TryFindCSharpSameLineDeclarationBodyOpenColumn(
        string structuralLine,
        int searchStart,
        out int bodyOpenColumn)
    {
        bodyOpenColumn = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var column = searchStart; column < structuralLine.Length; column++)
        {
            switch (structuralLine[column])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')' when parenDepth > 0:
                    parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case '{' when parenDepth == 0 && bracketDepth == 0:
                    bodyOpenColumn = column;
                    return true;
                case ';' when parenDepth == 0 && bracketDepth == 0:
                    return false;
            }
        }

        return false;
    }

    private static SymbolRecord? FindInnermostCSharpDeclarationRangeContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int column)
    {
        SymbolRecord? best = null;
        var bestRange = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.Kind != "function"
                || candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.StartLine > lineNumber
                || candidate.BodyStartLine.Value < lineNumber
                || candidate.BodyEndLine.Value < lineNumber)
            {
                continue;
            }

            if (candidate.StartLine == lineNumber
                && (!TryFindCSharpFunctionNameColumn(structuralLine, candidate.Name, out var nameColumn)
                    || column < nameColumn))
            {
                continue;
            }

            var range = candidate.BodyEndLine.Value - candidate.StartLine;
            if (best == null || range < bestRange)
            {
                best = candidate;
                bestRange = range;
            }
        }

        return best;
    }

    private static bool TryFindCSharpFunctionNameColumn(string structuralLine, string? name, out int column)
    {
        column = -1;
        if (string.IsNullOrWhiteSpace(structuralLine) || string.IsNullOrWhiteSpace(name))
            return false;

        var searchStart = 0;
        while (searchStart < structuralLine.Length)
        {
            var index = structuralLine.IndexOf(name, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index - 1;
            if (before >= 0 && IsTypeExpressionIdentifierPart("csharp", structuralLine[before]))
            {
                searchStart = index + name.Length;
                continue;
            }

            var afterName = index + name.Length;
            if (afterName < structuralLine.Length && IsTypeExpressionIdentifierPart("csharp", structuralLine[afterName]))
            {
                searchStart = afterName;
                continue;
            }

            var after = SkipWhitespace(structuralLine, afterName);
            if (after < structuralLine.Length && structuralLine[after] == '<')
            {
                var genericClose = FindMatchingChar(structuralLine, after, '<', '>');
                if (genericClose > after)
                    after = SkipWhitespace(structuralLine, genericClose + 1);
            }

            if (after < structuralLine.Length && structuralLine[after] == '(')
            {
                column = index;
                return true;
            }

            searchStart = afterName;
        }

        return false;
    }

    private static bool TryGetSameLineSignatureSpan(
        SymbolRecord candidate,
        string structuralLine,
        out int startColumn,
        out int endColumn)
    {
        startColumn = candidate.StartColumn ?? -1;
        if (startColumn < 0 || startColumn > structuralLine.Length)
        {
            startColumn = FindSignatureOccurrenceStartColumn(
                structuralLine,
                candidate.Signature!,
                candidate.SameLineSignatureOccurrenceIndex ?? 0);
            if (startColumn < 0)
            {
                endColumn = -1;
                return false;
            }
        }

        endColumn = Math.Min(structuralLine.Length, startColumn + candidate.Signature!.Length);
        return endColumn > startColumn;
    }

    private static int FindSignatureOccurrenceStartColumn(string structuralLine, string signature, int occurrenceIndex)
    {
        if (occurrenceIndex < 0 || string.IsNullOrEmpty(structuralLine) || string.IsNullOrEmpty(signature))
            return -1;

        var currentOccurrence = 0;
        var searchStart = 0;
        while (searchStart < structuralLine.Length)
        {
            var matchIndex = structuralLine.IndexOf(signature, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0)
                return -1;

            if (currentOccurrence == occurrenceIndex)
                return matchIndex;

            currentOccurrence++;
            searchStart = matchIndex + signature.Length;
        }

        return -1;
    }

    private static bool TryStartCSharpRawString(
        string line,
        int startIndex,
        out int openingLength,
        out int delimiterLength)
    {
        openingLength = 0;
        delimiterLength = 0;

        var quoteIndex = startIndex;
        while (quoteIndex < line.Length && line[quoteIndex] == '$')
            quoteIndex++;

        delimiterLength = CountCharacterRun(line, quoteIndex, '"');
        if (delimiterLength < 3)
            return false;

        openingLength = (quoteIndex - startIndex) + delimiterLength;
        return true;
    }

    private static bool TryStartCSharpVerbatimString(string line, int startIndex, out int openingLength)
    {
        openingLength = 0;
        if (StartsWithOrdinal(line, startIndex, "$@\"") || StartsWithOrdinal(line, startIndex, "@$\""))
        {
            openingLength = 3;
            return true;
        }

        if (!StartsWithOrdinal(line, startIndex, "@\""))
            return false;

        openingLength = 2;
        return true;
    }

    private static bool TryStartCSharpRegularString(string line, int startIndex, out int openingLength)
    {
        openingLength = 0;
        if (StartsWithOrdinal(line, startIndex, "$\""))
        {
            openingLength = 2;
            return true;
        }

        if (line[startIndex] != '"')
            return false;

        openingLength = 1;
        return true;
    }

    private static bool StartsWithOrdinal(string line, int startIndex, string value)
    {
        if (startIndex + value.Length > line.Length)
            return false;

        return string.Compare(line, startIndex, value, 0, value.Length, StringComparison.Ordinal) == 0;
    }

    private static int CountCharacterRun(string line, int startIndex, char value)
    {
        var index = startIndex;
        while (index < line.Length && line[index] == value)
            index++;

        return index - startIndex;
    }

    private static int GetSameLineContainerKindRank(string? kind) => kind switch
    {
        "function" => 0,
        "property" => 1,
        "class" => 2,
        "struct" => 3,
        "interface" => 4,
        "enum" => 5,
        "namespace" => 6,
        _ => 7,
    };

    internal static SymbolRecord? FindInnermostClassLike(IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            // class/struct/enum are all ctor-owner kinds across supported languages. Java enum bodies
            // can declare constructors and chain via `this(...)`; C# enum cannot declare constructors
            // at all, so the chain regex will not match inside one even if we pick it up here.
            // class/struct/enum はいずれもコンストラクタを持ちうる宿主種別。Java enum は `this(...)`
            // 連鎖を書けるため含める。C# enum はコンストラクタ自体を持てないので副作用は出ない。
            if (candidate.Kind != "class" && candidate.Kind != "struct" && candidate.Kind != "enum")
                continue;
            if (candidate.BodyStartLine!.Value <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Same-line Java ctor span capturing the declarator name plus the 0-based indices of the
    /// ctor name, the opening `{` of the body, and the matching `}` on the same line (or -1
    /// when no matching close brace is found). Used to override the container for body-level
    /// calls and to suppress the bogus declarator self-call on the ctor name.
    /// same-line Java ctor の宣言情報。ctor 名位置・body `{` 位置・body `}` 位置を保持し、
    /// body 内の call に合成 function コンテナを流すのと、宣言子 `CtorName(` が誤って
    /// call として記録されるのを抑止するのに使う。
    /// </summary>
}
