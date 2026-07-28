using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Database;

namespace CodeIndex.Indexer;

internal static class CSharpSymbolNameNormalizer
{
    private static readonly Regex TypeWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TypeDoubleColonWhitespaceRegex = new(@"\s*::\s*", RegexOptions.Compiled);
    private static readonly Regex TypeDotWhitespaceRegex = new(@"\s*\.\s*", RegexOptions.Compiled);

    public static string Normalize(string name, Match match, string matchLine)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        if (match.Groups["conversionKind"].Success
            && TryReadConversionOperatorName(match, matchLine, out var conversionOperatorName))
        {
            return conversionOperatorName;
        }

        if (name == "this" && match.Value.Contains("this", StringComparison.Ordinal) && match.Value.Contains('[', StringComparison.Ordinal))
            return "Item";

        // Source-only identifier escapes are not part of the persisted symbol contract.
        // Keep exact-name lookup on the canonical spelling used by import and base-type resolution.
        return NormalizeVerbatimIdentifiers(name);
    }

    /// <summary>
    /// Build the persisted folded identity for an explicit-interface implementation. The
    /// user-facing symbol name remains the short member name, while this key preserves the
    /// normalized interface qualifier and method generic arity. Member kind and normalized
    /// signature remain separate columns in the canonical symbol row.
    ///
    /// 明示的インターフェース実装の永続 folded identity を構築する。ユーザー向け表示名は
    /// 短いメンバー名のままにし、この key には正規化した interface qualifier と method の
    /// generic arity を保持する。member kind と正規化済み signature は canonical symbol row
    /// の別列として保持される。
    /// </summary>
    internal static string? BuildExplicitInterfaceIdentityNameFolded(string name, Match match)
    {
        var qualifierGroup = match.Groups["explicitInterface"];
        if (!qualifierGroup.Success || string.IsNullOrWhiteSpace(qualifierGroup.Value))
            return null;

        var qualifier = NormalizeTypeDisplayName(qualifierGroup.Value);
        var typeParameters = match.Groups["explicitTypeParameters"];
        var arity = typeParameters.Success
            && TryCountTopLevelTypeArguments(typeParameters.Value, out var parsedArity)
                ? parsedArity
                : 0;

        return BuildExplicitInterfaceIdentityNameFolded(qualifier, name, arity);
    }

    /// <summary>
    /// Reconstruct an explicit-interface identity from persisted display name + signature.
    /// Fold validation and maintenance use this so a rewrite never replaces the qualified
    /// language identity with the short discovery alias.
    ///
    /// 永続化済みの表示名と signature から明示的インターフェース identity を再構築する。
    /// fold 検証・maintenance が、修飾済み language identity を短い discovery alias で
    /// 上書きしないために使用する。
    /// </summary>
    internal static string? BuildExplicitInterfaceIdentityNameFolded(string name, string? signature)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(signature))
            return null;

        var sourceName = string.Equals(name, "Item", StringComparison.Ordinal) ? "this" : name;
        var memberMarker = "." + sourceName;
        var declarationBodyStart = FindDeclarationBodyStart(signature);
        var searchStart = 0;
        while (searchStart < signature.Length)
        {
            var memberIndex = signature.IndexOf(
                memberMarker,
                searchStart,
                StringComparison.Ordinal);
            if (memberIndex <= 0)
                return null;
            if (memberIndex >= declarationBodyStart)
                return null;

            var cursor = memberIndex + memberMarker.Length;
            while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
                cursor++;
            if (TryReadExplicitInterfaceMemberArity(signature, cursor, out var arity))
            {
                var qualifierEnd = memberIndex;
                var qualifierStart = FindExplicitInterfaceQualifierStart(signature, qualifierEnd);
                if (qualifierStart < qualifierEnd)
                {
                    var qualifier = NormalizeTypeDisplayName(signature[qualifierStart..qualifierEnd]);
                    return BuildExplicitInterfaceIdentityNameFolded(qualifier, name, arity);
                }
            }

            searchStart = memberIndex + memberMarker.Length;
        }

        return null;
    }

    private static int FindDeclarationBodyStart(string signature)
    {
        var expressionBodyStart = signature.IndexOf("=>", StringComparison.Ordinal);
        var blockBodyStart = signature.IndexOf('{');
        var initializerStart = signature.IndexOf('=');
        var bodyStart = signature.Length;
        if (expressionBodyStart >= 0)
            bodyStart = Math.Min(bodyStart, expressionBodyStart);
        if (blockBodyStart >= 0)
            bodyStart = Math.Min(bodyStart, blockBodyStart);
        if (initializerStart >= 0)
            bodyStart = Math.Min(bodyStart, initializerStart);

        return bodyStart;
    }

    private static bool TryReadExplicitInterfaceMemberArity(
        string signature,
        int cursor,
        out int arity)
    {
        arity = 0;
        if (cursor >= signature.Length)
            return false;

        if (signature[cursor] != '<')
        {
            // Reject a matching qualified return/parameter type such as `Models.Run Run()`.
            // A non-generic explicit member name is followed immediately by its
            // parameter/indexer list, accessor body, expression body, or terminator.
            return signature[cursor] is '(' or '[' or '{' or '=' or ';';
        }

        var typeParameterEnd = FindBalancedTypeArgumentListEnd(signature, cursor);
        if (typeParameterEnd <= cursor
            || !TryCountTopLevelTypeArguments(
                signature[cursor..(typeParameterEnd + 1)],
                out arity))
        {
            arity = 0;
            return false;
        }

        cursor = typeParameterEnd + 1;
        while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
            cursor++;

        // A generic explicit-interface member is a method. Requiring its parameter list
        // prevents a same-named qualified generic return/parameter type from being mistaken
        // for the member declaration.
        if (cursor < signature.Length && signature[cursor] == '(')
            return true;

        arity = 0;
        return false;
    }

    /// <summary>
    /// Normalize the exact-query spelling documented for C# explicit-interface members into
    /// the same folded identity used by extraction. A terminal generic argument list is reduced
    /// to arity so `IFoo.Run&lt;T&gt;` and an implementation declared as `IFoo.Run&lt;TValue&gt;`
    /// share the language identity without collapsing into an unqualified `Run`.
    ///
    /// C# 明示的インターフェースメンバー向けに文書化した完全一致 query 表記を、抽出時と
    /// 同じ folded identity へ正規化する。末尾の generic 引数リストは arity に変換し、
    /// `IFoo.Run&lt;T&gt;` と `IFoo.Run&lt;TValue&gt;` を同一 identity としつつ、非修飾の
    /// `Run` へは統合しない。
    /// </summary>
    internal static string NormalizeExplicitInterfaceQueryIdentityNameFolded(string query)
    {
        var normalized = NormalizeTypeDisplayName(query);
        var lastDot = FindLastTopLevelDot(normalized);
        if (lastDot < 0)
            return NameFold.Fold(normalized) ?? normalized;

        var leafStart = lastDot + 1;
        var genericStart = normalized.IndexOf('<', leafStart);
        if (genericStart >= 0
            && normalized.EndsWith('>')
            && TryCountTopLevelTypeArguments(normalized[genericStart..], out var arity))
        {
            normalized = normalized[..genericStart] + $"`{arity}";
        }

        if (string.Equals(normalized[(lastDot + 1)..], "this", StringComparison.Ordinal))
            normalized = normalized[..(lastDot + 1)] + "Item";

        return NameFold.Fold(normalized) ?? normalized;
    }

    private static bool TryReadConversionOperatorName(Match match, string matchLine, out string name)
    {
        name = string.Empty;

        var conversionKind = match.Groups["conversionKind"].ValueSpan.Trim().ToString();
        if (conversionKind.Length == 0)
            return false;

        var cursor = match.Index + match.Length;
        while (cursor < matchLine.Length && char.IsWhiteSpace(matchLine[cursor]))
            cursor++;

        var hasChecked = false;
        if (StartsWithKeyword(matchLine, cursor, "checked"))
        {
            hasChecked = true;
            cursor += "checked".Length;
            while (cursor < matchLine.Length && char.IsWhiteSpace(matchLine[cursor]))
                cursor++;
        }

        if (!TryReadTypeUntilParameterList(matchLine, cursor, out var targetType))
            return false;

        var normalizedTargetType = NormalizeTypeDisplayName(targetType);
        name = hasChecked
            ? $"{conversionKind} operator checked {normalizedTargetType}"
            : $"{conversionKind} operator {normalizedTargetType}";
        return true;
    }

    private static bool TryReadTypeUntilParameterList(string line, int startIndex, out string typeName)
    {
        typeName = string.Empty;
        var builder = new StringBuilder(Math.Max(0, line.Length - startIndex));
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var sawAnyTypeToken = false;

        for (var index = startIndex; index < line.Length; index++)
        {
            var ch = line[index];
            switch (ch)
            {
                case '(':
                    if (angleDepth == 0 && bracketDepth == 0 && parenDepth == 0 && sawAnyTypeToken)
                    {
                        typeName = builder.ToString().Trim();
                        return typeName.Length > 0;
                    }

                    parenDepth++;
                    builder.Append(ch);
                    if (!char.IsWhiteSpace(ch))
                        sawAnyTypeToken = true;
                    break;

                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    builder.Append(ch);
                    if (!char.IsWhiteSpace(ch))
                        sawAnyTypeToken = true;
                    break;

                case '<':
                    angleDepth++;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                case '[':
                    bracketDepth++;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    builder.Append(ch);
                    sawAnyTypeToken = true;
                    break;

                default:
                    builder.Append(ch);
                    if (!char.IsWhiteSpace(ch))
                        sawAnyTypeToken = true;
                    break;
            }
        }

        return false;
    }

    private static bool StartsWithKeyword(string line, int startIndex, string keyword)
    {
        if (startIndex < 0 || startIndex + keyword.Length > line.Length)
            return false;

        if (string.CompareOrdinal(line, startIndex, keyword, 0, keyword.Length) != 0)
            return false;

        var nextIndex = startIndex + keyword.Length;
        return nextIndex >= line.Length || char.IsWhiteSpace(line[nextIndex]);
    }

    private static string NormalizeTypeDisplayName(string typeName)
    {
        var normalized = TypeWhitespaceRegex.Replace(typeName.Trim(), " ");
        normalized = TypeDoubleColonWhitespaceRegex.Replace(normalized, "::");
        normalized = TypeDotWhitespaceRegex.Replace(normalized, ".");
        normalized = NormalizeTypeTokenSpacing(normalized);
        return NormalizeVerbatimIdentifiers(normalized);
    }

    private static int FindLastTopLevelDot(string value)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        var lastDot = -1;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '.':
                    if (angleDepth == 0 && bracketDepth == 0)
                        lastDot = index;
                    break;
            }
        }

        return lastDot;
    }

    private static string BuildExplicitInterfaceIdentityNameFolded(
        string qualifier,
        string name,
        int arity)
    {
        var identityName = $"{qualifier}.{name}";
        if (arity > 0)
            identityName += $"`{arity}";

        return NameFold.Fold(identityName) ?? identityName;
    }

    private static int FindExplicitInterfaceQualifierStart(string signature, int qualifierEnd)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        for (var index = qualifierEnd - 1; index >= 0; index--)
        {
            switch (signature[index])
            {
                case '>':
                    angleDepth++;
                    break;
                case '<':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                default:
                    if (char.IsWhiteSpace(signature[index])
                        && angleDepth == 0
                        && bracketDepth == 0)
                    {
                        return index + 1;
                    }
                    break;
            }
        }

        return 0;
    }

    private static int FindBalancedTypeArgumentListEnd(string value, int startIndex)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        for (var index = startIndex; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth--;
                    if (angleDepth == 0 && bracketDepth == 0 && parenDepth == 0)
                        return index;
                    if (angleDepth < 0)
                        return -1;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
            }
        }

        return -1;
    }

    private static bool TryCountTopLevelTypeArguments(string value, out int arity)
    {
        arity = 0;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 3 || trimmed[0] != '<' || trimmed[^1] != '>')
            return false;

        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var sawToken = false;
        arity = 1;
        foreach (var ch in trimmed)
        {
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth--;
                    if (angleDepth < 0)
                        return false;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ',':
                    if (angleDepth == 1 && bracketDepth == 0 && parenDepth == 0)
                        arity++;
                    break;
                default:
                    if (!char.IsWhiteSpace(ch) && ch is not '<' and not '>')
                        sawToken = true;
                    break;
            }
        }

        return angleDepth == 0 && sawToken;
    }

    private static string NormalizeVerbatimIdentifiers(string value)
    {
        if (string.IsNullOrEmpty(value)
            || (value.IndexOf('@', StringComparison.Ordinal) < 0
                && value.IndexOf("global::", StringComparison.Ordinal) < 0
                && value.IndexOf('\\', StringComparison.Ordinal) < 0))
        {
            return value;
        }

        return ExactSourceSearchNormalizer.Normalize(value, "csharp");
    }

    internal static bool IsVerbatimIdentifierPrefix(string value, int index)
    {
        if (value[index] != '@' || index + 1 >= value.Length || !IsIdentifierStart(value[index + 1]))
            return false;

        return index == 0 || !IsIdentifierChar(value[index - 1]);
    }

    internal static bool IsVerbatimIdentifierPrefix(ReadOnlySpan<char> value, int index)
    {
        if (value[index] != '@' || index + 1 >= value.Length || !IsIdentifierStart(value[index + 1]))
            return false;

        return index == 0 || !IsIdentifierChar(value[index - 1]);
    }

    internal static bool IsIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierChar(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static string NormalizeTypeTokenSpacing(string typeName)
    {
        var builder = new StringBuilder(typeName.Length);

        for (var index = 0; index < typeName.Length; index++)
        {
            var ch = typeName[index];
            switch (ch)
            {
                case ' ':
                    var previous = GetLastNonWhitespace(builder);
                    var next = FindNextNonWhitespace(typeName, index + 1);
                    if (!previous.HasValue || !next.HasValue)
                        continue;

                    if (ShouldInsertTypeSpace(previous.Value, next.Value) && (builder.Length == 0 || builder[^1] != ' '))
                        builder.Append(' ');
                    break;

                case ',':
                    TrimTrailingWhitespace(builder);
                    builder.Append(',');
                    var nextAfterComma = FindNextNonWhitespace(typeName, index + 1);
                    if (nextAfterComma.HasValue && nextAfterComma.Value is not ')' and not '>' and not ']')
                        builder.Append(' ');
                    break;

                case '<':
                case '>':
                case '[':
                case ']':
                case '(':
                case ')':
                case '?':
                    TrimTrailingWhitespace(builder);
                    builder.Append(ch);
                    break;

                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static char? GetLastNonWhitespace(StringBuilder builder)
    {
        for (var index = builder.Length - 1; index >= 0; index--)
        {
            if (!char.IsWhiteSpace(builder[index]))
                return builder[index];
        }

        return null;
    }

    private static char? FindNextNonWhitespace(string text, int startIndex)
    {
        for (var index = startIndex; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
                return text[index];
        }

        return null;
    }

    private static void TrimTrailingWhitespace(StringBuilder builder)
    {
        while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
            builder.Length--;
    }

    private static bool ShouldInsertTypeSpace(char previous, char next)
    {
        if (IsTypeIdentifierChar(previous) && IsTypeIdentifierStart(next))
            return true;

        return previous is '>' or ']' or ')' or '?' or '*'
            && IsTypeIdentifierStart(next);
    }

    private static bool IsTypeIdentifierStart(char ch)
    {
        return ch == '@' || ch == '_' || char.IsLetter(ch);
    }

    private static bool IsTypeIdentifierChar(char ch)
    {
        return IsTypeIdentifierStart(ch) || char.IsDigit(ch);
    }
}
