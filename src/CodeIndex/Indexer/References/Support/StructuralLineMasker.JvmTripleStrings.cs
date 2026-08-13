namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private enum JvmTripleStringPolicy
    {
        Kotlin,
        Scala,
    }

    private static void MaskKotlinTripleStringContents(string[] lines)
    {
        var scanner = new JvmTripleStringScanner(
            lines,
            JvmTripleStringPolicy.Kotlin);
        scanner.MaskLines();
    }

    private static void MaskScalaTripleStringContents(string[] lines)
    {
        var scanner = new JvmTripleStringScanner(
            lines,
            JvmTripleStringPolicy.Scala);
        scanner.MaskLines();
    }

    private struct JvmTripleStringScanner(
        string[] lines,
        JvmTripleStringPolicy policy)
    {
        private readonly Stack<int> _deepTripleHashCounts = new();
        private bool _insideTriple;
        private bool _tripleInterpolates;
        private int _blockCommentDepth;
        private int _holeBraceDepth = -1;
        private bool _nestedTripleOpen;
        private bool _nestedTripleInterpolates;
        private int _nestedHoleBraceDepth = -1;
        private int _deepTripleDepth;

        internal void MaskLines()
        {
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (lines[lineIndex].Length == 0)
                    continue;

                ScanLine(lineIndex);
            }
        }

        private void ScanLine(int lineIndex)
        {
            var line = new JvmTripleStringLine(lines[lineIndex]);
            while (line.Position < line.Text.Length)
            {
                if (_blockCommentDepth > 0)
                {
                    ScanBlockComment(ref line);
                    continue;
                }

                if (!_insideTriple)
                {
                    ScanCode(ref line);
                    continue;
                }

                if (_holeBraceDepth < 0)
                {
                    ScanTripleBody(ref line);
                    continue;
                }

                if (!_nestedTripleOpen)
                {
                    ScanInterpolationHole(ref line);
                    continue;
                }

                if (_nestedHoleBraceDepth < 0)
                {
                    ScanNestedTripleBody(ref line);
                    continue;
                }

                if (_deepTripleDepth > 0)
                    ScanOpaqueDeepTriple(ref line, lineIndex);
                else
                    ScanNestedInterpolationHole(ref line);
            }

            if (line.WasMasked)
                lines[lineIndex] = line.CreateMaskedText();
        }

        private void ScanBlockComment(ref JvmTripleStringLine line)
        {
            if (StartsWith(line.Text, line.Position, "/*"))
            {
                line.MaskAndAdvance(2);
                _blockCommentDepth++;
                return;
            }

            if (StartsWith(line.Text, line.Position, "*/"))
            {
                line.MaskAndAdvance(2);
                _blockCommentDepth--;
                return;
            }

            line.MaskAndAdvance();
        }

        private void ScanCode(ref JvmTripleStringLine line)
        {
            if (StartsWith(line.Text, line.Position, "//"))
            {
                line.MoveToEnd();
                return;
            }

            if (StartsWith(line.Text, line.Position, "/*"))
            {
                line.MaskAndAdvance(2);
                _blockCommentDepth = 1;
                return;
            }

            if (IsTripleQuoteAt(line.Text, line.Position))
            {
                _tripleInterpolates =
                    TripleAtPositionInterpolates(line.Text, line.Position);
                line.MaskAndAdvance(3);
                _insideTriple = true;
                return;
            }

            if (line.Text[line.Position] is '"' or '\'')
            {
                line.Position =
                    SkipJsSingleLineString(line.Text, line.Position);
                return;
            }

            line.Position++;
        }

        private void ScanTripleBody(ref JvmTripleStringLine line)
        {
            if (IsTripleQuoteAt(line.Text, line.Position))
            {
                line.MaskAndAdvance(3);
                _insideTriple = false;
                _tripleInterpolates = false;
                ResetNestedTripleState();
                return;
            }

            if (_tripleInterpolates
                && StartsWith(line.Text, line.Position, "${"))
            {
                line.MaskAndAdvance(2);
                _holeBraceDepth = 0;
                return;
            }

            line.MaskAndAdvance();
        }

        private void ScanInterpolationHole(ref JvmTripleStringLine line)
        {
            if (TryScanHoleComment(ref line))
                return;

            if (IsTripleQuoteAt(line.Text, line.Position))
            {
                _nestedTripleInterpolates =
                    TripleAtPositionInterpolates(line.Text, line.Position);
                line.MaskAndAdvance(3);
                _nestedTripleOpen = true;
                _nestedHoleBraceDepth = -1;
                return;
            }

            if (TrySkipQuotedHoleLiteral(ref line))
                return;

            if (line.Text[line.Position] == '{')
            {
                _holeBraceDepth++;
                line.Position++;
                return;
            }

            if (line.Text[line.Position] == '}')
            {
                if (_holeBraceDepth == 0)
                {
                    line.MaskAndAdvance();
                    _holeBraceDepth = -1;
                    return;
                }

                _holeBraceDepth--;
            }

            line.Position++;
        }

        private void ScanNestedTripleBody(ref JvmTripleStringLine line)
        {
            if (IsTripleQuoteAt(line.Text, line.Position))
            {
                line.MaskAndAdvance(3);
                ResetNestedTripleState();
                return;
            }

            if (_nestedTripleInterpolates
                && StartsWith(line.Text, line.Position, "${"))
            {
                line.MaskAndAdvance(2);
                _nestedHoleBraceDepth = 0;
                return;
            }

            line.MaskAndAdvance();
        }

        private void ScanNestedInterpolationHole(
            ref JvmTripleStringLine line)
        {
            if (TryScanHoleComment(ref line))
                return;

            if (IsTripleQuoteAt(line.Text, line.Position))
            {
                line.MaskAndAdvance(3);
                _deepTripleDepth = 1;
                _deepTripleHashCounts.Push(0);
                return;
            }

            if (TrySkipQuotedHoleLiteral(ref line))
                return;

            if (line.Text[line.Position] == '{')
            {
                _nestedHoleBraceDepth++;
                line.Position++;
                return;
            }

            if (line.Text[line.Position] == '}')
            {
                if (_nestedHoleBraceDepth == 0)
                {
                    line.MaskAndAdvance();
                    _nestedHoleBraceDepth = -1;
                    return;
                }

                _nestedHoleBraceDepth--;
            }

            line.Position++;
        }

        private void ScanOpaqueDeepTriple(
            ref JvmTripleStringLine line,
            int lineIndex)
        {
            var hashCount = policy == JvmTripleStringPolicy.Scala
                ? CountRun(line.Text, line.Position, '#')
                : 0;
            var quoteIndex = line.Position + hashCount;
            if (!IsTripleQuoteAt(line.Text, quoteIndex))
            {
                line.MaskAndAdvance();
                return;
            }

            var delimiterLength = hashCount + 3;
            if (LooksLikeDeepTripleOpenerContext(
                    lines,
                    lineIndex,
                    line.Position,
                    delimiterLength))
            {
                line.MaskAndAdvance(delimiterLength);
                _deepTripleDepth++;
                _deepTripleHashCounts.Push(hashCount);
                return;
            }

            var currentHashCount = _deepTripleHashCounts.Count > 0
                ? _deepTripleHashCounts.Peek()
                : 0;
            if (hashCount != currentHashCount)
            {
                line.MaskAndAdvance();
                return;
            }

            line.MaskAndAdvance(delimiterLength);
            _deepTripleDepth--;
            if (_deepTripleHashCounts.Count > 0)
                _deepTripleHashCounts.Pop();
        }

        private bool TryScanHoleComment(ref JvmTripleStringLine line)
        {
            if (StartsWith(line.Text, line.Position, "//"))
            {
                line.MaskToEnd();
                return true;
            }

            if (!StartsWith(line.Text, line.Position, "/*"))
                return false;

            line.MaskAndAdvance(2);
            _blockCommentDepth = 1;
            return true;
        }

        private static bool TrySkipQuotedHoleLiteral(
            ref JvmTripleStringLine line)
        {
            if (line.Text[line.Position] is not ('"' or '\''))
                return false;

            line.Position =
                SkipJsSingleLineString(line.Text, line.Position);
            return true;
        }

        private bool TripleAtPositionInterpolates(
            string line,
            int quoteIndex) =>
            policy == JvmTripleStringPolicy.Kotlin
            || (quoteIndex > 0 && IsIdentifierPart(line[quoteIndex - 1]));

        private void ResetNestedTripleState()
        {
            _nestedTripleOpen = false;
            _nestedTripleInterpolates = false;
            _nestedHoleBraceDepth = -1;
            _deepTripleDepth = 0;
            _deepTripleHashCounts.Clear();
        }

        private static bool IsTripleQuoteAt(string line, int position) =>
            position + 2 < line.Length
            && line[position] == '"'
            && line[position + 1] == '"'
            && line[position + 2] == '"';
    }

    private struct JvmTripleStringLine(string text)
    {
        private char[]? _masked;

        internal string Text { get; } = text;

        internal int Position { get; set; }

        internal readonly bool WasMasked => _masked is not null;

        internal void MaskAndAdvance(int length = 1)
        {
            _masked ??= Text.ToCharArray();
            ReplaceWithSpaces(_masked, Position, length);
            Position += length;
        }

        internal void MaskToEnd()
        {
            MaskAndAdvance(Text.Length - Position);
        }

        internal void MoveToEnd()
        {
            Position = Text.Length;
        }

        internal readonly string CreateMaskedText() =>
            new(_masked!);
    }
}
