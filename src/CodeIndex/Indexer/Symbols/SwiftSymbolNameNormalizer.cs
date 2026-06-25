using System.Text;

namespace CodeIndex.Indexer;

internal static class SwiftSymbolNameNormalizer
{
    internal static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var trimmed = StripImportKindPrefix(name.Trim());
        if (trimmed.IndexOf('<') < 0
            && trimmed.IndexOf(':') < 0
            && trimmed.IndexOf("where", StringComparison.Ordinal) < 0
            && trimmed.IndexOf('/') < 0)
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var inBackticks = false;

        for (var index = 0; index < trimmed.Length; index++)
        {
            var ch = trimmed[index];

            if (inBackticks)
            {
                builder.Append(ch);
                if (ch == '`')
                    inBackticks = false;
                continue;
            }

            if (ch == '`')
            {
                builder.Append(ch);
                inBackticks = true;
                continue;
            }

            if (angleDepth == 0 && parenDepth == 0 && bracketDepth == 0)
            {
                if (ch == ':' || ch == '{')
                    break;

                if (ch == '/' && index + 1 < trimmed.Length)
                {
                    var next = trimmed[index + 1];
                    if (next == '/' || next == '*')
                        break;
                }

                if (char.IsWhiteSpace(ch))
                {
                    var nextIndex = index + 1;
                    while (nextIndex < trimmed.Length && char.IsWhiteSpace(trimmed[nextIndex]))
                        nextIndex++;

                    if (nextIndex >= trimmed.Length)
                        break;

                    if (trimmed[nextIndex] == ':'
                        || trimmed[nextIndex] == '{'
                        || (trimmed[nextIndex] == '/' && nextIndex + 1 < trimmed.Length && trimmed[nextIndex + 1] is '/' or '*')
                        || StartsWithWord(trimmed, nextIndex, "where"))
                    {
                        break;
                    }

                    continue;
                }

                if (StartsWithWord(trimmed, index, "where"))
                    break;
            }

            switch (ch)
            {
                case '<':
                    angleDepth++;
                    builder.Append(ch);
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    builder.Append(ch);
                    break;
                case '(':
                    parenDepth++;
                    builder.Append(ch);
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    builder.Append(ch);
                    break;
                case '[':
                    bracketDepth++;
                    builder.Append(ch);
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    builder.Append(ch);
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static string StripImportKindPrefix(string name)
    {
        foreach (var prefix in ImportKindPrefixes)
        {
            if (!StartsWithWord(name, 0, prefix))
                continue;

            var index = prefix.Length;
            while (index < name.Length && char.IsWhiteSpace(name[index]))
                index++;

            return index >= name.Length
                ? string.Empty
                : name[index..].TrimStart();
        }

        return name;
    }

    private static readonly string[] ImportKindPrefixes =
    [
        "class",
        "enum",
        "func",
        "let",
        "operator",
        "protocol",
        "struct",
        "typealias",
        "var",
    ];

    private static bool StartsWithWord(string text, int index, string word)
    {
        if (index < 0 || index + word.Length > text.Length)
            return false;

        if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0)
            return false;

        var beforeOk = index == 0 || !IsWordChar(text[index - 1]);
        var afterIndex = index + word.Length;
        var afterOk = afterIndex >= text.Length || !IsWordChar(text[afterIndex]);
        return beforeOk && afterOk;
    }

    private static bool IsWordChar(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);
}
