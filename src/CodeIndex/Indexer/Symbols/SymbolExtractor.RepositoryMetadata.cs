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

                if (TryGetConfigRuleName(trimmed, out var configRuleName))
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

    private static bool TryGetGitAttributesTokens(
        ReadOnlySpan<char> trimmed,
        out string pattern,
        out string[] attributes)
    {
        pattern = string.Empty;
        attributes = [];
        if (trimmed.IsEmpty || trimmed[0] == '#')
            return false;

        var tokens = trimmed.ToString().Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
            return false;

        pattern = tokens[0];
        attributes = tokens[1..];
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

    private static bool TryGetBracketSection(
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
        var closingOffset = trimmed[start..].IndexOf(closing);
        if (closingOffset < 0)
            return false;

        var value = trimmed.Slice(start, closingOffset).Trim();
        if (value.IsEmpty)
            return false;

        var remainder = trimmed[(start + closingOffset + closing.Length)..].Trim();
        if (!remainder.IsEmpty && remainder[0] is not ('#' or ';'))
            return false;

        section = value.ToString();
        return true;
    }

    private static bool TryGetConfigRuleName(ReadOnlySpan<char> trimmed, out string ruleName)
    {
        ruleName = string.Empty;
        var openParen = trimmed.IndexOf('(');
        if (openParen <= 0 || !trimmed[(openParen + 1)..].Trim().IsEmpty)
            return false;

        var name = trimmed[..openParen].Trim();
        if (name.IsEmpty || !IsMetadataIdentifierStart(name[0]))
            return false;

        for (var index = 1; index < name.Length; index++)
        {
            if (!IsMetadataIdentifierPart(name[index]))
                return false;
        }

        ruleName = name.ToString();
        return true;
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
