namespace CodeIndex.Database;

internal static partial class SearchMatchClassifier
{
    // Python 3.12/3.13 lexical origins, including PEP 701 replacement fields. This
    // is not a Python parser and never imports or executes indexed source.
    internal sealed class PythonOriginContext
    {
        internal const int NestingLimit = 64;
        internal const int SpanLimit = 262144;
        private readonly Dictionary<int, PythonLine> _lines = new();
        private readonly List<Frame> _frames = new();
        private readonly CancellationToken _cancellation;
        private int _spanCount;
        private int _unknownLine = int.MaxValue;
        private int _unknownColumn;
        private string _reason = "indexed_prefix_unavailable";
        private int? _retryPasses;
        internal long ComparedCharacters { get; private set; }

        public PythonOriginContext(IReadOnlyDictionary<int, string> lines, CancellationToken cancellation = default)
            : this(_ => new CSharpOriginWindow(lines), 1, cancellation) { }

        public PythonOriginContext(Func<int, CSharpOriginWindow> readWindow, int passLimit,
            CancellationToken cancellation = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(passLimit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(passLimit, CSharpOriginPassLimit);
            _cancellation = cancellation;
            var line = 1;
            for (var pass = 1; pass <= passLimit; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                var window = readWindow(line);
                var remaining = CSharpContextCharacterLimit;
                var first = line;
                var end = line + CSharpContextLineLimit;
                var reason = window.StopReason;
                for (; line < end; line++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!window.Lines.TryGetValue(line, out var source) || source.Length > remaining)
                    {
                        if (source is not null)
                            reason = "character_budget_exhausted";
                        break;
                    }
                    remaining -= source.Length;
                    var parsed = new PythonLine(source);
                    _lines.Add(line, parsed);
                    if (!ParseLine(line, parsed))
                        return;
                }
                if (line == end)
                    reason = "line_budget_exhausted";
                var resumable = reason is "line_budget_exhausted" or "character_budget_exhausted" or "chunk_budget_exhausted";
                if (resumable && line > first && pass < passLimit)
                    continue;
                if (resumable && line > first && passLimit < CSharpOriginPassLimit)
                    _retryPasses = passLimit + 1;
                Fail(line, 0, reason);
                return;
            }
        }

