using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool IsCSharpCastPrefixIdentifier(string line, int tokenStartColumn, string token)
    {
        if (tokenStartColumn > 0 && line[tokenStartColumn - 1] == '@')
            return false;

        return string.Equals(token, "return", StringComparison.Ordinal)
            || string.Equals(token, "await", StringComparison.Ordinal)
            || string.Equals(token, "throw", StringComparison.Ordinal)
            || IsCSharpQueryClauseKeyword(token);
    }

    private static bool LooksLikeCSharpCastTypeText(
        string text,
        int lineNumber,
        int column,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        var index = 0;
        if (!TryConsumeCSharpCastType(trimmed, ref index))
            return false;

        SkipCSharpCastTypeWhitespace(trimmed, ref index);
        if (index != trimmed.Length)
            return false;

        var shape = AnalyzeCSharpCastTypeShape(trimmed);
        if (shape.IdentifierSegments.Count == 0)
            return shape.HasTypeOnlySyntax;

        var resolvedQualifiedName = shape.SimpleQualifiedName == null
            ? null
            : ResolveCSharpQualifiedAliasTarget(shape.SimpleQualifiedName, lineNumber, csharpUsingAliases);
        var resolvedBareName = resolvedQualifiedName == null
            ? null
            : ExtractBareTypeName(resolvedQualifiedName);

        var lastSegment = shape.IdentifierSegments[^1];
        if (HasKnownNonTerminalTypeSegment(shape.IdentifierSegments, csharpKnownTypeNames)
            && !IsKnownCSharpCastTypeName(lastSegment, resolvedBareName, csharpKnownTypeNames))
        {
            return false;
        }

        if (IsKnownCSharpCastTypeName(lastSegment, resolvedBareName, csharpKnownTypeNames)
            || (!string.IsNullOrWhiteSpace(resolvedQualifiedName) && csharpKnownTypeNames.Contains(resolvedQualifiedName)))
        {
            return true;
        }

        if (shape.SimpleQualifiedName != null
            && string.Equals(shape.SimpleQualifiedName, resolvedQualifiedName, StringComparison.Ordinal)
            && HasCSharpFunctionValueReceiverConflict(
                GetFirstQualifiedSegment(shape.SimpleQualifiedName),
                lineNumber,
                column,
                csharpFunctionValueReceiverNames))
        {
            return false;
        }

        if (shape.HasTypeOnlySyntax)
            return true;

        return shape.AllIdentifiersTypeLike && shape.IdentifierSegments.Count <= 2;
    }

    private static bool TryConsumeCSharpCastType(string text, ref int index)
    {
        if (!TryConsumeCSharpCastTypeCore(text, ref index))
            return false;

        while (true)
        {
            var checkpoint = index;
            SkipCSharpCastTypeWhitespace(text, ref index);
            if (TryConsumeCSharpCastArraySuffix(text, ref index)
                || TryConsumeCSharpCastNullableSuffix(text, ref index))
            {
                continue;
            }

            index = checkpoint;
            return true;
        }
    }

    private static bool TryConsumeCSharpCastTypeCore(string text, ref int index)
    {
        SkipCSharpCastTypeWhitespace(text, ref index);
        if (index < text.Length && text[index] == '(')
            return TryConsumeCSharpCastTupleType(text, ref index);

        return TryConsumeCSharpCastQualifiedType(text, ref index);
    }

    private static bool TryConsumeCSharpCastQualifiedType(string text, ref int index)
    {
        if (!TryConsumeCSharpCastIdentifier(text, ref index, out var token))
            return false;

        if (!TryConsumeCSharpCastGenericArgumentList(text, ref index))
            return false;

        while (true)
        {
            var checkpoint = index;
            SkipCSharpCastTypeWhitespace(text, ref index);
            if (!TryConsumeCSharpCastQualifiedTypeSeparator(text, ref index))
            {
                index = checkpoint;
                return true;
            }

            if (!TryConsumeCSharpCastIdentifier(text, ref index, out token))
                return false;

            if (!TryConsumeCSharpCastGenericArgumentList(text, ref index))
                return false;
        }
    }

    private static bool TryConsumeCSharpCastTupleType(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '(')
            return false;

        index++;
        while (true)
        {
            if (!TryConsumeCSharpCastType(text, ref index))
                return false;

            var checkpoint = index;
            if (TryConsumeCSharpCastIdentifier(text, ref index, out _))
            {
                // Tuple element names are optional and do not affect type-likeness.
            }
            else
            {
                index = checkpoint;
            }

            SkipCSharpCastTypeWhitespace(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == ')')
            {
                index++;
                return true;
            }

            if (text[index] != ',')
                return false;

            index++;
        }
    }

    private static bool TryConsumeCSharpCastGenericArgumentList(string text, ref int index)
    {
        var checkpoint = index;
        SkipCSharpCastTypeWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '<')
        {
            index = checkpoint;
            return true;
        }

        index++;
        while (true)
        {
            if (!TryConsumeCSharpCastType(text, ref index))
                return false;

            SkipCSharpCastTypeWhitespace(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == '>')
            {
                index++;
                return true;
            }

            if (text[index] != ',')
                return false;

            index++;
        }
    }

    private static bool TryConsumeCSharpCastArraySuffix(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '[')
            return false;

        index++;
        SkipCSharpCastTypeWhitespace(text, ref index);
        while (index < text.Length && text[index] == ',')
        {
            index++;
            SkipCSharpCastTypeWhitespace(text, ref index);
        }

        if (index >= text.Length || text[index] != ']')
            return false;

        index++;
        return true;
    }

    private static bool TryConsumeCSharpCastNullableSuffix(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '?')
            return false;

        index++;
        return true;
    }

    private static bool TryConsumeCSharpCastQualifiedTypeSeparator(string text, ref int index)
    {
        if (index >= text.Length)
            return false;

        if (text[index] == '.')
        {
            index++;
            return true;
        }

        if (index + 1 < text.Length && text[index] == ':' && text[index + 1] == ':')
        {
            index += 2;
            return true;
        }

        return false;
    }

    private static bool TryConsumeCSharpCastIdentifier(string text, ref int index, out string token)
    {
        SkipCSharpCastTypeWhitespace(text, ref index);
        token = string.Empty;
        if (index >= text.Length)
            return false;

        var start = index;
        if (text[index] == '@')
        {
            index++;
            if (index >= text.Length || !IsCSharpIdentifierStart(text[index]))
            {
                index = start;
                return false;
            }
        }
        else if (!IsCSharpIdentifierStart(text[index]))
        {
            return false;
        }

        index++;
        while (index < text.Length && IsCSharpIdentifierPart(text[index]))
            index++;

        token = text.Substring(start, index - start);
        return true;
    }

    private static CSharpCastTypeShape AnalyzeCSharpCastTypeShape(string text)
    {
        var segments = new List<string>();
        var simpleQualifiedName = new System.Text.StringBuilder(text.Length);
        var hasTypeOnlySyntax = false;
        var allIdentifiersTypeLike = true;
        var simpleQualifiedCandidate = true;

        for (var index = 0; index < text.Length;)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '@' || IsCSharpIdentifierStart(current))
            {
                var start = index;
                if (current == '@')
                    index++;
                if (index < text.Length)
                    index++;
                while (index < text.Length && IsCSharpIdentifierPart(text[index]))
                    index++;

                var token = text.Substring(start, index - start);
                segments.Add(token);
                allIdentifiersTypeLike &= IsLikelyCSharpTypeIdentifier(token);
                if (simpleQualifiedCandidate)
                    simpleQualifiedName.Append(token);
                continue;
            }

            switch (current)
            {
                case '.':
                    if (simpleQualifiedCandidate)
                        simpleQualifiedName.Append(current);
                    index++;
                    continue;
                case ':':
                    if (index + 1 < text.Length && text[index + 1] == ':')
                    {
                        hasTypeOnlySyntax = true;
                        if (simpleQualifiedCandidate)
                            simpleQualifiedName.Append("::");
                        index += 2;
                        continue;
                    }

                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                case '<':
                case '[':
                case '?':
                case '(':
                    hasTypeOnlySyntax = true;
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                case '>':
                case ']':
                case ')':
                case ',':
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                default:
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
            }
        }

        return new CSharpCastTypeShape(
            segments,
            simpleQualifiedCandidate && simpleQualifiedName.Length > 0 ? simpleQualifiedName.ToString() : null,
            hasTypeOnlySyntax,
            allIdentifiersTypeLike);
    }

    private static bool HasKnownNonTerminalTypeSegment(IReadOnlyList<string> segments, IReadOnlySet<string> csharpKnownTypeNames)
    {
        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(segments[index])))
                return true;
        }

        return false;
    }

    private static bool IsKnownCSharpCastTypeName(string candidate, string? resolvedCandidate, IReadOnlySet<string> csharpKnownTypeNames)
    {
        return csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(candidate))
            || (!string.IsNullOrWhiteSpace(resolvedCandidate) && csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(resolvedCandidate)));
    }

    private static bool HasCSharpFunctionValueReceiverConflict(
        string candidate,
        int lineNumber,
        int column,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (string.IsNullOrWhiteSpace(candidate) || csharpFunctionValueReceiverNames.Count == 0)
            return false;

        var normalizedCandidate = NormalizeCSharpIdentifier(candidate);
        return HasCSharpFunctionValueReceiverName(csharpFunctionValueReceiverNames, normalizedCandidate, lineNumber, column);
    }

    private static bool HasCSharpFunctionValueReceiverName(
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames,
        string receiverName,
        int lineNumber,
        int column)
    {
        for (var index = 0; index < csharpFunctionValueReceiverNames.Count; index++)
        {
            var record = csharpFunctionValueReceiverNames[index];
            if (IsWithinCSharpScope(record, lineNumber, column)
                && string.Equals(record.Name, receiverName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyCSharpTypeIdentifier(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var normalized = token[0] == '@' ? token.Substring(1) : token;
        if (normalized.Length == 0)
            return false;

        return IsCSharpBuiltInTypeKeyword(normalized)
            || char.IsUpper(normalized[0]);
    }

    private static void SkipCSharpCastTypeWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsCSharpBuiltInTypeKeyword(string text)
    {
        return string.Equals(text, "bool", StringComparison.Ordinal)
            || string.Equals(text, "byte", StringComparison.Ordinal)
            || string.Equals(text, "sbyte", StringComparison.Ordinal)
            || string.Equals(text, "short", StringComparison.Ordinal)
            || string.Equals(text, "ushort", StringComparison.Ordinal)
            || string.Equals(text, "int", StringComparison.Ordinal)
            || string.Equals(text, "uint", StringComparison.Ordinal)
            || string.Equals(text, "long", StringComparison.Ordinal)
            || string.Equals(text, "ulong", StringComparison.Ordinal)
            || string.Equals(text, "nint", StringComparison.Ordinal)
            || string.Equals(text, "nuint", StringComparison.Ordinal)
            || string.Equals(text, "char", StringComparison.Ordinal)
            || string.Equals(text, "float", StringComparison.Ordinal)
            || string.Equals(text, "double", StringComparison.Ordinal)
            || string.Equals(text, "decimal", StringComparison.Ordinal)
            || string.Equals(text, "string", StringComparison.Ordinal)
            || string.Equals(text, "object", StringComparison.Ordinal)
            || string.Equals(text, "dynamic", StringComparison.Ordinal);
    }

    private static bool CanStartCSharpParenthesizedQueryClauseAfterPlusOrMinus(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int operatorLineIndex,
        int operatorColumn,
        int operatorEndColumn,
        char operatorToken)
    {
        if (operatorLineIndex < 0 || operatorColumn < 0)
            return false;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                operatorLineIndex,
                operatorColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out var previousTokenEndColumn,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken)
            || previousPunctuationToken != operatorToken
            || previousTokenLineIndex != operatorLineIndex
            || previousTokenEndColumn != operatorEndColumn - 1)
        {
            return false;
        }

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn - 1,
                out var operandTokenLineIndex,
                out var operandTokenStartColumn,
                out _,
                out var operandIdentifierToken,
                out var operandPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(operandIdentifierToken))
            return true;

        return operandPunctuationToken switch
        {
            ')' or ']' or '}' or '"' or '\'' => true,
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                operandTokenLineIndex,
                operandTokenStartColumn),
            _ => false
        };
    }

    private static bool CanStartCSharpParenthesizedQueryClauseAfterBang(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int bangLineIndex,
        int bangColumn)
    {
        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                bangLineIndex,
                bangColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out _,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return !IsCSharpParenthesizedQueryClausePrefixIdentifier(
                structuralLines[previousTokenLineIndex],
                previousTokenStartColumn,
                previousIdentifierToken);

        return previousPunctuationToken switch
        {
            ')' or ']' or '}' or '"' or '\'' => true,
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            _ => false
        };
    }

    private static bool IsCSharpParenthesizedQueryClausePrefixIdentifier(string line, int tokenStartColumn, string token)
    {
        if (tokenStartColumn > 0 && line[tokenStartColumn - 1] == '@')
            return false;

        return string.Equals(token, "await", StringComparison.Ordinal)
            || string.Equals(token, "throw", StringComparison.Ordinal)
            || IsCSharpQueryClauseKeyword(token);
    }

    private static bool LooksLikeCSharpNullableTypeSuffixInCastOrTypeTest(
        IReadOnlyList<string> structuralLines,
        int questionLineIndex,
        int questionColumn)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var currentLineIndex = questionLineIndex;
        var currentColumn = questionColumn - 1;
        while (TryGetPreviousTopLevelToken(
                   structuralLines,
                   currentLineIndex,
                   currentColumn,
                   out var tokenLineIndex,
                   out var tokenStartColumn,
                   out _,
                   out var identifierToken,
                   out var punctuationToken))
        {
            if (!string.IsNullOrEmpty(identifierToken))
            {
                if (angleDepth == 0
                    && bracketDepth == 0
                    && parenDepth == 0
                    && (string.Equals(identifierToken, "as", StringComparison.Ordinal)
                        || string.Equals(identifierToken, "is", StringComparison.Ordinal)))
                {
                    return true;
                }

                currentLineIndex = tokenLineIndex;
                currentColumn = tokenStartColumn - 1;
                continue;
            }

            switch (punctuationToken)
            {
                case '.':
                case '?':
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ',':
                    if (angleDepth > 0 || bracketDepth > 0 || parenDepth > 0)
                    {
                        currentLineIndex = tokenLineIndex;
                        currentColumn = tokenStartColumn - 1;
                        continue;
                    }

                    return false;
                case '>':
                    angleDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '<':
                    if (angleDepth == 0)
                        return false;

                    angleDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ']':
                    bracketDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '[':
                    if (bracketDepth == 0)
                        return false;

                    bracketDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ')':
                    parenDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '(':
                    if (parenDepth == 0)
                        return false;

                    parenDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

}
