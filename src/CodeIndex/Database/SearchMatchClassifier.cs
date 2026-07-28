namespace CodeIndex.Database;

internal static class SearchMatchClassifier
{
    public const string Code = "code";
    public const string Comment = "comment";
    public const string StringLiteral = "string_literal";
    public const string RegexLiteral = "regex_literal";
    public const string HelpText = "help_text";
    public const string SchemaDescription = "schema_description";
    public const string Unknown = "unknown";

    public static SearchMatchFacet Classify(
        string path,
        string? lang,
        int line,
        string text,
        int column,
        int length,
        string? enclosingSymbolKind = null,
        IReadOnlyDictionary<int, string>? lineContext = null)
    {
        var origin = ClassifyOrigin(path, lang, line, text, column, enclosingSymbolKind, lineContext);
        var testFile = IsLikelyTestPath(path);
        var testSymbol = string.Equals(enclosingSymbolKind, "test.method", StringComparison.OrdinalIgnoreCase);
        var testFixture = (testFile || testSymbol) && IsStringLikeOrigin(origin);
        return new SearchMatchFacet
        {
            Line = line,
            Column = Math.Max(1, column),
            Length = Math.Max(1, length),
            Origin = origin,
            TestFile = testFile,
            TestSymbol = testSymbol,
            TestFixture = testFixture,
        };
    }

    public static bool IsStringLikeOrigin(string origin)
        => origin is StringLiteral or RegexLiteral or HelpText or SchemaDescription;

    public static bool IsLikelyTestPath(string path)
    {
        var slashPath = path.Replace('\\', '/');
        var normalized = slashPath.ToLowerInvariant();
        var fileName = Path.GetFileName(slashPath);
        if (normalized.StartsWith("tests/", StringComparison.Ordinal) ||
            normalized.StartsWith("test/", StringComparison.Ordinal) ||
            normalized.Contains("/tests/", StringComparison.Ordinal) ||
            normalized.Contains("/test/", StringComparison.Ordinal) ||
            normalized.Contains("/fixtures/", StringComparison.Ordinal) ||
            normalized.Contains("/testdata/", StringComparison.Ordinal))
        {
            return true;
        }

        return IsLikelyTestFileName(fileName);
    }

    private static bool IsLikelyTestFileName(string fileName)
    {
        var lowerFileName = fileName.ToLowerInvariant();
        if (lowerFileName is "conftest.py")
            return true;

        var extension = Path.GetExtension(fileName);
        var baseName = extension.Length == 0 ? fileName : fileName[..^extension.Length];
        var lowerBaseName = baseName.ToLowerInvariant();

        if (lowerBaseName is "test" or "tests" or "conftest")
            return true;
        if (lowerBaseName.StartsWith("test.", StringComparison.Ordinal) ||
            lowerBaseName.StartsWith("tests.", StringComparison.Ordinal) ||
            lowerBaseName.StartsWith("test_", StringComparison.Ordinal) ||
            lowerBaseName.StartsWith("tests_", StringComparison.Ordinal) ||
            lowerBaseName.StartsWith("test-", StringComparison.Ordinal) ||
            lowerBaseName.StartsWith("tests-", StringComparison.Ordinal))
        {
            return true;
        }
        if (lowerBaseName.EndsWith(".test", StringComparison.Ordinal) ||
            lowerBaseName.EndsWith(".tests", StringComparison.Ordinal) ||
            lowerBaseName.EndsWith(".spec", StringComparison.Ordinal) ||
            lowerBaseName.EndsWith("_test", StringComparison.Ordinal) ||
            lowerBaseName.EndsWith("_tests", StringComparison.Ordinal) ||
            lowerBaseName.EndsWith("-test", StringComparison.Ordinal) ||
            lowerBaseName.EndsWith("-tests", StringComparison.Ordinal))
        {
            return true;
        }

        return baseName.EndsWith("Test", StringComparison.Ordinal) ||
               baseName.EndsWith("Tests", StringComparison.Ordinal);
    }

