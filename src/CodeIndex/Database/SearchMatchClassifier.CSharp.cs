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
        private readonly CancellationToken _cancellation;
        private int _unknownLine = int.MaxValue;
        private int _unknownColumn;
        private string _unknownReason = "indexed_prefix_unavailable";

        public CSharpOriginContext(string path, IReadOnlyDictionary<int, string> context,
            CancellationToken cancellation = default)
        {
            _cancellation = cancellation;
            var schema = IsSchemaDescriptionPath(path) ? new CSharpSchemaCalls() : null;
            var remaining = CSharpContextCharacterLimit;
            var state = 0; // code, block comment, verbatim string, raw string, ordinary string, format
            var quotes = 0;
            var dollars = 0;
            var quote = '"';
            var expressions = new Stack<InterpolationFrame>();
            var pendingLine = 0;
            var pendingColumn = 0;
            CSharpStringLabel? label = null;
            for (var line = 1; line <= CSharpContextLineLimit; line++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!context.TryGetValue(line, out var source) || source.Length > remaining)
                {
                    SetUnknown(pendingLine > 0 ? pendingLine : line, pendingLine > 0 ? pendingColumn : 0,
                        source is null ? "indexed_prefix_unavailable" : "character_budget_exhausted");
                    return;
                }
                remaining -= source.Length;
                var parsed = new CSharpOriginLine(path, source, schema is not null, cancellation);
                _lines.Add(line, parsed);
                var i = 0;
                while (i < source.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((state == 0 || state == 5) && expressions.TryPeek(out var expression))
                    {
                        var ch = source[i];
                        if (ch == '}' && expression.Delimiters.Count == 0)
                        {
                            var run = CountRun(source, i, '}');
                            var required = expression.State == 3 ? expression.Dollars : 1;
                            if (run < required)
                            {
                                SetUnknown(pendingLine, pendingColumn, "unbalanced_interpolation");
                                return;
                            }
                            parsed.Add(i, i + required, StringLiteral, expression.Label);
                            i += required;
                            state = expression.State;
                            quotes = expression.Quotes;
                            dollars = expression.Dollars;
                            quote = '"';
                            label = expression.Label;
                            expressions.Pop();
                            continue;
                        }
                        if (state == 5)
                        {
                            if (ch is '{' or '"')
                            {
                                SetUnknown(pendingLine, pendingColumn, "unsupported_interpolation_format");
                                return;
                            }
                            parsed.Add(i, i + 1, StringLiteral, expression.Label);
                            i++;
                            continue;
                        }
                        if (ch is '(' or '[' or '{')
                        {
                            if (expression.Delimiters.Count == 64)
                            {
                                SetUnknown(pendingLine, pendingColumn, "interpolation_nesting_limit");
                                return;
                            }
                            expression.Delimiters.Push(ch);
                        }
                        else if (ch is ')' or ']' or '}')
                        {
                            var expected = ch == ')' ? '(' : ch == ']' ? '[' : '{';
                            if (!expression.Delimiters.TryPop(out var opener) || opener != expected)
                            {
                                SetUnknown(pendingLine, pendingColumn, "unbalanced_interpolation");
                                return;
                            }
                        }
                        else if (ch == ':' && expression.Delimiters.Count == 0 &&
                            !(i + 1 < source.Length && source[i + 1] == ':') && !(i > 0 && source[i - 1] == ':'))
                        {
                            state = 5;
                            continue;
                        }
                    }
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
                    var ordinary = state == 4;
                    if (state == 0)
                    {
                        if (source[i] is not ('"' or '\''))
                        {
                            schema?.Consume(source, i, line);
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
                            SetUnknown(line, i, "interpolation_nesting_limit");
                            return;
                        }
                        i += raw ? run : 1;
                        var schemaOrigin = parsed.HasDescriptionProperty
                            ? startIndex == parsed.DescriptionQuote ? SchemaDescription : null
                            : schema?.Exhausted == true ? Unknown
                            : schema?.IsDescriptionArgument(line) == true ? SchemaDescription : null;
                        label = new CSharpStringLabel(parsed, schemaOrigin);
                        state = raw ? 3 : verbatim ? 2 : 4;
                        ordinary = state == 4;
                        quotes = run;
                        if (dollars > 0 && pendingLine == 0)
                        {
                            pendingLine = line;
                            pendingColumn = startIndex;
                        }
                    }

                    var enteredExpression = false;
                    while (i < source.Length)
                    {
                        if ((i & 4095) == 0)
                            cancellation.ThrowIfCancellationRequested();
                        var ch = source[i];
                        if (dollars > 0 && ch == '}')
                        {
                            var run = CountRun(source, i, '}');
                            if (state == 3 ? run >= dollars : run % 2 != 0)
                            {
                                SetUnknown(pendingLine, pendingColumn, "unbalanced_interpolation");
                                return;
                            }
                            i += run;
                            continue;
                        }
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
                                var run = CountRun(source, i, '{');
                                if (expressions.Count == 64 || state == 3 && run >= 2 * dollars)
                                {
                                    SetUnknown(pendingLine, pendingColumn, "interpolation_nesting_limit");
                                    return;
                                }
                                i += state == 3 ? run : 1;
                                parsed.Add(startIndex, i, StringLiteral, label);
                                expressions.Push(new InterpolationFrame(state, quotes, dollars, label));
                                state = 0;
                                enteredExpression = true;
                                break;
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
                                state = 0;
                                break;
                            }
                        }
                        i++;
                    }
                    if (!enteredExpression)
                        parsed.Add(startIndex, i, StringLiteral, label);
                    if (state == 0 && expressions.Count == 0)
                        pendingLine = 0;
                }
                if (state == 4)
                {
                    SetUnknown(pendingLine > 0 ? pendingLine : line, pendingLine > 0 ? pendingColumn : 0,
                        "unterminated_ordinary_string");
                    return;
                }
            }
            if (pendingLine > 0)
                SetUnknown(pendingLine, pendingColumn, "unterminated_interpolation");
            else
                SetUnknown(CSharpContextLineLimit + 1, 0, "line_budget_exhausted");
        }

        private sealed class InterpolationFrame(int state, int quotes, int dollars, CSharpStringLabel? label)
        {
            public int State { get; } = state;
            public int Quotes { get; } = quotes;
            public int Dollars { get; } = dollars;
            public CSharpStringLabel? Label { get; } = label;
            public Stack<char> Delimiters { get; } = new();
        }

        public string GetOrigin(int line, string text, int index)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (line > _unknownLine || line == _unknownLine && index >= _unknownColumn ||
                !_lines.TryGetValue(line, out var parsed) ||
                !parsed.MatchesText(text))
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

        public SearchOriginUnavailable GetUnavailable(int line, string text)
        {
            if (_lines.TryGetValue(line, out var parsed) && !parsed.MatchesText(text))
                return new SearchOriginUnavailable { Reason = "indexed_text_mismatch", StartLine = line, StartColumn = 1, Extent = "line" };
            if (line < _unknownLine && _lines.ContainsKey(line))
                return new SearchOriginUnavailable { Reason = "schema_context_unavailable", StartLine = line, StartColumn = 1, Extent = "line" };
            return new SearchOriginUnavailable
            {
                Reason = _unknownReason,
                StartLine = _unknownLine == int.MaxValue ? line : _unknownLine,
                StartColumn = _unknownColumn + 1,
                Extent = "remaining_file",
            };
        }

        private void SetUnknown(int line, int column, string reason = "indexed_prefix_unavailable")
        {
            _unknownLine = line;
            _unknownColumn = column;
            _unknownReason = reason;
        }

        private static int CountRun(string source, int start, char value)
        {
            var end = start;
            while (end < source.Length && source[end] == value)
                end++;
            return end - start;
        }

        private sealed class CSharpOriginLine
        {
            private readonly string _path;
            private readonly CancellationToken _cancellation;
            private string? _stringOrigin;
            private string? _matchedView;
            public string Text { get; }
            public bool HasDescriptionProperty { get; }
            public int DescriptionQuote { get; } = -1;
            public List<CSharpOriginSpan> Spans { get; } = [];
            public CSharpOriginLine(string path, string text, bool schemaPath, CancellationToken cancellation)
            {
                _path = path;
                _cancellation = cancellation;
                Text = text;
                const string property = "[\"description\"]";
                var propertyIndex = schemaPath ? text.IndexOf(property, StringComparison.Ordinal) : -1;
                HasDescriptionProperty = propertyIndex >= 0;
                if (HasDescriptionProperty)
                {
                    var equals = text.IndexOf('=', propertyIndex + property.Length);
                    DescriptionQuote = equals < 0 ? -1 : text.IndexOf('"', equals + 1);
                }
            }
            public string StringOrigin
            {
                get
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (_stringOrigin is not null)
                        return _stringOrigin;
                    var regex = LooksLikeRegexString(Text);
                    _cancellation.ThrowIfCancellationRequested();
                    return _stringOrigin = regex ? RegexLiteral : LooksLikeHelpText(_path, Text) ? HelpText : StringLiteral;
                }
            }
            public bool MatchesText(string text)
            {
                if (ReferenceEquals(Text, text) || ReferenceEquals(_matchedView, text))
                    return true;
                if (!string.Equals(Text, text, StringComparison.Ordinal))
                    return false;
                _matchedView = text;
                return true;
            }
            public void Add(int start, int end, string origin, CSharpStringLabel? label = null)
            {
                if (end > start)
                {
                    if (Spans.Count > 0 && Spans[^1] is var previous && previous.End == start &&
                        previous.Origin == origin && ReferenceEquals(previous.Label, label))
                    {
                        Spans[^1] = previous with { End = end };
                        return;
                    }
                    Spans.Add(new CSharpOriginSpan(start, end, origin, label));
                }
            }
        }

        private readonly record struct CSharpOriginSpan(int Start, int End, string Origin, CSharpStringLabel? Label);

        private sealed class CSharpStringLabel(CSharpOriginLine line, string? schemaOrigin)
        {
            public string Origin => schemaOrigin ?? line.StringOrigin;
        }

        // Track only code delimiters; comments and string bodies never participate.
        // Work is linear in prefix characters plus at most 64 frames per string,
        // independent of the number/order of matches requested by a caller.
        private sealed class CSharpSchemaCalls
        {
            private readonly SchemaCall[] _calls = new SchemaCall[64];
            private int _count;
            private int _parentheses;
            private int _brackets;
            private int _braces;
            public bool Exhausted { get; private set; }

            public void Consume(string source, int index, int line)
            {
                switch (source[index])
                {
                    case '(':
                        _parentheses++;
                        var prefix = source.AsSpan(0, index + 1);
                        var expected = prefix.EndsWith("StringOrArraySchema(", StringComparison.Ordinal) ? 0
                            : prefix.EndsWith("CreateToolDefinition(", StringComparison.Ordinal) ||
                              prefix.EndsWith("AppendConstraintDescription(", StringComparison.Ordinal) ? 1 : -1;
                        if (expected < 0)
                            break;
                        if (_count == _calls.Length)
                        {
                            Exhausted = true;
                            break;
                        }
                        _calls[_count++] = new SchemaCall(_parentheses, _brackets, _braces, line, expected, 0);
                        break;
                    case ')':
                        while (_count > 0 && _calls[_count - 1].Parentheses >= _parentheses)
                            _count--;
                        _parentheses = Math.Max(0, _parentheses - 1);
                        break;
                    case '[': _brackets++; break;
                    case ']': _brackets = Math.Max(0, _brackets - 1); break;
                    case '{': _braces++; break;
                    case '}': _braces = Math.Max(0, _braces - 1); break;
                    case ',' when _count > 0:
                        ref var call = ref _calls[_count - 1];
                        if (call.Parentheses == _parentheses && call.Brackets == _brackets && call.Braces == _braces)
                            call = call with { Argument = call.Argument + 1 };
                        break;
                }
            }

            public bool IsDescriptionArgument(int line)
            {
                for (var i = _count - 1; i >= 0; i--)
                    if (line - _calls[i].Line <= 64 && _calls[i].Argument == _calls[i].Expected)
                        return true;
                return false;
            }

            private readonly record struct SchemaCall(int Parentheses, int Brackets, int Braces, int Line, int Expected, int Argument);
        }
    }
}
