using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class DynamicDeclarativeReferenceExtractor
{
    public static void EmitAdditionalReferences(
        string language,
        string preparedLine,
        string structuralLine,
        ExtractionState state,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Action<string, int> addCallLikeReference)
    {
        var importScanLine = language == "tcl"
            ? state.GetCallScanLine(language, lineNumber, structuralLine)
            : structuralLine;
        EmitImportReference(
            language,
            importScanLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        if (language is "prolog" or "ambiguous_pl")
        {
            foreach (var call in state.GetPrologGoalCalls(lineNumber))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    return;

                if (!state.CallableNames.Contains(call.Name))
                    continue;

                var prologContainer = call.IsTopLevelDirective
                    ? null
                    : state.ResolveContainer(lineNumber, call.Column, fallback: null);
                if (call.IsTopLevelDirective)
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        call.Name,
                        call.Column,
                        "call",
                        context,
                        lineNumber,
                        container: null,
                        language);
                    continue;
                }

                if (prologContainer != null
                    && !string.Equals(prologContainer.Name, call.Name, StringComparison.Ordinal))
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        call.Name,
                        call.Column,
                        "call",
                        context,
                        lineNumber,
                        prologContainer,
                        language);
                    continue;
                }

                addCallLikeReference(call.Name, call.Column);
            }

            return;
        }

        var callRegex = language switch
        {
            "crystal" => CrystalBareCallRegex,
            "groovy" => GroovyBareCallRegex,
            "tcl" => TclCommandRegex,
            _ => null,
        };
        if (callRegex == null)
            return;

        if (language == "crystal")
        {
            foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                         CrystalSuffixedParenthesizedCallRegex,
                         preparedLine,
                         references))
            {
                var nameGroup = match.Groups["name"];
                if (state.CallableNames.Contains(nameGroup.Value))
                    addCallLikeReference(nameGroup.Value, nameGroup.Index);
            }

            foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                         CrystalControlPredicateCallRegex,
                         preparedLine,
                         references))
            {
                var nameGroup = match.Groups["name"];
                if (state.CallableNames.Contains(nameGroup.Value))
                    addCallLikeReference(nameGroup.Value, nameGroup.Index);
            }
        }
        else if (language == "groovy")
        {
            EmitGroovyControlBodyBareCalls(
                preparedLine,
                state.CallableNames,
                addCallLikeReference,
                references);
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(callRegex, preparedLine, references))
        {
            var nameGroup = match.Groups["name"];
            if (language == "tcl")
            {
                if (!state.TryResolveTclCallable(
                        nameGroup.Value,
                        out var referenceName,
                        out var targetQualifier,
                        out var referenceNameOffset))
                {
                    continue;
                }

                if (targetQualifier != null
                    || !string.Equals(referenceName, nameGroup.Value, StringComparison.Ordinal))
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        referenceName,
                        nameGroup.Index + referenceNameOffset,
                        "call",
                        context,
                        lineNumber,
                        resolveContainerForCall(nameGroup.Index),
                        language,
                        targetQualifier);
                }
                else
                {
                    addCallLikeReference(nameGroup.Value, nameGroup.Index);
                }

                continue;
            }

            if (!state.CallableNames.Contains(nameGroup.Value))
                continue;
            if (language == "groovy"
                && IsGroovyClosureParameterHeader(preparedLine, nameGroup.Index))
            {
                continue;
            }

            addCallLikeReference(nameGroup.Value, nameGroup.Index);
        }

    }

    private static void EmitGroovyControlBodyBareCalls(
        string line,
        IReadOnlySet<string> callableNames,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references)
    {
        for (var keywordColumn = 0; keywordColumn < line.Length; keywordColumn++)
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                return;

            if (!char.IsLetter(line[keywordColumn]))
                continue;

            var keywordEnd = keywordColumn + 1;
            while (keywordEnd < line.Length
                && (char.IsLetterOrDigit(line[keywordEnd]) || line[keywordEnd] == '_'))
            {
                keywordEnd++;
            }

            var keyword = line.AsSpan(keywordColumn, keywordEnd - keywordColumn);
            if (!keyword.SequenceEqual("if")
                && !keyword.SequenceEqual("while")
                && !keyword.SequenceEqual("for"))
            {
                keywordColumn = keywordEnd - 1;
                continue;
            }

            var openingColumn = SkipWhitespace(line, keywordEnd);
            if (openingColumn >= line.Length || line[openingColumn] != '(')
            {
                keywordColumn = keywordEnd - 1;
                continue;
            }

            var closingColumn = FindMatchingParenthesis(line, openingColumn);
            if (closingColumn < 0)
            {
                keywordColumn = keywordEnd - 1;
                continue;
            }

            var nameColumn = SkipWhitespace(line, closingColumn + 1);
            if (nameColumn >= line.Length
                || !IsIdentifierStart(line[nameColumn]))
            {
                keywordColumn = closingColumn;
                continue;
            }

            var nameEnd = nameColumn + 1;
            while (nameEnd < line.Length
                && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] == '_'))
            {
                nameEnd++;
            }

            var name = line[nameColumn..nameEnd];
            var nextColumn = SkipWhitespace(line, nameEnd);
            if (callableNames.Contains(name)
                && (nextColumn >= line.Length
                    || (line[nextColumn] != '('
                        && line[nextColumn] != ':'
                        && line[nextColumn] != '=')))
            {
                addCallLikeReference(name, nameColumn);
            }

            keywordColumn = closingColumn;
        }
    }

    private static int FindMatchingParenthesis(string line, int openingColumn)
    {
        var depth = 0;
        for (var column = openingColumn; column < line.Length; column++)
        {
            if (line[column] == '(')
            {
                depth++;
            }
            else if (line[column] == ')' && --depth == 0)
            {
                return column;
            }
        }

        return -1;
    }

    private static bool IsGroovyClosureParameterHeader(string line, int nameColumn)
    {
        var openingBrace = line.LastIndexOf('{', Math.Max(0, nameColumn - 1));
        if (openingBrace < 0)
            return false;

        var closingBraceBeforeName = line.LastIndexOf('}', Math.Max(0, nameColumn - 1));
        if (closingBraceBeforeName > openingBrace)
            return false;

        var arrowColumn = line.IndexOf("->", nameColumn, StringComparison.Ordinal);
        if (arrowColumn < 0)
            return false;

        var closingBraceAfterName = line.IndexOf('}', nameColumn);
        if (closingBraceAfterName >= 0 && closingBraceAfterName < arrowColumn)
            return false;

        return line.AsSpan(nameColumn, arrowColumn - nameColumn)
            .IndexOfAny(';', '{', '}') < 0;
    }

    private static int SkipWhitespace(string line, int column)
    {
        while (column < line.Length && char.IsWhiteSpace(line[column]))
            column++;
        return column;
    }

    private static bool IsIdentifierStart(char value) =>
        value == '_' || char.IsLetter(value);

    public static bool ShouldSuppressGenericCall(
        string language,
        string preparedLine,
        string name,
        int callIndex,
        int lineNumber,
        ExtractionState? state,
        SymbolRecord? container)
    {
        if (language == "ambiguous_pl")
            return state?.HasPrologContainer(lineNumber) == true
                || state?.IsPrologDirectiveLine(lineNumber) == true;
        if (state?.IsDeclarationAt(lineNumber, callIndex, name) == true)
            return true;
        if (language == "crystal")
        {
            return MatchesDeclarationAt(CrystalMethodDeclarationRegex, preparedLine, name, callIndex)
                || MatchesDeclarationAt(CrystalFunDeclarationRegex, preparedLine, name, callIndex);
        }
        if (language != "groovy")
            return false;
        if (name is "super" or "synchronized" or "this")
            return true;

        if (MatchesDeclarationAt(GroovyMethodDeclarationRegex, preparedLine, name, callIndex))
            return true;
        return container?.Kind == "class"
            && string.Equals(container.Name, name, StringComparison.Ordinal)
            && MatchesDeclarationAt(GroovyConstructorDeclarationRegex, preparedLine, name, callIndex);
    }

    private static bool MatchesDeclarationAt(
        Regex regex,
        string line,
        string name,
        int callIndex)
    {
        foreach (Match declaration in BoundedRegex.EnumerateMatches(regex, line))
        {
            var nameGroup = declaration.Groups["name"];
            if (nameGroup.Index == callIndex
                && string.Equals(nameGroup.Value, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int SkipQuotedToken(string line, int startColumn, char delimiter)
    {
        for (var column = startColumn + 1; column < line.Length; column++)
        {
            if (line[column] == '\\')
            {
                column++;
                continue;
            }

            if (line[column] != delimiter)
                continue;

            if (column + 1 < line.Length && line[column + 1] == delimiter)
            {
                column++;
                continue;
            }

            return column + 1;
        }

        return line.Length;
    }

    private static bool HasClosingQuotedDelimiter(
        string line,
        int startColumn,
        char delimiter)
    {
        for (var column = startColumn + 1; column < line.Length; column++)
        {
            if (line[column] == '\\')
            {
                column++;
                continue;
            }

            if (line[column] == delimiter)
                return true;
        }

        return false;
    }

    private static void FillWithSpaces(char[] buffer, int startColumn)
    {
        for (var column = startColumn; column < buffer.Length; column++)
            buffer[column] = ' ';
    }

    private static void FillWithSpaces(char[] buffer, int startColumn, int endColumn)
    {
        for (var column = startColumn; column < endColumn && column < buffer.Length; column++)
            buffer[column] = ' ';
    }

    private static bool IsPrologHashOperator(string line, int column)
    {
        if (column + 1 >= line.Length)
            return false;

        return line[column + 1] is '=' or '<' or '>' or '\\' or '/' or '#';
    }

    private static bool IsPerlLastIndexVariable(string line, int column)
    {
        if (column <= 0
            || line[column - 1] != '$'
            || column + 1 >= line.Length)
        {
            return false;
        }

        return line[column + 1] == '{'
            || line[column + 1] == '_'
            || char.IsLetter(line[column + 1]);
    }

    private static bool IsPerlHashSigil(string line, int column)
    {
        if (column + 1 >= line.Length
            || (line[column + 1] != '{'
                && line[column + 1] != '_'
                && !char.IsLetter(line[column + 1])))
        {
            return false;
        }

        var previousColumn = column - 1;
        while (previousColumn >= 0 && char.IsWhiteSpace(line[previousColumn]))
            previousColumn--;

        var tokenEnd = column + 1;
        if (line[tokenEnd] == '{')
            return true;
        while (tokenEnd < line.Length
            && (char.IsLetterOrDigit(line[tokenEnd]) || line[tokenEnd] == '_'))
        {
            tokenEnd++;
        }
        while (tokenEnd < line.Length && char.IsWhiteSpace(line[tokenEnd]))
            tokenEnd++;

        if (previousColumn < 0)
            return tokenEnd < line.Length && line[tokenEnd] is '=' or '{' or '[';

        var prefix = line.AsSpan(0, previousColumn + 1);
        if (prefix.Contains(":-", StringComparison.Ordinal)
            || prefix.Contains("-->", StringComparison.Ordinal)
            || line[previousColumn] == '.')
        {
            return false;
        }

        if (line[previousColumn] is '=' or '(' or '[' or '{' or ',' or '\\')
            return true;
        if (line[previousColumn] == '>'
            && previousColumn > 0
            && line[previousColumn - 1] == '=')
        {
            return true;
        }

        var previousTokenStart = previousColumn;
        while (previousTokenStart >= 0
            && (char.IsLetterOrDigit(line[previousTokenStart])
                || line[previousTokenStart] == '_'))
        {
            previousTokenStart--;
        }
        var previousToken = line.AsSpan(previousTokenStart + 1, previousColumn - previousTokenStart);
        return previousToken.Equals("my", StringComparison.Ordinal)
            || previousToken.Equals("our", StringComparison.Ordinal)
            || previousToken.Equals("state", StringComparison.Ordinal)
            || previousToken.Equals("local", StringComparison.Ordinal)
            || previousToken.Equals("return", StringComparison.Ordinal)
            || previousToken.Equals("keys", StringComparison.Ordinal)
            || previousToken.Equals("values", StringComparison.Ordinal)
            || previousToken.Equals("each", StringComparison.Ordinal)
            || previousToken.Equals("delete", StringComparison.Ordinal)
            || previousToken.Equals("exists", StringComparison.Ordinal)
            || previousToken.Equals("defined", StringComparison.Ordinal)
            || previousToken.Equals("scalar", StringComparison.Ordinal);
    }

    private static bool IsLikelyPerlModuloOperator(string line, int column)
    {
        var prefix = line.AsSpan(0, column);
        var hasPerlContext = prefix.Contains('$')
            || prefix.Contains('@')
            || StartsWithPerlStatementKeyword(prefix);
        if (!hasPerlContext)
            return false;

        var previousColumn = column - 1;
        while (previousColumn >= 0 && char.IsWhiteSpace(line[previousColumn]))
            previousColumn--;
        if (previousColumn < 0)
            return false;

        if (column + 1 < line.Length && line[column + 1] == '=')
            return true;

        var nextColumn = column + 1;
        while (nextColumn < line.Length && char.IsWhiteSpace(line[nextColumn]))
            nextColumn++;
        if (nextColumn >= line.Length)
            return false;

        var previous = line[previousColumn];
        var next = line[nextColumn];
        return (char.IsLetterOrDigit(previous) || previous is '_' or ')' or ']' or '}')
            && (char.IsLetterOrDigit(next) || next is '_' or '$' or '@' or '(' or '+' or '-');
    }

    private static bool StartsWithPerlStatementKeyword(ReadOnlySpan<char> prefix)
    {
        prefix = prefix.TrimStart();
        foreach (var keyword in new[] { "my", "our", "state", "local", "return" })
        {
            if (!prefix.StartsWith(keyword, StringComparison.Ordinal))
                continue;
            if (prefix.Length == keyword.Length || char.IsWhiteSpace(prefix[keyword.Length]))
                return true;
        }

        return false;
    }

    private static bool IsLikelySlashyLiteralStart(string line, int column)
    {
        if (column + 1 >= line.Length || line[column + 1] is '/' or '*')
            return false;

        var previousColumn = column - 1;
        while (previousColumn >= 0 && char.IsWhiteSpace(line[previousColumn]))
            previousColumn--;

        if (previousColumn < 0)
            return true;

        if (line[previousColumn] is '=' or '(' or '[' or '{' or ',' or ':' or ';'
            or '!' or '&' or '|' or '?' or '+' or '-' or '*' or '%' or '~')
        {
            return true;
        }

        var tokenEnd = previousColumn + 1;
        while (previousColumn >= 0
            && (char.IsLetterOrDigit(line[previousColumn]) || line[previousColumn] == '_'))
        {
            previousColumn--;
        }

        var token = line.AsSpan(previousColumn + 1, tokenEnd - previousColumn - 1);
        return token.SequenceEqual("return")
            || token.SequenceEqual("case")
            || token.SequenceEqual("throw")
            || token.SequenceEqual("assert")
            || token.SequenceEqual("in")
            || token.SequenceEqual("when")
            || token.SequenceEqual("if")
            || token.SequenceEqual("elsif")
            || token.SequenceEqual("unless")
            || token.SequenceEqual("while")
            || token.SequenceEqual("until");
    }

    private static void EnqueueAmbiguousPerlHeredocDelimiters(
        string line,
        string maskedLine,
        Queue<AmbiguousPerlHeredocDelimiter> delimiters)
    {
        for (var column = 0; column + 1 < line.Length;)
        {
            if (line[column] is '\'' or '"' or '`')
            {
                column = SkipQuotedToken(line, column, line[column]);
                continue;
            }

            if (line[column] != '<'
                || line[column + 1] != '<'
                || maskedLine[column] != '<'
                || maskedLine[column + 1] != '<')
            {
                column++;
                continue;
            }

            var delimiterColumn = column + 2;
            var allowIndent = delimiterColumn < line.Length
                && line[delimiterColumn] == '~';
            if (allowIndent)
                delimiterColumn++;

            var beforeWhitespace = delimiterColumn;
            delimiterColumn = SkipWhitespace(line, delimiterColumn);
            var hasWhitespace = delimiterColumn > beforeWhitespace;
            if (delimiterColumn >= line.Length)
                break;

            string? delimiter = null;
            var nextColumn = delimiterColumn + 1;
            if (line[delimiterColumn] is '\'' or '"' or '`')
            {
                var quote = line[delimiterColumn];
                var closingColumn = delimiterColumn + 1;
                while (closingColumn < line.Length
                    && line[closingColumn] != quote)
                {
                    if (line[closingColumn] == '\\'
                        && closingColumn + 1 < line.Length)
                    {
                        closingColumn += 2;
                    }
                    else
                    {
                        closingColumn++;
                    }
                }

                if (closingColumn < line.Length)
                {
                    delimiter = line[(delimiterColumn + 1)..closingColumn];
                    nextColumn = closingColumn + 1;
                }
            }
            else
            {
                if (line[delimiterColumn] == '\\')
                {
                    delimiterColumn++;
                }
                else if (hasWhitespace)
                {
                    column += 2;
                    continue;
                }

                var delimiterEnd = delimiterColumn;
                while (delimiterEnd < line.Length
                    && (char.IsLetterOrDigit(line[delimiterEnd])
                        || line[delimiterEnd] == '_'))
                {
                    delimiterEnd++;
                }

                if (delimiterEnd > delimiterColumn
                    && (char.IsLetter(line[delimiterColumn])
                        || line[delimiterColumn] == '_'))
                {
                    delimiter = line[delimiterColumn..delimiterEnd];
                    nextColumn = delimiterEnd;
                }
            }

            if (!string.IsNullOrEmpty(delimiter))
                delimiters.Enqueue(new AmbiguousPerlHeredocDelimiter(delimiter, allowIndent));
            column = Math.Max(nextColumn, column + 2);
        }
    }

    private static bool TryBeginAmbiguousPerlQuoteLikeLiteral(
        string line,
        char[] buffer,
        int column,
        out AmbiguousPerlQuoteLikeState state,
        out int contentColumn)
    {
        state = null!;
        contentColumn = column;
        if (column > 0
            && (char.IsLetterOrDigit(line[column - 1]) || line[column - 1] == '_'))
        {
            return false;
        }

        var operatorLength = line.AsSpan(column) switch
        {
            var span when span.StartsWith("qq", StringComparison.Ordinal)
                || span.StartsWith("qr", StringComparison.Ordinal)
                || span.StartsWith("qw", StringComparison.Ordinal)
                || span.StartsWith("qx", StringComparison.Ordinal)
                || span.StartsWith("tr", StringComparison.Ordinal) => 2,
            var span when span.StartsWith("q", StringComparison.Ordinal)
                || span.StartsWith("m", StringComparison.Ordinal)
                || span.StartsWith("s", StringComparison.Ordinal)
                || span.StartsWith("y", StringComparison.Ordinal) => 1,
            _ => 0,
        };
        if (operatorLength == 0)
            return false;

        var delimiterColumn = SkipWhitespace(line, column + operatorLength);
        if (delimiterColumn >= line.Length
            || char.IsLetterOrDigit(line[delimiterColumn])
            || line[delimiterColumn] == '_')
        {
            return false;
        }
        var delimiterSpan = line.AsSpan(delimiterColumn);
        if (delimiterSpan.StartsWith("=>", StringComparison.Ordinal)
            || delimiterSpan.StartsWith(":-", StringComparison.Ordinal)
            || delimiterSpan.StartsWith("-->", StringComparison.Ordinal))
        {
            return false;
        }

        var openingDelimiter = line[delimiterColumn];
        var closingDelimiter = GetPairedClosingDelimiter(openingDelimiter);
        var remainingSegments = line.AsSpan(column, operatorLength).SequenceEqual("s")
            || line.AsSpan(column, operatorLength).SequenceEqual("tr")
            || line.AsSpan(column, operatorLength).SequenceEqual("y")
                ? 2
                : 1;
        FillWithSpaces(buffer, column, delimiterColumn + 1);
        state = new AmbiguousPerlQuoteLikeState(
            openingDelimiter,
            closingDelimiter,
            remainingSegments);
        contentColumn = delimiterColumn + 1;
        return true;
    }

    private static void MaskAmbiguousPerlQuoteLikeCharacter(
        string line,
        char[] buffer,
        ref int column,
        ref AmbiguousPerlQuoteLikeState? state)
    {
        var current = state!;
        buffer[column] = ' ';
        if (current.AwaitingNextOpeningDelimiter)
        {
            if (char.IsWhiteSpace(line[column]))
            {
                column++;
                return;
            }

            current.OpeningDelimiter = line[column];
            current.ClosingDelimiter = GetPairedClosingDelimiter(line[column]);
            current.DelimiterDepth = current.OpeningDelimiter == current.ClosingDelimiter ? 0 : 1;
            current.AwaitingNextOpeningDelimiter = false;
            column++;
            return;
        }

        if (line[column] == '\\' && column + 1 < line.Length)
        {
            buffer[column + 1] = ' ';
            column += 2;
            return;
        }

        if (current.OpeningDelimiter != current.ClosingDelimiter
            && line[column] == current.OpeningDelimiter)
        {
            current.DelimiterDepth++;
            column++;
            return;
        }

        if (line[column] != current.ClosingDelimiter)
        {
            column++;
            return;
        }

        if (current.OpeningDelimiter != current.ClosingDelimiter
            && --current.DelimiterDepth > 0)
        {
            column++;
            return;
        }

        current.RemainingSegments--;
        if (current.RemainingSegments == 0)
        {
            state = null;
        }
        else if (current.OpeningDelimiter != current.ClosingDelimiter)
        {
            current.AwaitingNextOpeningDelimiter = true;
        }
        else
        {
            current.DelimiterDepth = 0;
        }

        column++;
    }

    private static char GetPairedClosingDelimiter(char openingDelimiter) =>
        openingDelimiter switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '<' => '>',
            _ => openingDelimiter,
        };

    private static bool TryBeginCrystalPercentLiteral(
        string line,
        int column,
        out char openingDelimiter,
        out char closingDelimiter,
        out int contentColumn)
    {
        openingDelimiter = '\0';
        closingDelimiter = '\0';
        contentColumn = column;
        if (line[column] != '%' || column + 1 >= line.Length)
            return false;

        var delimiterColumn = column + 1;
        var hasTypePrefix = line[delimiterColumn] is 'q' or 'Q' or 'w' or 'W' or 'i' or 'I' or 'x' or 'r';
        if (hasTypePrefix)
            delimiterColumn++;
        if (delimiterColumn >= line.Length
            || char.IsLetterOrDigit(line[delimiterColumn])
            || char.IsWhiteSpace(line[delimiterColumn])
            || (!hasTypePrefix && line[delimiterColumn] is not ('(' or '[' or '{' or '<')))
        {
            return false;
        }

        openingDelimiter = line[delimiterColumn];
        closingDelimiter = openingDelimiter switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '<' => '>',
            _ => openingDelimiter,
        };
        contentColumn = delimiterColumn + 1;
        return true;
    }

}