    private static string ClassifyOrigin(
        string path,
        string? lang,
        int line,
        string text,
        int column,
        string? enclosingSymbolKind,
        IReadOnlyDictionary<int, string>? lineContext)
    {
        if (text.Length == 0)
            return Code;

        var index = Math.Clamp(column - 1, 0, Math.Max(0, text.Length - 1));
        var normalizedLang = lang?.ToLowerInvariant();
        if (string.Equals(normalizedLang, "csharp", StringComparison.Ordinal))
            return ClassifyCSharp(path, line, text, index, lineContext);

        if (string.Equals(normalizedLang, "markdown", StringComparison.Ordinal))
        {
            return string.Equals(enclosingSymbolKind, "code", StringComparison.OrdinalIgnoreCase)
                ? Code
                : HelpText;
        }

        if (IsInsideGitHubActionsRunBlock(path, normalizedLang, line, text, lineContext))
            return Code;

        if (LooksLikeWholeLineComment(normalizedLang, text) ||
            IsInsideLineCommentSpan(normalizedLang, text, index) ||
            IsInsideInlineBlockCommentSpan(normalizedLang, text, index))
        {
            return Comment;
        }

        if (IsInsideQuotedSpan(text, index))
            return LooksLikeHelpText(path, text) ? HelpText : StringLiteral;

        if (IsLikelySlashRegexLiteral(normalizedLang, text, index))
            return RegexLiteral;

        return Code;
    }

