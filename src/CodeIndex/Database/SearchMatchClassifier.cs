namespace CodeIndex.Database;

internal static class SearchMatchClassifier
{
    public const string Code = "code";
    public const string Comment = "comment";
    public const string StringLiteral = "string_literal";
    public const string RegexLiteral = "regex_literal";
    public const string HelpText = "help_text";
    public const string Unknown = "unknown";

    public static SearchMatchFacet Classify(
        string path,
        string? lang,
        int line,
        string text,
        int column,
        int length,
        string? enclosingSymbolKind = null)
    {
        var origin = ClassifyOrigin(path, lang, text, column);
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
        => origin is StringLiteral or RegexLiteral or HelpText;

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

    private static string ClassifyOrigin(string path, string? lang, string text, int column)
    {
        if (text.Length == 0)
            return Code;

        var index = Math.Clamp(column - 1, 0, Math.Max(0, text.Length - 1));
        var normalizedLang = lang?.ToLowerInvariant();
        if (string.Equals(normalizedLang, "csharp", StringComparison.Ordinal))
            return ClassifyCSharp(path, text, index);

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

    private static string ClassifyCSharp(string path, string text, int index)
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
