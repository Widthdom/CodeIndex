namespace CodeIndex.Indexer;

internal static class CSharpTypeReferenceArity
{
    internal static int? GetReferenceArity(string? context, string? symbolName, long? columnNumber)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(symbolName))
            return null;

        var occurrence = FindClosestIdentifierOccurrence(context, symbolName, columnNumber);
        return occurrence < 0 ? null : ReadArityAfterIdentifier(context, occurrence, symbolName.Length);
    }

    internal static bool IsMemberReceiver(string? context, string? symbolName, long? columnNumber)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(symbolName))
            return false;

        var occurrence = FindClosestIdentifierOccurrence(context, symbolName, columnNumber);
        if (occurrence < 0)
            return false;

        var cursor = occurrence + symbolName.Length;
        SkipWhitespace(context, ref cursor);
        return cursor < context.Length && context[cursor] == '.';
    }

    internal static int? GetDefinitionArity(string? signature, string? symbolName, string? symbolKind)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return null;
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var searchStart = FindDeclarationKeywordEnd(signature, symbolKind);
        var occurrence = FindDefinitionIdentifierOccurrence(signature, symbolName, searchStart, symbolKind);
        return occurrence < 0 ? null : ReadArityAfterIdentifier(signature, occurrence, symbolName.Length);
    }

    private static int FindClosestIdentifierOccurrence(string text, string identifier, long? columnNumber)
    {
        var expectedIndex = columnNumber is > 0 and <= int.MaxValue
            ? (int)columnNumber.Value - 1
            : int.MaxValue;
        var bestIndex = -1;
        var bestDistance = int.MaxValue;
        for (var searchAt = 0; searchAt <= text.Length - identifier.Length;)
        {
            var occurrence = text.IndexOf(identifier, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                break;

            if (IsIdentifierOccurrence(text, occurrence, identifier.Length))
            {
                // Reference columns are measured against the original line while context is
                // trimmed. Trimming can only move the matching token to the left, so an
                // occurrence to the right of the original column cannot be the referenced
                // token. This upper bound prevents a later same-name generic from stealing
                // the first reference on an indented line.
                // reference column は元の行基準だが context は trim 済みである。trim により
                // 対象 token は左へしか動かないため、元 column より右の occurrence は候補外。
                // これにより indent された同一行の後続同名 generic への誤対応を防ぐ。
                if (occurrence > expectedIndex)
                {
                    searchAt = occurrence + Math.Max(1, identifier.Length);
                    continue;
                }

                var distance = expectedIndex == int.MaxValue
                    ? occurrence
                    : expectedIndex - occurrence;
                if (distance < bestDistance)
                {
                    bestIndex = occurrence;
                    bestDistance = distance;
                }
            }

            searchAt = occurrence + Math.Max(1, identifier.Length);
        }

        if (bestIndex >= 0)
            return bestIndex;

        // Legacy/plugin rows can carry a column that is already context-relative or no
        // usable column at all. In that case retain a deterministic exact-case fallback.
        for (var searchAt = 0; searchAt <= text.Length - identifier.Length;)
        {
            var occurrence = text.IndexOf(identifier, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                break;
            if (IsIdentifierOccurrence(text, occurrence, identifier.Length))
                return occurrence;
            searchAt = occurrence + Math.Max(1, identifier.Length);
        }

        return -1;
    }

    private static int FindDefinitionIdentifierOccurrence(
        string signature,
        string symbolName,
        int searchStart,
        string? symbolKind)
    {
        var isDelegate = string.Equals(symbolKind, "delegate", StringComparison.Ordinal);
        for (var searchAt = Math.Clamp(searchStart, 0, signature.Length);
             searchAt <= signature.Length - symbolName.Length;)
        {
            var occurrence = signature.IndexOf(symbolName, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                break;

            if (IsIdentifierOccurrence(signature, occurrence, symbolName.Length))
            {
                if (!isDelegate
                    || IsDelegateDeclarationName(signature, occurrence, symbolName.Length))
                {
                    return occurrence;
                }
            }

            searchAt = occurrence + Math.Max(1, symbolName.Length);
        }

        return -1;
    }

    private static int FindDeclarationKeywordEnd(string signature, string? symbolKind)
    {
        if (string.IsNullOrWhiteSpace(symbolKind))
            return 0;

        var keyword = symbolKind switch
        {
            "class" => "class",
            "struct" => "struct",
            "record" => "record",
            "interface" => "interface",
            "enum" => "enum",
            "delegate" => "delegate",
            _ => null,
        };
        if (keyword == null)
            return 0;

        for (var searchAt = 0; searchAt <= signature.Length - keyword.Length;)
        {
            var occurrence = signature.IndexOf(keyword, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                return 0;
            if (IsIdentifierOccurrence(signature, occurrence, keyword.Length))
                return occurrence + keyword.Length;
            searchAt = occurrence + keyword.Length;
        }

        return 0;
    }

    private static int? ReadArityAfterIdentifier(string text, int occurrence, int identifierLength)
    {
        var cursor = occurrence + identifierLength;
        if (!SkipCSharpTrivia(text, ref cursor))
            return null;
        if (cursor >= text.Length || text[cursor] != '<')
            return 0;

        return TryCountTopLevelTypeArguments(text, cursor, out var arity, out _) ? arity : null;
    }

    private static bool TryCountTopLevelTypeArguments(
        string text,
        int openAngleIndex,
        out int arity,
        out int closeAngleIndex)
    {
        arity = 1;
        closeAngleIndex = -1;
        var angleDepth = 1;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = openAngleIndex + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipQuotedLiteral(text, i, c);
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                    return false;
                if (text[i + 1] == '*')
                {
                    i = SkipBlockComment(text, i);
                    if (i >= text.Length)
                        return false;
                    continue;
                }
            }

            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        closeAngleIndex = i;
                        return true;
                    }
                    break;
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    if (parenthesisDepth > 0)
                        parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ',' when angleDepth == 1
                                   && parenthesisDepth == 0
                                   && bracketDepth == 0
                                   && braceDepth == 0:
                    arity++;
                    break;
            }
        }

        return false;
    }

    private static int SkipQuotedLiteral(string text, int quoteIndex, char quote)
    {
        var verbatim = quote == '"' && quoteIndex > 0 && text[quoteIndex - 1] == '@';
        for (var i = quoteIndex + 1; i < text.Length; i++)
        {
            if (!verbatim && text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }
            if (text[i] != quote)
                continue;
            if (verbatim && i + 1 < text.Length && text[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i;
        }

        return text.Length;
    }

    private static int SkipBlockComment(string text, int slashIndex)
    {
        var closeIndex = text.IndexOf("*/", slashIndex + 2, StringComparison.Ordinal);
        return closeIndex < 0 ? text.Length : closeIndex + 1;
    }

    private static bool IsDelegateDeclarationName(
        string signature,
        int occurrence,
        int identifierLength)
    {
        var cursor = occurrence + identifierLength;
        if (!SkipCSharpTrivia(signature, ref cursor))
            return false;
        if (cursor < signature.Length && signature[cursor] == '<')
        {
            if (!TryCountTopLevelTypeArguments(
                    signature,
                    cursor,
                    out _,
                    out var closeAngleIndex))
            {
                return false;
            }

            cursor = closeAngleIndex + 1;
            if (!SkipCSharpTrivia(signature, ref cursor))
                return false;
        }

        return cursor < signature.Length && signature[cursor] == '(';
    }

    private static bool IsIdentifierOccurrence(string text, int occurrence, int length)
    {
        var before = occurrence - 1;
        var after = occurrence + length;
        return (before < 0 || !IsIdentifierPart(text[before]))
               && (after >= text.Length || !IsIdentifierPart(text[after]));
    }

    private static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private static void SkipWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;
    }

    private static bool SkipCSharpTrivia(string text, ref int cursor)
    {
        while (cursor < text.Length)
        {
            SkipWhitespace(text, ref cursor);
            if (cursor + 1 >= text.Length || text[cursor] != '/')
                return true;

            if (text[cursor + 1] == '/')
            {
                cursor = text.Length;
                return false;
            }

            if (text[cursor + 1] != '*')
                return true;

            var closeIndex = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
            if (closeIndex < 0)
            {
                cursor = text.Length;
                return false;
            }

            cursor = closeIndex + 2;
        }

        return true;
    }
}