    private static string ClassifyCSharp(
        string path,
        int line,
        string text,
        int index,
        IReadOnlyDictionary<int, string>? lineContext)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal))
        {
            return Comment;
        }

        var i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                return index >= i ? Comment : Code;

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var spanEnd = end < 0 ? text.Length - 1 : end + 1;
                if (index >= i && index <= spanEnd)
                    return Comment;
                i = spanEnd + 1;
                continue;
            }

            if (StartsCSharpString(text, i, out var contentStart, out var contentEnd))
            {
                if (index >= contentStart && index <= contentEnd)
                {
                    if (LooksLikeRegexString(text))
                        return RegexLiteral;
                    if (LooksLikeSchemaDescription(path, line, text, contentStart, lineContext))
                        return SchemaDescription;
                    return LooksLikeHelpText(path, text) ? HelpText : StringLiteral;
                }

                i = Math.Max(i + 1, contentEnd + 2);
                continue;
            }

            i++;
        }

        return Code;
    }

    private static bool StartsCSharpString(string text, int index, out int contentStart, out int contentEnd)
    {
        contentStart = index;
        contentEnd = index - 1;

        if (index + 2 < text.Length && text[index] == '"' && text[index + 1] == '"' && text[index + 2] == '"')
        {
            var end = text.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
            contentStart = index + 3;
            contentEnd = end < 0 ? text.Length - 1 : end - 1;
            return true;
        }

        var quoteIndex = index;
        while (quoteIndex < text.Length && text[quoteIndex] is '$' or '@')
            quoteIndex++;

        if (quoteIndex >= text.Length)
            return false;

        if (text[quoteIndex] == '\'')
        {
            contentStart = quoteIndex + 1;
            for (var i = contentStart; i < text.Length; i++)
            {
                if (text[i] != '\'' || (i > contentStart && text[i - 1] == '\\'))
                    continue;

                contentEnd = i - 1;
                return true;
            }

            contentEnd = text.Length - 1;
            return true;
        }

        if (text[quoteIndex] != '"')
            return false;

        if (quoteIndex + 2 < text.Length && text[quoteIndex + 1] == '"' && text[quoteIndex + 2] == '"')
        {
            var end = text.IndexOf("\"\"\"", quoteIndex + 3, StringComparison.Ordinal);
            contentStart = quoteIndex + 3;
            contentEnd = end < 0 ? text.Length - 1 : end - 1;
            return true;
        }

        var prefix = text.AsSpan(index, quoteIndex - index);
        var verbatim = prefix.Contains('@');
        contentStart = quoteIndex + 1;
        for (var i = contentStart; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }
            if (!verbatim && i > contentStart && text[i - 1] == '\\')
                continue;

            contentEnd = i - 1;
            return true;
        }

        contentEnd = text.Length - 1;
        return true;
    }

    private static bool LooksLikeRegexString(string text)
        => text.Contains("Regex", StringComparison.Ordinal) ||
           text.Contains("GeneratedRegex", StringComparison.Ordinal);

    private static bool LooksLikeHelpText(string path, string text)
    {
        var fileName = Path.GetFileName(path);
        return fileName is "ConsoleUi.cs" or "ProgramRunner.cs" or "CliFlagSchema.cs" ||
               text.Contains("Usage:", StringComparison.Ordinal) ||
               text.Contains("--", StringComparison.Ordinal);
    }

    private static bool LooksLikeSchemaDescription(
        string path,
        int line,
        string text,
        int contentStart,
        IReadOnlyDictionary<int, string>? lineContext)
    {
        var normalizedPath = path.Replace('\\', '/');
        if (normalizedPath is not "src/CodeIndex/Mcp/McpToolDefinitions.cs"
            and not "src/CodeIndex/Mcp/McpToolCatalog.cs")
        {
            return false;
        }

        const string descriptionProperty = "[\"description\"]";
        var propertyIndex = text.IndexOf(descriptionProperty, StringComparison.Ordinal);
        if (propertyIndex >= 0)
        {
            var equalsIndex = text.IndexOf('=', propertyIndex + descriptionProperty.Length);
            var valueQuoteIndex = equalsIndex < 0 ? -1 : text.IndexOf('"', equalsIndex + 1);
            return valueQuoteIndex >= 0 && contentStart == valueQuoteIndex + 1;
        }

        return IsDescriptionBuilderArgument(line, text, contentStart, lineContext);
    }

    private static bool IsDescriptionBuilderArgument(
        int line,
        string text,
        int contentStart,
        IReadOnlyDictionary<int, string>? lineContext)
    {
        var context = text;
        var targetIndex = contentStart;
        if (lineContext is not null)
        {
            var builder = new System.Text.StringBuilder();
            for (var candidateLine = Math.Max(1, line - 64); candidateLine <= line; candidateLine++)
            {
                var candidateText = candidateLine == line
                    ? text
                    : lineContext.TryGetValue(candidateLine, out var value) ? value : string.Empty;
                if (candidateLine == line)
                    targetIndex = builder.Length + contentStart;
                builder.AppendLine(candidateText);
            }
            context = builder.ToString();
        }

        return IsInvocationArgument(context, targetIndex, "CreateToolDefinition(", expectedArgumentIndex: 1) ||
               IsInvocationArgument(context, targetIndex, "StringOrArraySchema(", expectedArgumentIndex: 0) ||
               IsInvocationArgument(context, targetIndex, "AppendConstraintDescription(", expectedArgumentIndex: 1);
    }

    private static bool IsInvocationArgument(
        string text,
        int targetIndex,
        string invocation,
        int expectedArgumentIndex)
    {
        var callIndex = text.LastIndexOf(invocation, Math.Min(targetIndex, text.Length - 1), StringComparison.Ordinal);
        if (callIndex < 0)
            return false;

        var argumentIndex = 0;
        var parentheses = 0;
        var brackets = 0;
        var braces = 0;
        for (var i = callIndex + invocation.Length; i < targetIndex && i < text.Length; i++)
        {
            if (StartsCSharpString(text, i, out var contentStart, out var contentEnd))
            {
                if (targetIndex >= contentStart && targetIndex <= contentEnd)
                    return argumentIndex == expectedArgumentIndex;
                i = Math.Max(i, contentEnd + 1);
                continue;
            }

            if (text[i] == '\'')
            {
                i = SkipCharacterLiteral(text, i);
                continue;
            }

            switch (text[i])
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    if (parentheses == 0 && brackets == 0 && braces == 0)
                        return false;
                    parentheses = Math.Max(0, parentheses - 1);
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets = Math.Max(0, brackets - 1);
                    break;
                case '{':
                    braces++;
                    break;
                case '}':
                    braces = Math.Max(0, braces - 1);
                    break;
                case ',' when parentheses == 0 && brackets == 0 && braces == 0:
                    argumentIndex++;
                    break;
            }
        }

        return argumentIndex == expectedArgumentIndex;
    }

    private static int SkipCharacterLiteral(string text, int quoteIndex)
    {
        for (var i = quoteIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (text[i] == '\'')
                return i;
        }
        return text.Length - 1;
    }

    private static bool IsInsideGitHubActionsRunBlock(
        string path,
        string? lang,
        int line,
        string text,
        IReadOnlyDictionary<int, string>? lineContext)
    {
        if (lineContext is null || lang != "yaml" || !IsGitHubWorkflowPath(path))
            return false;

        var currentIndent = CountLeadingSpaces(text);
        if (currentIndent == 0)
            return false;

        for (var candidateLine = line - 1; candidateLine >= 1; candidateLine--)
        {
            if (!lineContext.TryGetValue(candidateLine, out var candidateText))
                break;

            if (string.IsNullOrWhiteSpace(candidateText))
                continue;

            var candidateIndent = CountLeadingSpaces(candidateText);
            if (candidateIndent >= currentIndent)
                continue;

            var trimmed = candidateText.TrimStart();
            if (IsYamlRunBlockScalar(trimmed))
                return true;

            if (LooksLikeYamlMappingKey(trimmed))
                return false;
        }

        return false;
    }

    private static bool IsGitHubWorkflowPath(string path)
    {
        var slashPath = path.Replace('\\', '/');
        return slashPath.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
               (slashPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                slashPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsYamlRunBlockScalar(string trimmed)
    {
        if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            return false;

        var value = trimmed["run:".Length..].TrimStart();
        if (value.Length == 0 || value[0] is not ('|' or '>'))
            return false;

        if (value.Length == 1)
            return true;

        return value[1] is '-' or '+' or ' ' or '#';
    }

    private static bool LooksLikeYamlMappingKey(string trimmed)
    {
        var colonIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
            return false;

        for (var i = 0; i < colonIndex; i++)
        {
            var ch = trimmed[i];
            if (!char.IsLetterOrDigit(ch) && ch is not '_' and not '-')
                return false;
        }

        return true;
    }

    private static int CountLeadingSpaces(string text)
    {
        var count = 0;
        while (count < text.Length && text[count] == ' ')
            count++;
        return count;
    }

    private static bool LooksLikeWholeLineComment(string? lang, string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return false;
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/*", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal) ||
            trimmed.StartsWith("<!--", StringComparison.Ordinal))
        {
            return true;
        }

        return (lang is "python" or "shell" or "ruby" or "perl" or "r" or "yaml" or "toml" or "dockerfile") &&
               trimmed.StartsWith("#", StringComparison.Ordinal);
    }

    private static bool IsInsideLineCommentSpan(string? lang, string text, int index)
    {
        foreach (var marker in GetLineCommentMarkers(lang))
        {
            var markerIndex = FindUnquotedMarker(text, marker);
            if (markerIndex >= 0 && index >= markerIndex)
                return true;
        }

        return false;
    }

    private static string[] GetLineCommentMarkers(string? lang) => lang switch
    {
        "javascript" or "typescript" or "java" or "c" or "cpp" or "c++" or "go" or "rust" or "swift" or "kotlin" or "scala" or "php" => ["//"],
        "python" or "shell" or "bash" or "zsh" or "ruby" or "perl" or "r" or "yaml" or "toml" or "dockerfile" => ["#"],
        "sql" => ["--"],
        _ => [],
    };

    private static int FindUnquotedMarker(string text, string marker)
    {
        for (var i = 0; i <= text.Length - marker.Length; i++)
        {
            if (TrySkipQuotedSpan(text, ref i) || TrySkipInlineBlockComment(text, ref i))
                continue;

            if (text.AsSpan(i, marker.Length).SequenceEqual(marker))
                return i;
        }

        return -1;
    }

    private static bool IsInsideInlineBlockCommentSpan(string? lang, string text, int index)
    {
        if (!SupportsInlineBlockComments(lang))
            return false;

        for (var i = 0; i < text.Length; i++)
        {
            if (TrySkipQuotedSpan(text, ref i))
                continue;

            if (i + 1 >= text.Length || text[i] != '/' || text[i + 1] != '*')
                continue;

            var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            var spanEnd = end < 0 ? text.Length - 1 : end + 1;
            if (index >= i && index <= spanEnd)
                return true;

            i = spanEnd;
        }

        return false;
    }

    private static bool SupportsInlineBlockComments(string? lang)
        => lang is "javascript" or "typescript" or "java" or "c" or "cpp" or "c++" or "go" or "rust" or "swift" or "kotlin" or "scala" or "php" or "css" or "scss" or "less" or "sql";

    private static bool TrySkipQuotedSpan(string text, ref int index)
    {
        var quote = text[index];
        if (quote is not ('"' or '\''))
            return false;

        var end = index + 1;
        while (end < text.Length)
        {
            if (text[end] == quote && text[end - 1] != '\\')
                break;
            end++;
        }

        index = end;
        return true;
    }

    private static bool TrySkipInlineBlockComment(string text, ref int index)
    {
        if (index + 1 >= text.Length || text[index] != '/' || text[index + 1] != '*')
            return false;

        var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        index = end < 0 ? text.Length - 1 : end + 1;
        return true;
    }

    private static bool IsInsideQuotedSpan(string text, int index)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var quote = text[i];
            if (quote is not ('"' or '\''))
                continue;

            var end = i + 1;
            while (end < text.Length)
            {
                if (text[end] == quote && text[end - 1] != '\\')
                    break;
                end++;
            }

            if (index > i && index < end)
                return true;
            i = end;
        }

        return false;
    }

    private static bool IsLikelySlashRegexLiteral(string? lang, string text, int index)
    {
        if (lang is not ("javascript" or "typescript"))
            return false;

        var start = text.LastIndexOf('/', Math.Clamp(index, 0, text.Length - 1));
        if (start < 0 || start + 1 >= text.Length || text[start + 1] is '/' or '*')
            return false;

        var end = text.IndexOf('/', start + 1);
        if (end <= start || index <= start || index >= end)
            return false;

        var before = start == 0 ? '\0' : text[start - 1];
        return before is '\0' or '=' or '(' or ':' or ',' or '!' or '?' or '[' or '{';
    }
}
