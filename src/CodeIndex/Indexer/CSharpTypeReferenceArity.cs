using System.Text;

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

    internal static int? GetInvocationArgumentCount(
        string? context,
        string? symbolName,
        long? columnNumber)
        => GetInvocationArgumentCount(context, symbolName, columnNumber, spanLength: null);

    internal static int? GetInvocationArgumentCount(
        string? context,
        string? symbolName,
        long? columnNumber,
        long? spanLength)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(symbolName))
            return null;

        var occurrence = FindInvocationIdentifierOccurrence(
            context,
            symbolName,
            columnNumber,
            spanLength);
        if (occurrence < 0)
            return null;

        return TryFindInvocationOpenParenthesis(
                   context,
                   occurrence,
                   ReadIdentifierLength(context, occurrence),
                   out var openParenthesis)
               && TryAnalyzeTopLevelParameters(
                   context,
                   openParenthesis,
                   out var count,
                   out var hasNamedArgument,
                   out _,
                   out _,
                   out _,
                   out _)
               && !hasNamedArgument
            ? count
            : null;
    }

    internal static bool HasCompleteInvocationArgumentList(
        string? context,
        string? symbolName,
        long? columnNumber,
        long? spanLength)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(symbolName))
            return false;

        var occurrence = FindInvocationIdentifierOccurrence(
            context,
            symbolName,
            columnNumber,
            spanLength);
        return occurrence >= 0
               && TryFindInvocationOpenParenthesis(
                   context,
                   occurrence,
                   ReadIdentifierLength(context, occurrence),
                   out var openParenthesis)
               && TryAnalyzeTopLevelParameters(
                   context,
                   openParenthesis,
                   out _,
                   out _,
                   out _,
                   out _,
                   out _,
                   out _);
    }

    internal static int? GetUnambiguousInvocationArgumentCount(
        string? context,
        string? symbolName,
        long? columnNumber)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(symbolName))
            return null;

        var occurrence = FindClosestIdentifierOccurrence(context, symbolName, columnNumber);
        if (occurrence < 0)
            return null;

        var cursor = occurrence + symbolName.Length;
        if (!SkipCSharpTrivia(context, ref cursor)
            || cursor >= context.Length
            || context[cursor] != '(')
        {
            // Generic method binding needs type inference and deliberately stays unresolved.
            return null;
        }

        return TryAnalyzeTopLevelParameters(
                   context,
                   cursor,
                   out var count,
                   out var hasNamedArgument,
                   out var hasTopLevelColon,
                   out _,
                   out _,
                   out var hasAngleBrackets)
               && !hasNamedArgument
               && !hasTopLevelColon
               && !hasAngleBrackets
            ? count
            : null;
    }

    internal static int? GetUnambiguousCallableParameterCount(
        string? signature,
        string? symbolName,
        string? symbolKind)
    {
        if (!string.Equals(symbolKind, "function", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(symbolName))
        {
            return null;
        }

        for (var searchAt = 0; searchAt <= signature.Length - symbolName.Length;)
        {
            var occurrence = signature.IndexOf(symbolName, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                return null;
            searchAt = occurrence + Math.Max(1, symbolName.Length);
            if (!IsIdentifierOccurrence(signature, occurrence, symbolName.Length))
                continue;

            // A partial method's defining declaration and implementation may legally
            // disagree about where an optional default is written. Treat the physical
            // rows as one binding-sensitive family instead of narrowing only one side.
            if (ContainsIdentifier(signature, "partial", occurrence))
                return null;

            var cursor = occurrence + symbolName.Length;
            if (!SkipCSharpTrivia(signature, ref cursor)
                || cursor >= signature.Length
                || signature[cursor] != '(')
            {
                // Generic callables and malformed/truncated signatures remain ambiguous.
                continue;
            }

            return TryAnalyzeTopLevelParameters(
                       signature,
                       cursor,
                       out var count,
                       out _,
                       out _,
                       out var hasOptionalDefault,
                       out var hasBindingSensitiveModifier,
                       out _)
                   && !hasOptionalDefault
                   && !hasBindingSensitiveModifier
                ? count
                : null;
        }

        return null;
    }

    internal static int? GetDefinitionArity(string? signature, string? symbolName, string? symbolKind)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return null;
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var declarationSignature = SymbolExtractor.SanitizeCSharpDeclarationSignature(signature);
        var searchStart = FindDeclarationKeywordEnd(declarationSignature, symbolKind, symbolName);
        var occurrence = FindDefinitionIdentifierOccurrence(
            declarationSignature,
            symbolName,
            searchStart,
            symbolKind);
        return occurrence < 0 ? null : ReadArityAfterIdentifier(signature, occurrence, symbolName.Length);
    }

    internal static int? GetConstructorParameterCount(
        string? signature,
        string? symbolName,
        string? symbolKind)
        => AnalyzeConstructorParameters(signature, symbolName, symbolKind) is { BindingSensitive: false } analysis
            ? analysis.Count
            : null;

    internal static bool HasBindingSensitiveConstructorParameters(
        string? signature,
        string? symbolName,
        string? symbolKind)
        => AnalyzeConstructorParameters(signature, symbolName, symbolKind) is { BindingSensitive: true };

    private static ConstructorParameterAnalysis? AnalyzeConstructorParameters(
        string? signature,
        string? symbolName,
        string? symbolKind)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(symbolName))
            return null;

        var typeDeclaration = symbolKind is "class" or "struct" or "record";
        var constructorFunction = string.Equals(symbolKind, "function", StringComparison.Ordinal);
        if (!typeDeclaration && !constructorFunction)
            return null;

        var searchableSignature = typeDeclaration
            ? SymbolExtractor.SanitizeCSharpDeclarationSignature(signature)
            : signature;
        var searchStart = typeDeclaration
            ? FindDeclarationKeywordEnd(searchableSignature, symbolKind, symbolName)
            : 0;
        for (var searchAt = Math.Clamp(searchStart, 0, searchableSignature.Length);
             searchAt <= searchableSignature.Length - symbolName.Length;)
        {
            var occurrence = searchableSignature.IndexOf(symbolName, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                return null;
            searchAt = occurrence + Math.Max(1, symbolName.Length);
            if (!IsIdentifierOccurrence(searchableSignature, occurrence, symbolName.Length))
                continue;

            if (constructorFunction)
            {
                var previous = occurrence - 1;
                while (previous >= 0 && char.IsWhiteSpace(signature[previous]))
                    previous--;
                if (previous >= 0 && signature[previous] == '~')
                    return null;
                if (ContainsIdentifier(signature, "static", occurrence))
                    return null;
            }

            var cursor = occurrence + symbolName.Length;
            if (!SkipCSharpTrivia(signature, ref cursor))
                return null;
            if (typeDeclaration && cursor < signature.Length && signature[cursor] == '<')
            {
                if (!TryCountTopLevelTypeArguments(
                        signature,
                        cursor,
                        out _,
                        out var closeAngleIndex))
                {
                    return null;
                }

                cursor = closeAngleIndex + 1;
                if (!SkipCSharpTrivia(signature, ref cursor))
                    return null;
            }

            if (cursor < signature.Length && signature[cursor] == '(')
            {
                return TryAnalyzeTopLevelParameters(
                    signature,
                    cursor,
                    out var count,
                    out _,
                    out _,
                    out var hasOptionalDefault,
                    out var hasBindingSensitiveModifier,
                    out _)
                    ? new ConstructorParameterAnalysis(
                        count,
                        hasOptionalDefault || hasBindingSensitiveModifier)
                    : null;
            }
            if (typeDeclaration)
                return null;
        }

        return null;
    }

    private readonly record struct ConstructorParameterAnalysis(
        int Count,
        bool BindingSensitive);

    internal static bool IsValueTypeDeclaration(string? signature, string? symbolKind)
        => string.Equals(symbolKind, "struct", StringComparison.Ordinal)
           || (string.Equals(symbolKind, "record", StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(signature)
               && ContainsIdentifier(signature, "struct", signature.Length));

    internal static string NormalizeTypeIdentityArity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return string.Empty;

        var value = identity.Trim();
        if (value.StartsWith("global::", StringComparison.Ordinal))
            value = value["global::".Length..];

        var normalized = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var current = value[index];
            if (char.IsWhiteSpace(current) || current == '@')
            {
                index++;
                continue;
            }

            if (!IsIdentifierPart(current) || char.IsDigit(current))
            {
                if (current != '?')
                    normalized.Append(current);
                index++;
                continue;
            }

            var identifierStart = index;
            while (index < value.Length && IsIdentifierPart(value[index]))
                index++;
            normalized.Append(value, identifierStart, index - identifierStart);

            var genericStart = index;
            SkipWhitespace(value, ref genericStart);
            if (genericStart >= value.Length || value[genericStart] != '<')
                continue;
            if (!TryCountTopLevelTypeArguments(
                    value,
                    genericStart,
                    out var arity,
                    out var closeAngleIndex))
            {
                return string.Empty;
            }

            normalized.Append('`');
            normalized.Append(arity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            index = closeAngleIndex + 1;
        }

        return normalized.ToString();
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

    private static int FindInvocationIdentifierOccurrence(
        string text,
        string canonicalIdentifier,
        long? columnNumber,
        long? spanLength)
    {
        var recordedOccurrence = -1;
        var hasRecordedInvocation = columnNumber is > 0 and <= int.MaxValue
            && spanLength is > 0 and <= int.MaxValue
            && TryReadIdentifierAtRecordedSpan(
                text,
                (int)columnNumber.Value - 1,
                (int)spanLength.Value,
                out recordedOccurrence)
            && TryFindInvocationOpenParenthesis(
                text,
                recordedOccurrence,
                ReadIdentifierLength(text, recordedOccurrence),
                out _);
        if (hasRecordedInvocation)
            return recordedOccurrence;

        var canonicalOccurrence = FindClosestObjectCreationIdentifierOccurrence(
            text,
            canonicalIdentifier,
            columnNumber);
        if (canonicalOccurrence >= 0)
            return canonicalOccurrence;

        return spanLength is > 0 and <= int.MaxValue
            ? FindUniqueObjectCreationIdentifierBySpan(text, (int)spanLength.Value)
            : -1;
    }

    private static int FindClosestObjectCreationIdentifierOccurrence(
        string text,
        string identifier,
        long? columnNumber)
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
            searchAt = occurrence + Math.Max(1, identifier.Length);
            if (!IsIdentifierOccurrence(text, occurrence, identifier.Length)
                || occurrence > expectedIndex
                || !IsObjectCreationIdentifier(text, occurrence))
            {
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

        if (bestIndex >= 0)
            return bestIndex;

        for (var searchAt = 0; searchAt <= text.Length - identifier.Length;)
        {
            var occurrence = text.IndexOf(identifier, searchAt, StringComparison.Ordinal);
            if (occurrence < 0)
                break;
            if (IsIdentifierOccurrence(text, occurrence, identifier.Length)
                && IsObjectCreationIdentifier(text, occurrence))
            {
                return occurrence;
            }
            searchAt = occurrence + Math.Max(1, identifier.Length);
        }

        return -1;
    }

    private static bool TryReadIdentifierAtRecordedSpan(
        string text,
        int recordedStart,
        int recordedLength,
        out int identifierStart)
    {
        identifierStart = -1;
        if (recordedStart < 0
            || recordedLength <= 0
            || recordedStart + recordedLength > text.Length)
        {
            return false;
        }

        identifierStart = text[recordedStart] == '@' ? recordedStart + 1 : recordedStart;
        var identifierLength = recordedLength - (identifierStart - recordedStart);
        return identifierLength > 0
               && IsIdentifierStart(text[identifierStart])
               && ReadIdentifierLength(text, identifierStart) == identifierLength;
    }

    private static int FindUniqueObjectCreationIdentifierBySpan(string text, int spanLength)
    {
        var uniqueOccurrence = -1;
        for (var cursor = 0; cursor < text.Length;)
        {
            if (text[cursor] is '"' or '\'')
            {
                if (text[cursor] == '"'
                    && cursor + 2 < text.Length
                    && text[cursor + 1] == '"'
                    && text[cursor + 2] == '"')
                {
                    return -1;
                }

                cursor = SkipQuotedLiteral(text, cursor, text[cursor]) + 1;
                continue;
            }
            if (text[cursor] == '/' && cursor + 1 < text.Length)
            {
                if (text[cursor + 1] == '/')
                {
                    cursor = SkipLineComment(text, cursor);
                    continue;
                }
                if (text[cursor + 1] == '*')
                {
                    cursor = SkipBlockComment(text, cursor) + 1;
                    continue;
                }
            }

            var tokenStart = cursor;
            var identifierStart = text[cursor] == '@' ? cursor + 1 : cursor;
            if (identifierStart >= text.Length || !IsIdentifierStart(text[identifierStart]))
            {
                cursor++;
                continue;
            }

            var identifierLength = ReadIdentifierLength(text, identifierStart);
            cursor = identifierStart + identifierLength;
            if (cursor - tokenStart != spanLength
                || !IsObjectCreationIdentifier(text, identifierStart))
            {
                continue;
            }

            if (uniqueOccurrence >= 0)
                return -1;
            uniqueOccurrence = identifierStart;
        }

        return uniqueOccurrence;
    }

    private static bool IsObjectCreationIdentifier(string text, int identifierStart)
    {
        var cursor = identifierStart;
        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;
        if (!SkipCSharpTriviaBackward(text, ref cursor))
            return false;

        while (cursor > 0 && text[cursor - 1] == '.')
        {
            cursor--;
            if (!SkipCSharpTriviaBackward(text, ref cursor)
                || !SkipTypeIdentifierBackward(text, ref cursor)
                || !SkipCSharpTriviaBackward(text, ref cursor))
            {
                return false;
            }
        }

        if (cursor >= 2 && text[cursor - 2] == ':' && text[cursor - 1] == ':')
        {
            cursor -= 2;
            if (!SkipCSharpTriviaBackward(text, ref cursor)
                || !SkipTypeIdentifierBackward(text, ref cursor)
                || !SkipCSharpTriviaBackward(text, ref cursor))
            {
                return false;
            }
        }

        const string keyword = "new";
        var keywordStart = cursor - keyword.Length;
        return keywordStart >= 0
               && text.AsSpan(keywordStart, keyword.Length).SequenceEqual(keyword)
               && IsIdentifierOccurrence(text, keywordStart, keyword.Length)
               && TryFindInvocationOpenParenthesis(
                   text,
                   identifierStart,
                   ReadIdentifierLength(text, identifierStart),
                   out _);
    }

    private static bool SkipTypeIdentifierBackward(string text, ref int cursor)
    {
        if (cursor > 0 && text[cursor - 1] == '>')
        {
            var angleDepth = 1;
            cursor--;
            while (cursor > 0 && angleDepth > 0)
            {
                cursor--;
                angleDepth += text[cursor] switch
                {
                    '>' => 1,
                    '<' => -1,
                    _ => 0,
                };
            }
            if (angleDepth != 0 || !SkipCSharpTriviaBackward(text, ref cursor))
                return false;
        }

        var identifierEnd = cursor;
        while (cursor > 0 && IsIdentifierPart(text[cursor - 1]))
            cursor--;
        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;
        return cursor < identifierEnd;
    }

    private static bool TryFindInvocationOpenParenthesis(
        string text,
        int identifierStart,
        int identifierLength,
        out int openParenthesis)
    {
        openParenthesis = -1;
        var cursor = identifierStart + identifierLength;
        if (!SkipCSharpTrivia(text, ref cursor))
            return false;
        if (cursor < text.Length && text[cursor] == '<')
        {
            if (!TryCountTopLevelTypeArguments(
                    text,
                    cursor,
                    out _,
                    out var closeAngleIndex))
            {
                return false;
            }

            cursor = closeAngleIndex + 1;
            if (!SkipCSharpTrivia(text, ref cursor))
                return false;
        }

        if (cursor >= text.Length || text[cursor] != '(')
            return false;
        openParenthesis = cursor;
        return true;
    }

    private static int ReadIdentifierLength(string text, int identifierStart)
    {
        var cursor = identifierStart;
        while (cursor < text.Length && IsIdentifierPart(text[cursor]))
            cursor++;
        return cursor - identifierStart;
    }

    private static bool ContainsIdentifier(string text, string identifier, int endExclusive)
    {
        for (var searchAt = 0; searchAt <= endExclusive - identifier.Length;)
        {
            var occurrence = text.IndexOf(identifier, searchAt, StringComparison.Ordinal);
            if (occurrence < 0 || occurrence >= endExclusive)
                return false;
            if (IsIdentifierOccurrence(text, occurrence, identifier.Length))
                return true;
            searchAt = occurrence + identifier.Length;
        }

        return false;
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

    private static int FindDeclarationKeywordEnd(
        string signature,
        string? symbolKind,
        string? symbolName = null)
    {
        if (string.IsNullOrWhiteSpace(symbolKind))
            return 0;

        string[]? keywords = symbolKind switch
        {
            // Plain records are emitted through the existing class kind, while record
            // structs use the struct kind. Accept the source declaration keyword for
            // both representations so earlier same-name attribute arguments cannot be
            // mistaken for the declaration identifier.
            // plain record は既存の class kind、record struct は struct kind で出力される。
            // source 上の record keyword も候補にし、先行 attribute 内の同名参照を
            // declaration identifier と誤認しない。
            "class" => ["class", "record"],
            "struct" => ["struct", "record"],
            "record" => ["record"],
            "interface" => ["interface"],
            "enum" => ["enum"],
            "delegate" => ["delegate"],
            _ => null,
        };
        if (keywords == null)
            return 0;

        var bestDeclarationStart = int.MaxValue;
        foreach (var keyword in keywords)
        {
            for (var searchAt = 0; searchAt <= signature.Length - keyword.Length;)
            {
                var occurrence = signature.IndexOf(keyword, searchAt, StringComparison.Ordinal);
                if (occurrence < 0)
                    break;
                searchAt = occurrence + keyword.Length;
                if (!IsIdentifierOccurrence(signature, occurrence, keyword.Length))
                    continue;

                var declarationStart = searchAt;
                if (!SkipCSharpTrivia(signature, ref declarationStart))
                    continue;
                if (keyword == "record")
                    SkipOptionalRecordTypeKeyword(signature, ref declarationStart);
                if (!string.IsNullOrWhiteSpace(symbolName)
                    && !IsDeclarationIdentifierAt(signature, declarationStart, symbolName))
                {
                    continue;
                }

                bestDeclarationStart = Math.Min(bestDeclarationStart, declarationStart);
            }
        }

        return bestDeclarationStart == int.MaxValue ? 0 : bestDeclarationStart;
    }

    private static void SkipOptionalRecordTypeKeyword(string signature, ref int cursor)
    {
        foreach (var keyword in new[] { "class", "struct" })
        {
            if (cursor + keyword.Length > signature.Length
                || !signature.AsSpan(cursor, keyword.Length).SequenceEqual(keyword)
                || !IsIdentifierOccurrence(signature, cursor, keyword.Length))
            {
                continue;
            }

            cursor += keyword.Length;
            SkipCSharpTrivia(signature, ref cursor);
            return;
        }
    }

    private static bool IsDeclarationIdentifierAt(
        string signature,
        int cursor,
        string symbolName)
    {
        if (cursor < signature.Length && signature[cursor] == '@')
            cursor++;
        return cursor + symbolName.Length <= signature.Length
               && signature.AsSpan(cursor, symbolName.Length).SequenceEqual(symbolName)
               && IsIdentifierOccurrence(signature, cursor, symbolName.Length);
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
                {
                    i = SkipLineComment(text, i) - 1;
                    if (i >= text.Length)
                        return false;
                    continue;
                }
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

    private static bool TryCountTopLevelParameters(string text, int openParenthesis, out int count)
        => TryAnalyzeTopLevelParameters(
            text,
            openParenthesis,
            out count,
            out _,
            out _,
            out _,
            out _,
            out _);

    private static bool TryAnalyzeTopLevelParameters(
        string text,
        int openParenthesis,
        out int count,
        out bool hasNamedArgument,
        out bool hasTopLevelColon,
        out bool hasTopLevelEquals,
        out bool hasBindingSensitiveModifier,
        out bool hasAngleBrackets)
    {
        count = 0;
        hasNamedArgument = false;
        hasTopLevelColon = false;
        hasTopLevelEquals = false;
        hasBindingSensitiveModifier = false;
        hasAngleBrackets = false;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;
        var hasItemContent = false;
        var hasTopLevelSeparator = false;
        var itemStart = openParenthesis + 1;
        for (var i = openParenthesis + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                // Raw strings can contain unescaped commas and quote characters. The
                // lightweight scanner deliberately keeps those calls ambiguous.
                if (c == '"'
                    && i + 2 < text.Length
                    && text[i + 1] == '"'
                    && text[i + 2] == '"')
                {
                    return false;
                }

                i = SkipQuotedLiteral(text, i, c);
                if (i >= text.Length)
                    return false;
                hasItemContent = true;
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i = SkipLineComment(text, i) - 1;
                    if (i >= text.Length)
                        return false;
                    continue;
                }
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
                case '(':
                    parenthesisDepth++;
                    hasItemContent = true;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    hasItemContent = true;
                    break;
                case ')' when bracketDepth == 0 && braceDepth == 0 && angleDepth == 0:
                    if (hasTopLevelSeparator && !hasItemContent)
                        return false;
                    count = hasItemContent ? count + 1 : 0;
                    return true;
                case '[':
                    bracketDepth++;
                    hasItemContent = true;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    hasItemContent = true;
                    break;
                case '{':
                    braceDepth++;
                    hasItemContent = true;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    hasItemContent = true;
                    break;
                case '<':
                    hasAngleBrackets = true;
                    angleDepth++;
                    hasItemContent = true;
                    break;
                case '>' when angleDepth > 0:
                    angleDepth--;
                    hasItemContent = true;
                    break;
                case ',' when parenthesisDepth == 0
                                   && bracketDepth == 0
                                   && braceDepth == 0
                                   && angleDepth == 0:
                    if (!hasItemContent)
                        return false;
                    count++;
                    hasItemContent = false;
                    hasTopLevelSeparator = true;
                    itemStart = i + 1;
                    break;
                case ':' when parenthesisDepth == 0
                                  && bracketDepth == 0
                                  && braceDepth == 0
                                  && angleDepth == 0:
                    hasTopLevelColon = true;
                    hasNamedArgument |= IsNamedArgumentPrefix(text, itemStart, i);
                    hasItemContent = true;
                    break;
                case '=' when parenthesisDepth == 0
                                  && bracketDepth == 0
                                  && braceDepth == 0
                                  && angleDepth == 0:
                    hasTopLevelEquals = true;
                    hasItemContent = true;
                    break;
                default:
                    if (parenthesisDepth == 0
                        && braceDepth == 0
                        && angleDepth == 0
                        && IsIdentifierStart(c))
                    {
                        var identifierEnd = i + 1;
                        while (identifierEnd < text.Length && IsIdentifierPart(text[identifierEnd]))
                            identifierEnd++;
                        var identifier = text.AsSpan(i, identifierEnd - i);
                        var escapedIdentifier = i > 0 && text[i - 1] == '@';
                        hasBindingSensitiveModifier |= bracketDepth == 0
                            ? !escapedIdentifier
                              && (identifier.SequenceEqual("params".AsSpan())
                                  || identifier.SequenceEqual("this".AsSpan()))
                            : identifier.SequenceEqual("Optional".AsSpan())
                              || identifier.SequenceEqual("OptionalAttribute".AsSpan())
                              || identifier.SequenceEqual("DefaultParameterValue".AsSpan())
                              || identifier.SequenceEqual("DefaultParameterValueAttribute".AsSpan());
                        i = identifierEnd - 1;
                    }
                    hasItemContent |= !char.IsWhiteSpace(c);
                    break;
            }
        }

        return false;
    }

    private static bool IsNamedArgumentPrefix(string text, int itemStart, int colonIndex)
    {
        if ((colonIndex > 0 && text[colonIndex - 1] == ':')
            || (colonIndex + 1 < text.Length && text[colonIndex + 1] == ':'))
        {
            return false;
        }

        var cursor = itemStart;
        if (!SkipCSharpTrivia(text, ref cursor) || cursor >= colonIndex)
            return false;
        if (text[cursor] == '@')
            cursor++;
        if (cursor >= colonIndex || !IsIdentifierStart(text[cursor]))
            return false;

        cursor++;
        while (cursor < colonIndex && IsIdentifierPart(text[cursor]))
            cursor++;
        return SkipCSharpTrivia(text, ref cursor) && cursor == colonIndex;
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

    private static int SkipLineComment(string text, int slashIndex)
    {
        var newlineIndex = text.IndexOf('\n', slashIndex + 2);
        return newlineIndex < 0 ? text.Length : newlineIndex + 1;
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

    private static bool IsIdentifierStart(char c)
        => char.IsLetter(c) || c == '_';

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
                cursor = SkipLineComment(text, cursor);
                continue;
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

    private static bool SkipCSharpTriviaBackward(string text, ref int cursor)
    {
        while (cursor > 0)
        {
            while (cursor > 0 && char.IsWhiteSpace(text[cursor - 1]))
                cursor--;
            if (cursor < 2 || text[cursor - 2] != '*' || text[cursor - 1] != '/')
                return true;

            var openIndex = text.LastIndexOf("/*", cursor - 2, StringComparison.Ordinal);
            if (openIndex < 0)
                return false;
            cursor = openIndex;
        }

        return true;
    }
}
