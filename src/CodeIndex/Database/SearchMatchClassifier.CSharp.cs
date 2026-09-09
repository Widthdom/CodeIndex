namespace CodeIndex.Database;

internal static partial class SearchMatchClassifier
{
    internal const int CSharpContextLineLimit = 4096;
    internal const int CSharpContextCharacterLimit = 1024 * 1024;

    private static string ClassifyCSharpContext(
        string path, int line, string text, int index, IReadOnlyDictionary<int, string>? context)
    {
        // Standalone callers retain their line-local contract. Search supplies indexed context.
        if (context is null)
            return ClassifyCSharpLegacy(path, line, text, index, null);
        if (line < 1 || line > CSharpContextLineLimit)
            return Unknown;

        var state = 0; // code, block comment, verbatim string, raw string
        var quotes = 0;
        var interpolationBraces = 0;
        var origin = StringLiteral;
        var remaining = CSharpContextCharacterLimit;
        for (var current = 1; current <= line; current++)
        {
            if (current != line && !context.TryGetValue(current, out _))
                return Unknown;
            var source = current == line ? text : context[current];
            var stop = current == line ? index + 1 : source.Length;
            if (source.Length > remaining)
                return Unknown;
            remaining -= source.Length;
            for (var i = 0; i < stop; i++)
            {
                var ch = source[i];
                var target = current == line && i == index;
                if (state == 1)
                {
                    if (ch == '*' && i + 1 < source.Length && source[i + 1] == '/')
                    {
                        if (current == line && index <= i + 1)
                            return Comment;
                        i++;
                        state = 0;
                    }
                    else if (target)
                        return Comment;
                    continue;
                }
                if (state == 2)
                {
                    if (interpolationBraces > 0 && ch == '{')
                    {
                        if (i + 1 >= source.Length || source[i + 1] != '{')
                            return Unknown;
                        if (current == line && index <= i + 1)
                            return origin;
                        i++;
                        continue;
                    }
                    if (ch == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            if (current == line && index <= i + 1)
                                return origin;
                            i++;
                        }
                        else
                            state = 0;
                    }
                    if (target)
                        return origin;
                    continue;
                }
                if (state == 3)
                {
                    if (interpolationBraces > 0 && ch == '{' &&
                        source.AsSpan(i).StartsWith(new string('{', interpolationBraces), StringComparison.Ordinal))
                        return Unknown;
                    if (ch == '"')
                    {
                        var run = CountQuotes(source, i, remaining + stop);
                        if (run < 0)
                            return Unknown;
                        if (run >= quotes)
                        {
                            if (current == line && index < i + quotes)
                                return origin;
                            i += quotes - 1;
                            state = 0;
                            continue;
                        }
                        if (current == line && index < i + run)
                            return origin;
                        i += run - 1;
                    }
                    if (target)
                        return origin;
                    continue;
                }
                if (ch == '/' && i + 1 < source.Length)
                {
                    if (source[i + 1] == '/')
                    {
                        if (current == line)
                            return Comment;
                        break;
                    }
                    if (source[i + 1] == '*')
                    {
                        state = 1;
                        if (current == line && index <= i + 1)
                            return Comment;
                        i++;
                        continue;
                    }
                }
                if (ch is '"' or '\'')
                {
                    var run = ch == '"' ? CountQuotes(source, i, remaining + stop) : 1;
                    if (run < 0)
                        return Unknown;
                    var verbatim = ch == '"' && (i > 0 && source[i - 1] == '@' ||
                        i > 1 && source[i - 1] == '$' && source[i - 2] == '@');
                    var raw = !verbatim && run >= 3;
                    interpolationBraces = 0;
                    var prefix = i - 1;
                    if (prefix >= 0 && source[prefix] == '@')
                        prefix--;
                    while (prefix >= 0 && source[prefix] == '$')
                    {
                        if (++interpolationBraces > 64)
                            return Unknown;
                        prefix--;
                    }
                    var contentStart = i + (raw ? run : 1);
                    origin = LooksLikeSchemaDescription(path, current, source, contentStart, context)
                        ? SchemaDescription : LooksLikeRegexString(source) ? RegexLiteral
                        : LooksLikeHelpText(path, source) ? HelpText : StringLiteral;
                    if (raw || verbatim)
                    {
                        state = raw ? 3 : 2;
                        quotes = run;
                        if (current == line && index < contentStart)
                            return origin;
                        i = contentStart - 1;
                        continue;
                    }
                    // Ordinary strings and character literals cannot carry lexical state over a newline.
                    var quote = ch;
                    for (i++; i < source.Length; i++)
                    {
                        if (current == line && i >= index)
                            return origin;
                        if (i >= CSharpContextCharacterLimit)
                            return Unknown;
                        if (source[i] == '\\')
                        {
                            if (current == line && index == i + 1)
                                return origin;
                            i++;
                        }
                        else if (interpolationBraces > 0 && source[i] == '{')
                        {
                            if (i + 1 >= source.Length || source[i + 1] != '{')
                                return Unknown;
                            i++;
                        }
                        else if (source[i] == quote)
                            break;
                    }
                    continue;
                }
                if (target)
                    return Code;
            }
        }
        return Unknown;
    }

    private static int CountQuotes(string source, int start, int budget)
    {
        var end = start;
        while (end < source.Length && source[end] == '"')
        {
            if (end - start >= budget)
                return -1;
            end++;
        }
        return end - start;
    }
}
