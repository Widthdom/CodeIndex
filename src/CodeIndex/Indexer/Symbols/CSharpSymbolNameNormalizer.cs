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

    private static bool TryReadConversionOperatorName(Match match, string matchLine, out string name)
    {
        name = string.Empty;

        var conversionKind = match.Groups["conversionKind"].Value.Trim();
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
        var builder = new StringBuilder();
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

        if (!string.Equals(line.Substring(startIndex, keyword.Length), keyword, StringComparison.Ordinal))
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