        private bool ParseLine(int line, PythonLine parsed)
        {
            var source = parsed.Text;
            var continuedString = false;
            for (var i = 0; i < source.Length;)
            {
                _cancellation.ThrowIfCancellationRequested();
                var frame = _frames.Count == 0 ? null : _frames[^1];
                var ch = source[i];
                var start = i;
                if (frame?.Kind == FrameKind.String)
                {
                    if (ch == frame.Quote && (!frame.Triple ||
                        i + 2 < source.Length && source[i + 1] == ch && source[i + 2] == ch))
                    {
                        i += frame.Triple ? 3 : 1;
                        _frames.RemoveAt(_frames.Count - 1);
                    }
                    else if (ch == '\\')
                    {
                        if (!ConsumeEscape(source, ref i, frame.Raw, frame.Formatted, line, out continuedString)) return false;
                    }
                    else if (frame.Formatted && ch is '{' or '}')
                    {
                        if (i + 1 < source.Length && source[i + 1] == ch)
                            i += 2;
                        else if (ch == '}')
                            return Fail(line, i, "unbalanced_interpolation");
                        else
                        {
                            if (!Push(new Frame(FrameKind.Expression, line, i), line, i)) return false;
                            i++;
                        }
                    }
                    else
                        i++;
                    if (!Add(parsed, start, i, StringLiteral, line)) return false;
                    continue;
                }

                if (frame?.Kind == FrameKind.Format)
                {
                    if (ch == '\\')
                    {
                        if (!ConsumeEscape(source, ref i, OuterString()?.Raw == true, true, line, out continuedString)) return false;
                    }
                    else
                    {
                        if (ch == '}')
                            _frames.RemoveAt(_frames.Count - 1);
                        else if (ch == '{')
                        {
                            if (!Push(new Frame(FrameKind.Expression, line, i), line, i)) return false;
                        }
                        else if (OuterString()?.Quote == ch)
                            return Fail(line, i, "unbalanced_interpolation");
                        i++;
                    }
                    if (!Add(parsed, start, i, StringLiteral, line)) return false;
                    continue;
                }

                if (ch == '#')
                {
                    parsed.EndOrigin = Comment;
                    return Add(parsed, i, source.Length, Comment, line);
                }
                if (frame is not null && (char.IsWhiteSpace(ch) || ch == '\\' && i + 1 == source.Length))
                {
                    i++;
                    continue;
                }
                if (frame is not null && frame.Delimiters.Count == 0)
                {
                    if (frame.AwaitingConversion)
                    {
                        if (ch is not ('s' or 'r' or 'a'))
                            return Fail(line, i, "unsupported_interpolation_conversion");
                        frame.AwaitingConversion = false;
                        frame.Conversion = true;
                        if (!Add(parsed, i, ++i, StringLiteral, line)) return false;
                        continue;
                    }
                    if (ch == '}')
                    {
                        if (!frame.HasExpression) return Fail(line, i, "unbalanced_interpolation");
                        _frames.RemoveAt(_frames.Count - 1);
                        if (!Add(parsed, i, ++i, StringLiteral, line)) return false;
                        continue;
                    }
                    if (ch == ':')
                    {
                        if (!frame.HasExpression) return Fail(line, i, "unbalanced_interpolation");
                        frame.Kind = FrameKind.Format;
                        if (!Add(parsed, i, ++i, StringLiteral, line)) return false;
                        continue;
                    }
                    if (ch == '!' && (i + 1 == source.Length || source[i + 1] != '='))
                    {
                        if (!frame.HasExpression || frame.Conversion)
                            return Fail(line, i, "unsupported_interpolation_conversion");
                        frame.AwaitingConversion = true;
                        if (!Add(parsed, i, ++i, StringLiteral, line)) return false;
                        continue;
                    }
                    if (frame.Conversion || frame.Debug)
                        return Fail(line, i, "unsupported_interpolation_expression");
                    if (ch == '=' && frame.HasExpression &&
                        (i == 0 || source[i - 1] is not ('=' or '!' or '<' or '>' or ':')) &&
                        (i + 1 == source.Length || source[i + 1] != '='))
                    {
                        frame.Debug = true;
                        i++;
                        continue;
                    }
                }
                if (frame is not null && !char.IsWhiteSpace(ch))
                    frame.HasExpression = true;

                // Consume each identifier once; a directly adjacent quote determines the
                // prefix without rescanning an unbounded identifier from every character.
                var quoteIndex = i;
                if (IsPythonName(ch))
                {
                    var possiblePrefix = true;
                    do
                    {
                        possiblePrefix &= source[quoteIndex] is 'r' or 'R' or 'u' or 'U' or 'b' or 'B' or 'f' or 'F' or 't' or 'T';
                        quoteIndex++;
                        if ((quoteIndex & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                    } while (quoteIndex < source.Length && IsPythonName(source[quoteIndex]));
                    if (!possiblePrefix || quoteIndex == source.Length || source[quoteIndex] is not ('\'' or '"'))
                    {
                        i = quoteIndex;
                        continue;
                    }
                }
                if (source[quoteIndex] is '\'' or '"')
                {
                    var prefix = source.AsSpan(i, quoteIndex - i);
                    if (prefix.Length > 2)
                        return Fail(line, i, "unsupported_python_string_prefix");
                    var normalized = prefix.ToString().ToLowerInvariant();
                    if (normalized is not ("" or "r" or "u" or "b" or "f" or "br" or "rb" or "fr" or "rf"))
                        return Fail(line, i, "unsupported_python_string_prefix");
                    var quote = source[quoteIndex];
                    var triple = quoteIndex + 2 < source.Length && source[quoteIndex + 1] == quote && source[quoteIndex + 2] == quote;
                    if (!Push(new Frame(FrameKind.String, line, i)
                        { Quote = quote, Triple = triple, Raw = normalized.Contains('r'), Formatted = normalized.Contains('f') }, line, i))
                        return false;
                    i = quoteIndex + (triple ? 3 : 1);
                    if (!Add(parsed, start, i, StringLiteral, line)) return false;
                    continue;
                }
                if (frame is not null)
                {
                    if (ch is '(' or '[' or '{')
                    {
                        if (frame.Delimiters.Count == NestingLimit)
                            return Fail(line, i, "interpolation_nesting_limit");
                        frame.Delimiters.Push(ch);
                    }
                    else if (ch is ')' or ']' or '}')
                    {
                        var expected = ch == ')' ? '(' : ch == ']' ? '[' : '{';
                        if (!frame.Delimiters.TryPop(out var opener) || opener != expected)
                            return Fail(line, i, "unbalanced_interpolation");
                    }
                }
                i++;
            }
            var top = _frames.Count == 0 ? null : _frames[^1];
            if (top?.Kind == FrameKind.String && !top.Triple && !continuedString ||
                top?.Kind == FrameKind.Format && OuterString()?.Triple != true && !continuedString)
                return Fail(line, 0, "unterminated_ordinary_string");
            parsed.EndOrigin = top?.Kind is FrameKind.String or FrameKind.Format ? StringLiteral : Code;
            return true;
        }

        private bool ConsumeEscape(string source, ref int index, bool raw, bool formatted, int line, out bool continued)
        {
            var start = index++;
            continued = index == source.Length;
            if (continued) return true;
            if (formatted && !raw && source[index] == 'N' && index + 1 < source.Length && source[index + 1] == '{')
            {
                index += 2;
                while (index < source.Length && source[index] != '}')
                {
                    if ((index & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
                    index++;
                }
                if (index == source.Length) return Fail(line, start, "unsupported_python_escape");
                index++;
            }
            // Backslashes do not escape f-string braces, including in raw formats.
            // Consume pairs so an escaped backslash cannot begin a named escape.
            else if (!formatted || source[index] is not ('{' or '}'))
                index++;
            return true;
        }

        private Frame? OuterString()
        {
            for (var i = _frames.Count - 1; i >= 0; i--)
                if (_frames[i].Kind == FrameKind.String) return _frames[i];
            return null;
        }

        private static bool IsPythonName(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch >= 128;

        private bool Push(Frame frame, int line, int column)
        {
            if (_frames.Count == NestingLimit) return Fail(line, column, "interpolation_nesting_limit");
            _frames.Add(frame);
            return true;
        }

        private bool Add(PythonLine parsed, int start, int end, string origin, int line)
        {
            if (parsed.Spans.Count > 0 && parsed.Spans[^1] is var last && last.End == start && last.Origin == origin)
                parsed.Spans[^1] = last with { End = end };
            else
            {
                if (_spanCount == SpanLimit) return Fail(line, start, "python_span_budget_exhausted");
                parsed.Spans.Add(new OriginSpan(start, end, origin));
                _spanCount++;
            }
            return true;
        }

        private bool Fail(int line, int column, string reason)
        {
            // A missing closer can invalidate earlier decisions inside a literal or
            // interpolation. Publish none of that provisional region as executable code.
            var pending = _frames.Count == 0 ? null : _frames[0];
            _unknownLine = pending?.Line ?? line;
            _unknownColumn = pending?.Column ?? column;
            _reason = reason;
            _frames.Clear();
            return false;
        }

        public string GetOrigin(int line, string text, int index)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (line > _unknownLine || line == _unknownLine && index >= _unknownColumn ||
                !_lines.TryGetValue(line, out var parsed) || !MatchesText(parsed, text) || index < 0 || index > text.Length)
                return Unknown;
            if (index >= parsed.Text.Length) return parsed.EndOrigin;
            var low = 0;
            var high = parsed.Spans.Count - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                var span = parsed.Spans[mid];
                if (index < span.Start) high = mid - 1;
                else if (index >= span.End) low = mid + 1;
                else return span.Origin;
            }
            return Code;
        }

        public SearchOriginUnavailable GetUnavailable(int line, string text)
        {
            if (_lines.TryGetValue(line, out var parsed) && !MatchesText(parsed, text))
                return new() { Reason = "indexed_text_mismatch", StartLine = line, StartColumn = 1, Extent = "line" };
            return new()
            {
                Reason = _reason,
                StartLine = _unknownLine == int.MaxValue ? line : _unknownLine,
                StartColumn = _unknownColumn + 1,
                Extent = "remaining_file",
                RetryOriginPasses = _retryPasses,
                RecoveryGuidance = _retryPasses is { } passes
                    ? $"Restart without --cursor using --origin-passes {passes}; each Python pass reads at most 4096 lines, 8 Mi UTF-16 characters and 128 chunks."
                    : "Inspect unknown Python matches without exclusions. Missing, malformed or unsupported lexical context cannot be proved by larger result limits; review the indexed source and the Python origin syntax envelope.",
            };
        }

        private bool MatchesText(PythonLine parsed, string text)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (ReferenceEquals(parsed.Text, text)) return true;
            if (ReferenceEquals(parsed.ComparedView, text)) return parsed.ViewMatches;
            // Search's indexed line reader strips CRLF terminators, while regex find
            // preserves the original trailing CR. Compare the same physical source
            // without changing regex columns or lengths, including end positions.
            var lengthMatches = parsed.Text.Length == text.Length ||
                text.Length == parsed.Text.Length + 1 && text[^1] == '\r';
            var matches = lengthMatches;
            for (var start = 0; matches && start < parsed.Text.Length; start += 4096)
            {
                _cancellation.ThrowIfCancellationRequested();
                var length = Math.Min(4096, parsed.Text.Length - start);
                ComparedCharacters += length;
                matches = parsed.Text.AsSpan(start, length).SequenceEqual(text.AsSpan(start, length));
            }
            // One view per line, bounded by the admitted source length plus a CR. Repeated regex
            // occurrences must not compare or scan the complete line for every match.
            if (lengthMatches)
            {
                parsed.ComparedView = text;
                parsed.ViewMatches = matches;
            }
            return matches;
        }

        private enum FrameKind { String, Expression, Format }
        private sealed class Frame(FrameKind kind, int line, int column)
        {
            public FrameKind Kind = kind;
            public int Line { get; } = line;
            public int Column { get; } = column;
            public char Quote;
            public bool Triple;
            public bool Raw;
            public bool Formatted;
            public bool HasExpression;
            public bool Conversion;
            public bool AwaitingConversion;
            public bool Debug;
            public Stack<char> Delimiters { get; } = new();
        }
        private sealed class PythonLine(string text)
        {
            public string Text { get; } = text;
            public string? ComparedView;
            public bool ViewMatches;
            public string EndOrigin = Code;
            public List<OriginSpan> Spans { get; } = new();
        }
        private readonly record struct OriginSpan(int Start, int End, string Origin);
    }
}
