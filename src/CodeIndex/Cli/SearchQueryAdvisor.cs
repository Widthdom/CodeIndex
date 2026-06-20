namespace CodeIndex.Cli;

internal static class SearchQueryAdvisor
{
    internal const string ExactSubstringHintReason = "punctuation_heavy_query";
    internal const string CliExactSubstringSuggestedAction =
        "This looks like a literal code phrase; rerun with `--exact-substring`, for example: `cdidx search --exact-substring --query \"...\"`, for punctuation-sensitive matching.";
    internal const string McpExactSubstringSuggestedAction =
        "This looks like a literal code phrase; rerun the search with exactSubstring=true for punctuation-sensitive matching.";

    internal static SearchQueryHint? BuildExactSubstringHint(string? query, bool rawQuery, bool exact, bool prefix)
        => ShouldSuggestExactSubstring(query, rawQuery, exact, prefix)
            ? SearchQueryHint.ExactSubstring()
            : null;

    internal static bool ShouldSuggestExactSubstring(string? query, bool rawQuery, bool exact, bool prefix)
    {
        if (rawQuery || exact || prefix || string.IsNullOrWhiteSpace(query))
            return false;

        var trimmed = query.Trim();
        var hintProbe = TryUnwrapSingleDoubleQuotedPhrase(trimmed, out var phrase)
            ? phrase
            : trimmed;
        if (!hintProbe.Any(char.IsLetterOrDigit))
            return false;

        var tokens = hintProbe.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1 && IsOptionLookingLiteral(tokens[0]))
            return false;

        var punctuationCount = hintProbe.Count(IsCodePunctuation);
        if (punctuationCount >= 2)
            return true;

        return tokens.Any(IsStandaloneOperatorToken) || LooksLikeMultiTokenCodePhrase(tokens);
    }

    private static bool LooksLikeMultiTokenCodePhrase(string[] tokens)
        => tokens.Length >= 3 && tokens.Any(IsCommonCodeKeyword) && tokens.Any(IsIdentifierLikeToken);

    private static bool IsCommonCodeKeyword(string token)
        => token.ToLowerInvariant() is "await" or "catch" or "class" or "const" or "def" or "else" or "for" or "function"
            or "if" or "import" or "let" or "new" or "public" or "private" or "protected" or "return"
            or "static" or "throw" or "try" or "using" or "var" or "void" or "while";

    private static bool IsIdentifierLikeToken(string token)
        => token.Length > 0
            && (char.IsLetter(token[0]) || token[0] == '_' || token[0] == '@')
            && token.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '@' || ch == '.');

    private static bool TryUnwrapSingleDoubleQuotedPhrase(string query, out string phrase)
    {
        phrase = string.Empty;
        if (query.Length < 2 || query[0] != '"' || query[^1] != '"')
            return false;

        var builder = new System.Text.StringBuilder();
        for (var i = 1; i < query.Length - 1; i++)
        {
            if (query[i] != '"')
            {
                builder.Append(query[i]);
                continue;
            }

            if (i + 1 < query.Length - 1 && query[i + 1] == '"')
            {
                builder.Append('"');
                i++;
                continue;
            }

            return false;
        }

        phrase = builder.ToString();
        return true;
    }

    private static bool IsStandaloneOperatorToken(string token)
        => token.Length > 0
            && token.All(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && ch != '_')
            && token.Any(IsCodePunctuation);

    private static bool IsOptionLookingLiteral(string token)
        => token.StartsWith("-", StringComparison.Ordinal)
            && token.SkipWhile(ch => ch == '-').Any(char.IsLetterOrDigit);

    private static bool IsCodePunctuation(char ch)
    {
        if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '_')
            return false;

        return ch is '.'
            or ':' or ';' or ','
            or '=' or '$' or '@' or '#'
            or '%' or '^' or '&' or '|'
            or '!' or '?' or '+' or '-'
            or '*' or '/' or '\\'
            or '<' or '>'
            or '(' or ')' or '[' or ']'
            or '{' or '}'
            or '"' or '\'' or '`' or '~';
    }
}

public sealed class SearchQueryHint
{
    public string Reason { get; set; } = string.Empty;
    public string SuggestedAction { get; set; } = string.Empty;
    public string Flag { get; set; } = string.Empty;
    public string McpArgument { get; set; } = string.Empty;

    internal static SearchQueryHint ExactSubstring() => new()
    {
        Reason = SearchQueryAdvisor.ExactSubstringHintReason,
        SuggestedAction = SearchQueryAdvisor.CliExactSubstringSuggestedAction,
        Flag = "--exact-substring",
        McpArgument = "exactSubstring",
    };
}
