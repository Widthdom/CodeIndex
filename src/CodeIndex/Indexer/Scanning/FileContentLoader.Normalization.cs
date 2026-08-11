using System.Text;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    internal readonly record struct NormalizedIndexableContent(
        string Content,
        NormalizedContentFacts Facts)
    {
        internal int LineCount => Facts.LineCount;
        internal bool HasOversizeLine => Facts.HasOversizeLine;
        internal int ConflictMarkerLine => Facts.ConflictMarkerLine;
    }

    internal static string NormalizeLineEndings(string content)
    {
        var firstCarriageReturn = content.IndexOf('\r');
        if (firstCarriageReturn < 0)
            return content;

        var builder = new StringBuilder(content.Length);
        builder.Append(content, 0, firstCarriageReturn);

        for (var index = firstCarriageReturn; index < content.Length; index++)
        {
            if (content[index] != '\r')
            {
                builder.Append(content[index]);
                continue;
            }

            builder.Append('\n');
            if (index + 1 < content.Length && content[index + 1] == '\n')
                index++;
        }

        return builder.ToString();
    }

    internal static string StripLineLeadingInvisibles(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var firstStripIndex = FindFirstLineLeadingInvisible(content);
        if (firstStripIndex < 0)
            return content;

        var builder = new StringBuilder(content.Length - 1);
        if (firstStripIndex > 0)
            builder.Append(content, 0, firstStripIndex);
        var atLineStart = true;
        for (var index = firstStripIndex + 1; index < content.Length; index++)
        {
            var current = content[index];
            if (IsLineLeadingInvisible(current) && atLineStart)
                continue;
            builder.Append(current);
            atLineStart = current == '\n';
        }
        return builder.ToString();
    }

    private static int FindFirstLineLeadingInvisible(string content)
    {
        var searchOffset = 0;
        while (searchOffset < content.Length)
        {
            var relativeIndex = content.AsSpan(searchOffset).IndexOfAny('\uFEFF', '\u200B');
            if (relativeIndex < 0)
                return -1;

            var index = searchOffset + relativeIndex;
            if (index == 0 || content[index - 1] == '\n')
                return index;

            searchOffset = index + 1;
        }

        return -1;
    }

    private static bool IsLineLeadingInvisible(char current)
        => current is '\uFEFF' or '\u200B';

    internal static NormalizedIndexableContent NormalizeForIndexing(
        string content,
        bool discardReplacementLinesWhenNonUtf8Likely = false)
    {
        if (content.Length == 0)
            return new NormalizedIndexableContent(content, NormalizedContentFacts.Empty);

        var scanner = new IndexingNormalizationScanner(
            content,
            discardReplacementLinesWhenNonUtf8Likely);
        return scanner.Normalize();
    }

    internal static string NormalizeContentForPrepass(string content)
    {
        if (content.Length == 0)
            return content;
        var firstNormalizationIndex = FindFirstPrepassNormalizationIndex(content);
        if (firstNormalizationIndex < 0)
            return content;

        StringBuilder? builder = null;
        var atLineStart = firstNormalizationIndex == 0 || content[firstNormalizationIndex - 1] == '\n';

        StringBuilder EnsureBuilder(int sourceIndex)
        {
            builder ??= new StringBuilder(content.Length).Append(content, 0, sourceIndex);
            return builder;
        }

        for (var index = firstNormalizationIndex; index < content.Length; index++)
        {
            var current = content[index];
            if (IsLineLeadingInvisible(current) && atLineStart)
            {
                EnsureBuilder(index);
                continue;
            }

            if (current == '\r')
            {
                EnsureBuilder(index).Append('\n');
                if (index + 1 < content.Length && content[index + 1] == '\n')
                    index++;
                atLineStart = true;
                continue;
            }

            builder?.Append(current);
            atLineStart = current == '\n';
        }

        return builder?.ToString() ?? content;
    }

    private static int FindFirstPrepassNormalizationIndex(string content)
    {
        var searchOffset = 0;
        while (searchOffset < content.Length)
        {
            var relativeIndex = content.AsSpan(searchOffset).IndexOfAny('\r', '\uFEFF', '\u200B');
            if (relativeIndex < 0)
                return -1;

            var index = searchOffset + relativeIndex;
            if (content[index] == '\r' || index == 0 || content[index - 1] == '\n')
                return index;

            searchOffset = index + 1;
        }

        return -1;
    }

    private struct IndexingNormalizationScanner
    {
        private readonly string _content;
        private readonly bool _discardReplacementLinesWhenNonUtf8Likely;
        private readonly bool _trackFtsTokens;
        private StringBuilder? _builder;
        private int _outputLength;
        private int _lineCount;
        private int _currentLineLength;
        private int _firstOversizeLine;
        private int _conflictMarkerLine;
        private int _conflictScanByteCount;
        private bool _conflictScanComplete;
        private int _replacementCharacterCount;
        private List<int>? _replacementCharacterLines;
        private bool _retainReplacementCharacterLines;
        private int _firstOversizeFtsTokenLine;
        private int _ftsTokenLength;
        private int _pendingChunkStartOffset;
        private int _pendingFullChunkEndOffset;
        private List<int>? _additionalChunkStartOffsets;
        private List<int>? _fullChunkEndOffsets;
        private bool _trackChunkSlices;
        private bool _previousOutputWasLineBreak;
        private bool _atLineStart;

        internal IndexingNormalizationScanner(
            string content,
            bool discardReplacementLinesWhenNonUtf8Likely)
        {
            this = default;
            _content = content;
            _discardReplacementLinesWhenNonUtf8Likely =
                discardReplacementLinesWhenNonUtf8Likely;
            _trackFtsTokens = content.Length
                > CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength;
            _retainReplacementCharacterLines = true;
            _trackChunkSlices = true;
            _atLineStart = true;
        }

        internal NormalizedIndexableContent Normalize()
        {
            ScanContent();
            return BuildResult();
        }

        private void ScanContent()
        {
            for (var index = 0; index < _content.Length; index++)
            {
                var current = _content[index];
                if (IsLineLeadingInvisible(current) && _atLineStart)
                {
                    EnsureBuilder(index);
                    continue;
                }

                if (current == '\r')
                {
                    NormalizeCarriageReturn(ref index);
                    continue;
                }

                if (char.IsHighSurrogate(current)
                    && index + 1 < _content.Length
                    && char.IsLowSurrogate(_content[index + 1]))
                {
                    NormalizeSurrogatePair(ref index, current);
                    continue;
                }

                NormalizeSingleCharacter(index, current);
            }
        }

        private void NormalizeCarriageReturn(ref int sourceIndex)
        {
            EnsureBuilder(sourceIndex).Append('\n');
            BeginOutputUnit();
            TrackConflictBytes(1);
            FinishOutputLineBreak();
            if (sourceIndex + 1 < _content.Length
                && _content[sourceIndex + 1] == '\n')
            {
                sourceIndex++;
            }
        }

        private void NormalizeSurrogatePair(ref int sourceIndex, char highSurrogate)
        {
            var lowSurrogate = _content[++sourceIndex];
            _builder?.Append(highSurrogate).Append(lowSurrogate);
            BeginOutputUnit();
            var rune = new Rune(highSurrogate, lowSurrogate);
            TrackConflictBytes(rune.Utf8SequenceLength);
            if (_trackFtsTokens)
                TrackFtsRune(rune);
            FinishOutputChars(2);
        }

        private void NormalizeSingleCharacter(int sourceIndex, char current)
        {
            _builder?.Append(current);
            BeginOutputUnit();
            if (char.IsSurrogate(current))
            {
                TrackConflictBytes(3);
                if (_trackFtsTokens)
                    TrackInvalidFtsRune();
                FinishOutputChars(1);
                return;
            }

            var utf8ByteLength = current <= '\u007F'
                ? 1
                : new Rune(current).Utf8SequenceLength;
            TrackConflictBytes(utf8ByteLength, current, sourceIndex);
            TrackReplacementCharacter(current);
            if (current == '\n')
            {
                FinishOutputLineBreak();
                return;
            }

            if (_trackFtsTokens)
                TrackFtsRune(new Rune(current));
            FinishOutputChars(1);
        }

        private StringBuilder EnsureBuilder(int sourceIndex)
        {
            _builder ??= new StringBuilder(_content.Length)
                .Append(_content, 0, sourceIndex);
            return _builder;
        }

        private void BeginOutputUnit()
        {
            if (_outputLength == 0)
            {
                _lineCount = 1;
            }
            else if (_previousOutputWasLineBreak)
            {
                _lineCount++;

                if (!_trackChunkSlices)
                    return;

                var chunkStep = ChunkSplitter.ChunkSize - ChunkSplitter.Overlap;
                if (_lineCount > ChunkSplitter.ChunkSize
                    && (_lineCount - ChunkSplitter.ChunkSize - 1) % chunkStep == 0)
                {
                    (_additionalChunkStartOffsets ??= []).Add(_pendingChunkStartOffset);
                    (_fullChunkEndOffsets ??= []).Add(_pendingFullChunkEndOffset);
                }

                if (_lineCount > 1
                    && (_lineCount - 1) % chunkStep == 0)
                {
                    _pendingChunkStartOffset = _outputLength;
                }
            }
        }

        private void TrackConflictBytes(
            int utf8ByteLength,
            char firstChar = '\0',
            int sourceIndex = -1)
        {
            if (_conflictMarkerLine > 0 || _conflictScanComplete)
                return;

            _conflictScanByteCount += utf8ByteLength;
            if (_conflictScanByteCount > FileIndexer.ConflictMarkerScanLimitBytes)
            {
                _conflictScanComplete = true;
                return;
            }

            if (_atLineStart
                && sourceIndex >= 0
                && firstChar is '<' or '>'
                && FileIndexer.IsConflictMarkerLineStart(_content.AsSpan(sourceIndex)))
            {
                _conflictMarkerLine = _lineCount;
            }
        }

        private void TrackReplacementCharacter(char current)
        {
            if (current != '\uFFFD')
                return;

            _replacementCharacterCount++;
            if (_discardReplacementLinesWhenNonUtf8Likely
                && FileIndexer.MeetsNonUtf8LikelyReplacementThreshold(
                    _replacementCharacterCount,
                    _content.Length))
            {
                _replacementCharacterLines = null;
                _retainReplacementCharacterLines = false;
                return;
            }

            if (!_retainReplacementCharacterLines)
                return;

            if (_replacementCharacterLines is null
                || _replacementCharacterLines[^1] != _lineCount)
            {
                (_replacementCharacterLines ??= []).Add(_lineCount);
            }
        }

        private void TrackFtsRune(Rune rune)
        {
            if (_firstOversizeFtsTokenLine > 0)
                return;

            var isTokenRune = rune.Value <= '\u007F'
                ? FileIndexer.IsLikelyUnicode61AsciiTokenChar((char)rune.Value)
                : FileIndexer.IsLikelyUnicode61TokenRune(rune);
            if (isTokenRune)
            {
                _ftsTokenLength++;
                if (_ftsTokenLength > CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength)
                    _firstOversizeFtsTokenLine = _lineCount;
            }
            else
            {
                _ftsTokenLength = 0;
            }
        }

        private void TrackInvalidFtsRune()
        {
            if (_firstOversizeFtsTokenLine == 0)
                _ftsTokenLength = 0;
        }

        private void FinishOutputChars(int charCount)
        {
            _currentLineLength += charCount;
            if (_firstOversizeLine == 0
                && _currentLineLength > ChunkSplitter.MaxLineLength)
            {
                _firstOversizeLine = _lineCount;
                _trackChunkSlices = false;
                _additionalChunkStartOffsets = null;
                _fullChunkEndOffsets = null;
            }

            _outputLength += charCount;
            _previousOutputWasLineBreak = false;
            _atLineStart = false;
        }

        private void FinishOutputLineBreak()
        {
            if (_trackChunkSlices
                && _lineCount >= ChunkSplitter.ChunkSize
                && (_lineCount - ChunkSplitter.ChunkSize)
                    % (ChunkSplitter.ChunkSize - ChunkSplitter.Overlap) == 0)
            {
                _pendingFullChunkEndOffset = _outputLength;
            }

            _outputLength++;
            _currentLineLength = 0;
            _ftsTokenLength = 0;
            _previousOutputWasLineBreak = true;
            _atLineStart = true;
        }

        private NormalizedIndexableContent BuildResult()
        {
            var normalized = _builder?.ToString() ?? _content;
            var replacementLines = _discardReplacementLinesWhenNonUtf8Likely
                && FileIndexer.MeetsNonUtf8LikelyReplacementThreshold(
                    _replacementCharacterCount,
                    normalized.Length)
                    ? null
                    : _replacementCharacterLines?.ToArray();
            return new NormalizedIndexableContent(
                normalized,
                new NormalizedContentFacts(
                    _lineCount,
                    _firstOversizeLine,
                    _conflictMarkerLine,
                    _replacementCharacterCount,
                    replacementLines,
                    _firstOversizeFtsTokenLine,
                    BuildChunkSlices()));
        }

        private NormalizedChunkSlice[]? BuildChunkSlices()
        {
            if (_outputLength == 0
                || _firstOversizeLine > 0
                || _lineCount <= ChunkSplitter.ChunkSize)
            {
                return null;
            }

            var chunkStep = ChunkSplitter.ChunkSize - ChunkSplitter.Overlap;
            var chunkCount = 1
                + (_lineCount - ChunkSplitter.ChunkSize + chunkStep - 1) / chunkStep;
            var slices = new NormalizedChunkSlice[chunkCount];
            var effectiveContentLength = _previousOutputWasLineBreak
                ? _outputLength - 1
                : _outputLength;
            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var startOffset = chunkIndex == 0
                    ? 0
                    : _additionalChunkStartOffsets![chunkIndex - 1];
                var startLineIndex = chunkIndex * chunkStep;
                var endLineIndex = Math.Min(
                    startLineIndex + ChunkSplitter.ChunkSize,
                    _lineCount);
                var endOffset = endLineIndex < _lineCount
                    ? _fullChunkEndOffsets![chunkIndex]
                    : effectiveContentLength;
                slices[chunkIndex] = new NormalizedChunkSlice(
                    startOffset,
                    endOffset - startOffset);
            }

            return slices;
        }
    }
}
