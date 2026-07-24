using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<SymbolRecord> ExtractRepositoryMetadataSymbols(
        long fileId,
        string language,
        string[] lines)
    {
        var symbols = CreateSymbolListForLines(lines.Length);
        var truncated = false;
        string? containerName = null;
        string? containerKind = null;
        var configRuleIndex = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            var lineNumber = lineIndex + 1;
            if (language is "gitignore" or "dockerignore")
            {
                if (!TryGetIgnorePattern(trimmed, out var pattern, out var isNegated))
                    continue;

                if (!TryAddRepositoryMetadataSymbol(
                    fileId,
                    "rule",
                    pattern,
                    lineNumber,
                    lines,
                    parentName: null,
                    parentKind: null,
                    isNegated ? "include_rule" : "exclude_rule",
                    symbols,
                    ref truncated))
                {
                    break;
                }
                continue;
            }

            if (language == "gitattributes")
            {
                if (!TryGetGitAttributesTokens(trimmed, out var pattern, out var attributes))
                    continue;

                if (!TryAddRepositoryMetadataSymbol(
                    fileId,
                    "rule",
                    pattern,
                    lineNumber,
                    lines,
                    parentName: null,
                    parentKind: null,
                    "attribute_rule",
                    symbols,
                    ref truncated))
                {
                    break;
                }

                foreach (var attribute in attributes)
                {
                    var attributeName = GetGitAttributeName(attribute);
                    if (attributeName.Length == 0)
                        continue;

                    var propertyName = $"{pattern}.{attributeName}";
                    if (!TryAddRepositoryMetadataSymbol(
                        fileId,
                        "property",
                        propertyName,
                        lineNumber,
                        lines,
                        pattern,
                        "rule",
                        "attribute",
                        symbols,
                        ref truncated))
                    {
                        return symbols;
                    }
                }
                continue;
            }

            if (language == "toml"
                && TryGetBracketSection(trimmed, allowDoubleBrackets: true, out var tomlSection, out var isArrayTable))
            {
                containerName = tomlSection;
                containerKind = "namespace";
                if (!TryAddRepositoryMetadataSymbol(
                    fileId,
                    "namespace",
                    tomlSection,
                    lineNumber,
                    lines,
                    parentName: null,
                    parentKind: null,
                    isArrayTable ? "array_table" : "table",
                    symbols,
                    ref truncated))
                {
                    break;
                }
                continue;
            }

            if (language == "editorconfig"
                && TryGetBracketSection(trimmed, allowDoubleBrackets: false, out var editorConfigSection, out _))
            {
                containerName = editorConfigSection;
                containerKind = "namespace";
                if (!TryAddRepositoryMetadataSymbol(
                    fileId,
                    "namespace",
                    editorConfigSection,
                    lineNumber,
                    lines,
                    parentName: null,
                    parentKind: null,
                    "section",
                    symbols,
                    ref truncated))
                {
                    break;
                }
                continue;
            }

            if (language == "config")
            {
                if (trimmed[0] == '#')
                    continue;

                if (TryGetConfigRule(trimmed, out var configRuleName, out var inlineArguments))
                {
                    containerName = $"{configRuleName}[{configRuleIndex++}]";
                    containerKind = "rule";
                    if (!TryAddRepositoryMetadataSymbol(
                        fileId,
                        "rule",
                        containerName,
                        lineNumber,
                        lines,
                        parentName: null,
                        parentKind: null,
                        "config_rule",
                        symbols,
                        ref truncated))
                    {
                        break;
                    }

                    if (inlineArguments != null)
                    {
                        foreach (var argument in EnumerateConfigRuleArguments(inlineArguments))
                        {
                            if (!TryGetMetadataAssignment(
                                argument,
                                allowColon: false,
                                out var argumentKey,
                                out _))
                            {
                                continue;
                            }

                            if (!TryAddRepositoryMetadataSymbol(
                                fileId,
                                "property",
                                $"{containerName}.{argumentKey}",
                                lineNumber,
                                lines,
                                containerName,
                                containerKind,
                                "config_key",
                                symbols,
                                ref truncated))
                            {
                                return symbols;
                            }
                        }

                        containerName = null;
                        containerKind = null;
                    }
                    continue;
                }

                if (trimmed[0] == ')')
                {
                    containerName = null;
                    containerKind = null;
                    continue;
                }
            }

            var allowColon = language == "editorconfig";
            if (!TryGetMetadataAssignment(line, allowColon, out var key, out _))
                continue;

            var qualifiedName = containerName == null ? key : $"{containerName}.{key}";
            if (!TryAddRepositoryMetadataSymbol(
                fileId,
                "property",
                qualifiedName,
                lineNumber,
                lines,
                containerName,
                containerKind,
                language == "config" ? "config_key" : language == "toml" ? "toml_key" : "editorconfig_key",
                symbols,
                ref truncated))
            {
                break;
            }
        }

        return symbols;
    }

    private static bool TryAddRepositoryMetadataSymbol(
        long fileId,
        string kind,
        string name,
        int line,
        string[] lines,
        string? parentName,
        string? parentKind,
        string subKind,
        List<SymbolRecord> symbols,
        ref bool truncated)
    {
        if (name.Length == 0 || name.Length > StructuredDataMaxPathLength)
            return true;
        if (symbols.Count >= StructuredDataMaxSymbols)
        {
            truncated = true;
            return false;
        }

        if (!TryAddStructuredDataSymbol(
            fileId,
            kind,
            name,
            line,
            lines,
            parentName,
            symbols,
            "repository_metadata_symbol_budget_exceeded",
            ref truncated))
        {
            return false;
        }

        var symbol = symbols[^1];
        symbol.SubKind = subKind;
        if (parentName != null)
            symbol.ContainerKind = parentKind ?? "namespace";
        return true;
    }

    private static bool TryGetIgnorePattern(
        ReadOnlySpan<char> trimmed,
        out string pattern,
        out bool isNegated)
    {
        pattern = string.Empty;
        isNegated = false;
        if (trimmed.IsEmpty || trimmed[0] == '#')
            return false;

        if (trimmed.StartsWith(@"\#"))
            trimmed = trimmed[1..];
        else if (trimmed.StartsWith(@"\!"))
            trimmed = trimmed[1..];
        else if (trimmed[0] == '!')
        {
            isNegated = true;
            trimmed = trimmed[1..].TrimStart();
        }

        trimmed = trimmed.TrimEnd();
        if (trimmed.IsEmpty)
            return false;

        pattern = trimmed.ToString();
        return true;
    }

    internal static bool TryGetGitAttributesTokens(
        ReadOnlySpan<char> trimmed,
        out string pattern,
        out string[] attributes)
    {
        pattern = string.Empty;
        attributes = [];
        if (trimmed.IsEmpty || trimmed[0] == '#')
            return false;

        var patternEnd = 0;
        if (trimmed[0] == '"')
        {
            var builder = new System.Text.StringBuilder(trimmed.Length);
            for (var index = 1; index < trimmed.Length; index++)
            {
                var character = trimmed[index];
                if (character == '\\')
                {
                    if (++index >= trimmed.Length
                        || !TryAppendGitAttributesEscape(trimmed, ref index, builder))
                    {
                        return false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    patternEnd = index + 1;
                    break;
                }

                builder.Append(character);
            }

            if (patternEnd == 0
                || builder.Length == 0
                || builder.ToString().Any(char.IsControl))
            {
                return false;
            }
            pattern = builder.ToString();
        }
        else
        {
            while (patternEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[patternEnd]))
                patternEnd++;
            if (patternEnd == 0)
                return false;
            pattern = trimmed[..patternEnd].ToString();
        }

        var tokens = trimmed[patternEnd..].ToString().Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        attributes = tokens;
        return true;
    }

    private static bool TryAppendGitAttributesEscape(
        ReadOnlySpan<char> value,
        ref int index,
        System.Text.StringBuilder builder)
    {
        var escaped = value[index];
        switch (escaped)
        {
            case '\\':
            case '"':
                builder.Append(escaped);
                return true;
            case 'a':
                builder.Append('\a');
                return true;
            case 'b':
                builder.Append('\b');
                return true;
            case 't':
                builder.Append('\t');
                return true;
            case 'n':
                builder.Append('\n');
                return true;
            case 'v':
                builder.Append('\v');
                return true;
            case 'f':
                builder.Append('\f');
                return true;
            case 'r':
                builder.Append('\r');
                return true;
        }

        if (escaped is < '0' or > '7')
            return false;

        var octalValue = escaped - '0';
        var digitCount = 1;
        while (digitCount < 3
               && index + 1 < value.Length
               && value[index + 1] is >= '0' and <= '7')
        {
            octalValue = octalValue * 8 + value[++index] - '0';
            digitCount++;
        }

        if (octalValue > byte.MaxValue)
            return false;

        builder.Append((char)octalValue);
        return true;
    }

    private static string GetGitAttributeName(string attribute)
    {
        var span = attribute.AsSpan().Trim();
        if (!span.IsEmpty && span[0] is '-' or '!')
            span = span[1..];

        var equalsIndex = span.IndexOf('=');
        if (equalsIndex >= 0)
            span = span[..equalsIndex];
        return span.Trim().ToString();
    }

    internal static bool TryGetBracketSection(
        ReadOnlySpan<char> trimmed,
        bool allowDoubleBrackets,
        out string section,
        out bool isDoubleBracket)
    {
        section = string.Empty;
        isDoubleBracket = false;
        if (trimmed.Length < 3 || trimmed[0] != '[')
            return false;

        isDoubleBracket = allowDoubleBrackets && trimmed.Length >= 5 && trimmed[1] == '[';
        var closing = isDoubleBracket ? "]]".AsSpan() : "]".AsSpan();
        var start = isDoubleBracket ? 2 : 1;
        var searchOffset = start;
        while (searchOffset < trimmed.Length)
        {
            var relativeClosingOffset = trimmed[searchOffset..].IndexOf(closing);
            if (relativeClosingOffset < 0)
                return false;

            var closingOffset = searchOffset + relativeClosingOffset;
            var remainder = trimmed[(closingOffset + closing.Length)..].Trim();
            if (remainder.IsEmpty || remainder[0] is '#' or ';')
            {
                var value = trimmed.Slice(start, closingOffset - start).Trim();
                if (value.IsEmpty)
                    return false;

                section = value.ToString();
                return true;
            }

            searchOffset = closingOffset + closing.Length;
        }

        return false;
    }

    private static bool TryGetConfigRule(
        ReadOnlySpan<char> trimmed,
        out string ruleName,
        out string? inlineArguments)
    {
        ruleName = string.Empty;
        inlineArguments = null;
        var openParen = trimmed.IndexOf('(');
        if (openParen <= 0)
            return false;

        var name = trimmed[..openParen].Trim();
        if (name.IsEmpty || !IsMetadataIdentifierStart(name[0]))
            return false;

        for (var index = 1; index < name.Length; index++)
        {
            if (!IsMetadataIdentifierPart(name[index]))
                return false;
        }

        var arguments = trimmed[(openParen + 1)..].Trim();
        if (!arguments.IsEmpty)
        {
            var quote = '\0';
            var escaped = false;
            var nestedParentheses = 0;
            var closingIndex = -1;
            for (var index = 0; index < arguments.Length; index++)
            {
                var character = arguments[index];
                if (quote != '\0')
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (character == '\\' && quote == '"')
                        escaped = true;
                    else if (character == quote)
                        quote = '\0';
                    continue;
                }

                if (character is '"' or '\'')
                {
                    quote = character;
                    continue;
                }

                if (character == '(')
                    nestedParentheses++;
                else if (character == ')' && nestedParentheses > 0)
                    nestedParentheses--;
                else if (character == ')')
                {
                    closingIndex = index;
                    break;
                }
            }

            if (closingIndex < 0)
                return false;

            var remainder = arguments[(closingIndex + 1)..].Trim();
            if (!remainder.IsEmpty && remainder[0] != '#')
                return false;

            inlineArguments = arguments[..closingIndex].Trim().ToString();
        }

        ruleName = name.ToString();
        return true;
    }

    private static IEnumerable<string> EnumerateConfigRuleArguments(string arguments)
    {
        var start = 0;
        var quote = '\0';
        var escaped = false;
        var collectionDepth = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                    escaped = true;
                else if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character is '[' or '{' or '(')
                collectionDepth++;
            else if (character is (']' or '}' or ')') && collectionDepth > 0)
                collectionDepth--;
            else if (character == ',' && collectionDepth == 0)
            {
                yield return arguments[start..index].Trim();
                start = index + 1;
            }
        }

        if (start < arguments.Length)
            yield return arguments[start..].Trim();
    }

    internal static bool TryGetMetadataAssignment(
        string line,
        bool allowColon,
        out string key,
        out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var span = line.AsSpan();
        var quote = '\0';
        var escaped = false;
        var separatorIndex = -1;

        for (var index = 0; index < span.Length; index++)
        {
            var character = span[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                {
                    escaped = true;
                    continue;
                }

                if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == '=' || allowColon && character == ':')
            {
                separatorIndex = index;
                break;
            }
        }

        if (separatorIndex <= 0)
            return false;

        var keySpan = span[..separatorIndex].Trim();
        if (keySpan.IsEmpty || keySpan[0] is '#' or ';')
            return false;

        if (keySpan.Length >= 2
            && keySpan[0] == keySpan[^1]
            && keySpan[0] is '"' or '\'')
        {
            keySpan = keySpan[1..^1].Trim();
        }

        if (keySpan.IsEmpty || keySpan.Length > StructuredDataMaxPathLength)
            return false;

        key = keySpan.ToString();
        value = span[(separatorIndex + 1)..].Trim().ToString();
        return true;
    }

    private static bool IsMetadataIdentifierStart(char character) =>
        character == '_' || char.IsLetter(character);

    private static bool IsMetadataIdentifierPart(char character) =>
        character is '_' or '-' || char.IsLetterOrDigit(character);
}
