namespace CodeIndex.Database;

internal static partial class SearchMatchClassifier
{
    internal const int CSharpContextLineLimit = 4096;
    // Leave room above the existing 4 Mi-character semantic-analysis windows.
    internal const int CSharpContextCharacterLimit = 8 * 1024 * 1024;
    internal const int CSharpContextChunkLimit = 128;

    private static string ClassifyCSharpContext(
        string path, int line, string text, int index, IReadOnlyDictionary<int, string>? context)
        => context is null
            ? ClassifyCSharpLegacy(path, line, text, index, null)
            : new CSharpOriginContext(path, context).GetOrigin(line, text, index);

    // One bounded lexical pass per indexed prefix, shared by every result/occurrence.
    // String labels are lazy: schema detection must not run for every preceding literal.
    internal sealed class CSharpOriginContext
    {
        private readonly Dictionary<int, CSharpOriginLine> _lines = new();
        private int _unknownLine = int.MaxValue;
        private int _unknownColumn;

        public CSharpOriginContext(string path, IReadOnlyDictionary<int, string> context,
            CancellationToken cancellation = default)
        {
            var remaining = CSharpContextCharacterLimit;
            var state = 0; // code, block comment, verbatim string, raw string
            var quotes = 0;
            var dollars = 0;
            CSharpStringLabel? label = null;
            for (var line = 1; line <= CSharpContextLineLimit; line++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!context.TryGetValue(line, out var source) || source.Length > remaining)
                    break;
                remaining -= source.Length;
                var parsed = new CSharpOriginLine(source);
                _lines.Add(line, parsed);
                var i = 0;
                while (i < source.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (state == 1 || state == 0 && source.AsSpan(i).StartsWith("/*", StringComparison.Ordinal))
                    {
                        var start = i;
                        var end = source.IndexOf("*/", state == 1 ? i : i + 2, StringComparison.Ordinal);
                        state = end < 0 ? 1 : 0;
                        i = end < 0 ? source.Length : end + 2;
                        parsed.Add(start, i, Comment);
                        continue;
                    }
                    if (state == 0 && source.AsSpan(i).StartsWith("//", StringComparison.Ordinal))
                    {
                        parsed.Add(i, source.Length, Comment);
                        break;
                    }

                    var startIndex = i;
                    var quote = '"';
                    var ordinary = false;
                    if (state == 0)
                    {
                        if (source[i] is not ('"' or '\''))
                        {
                            i++;
                            continue;
                        }
                        quote = source[i];
                        var run = quote == '"' ? CountRun(source, i, '"') : 1;
                        var verbatim = quote == '"' && (i > 0 && source[i - 1] == '@' ||
                            i > 1 && source[i - 1] == '$' && source[i - 2] == '@');
                        var raw = !verbatim && run >= 3;
                        dollars = 0;
                        var prefix = i - 1;
                        if (prefix >= 0 && source[prefix] == '@')
                            prefix--;
                        if (quote == '"')
                            while (prefix >= 0 && source[prefix--] == '$')
                                dollars++;
                        if (dollars > 64)
                        {
                            SetUnknown(line, i);
                            return;
                        }
                        i += raw ? run : 1;
                        label = new CSharpStringLabel(path, line, source, i, context);
                        state = raw ? 3 : verbatim ? 2 : 0;
                        ordinary = state == 0;
                        quotes = run;
                    }

                    while (i < source.Length)
                    {
                        if ((i & 4095) == 0)
                            cancellation.ThrowIfCancellationRequested();
                        var ch = source[i];
                        if (dollars > 0 && ch == '{')
                        {
                            if (state != 3 && i + 1 < source.Length && source[i + 1] == '{')
                            {
                                i += 2;
                                continue;
                            }
                            var braces = 1;
                            while (state == 3 && braces < dollars && i + braces < source.Length && source[i + braces] == '{')
                                braces++;
                            if (state != 3 || braces >= dollars)
                            {
                                parsed.Add(startIndex, i, StringLiteral, label);
                                SetUnknown(line, i);
                                return;
                            }
                            i += braces;
                            continue;
                        }
                        if (state == 3 && ch == '"')
                        {
                            var run = CountRun(source, i, '"');
                            if (run >= quotes)
                            {
                                i += quotes;
                                state = 0;
                                break;
                            }
                            i += run;
                            continue;
                        }
                        if (state == 2 && ch == '"')
                        {
                            if (i + 1 < source.Length && source[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }
                            i++;
                            state = 0;
                            break;
                        }
                        if (ordinary)
                        {
                            if (ch == '\\')
                            {
                                i = Math.Min(source.Length, i + 2);
                                continue;
                            }
                            if (ch == quote)
                            {
                                i++;
                                break;
                            }
                        }
                        i++;
                    }
                    parsed.Add(startIndex, i, StringLiteral, label);
                }
            }
        }

        public string GetOrigin(int line, string text, int index)
        {
            if (line > _unknownLine || line == _unknownLine && index >= _unknownColumn ||
                !_lines.TryGetValue(line, out var parsed) ||
                !string.Equals(parsed.Text, text, StringComparison.Ordinal))
                return Unknown;
            var spans = parsed.Spans;
            var low = 0;
            var high = spans.Count - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                var span = spans[mid];
                if (index < span.Start)
                    high = mid - 1;
                else if (index >= span.End)
                    low = mid + 1;
                else
                    return span.Label?.Origin ?? span.Origin;
            }
            return Code;
        }

        private void SetUnknown(int line, int column)
        {
            _unknownLine = line;
            _unknownColumn = column;
        }

        private static int CountRun(string source, int start, char value)
        {
            var end = start;
            while (end < source.Length && source[end] == value)
                end++;
            return end - start;
        }

        private sealed class CSharpOriginLine(string text)
        {
            public string Text { get; } = text;
            public List<CSharpOriginSpan> Spans { get; } = [];
            public void Add(int start, int end, string origin, CSharpStringLabel? label = null)
            {
                if (end > start)
                    Spans.Add(new CSharpOriginSpan(start, end, origin, label));
            }
        }

        private readonly record struct CSharpOriginSpan(int Start, int End, string Origin, CSharpStringLabel? Label);

        private sealed class CSharpStringLabel(string path, int line, string text, int contentStart,
            IReadOnlyDictionary<int, string> context)
        {
            private string? _origin;
            public string Origin => _origin ??= LooksLikeSchemaDescription(path, line, text, contentStart, context)
                ? SchemaDescription : LooksLikeRegexString(text) ? RegexLiteral
                : LooksLikeHelpText(path, text) ? HelpText : StringLiteral;
        }
    }
}
