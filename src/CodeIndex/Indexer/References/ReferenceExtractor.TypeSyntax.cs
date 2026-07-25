using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static int SkipJavaAnnotation(string text, int start) => SkipJavaAnnotation(text.AsSpan(), start);

    internal static int SkipJavaAnnotation(ReadOnlySpan<char> text, int start)
    {
        int i = start + 1;
        var annotationStart = i;
        while (i < text.Length && IsJavaIdentifierPart(text[i]))
            i++;
        if (i < text.Length && text[i] == ':')
        {
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
        }
        else
        {
            i = annotationStart;
        }

        if (i < text.Length && text[i] == '`')
        {
            var closeOffset = text[(i + 1)..].IndexOf('`');
            if (closeOffset < 0)
                return start;
            var closeIndex = i + 1 + closeOffset;
            i = closeIndex + 1;
        }
        else
        {
            while (i < text.Length && (IsJavaIdentifierPart(text[i]) || text[i] == '.'))
                i++;
        }

        if (i < text.Length && text[i] == '(')
        {
            int close = FindMatchingChar(text, i, '(', ')');
            if (close >= 0)
                return close;
        }

        return i - 1;
    }

    internal static int FindMatchingChar(string text, int openIndex, char open, char close) => FindMatchingChar(text.AsSpan(), openIndex, open, close);

    internal static int FindMatchingChar(ReadOnlySpan<char> text, int openIndex, char open, char close)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
                depth++;
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int FindFirstTopLevelChar(string text, char target) => FindFirstTopLevelChar(text.AsSpan(), target);

    private static int FindFirstTopLevelChar(ReadOnlySpan<char> text, char target)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == target && angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0)
                return i;

            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }
        }

        return -1;
    }

    private static int FindTopLevelAssignmentIndex(string text)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case '=' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    if (i + 1 >= text.Length || text[i + 1] != '>')
                        return i;
                    break;
            }
        }

        return -1;
    }

    internal static List<(int Start, int Length)> GetTopLevelTokenSpans(string text)
    {
        var tokens = new List<(int Start, int Length)>();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int tokenStart = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }

            bool topLevelWhitespace = char.IsWhiteSpace(c) && angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0;
            if (topLevelWhitespace)
            {
                if (tokenStart >= 0)
                {
                    tokens.Add((tokenStart, i - tokenStart));
                    tokenStart = -1;
                }
                continue;
            }

            if (tokenStart < 0)
                tokenStart = i;
        }

        if (tokenStart >= 0)
            tokens.Add((tokenStart, text.Length - tokenStart));
        return tokens;
    }

    internal static List<(int Start, int Length)> SplitTopLevelCommaSpans(string text) => SplitTopLevelCommaSpans(text.AsSpan());

    internal static List<(int Start, int Length)> SplitTopLevelCommaSpans(ReadOnlySpan<char> text)
    {
        if (text.IndexOf(',') < 0)
            return [(0, text.Length)];

        var spans = new List<(int Start, int Length)>(4);
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    internal static (int Start, int Length) GetFirstTopLevelCommaSpan(string text)
    {
        if (text.IndexOf(',') < 0)
            return (0, text.Length);

        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case ',' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    return (0, i);
            }
        }

        return (0, text.Length);
    }

    internal static List<(int Start, int Length)> SplitTopLevelAmpersandSpans(string text) => SplitTopLevelAmpersandSpans(text.AsSpan());

    internal static List<(int Start, int Length)> SplitTopLevelAmpersandSpans(ReadOnlySpan<char> text)
    {
        if (text.IndexOf('&') < 0)
            return [(0, text.Length)];

        var spans = new List<(int Start, int Length)>(4);
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
                case '&' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    internal static int CountLeadingWhitespace(string text, int start, int length) => CountLeadingWhitespace(text.AsSpan(), start, length);

    internal static int CountLeadingWhitespace(ReadOnlySpan<char> text, int start, int length)
    {
        int count = 0;
        while (count < length && char.IsWhiteSpace(text[start + count]))
            count++;
        return count;
    }

    internal static int FindTypeListTerminator(string text, bool allowArrow) => FindTypeListTerminator(text.AsSpan(), allowArrow);

    internal static int FindTypeListTerminator(ReadOnlySpan<char> text, bool allowArrow)
    {
        int brace = FindFirstTopLevelChar(text, '{');
        int semi = FindFirstTopLevelChar(text, ';');
        int end = -1;
        if (brace >= 0) end = brace;
        if (semi >= 0 && (end < 0 || semi < end)) end = semi;
        if (allowArrow)
        {
            int arrow = text.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0 && (end < 0 || arrow < end))
                end = arrow;
        }
        return end;
    }

    private static string TrimTrailingTypeListTerminator(string text)
    {
        int end = FindTypeListTerminator(text, allowArrow: true);
        return end >= 0 ? text.Substring(0, end) : text;
    }

    internal static int FindJavaTypeListTerminator(string text, int start)
    {
        int terminator = FindJavaTypeListTerminator(text.AsSpan(start));
        return terminator >= 0 ? start + terminator : -1;
    }

    internal static int FindJavaTypeListTerminator(ReadOnlySpan<char> text)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
                angleDepth++;
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
                parenDepth++;
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0)
            {
                if (c == '{' || c == ';')
                    return i;
                if (IsJavaBaseListTerminatorKeyword(text, i, 0, "implements")
                    || IsJavaBaseListTerminatorKeyword(text, i, 0, "permits")
                    || IsJavaBaseListTerminatorKeyword(text, i, 0, "throws"))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    internal static int FindTopLevelKeyword(string text, string keyword) => FindTopLevelKeyword(text.AsSpan(), keyword);

    internal static int FindTopLevelKeyword(ReadOnlySpan<char> text, string keyword)
    {
        var keywordSpan = keyword.AsSpan();
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }

            if (angleDepth != 0 || parenDepth != 0 || squareDepth != 0 || braceDepth != 0)
                continue;
            if (i > 0 && IsJavaIdentifierPart(text[i - 1]))
                continue;
            if (i + keywordSpan.Length > text.Length || !text.Slice(i, keywordSpan.Length).SequenceEqual(keywordSpan))
                continue;
            int after = i + keywordSpan.Length;
            if (after < text.Length && IsJavaIdentifierPart(text[after]))
                continue;
            return i;
        }

        return -1;
    }

    private static bool IsCallablePrefixModifier(string language, string token) =>
        language == "csharp"
            ? token is "public" or "private" or "protected" or "internal" or "file" or "static" or "readonly" or "required" or "volatile" or "const"
                or "unsafe" or "new" or "sealed" or "abstract" or "virtual" or "override" or "extern" or "partial" or "async" or "ref" or "scoped"
            : token is "public" or "private" or "protected" or "static" or "final" or "abstract" or "synchronized" or "native" or "strictfp" or "default";

    private static bool IsParameterModifier(string language, string token) =>
        language == "csharp"
            ? token is "ref" or "out" or "in" or "params" or "this" or "scoped" or "readonly"
            : token is "final";

    private static bool IsDeclarationModifier(string language, string token) =>
        language == "csharp"
            ? token is "public" or "private" or "protected" or "internal" or "file" or "static" or "readonly" or "required" or "volatile" or "const"
                or "unsafe" or "new" or "sealed" or "abstract" or "virtual" or "override" or "extern" or "partial" or "async" or "ref" or "scoped" or "event"
            : token is "public" or "private" or "protected" or "static" or "final" or "abstract" or "volatile" or "transient" or "synchronized" or "native" or "strictfp";

    private static bool IsSimpleDeclarationIdentifier(string language, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        if (!IsTypeExpressionIdentifierStart(language, token[0]))
            return false;
        for (int i = 1; i < token.Length; i++)
        {
            if (!IsTypeExpressionIdentifierPart(language, token[i]))
                return false;
        }

        return true;
    }

    private static bool HasWhitespaceGap(string text, int start)
    {
        if (start >= text.Length)
            return false;
        for (int i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return true;
    }

    private static string NormalizeCSharpDocCref(string cref)
    {
        var text = cref.AsSpan().Trim();
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
            text = text[2..];
        int paren = text.IndexOf('(');
        if (paren >= 0)
            text = text[..paren];
        int brace = text.IndexOf('{');
        if (brace >= 0)
            text = text[..brace];
        return text.Trim().ToString();
    }

    private static bool IsCSharpIdentifierStart(char c) =>
        c == '_' || c == '@' || char.IsLetter(c);

    private static bool IsJavaIdentifierStart(char c) =>
        c == '_' || c == '$' || char.IsLetter(c);

    private static bool IsTypeExpressionIdentifierStart(string language, char c) =>
        language == "csharp" ? IsCSharpIdentifierStart(c) : IsJavaIdentifierStart(c);

    private static bool IsTypeExpressionIdentifierPart(string language, char c) =>
        language == "csharp" ? IsCSharpIdentifierPart(c) : IsJavaIdentifierPart(c);

}
