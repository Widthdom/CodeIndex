using System.Globalization;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class NormalizedContentFactsTests
{
    private const int RandomizedCaseCount = 1_500;
    private const int RandomizedSeed = 0x51C0DE;

    [Fact]
    public void NormalizeForIndexing_RandomizedFactsChunksAndValidationMatchIndependentOracle()
    {
        var random = new Random(RandomizedSeed);
        for (var caseIndex = 0; caseIndex < RandomizedCaseCount; caseIndex++)
        {
            var source = CreateRandomContent(random);
            var expected = LegacyNormalizeAndAnalyze(source);

            var actual = FileContentLoader.NormalizeForIndexing(source);

            Assert.Equal(expected.Content, actual.Content);
            if (!expected.Changed)
                Assert.Same(source, actual.Content);
            AssertFactsEqual(expected.Facts, actual.Facts, caseIndex, source);
            AssertChunksEqual(
                LegacySplitNormalized(17, expected.Content, expected.Facts),
                ChunkSplitter.SplitNormalized(17, actual.Content, actual.Facts),
                caseIndex,
                source);
            AssertValidationIssueParity(expected.Content, expected.Facts, actual.Facts, caseIndex, source);
        }
    }

    [Fact]
    public void NormalizeForIndexing_TargetedLengthTokenAndReplacementBoundariesMatchContract()
    {
        foreach (var (lineLength, expectedLine) in new[]
                 {
                     (ChunkSplitter.MaxLineLength - 1, 0),
                     (ChunkSplitter.MaxLineLength, 0),
                     (ChunkSplitter.MaxLineLength + 1, 2),
                 })
        {
            var normalized = FileContentLoader.NormalizeForIndexing("ok\n" + new string('a', lineLength));
            Assert.Equal(expectedLine, normalized.Facts.FirstOversizeLine);
        }

        var emoji = char.ConvertFromUtf32(0x1F680);
        var exactUtf16Boundary = FileContentLoader.NormalizeForIndexing(
            string.Concat(Enumerable.Repeat(emoji, ChunkSplitter.MaxLineLength / 2)));
        var overUtf16Boundary = FileContentLoader.NormalizeForIndexing(
            exactUtf16Boundary.Content + "a");
        Assert.Equal(0, exactUtf16Boundary.Facts.FirstOversizeLine);
        Assert.Equal(1, overUtf16Boundary.Facts.FirstOversizeLine);

        foreach (var (tokenLength, expectedLine) in new[]
                 {
                     (DbReader.FtsUnicode61MaxTokenLength - 1, 0),
                     (DbReader.FtsUnicode61MaxTokenLength, 0),
                     (DbReader.FtsUnicode61MaxTokenLength + 1, 2),
                 })
        {
            var normalized = FileContentLoader.NormalizeForIndexing("ok\n" + new string('x', tokenLength));
            Assert.Equal(expectedLine, normalized.Facts.FirstOversizeFtsTokenLine);
        }

        var astralLetter = char.ConvertFromUtf32(0x10400);
        var astralToken = FileContentLoader.NormalizeForIndexing(
            string.Concat(Enumerable.Repeat(astralLetter, DbReader.FtsUnicode61MaxTokenLength + 1)));
        Assert.Equal(1, astralToken.Facts.FirstOversizeFtsTokenLine);

        var exactWholeContentToken = FileContentLoader.NormalizeForIndexing(
            new string('x', DbReader.FtsUnicode61MaxTokenLength));
        var overWholeContentToken = FileContentLoader.NormalizeForIndexing(
            new string('x', DbReader.FtsUnicode61MaxTokenLength + 1));
        Assert.Equal(0, exactWholeContentToken.Facts.FirstOversizeFtsTokenLine);
        Assert.Equal(1, overWholeContentToken.Facts.FirstOversizeFtsTokenLine);

        var replacementFacts = FileContentLoader.NormalizeForIndexing("a\uFFFDb\uFFFD\n\uFFFD\nclean").Facts;
        Assert.Equal(3, replacementFacts.ReplacementCharacterCount);
        Assert.Equal([1, 2], replacementFacts.ReplacementCharacterLines);
    }

    [Fact]
    public void ConflictMarkerBudget_UsesNormalizedUtf8RuneBytesAcrossFactsAndPublicFallback()
    {
        var acceptedAscii = BuildMarkerAtNormalizedBytePosition(
            FileIndexer.ConflictMarkerScanLimitBytes,
            astralRuneCount: 0,
            useCrLfAndLeadingInvisible: false,
            unpairedSurrogateCount: 0);
        var rejectedAscii = BuildMarkerAtNormalizedBytePosition(
            FileIndexer.ConflictMarkerScanLimitBytes + 1,
            astralRuneCount: 0,
            useCrLfAndLeadingInvisible: false,
            unpairedSurrogateCount: 0);
        var acceptedAstral = BuildMarkerAtNormalizedBytePosition(
            FileIndexer.ConflictMarkerScanLimitBytes,
            astralRuneCount: 1_000,
            useCrLfAndLeadingInvisible: false,
            unpairedSurrogateCount: 0);
        var acceptedNormalizedFromCrLf = BuildMarkerAtNormalizedBytePosition(
            FileIndexer.ConflictMarkerScanLimitBytes,
            astralRuneCount: 250,
            useCrLfAndLeadingInvisible: true,
            unpairedSurrogateCount: 0);
        var acceptedUnpaired = BuildMarkerAtNormalizedBytePosition(
            FileIndexer.ConflictMarkerScanLimitBytes,
            astralRuneCount: 0,
            useCrLfAndLeadingInvisible: false,
            unpairedSurrogateCount: 1);

        AssertConflictParity(acceptedAscii, expectedLine: 2);
        AssertConflictParity(rejectedAscii, expectedLine: 0);
        AssertConflictParity(acceptedAstral, expectedLine: 2);
        AssertConflictParity(acceptedNormalizedFromCrLf, expectedLine: 2);
        AssertConflictParity(acceptedUnpaired, expectedLine: 2);
    }

    [Fact]
    public void ChunkSlices_TargetedLineBoundariesMatchIndependentOracle()
    {
        foreach (var lineCount in new[] { 1, 80, 81, 150, 151 })
        {
            foreach (var trailingNewline in new[] { false, true })
            {
                var content = string.Join('\n', Enumerable.Range(1, lineCount).Select(line => $"line {line}"));
                if (trailingNewline)
                    content += "\n";

                var expected = LegacyNormalizeAndAnalyze(content);
                var actual = FileContentLoader.NormalizeForIndexing(content);

                AssertFactsEqual(expected.Facts, actual.Facts, lineCount, content);
                AssertChunksEqual(
                    LegacySplitNormalized(23, content, expected.Facts),
                    ChunkSplitter.SplitNormalized(23, content, actual.Facts),
                    lineCount,
                    content);
                if (lineCount <= ChunkSplitter.ChunkSize)
                    Assert.Null(actual.Facts.ChunkSlices);
            }
        }
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void NormalizeForIndexing_OneHundredThousandShortLinesKeepsBoundaryAllocationBounded()
    {
        var content = string.Join('\n', Enumerable.Repeat("short", 100_000));
        _ = FileContentLoader.NormalizeForIndexing("warmup\ncontent");

        FileContentLoader.NormalizedIndexableContent normalized = default;
        var before = GC.GetAllocatedBytesForCurrentThread();
        normalized = FileContentLoader.NormalizeForIndexing(content);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(content, normalized.Content);
        Assert.Equal(100_000, normalized.Facts.LineCount);
        Assert.Equal(1_429, normalized.Facts.ChunkSlices?.Length);
        Assert.True(
            allocated < 256_000,
            $"Normalized facts allocated {allocated} bytes for 100,000 short lines.");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void NormalizeForIndexing_HighDecodeReplacementRatioDropsLineDetailsEarly()
    {
        var content = string.Join('\n', Enumerable.Repeat("\uFFFD", 100_000));
        _ = FileContentLoader.NormalizeForIndexing(
            "\uFFFD\n\uFFFD\n\uFFFD\n\uFFFD\n\uFFFD",
            discardReplacementLinesWhenNonUtf8Likely: true);

        FileContentLoader.NormalizedIndexableContent normalized = default;
        var before = GC.GetAllocatedBytesForCurrentThread();
        normalized = FileContentLoader.NormalizeForIndexing(
            content,
            discardReplacementLinesWhenNonUtf8Likely: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(content, normalized.Content);
        Assert.Equal(100_000, normalized.Facts.ReplacementCharacterCount);
        Assert.Null(normalized.Facts.ReplacementCharacterLines);
        Assert.True(
            allocated < 256_000,
            $"High-ratio replacement facts allocated {allocated} bytes for 100,000 lines.");
    }

    private static string CreateRandomContent(Random random)
    {
        var builder = new StringBuilder();
        var unitCount = random.Next(0, 220);
        for (var unitIndex = 0; unitIndex < unitCount; unitIndex++)
        {
            switch (random.Next(20))
            {
                case 0:
                    builder.Append('\n');
                    break;
                case 1:
                    builder.Append('\r');
                    break;
                case 2:
                    builder.Append("\r\n");
                    break;
                case 3:
                    builder.Append('\uFEFF');
                    break;
                case 4:
                    builder.Append('\u200B');
                    break;
                case 5:
                    builder.Append('\uFFFD');
                    break;
                case 6:
                    builder.Append("<<<<<<< HEAD");
                    break;
                case 7:
                    builder.Append(">>>>>>> branch");
                    break;
                case 8:
                    builder.Append('\u8A08');
                    break;
                case 9:
                    builder.Append('\u0301');
                    break;
                case 10:
                    builder.Append(char.ConvertFromUtf32(0x1F680));
                    break;
                case 11:
                    builder.Append('\uD800');
                    break;
                case 12:
                    builder.Append('\uDC00');
                    break;
                case 13:
                    builder.Append('_');
                    break;
                case 14:
                    builder.Append('-');
                    break;
                case 15:
                    builder.Append(' ');
                    break;
                default:
                    builder.Append((char)('a' + random.Next(26)));
                    break;
            }
        }

        return builder.ToString();
    }

    private static OracleResult LegacyNormalizeAndAnalyze(string source)
    {
        var builder = new StringBuilder(source.Length);
        var changed = false;
        var atLineStart = true;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (atLineStart && current is '\uFEFF' or '\u200B')
            {
                changed = true;
                continue;
            }

            if (current == '\r')
            {
                builder.Append('\n');
                changed = true;
                if (index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                atLineStart = true;
                continue;
            }

            builder.Append(current);
            atLineStart = current == '\n';
        }

        var normalized = changed ? builder.ToString() : source;
        return new OracleResult(normalized, changed, LegacyAnalyzeNormalized(normalized));
    }

    private static NormalizedContentFacts LegacyAnalyzeNormalized(string content)
    {
        if (content.Length == 0)
            return NormalizedContentFacts.Empty;

        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\n' && index + 1 < content.Length)
                lineStarts.Add(index + 1);
        }

        var firstOversizeLine = 0;
        var conflictMarkerLine = 0;
        var replacementCharacterCount = 0;
        List<int>? replacementLines = null;
        var firstOversizeFtsTokenLine = 0;
        var tokenLength = 0;
        var lineLength = 0;
        var lineNumber = 1;
        var atLineStart = true;
        var conflictByteCount = 0;
        var conflictScanComplete = false;

        for (var index = 0; index < content.Length;)
        {
            var current = content[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < content.Length
                && char.IsLowSurrogate(content[index + 1]))
            {
                var rune = new Rune(current, content[index + 1]);
                TrackConflictBytes(rune.Utf8SequenceLength, current, index);
                TrackFtsRune(rune);
                lineLength += 2;
                TrackOversizeLine();
                atLineStart = false;
                index += 2;
                continue;
            }

            var validRune = !char.IsSurrogate(current);
            var runeValue = validRune ? new Rune(current) : default;
            var utf8ByteLength = validRune
                ? current <= '\u007F' ? 1 : runeValue.Utf8SequenceLength
                : 3;
            TrackConflictBytes(utf8ByteLength, current, index);

            if (current == '\uFFFD')
            {
                replacementCharacterCount++;
                if (replacementLines is null || replacementLines[^1] != lineNumber)
                    (replacementLines ??= []).Add(lineNumber);
            }

            if (current == '\n')
            {
                tokenLength = 0;
                lineLength = 0;
                lineNumber++;
                atLineStart = true;
                index++;
                continue;
            }

            if (validRune)
                TrackFtsRune(runeValue);
            else
                tokenLength = 0;
            lineLength++;
            TrackOversizeLine();
            atLineStart = false;
            index++;
        }

        NormalizedChunkSlice[]? chunkSlices = null;
        if (firstOversizeLine == 0 && lineStarts.Count > ChunkSplitter.ChunkSize)
        {
            var slices = new List<NormalizedChunkSlice>();
            var effectiveContentLength = content.EndsWith('\n') ? content.Length - 1 : content.Length;
            for (var startLineIndex = 0; startLineIndex < lineStarts.Count; startLineIndex += ChunkSplitter.ChunkSize - ChunkSplitter.Overlap)
            {
                var endLineIndex = Math.Min(startLineIndex + ChunkSplitter.ChunkSize, lineStarts.Count);
                var startOffset = lineStarts[startLineIndex];
                var endOffset = endLineIndex < lineStarts.Count
                    ? lineStarts[endLineIndex] - 1
                    : effectiveContentLength;
                slices.Add(new NormalizedChunkSlice(startOffset, endOffset - startOffset));
                if (endLineIndex >= lineStarts.Count)
                    break;
            }

            chunkSlices = slices.ToArray();
        }

        return new NormalizedContentFacts(
            lineStarts.Count,
            firstOversizeLine,
            conflictMarkerLine,
            replacementCharacterCount,
            replacementLines?.ToArray(),
            firstOversizeFtsTokenLine,
            chunkSlices);

        void TrackConflictBytes(int byteLength, char firstChar, int sourceIndex)
        {
            if (conflictMarkerLine > 0 || conflictScanComplete)
                return;

            conflictByteCount += byteLength;
            if (conflictByteCount > FileIndexer.ConflictMarkerScanLimitBytes)
            {
                conflictScanComplete = true;
                return;
            }

            if (atLineStart
                && firstChar is '<' or '>'
                && (content.AsSpan(sourceIndex).StartsWith("<<<<<<<", StringComparison.Ordinal)
                    || content.AsSpan(sourceIndex).StartsWith(">>>>>>>", StringComparison.Ordinal)))
            {
                conflictMarkerLine = lineNumber;
            }
        }

        void TrackFtsRune(Rune rune)
        {
            if (firstOversizeFtsTokenLine > 0)
                return;

            var tokenRune = rune.Value == '_'
                || Rune.IsLetter(rune)
                || Rune.IsDigit(rune)
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark;
            if (!tokenRune)
            {
                tokenLength = 0;
                return;
            }

            tokenLength++;
            if (tokenLength > DbReader.FtsUnicode61MaxTokenLength)
                firstOversizeFtsTokenLine = lineNumber;
        }

        void TrackOversizeLine()
        {
            if (firstOversizeLine == 0 && lineLength > ChunkSplitter.MaxLineLength)
                firstOversizeLine = lineNumber;
        }
    }

    private static List<ExpectedChunk> LegacySplitNormalized(
        long fileId,
        string content,
        NormalizedContentFacts facts)
    {
        if (content.Length == 0 || facts.HasOversizeLine)
            return [];

        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\n' && index + 1 < content.Length)
                lineStarts.Add(index + 1);
        }

        var chunks = new List<ExpectedChunk>();
        var effectiveContentLength = content.EndsWith('\n') ? content.Length - 1 : content.Length;
        var chunkIndex = 0;
        for (var startLineIndex = 0; startLineIndex < lineStarts.Count; startLineIndex += ChunkSplitter.ChunkSize - ChunkSplitter.Overlap)
        {
            var endLineIndex = Math.Min(startLineIndex + ChunkSplitter.ChunkSize, lineStarts.Count);
            var startOffset = lineStarts[startLineIndex];
            var endOffset = endLineIndex < lineStarts.Count
                ? lineStarts[endLineIndex] - 1
                : effectiveContentLength;
            chunks.Add(new ExpectedChunk(
                fileId,
                chunkIndex,
                startLineIndex + 1,
                endLineIndex,
                content.Substring(startOffset, endOffset - startOffset)));
            chunkIndex++;
            if (endLineIndex >= lineStarts.Count)
                break;
        }

        return chunks;
    }

    private static void AssertFactsEqual(
        NormalizedContentFacts expected,
        NormalizedContentFacts actual,
        int caseIndex,
        string source)
    {
        var context = $"case {caseIndex}, source length {source.Length}";
        Assert.True(expected.LineCount == actual.LineCount, $"LineCount mismatch for {context}");
        Assert.True(expected.FirstOversizeLine == actual.FirstOversizeLine, $"FirstOversizeLine mismatch for {context}");
        Assert.True(expected.ConflictMarkerLine == actual.ConflictMarkerLine, $"ConflictMarkerLine mismatch for {context}");
        Assert.True(expected.ReplacementCharacterCount == actual.ReplacementCharacterCount, $"ReplacementCharacterCount mismatch for {context}");
        Assert.Equal(expected.ReplacementCharacterLines, actual.ReplacementCharacterLines);
        Assert.True(expected.FirstOversizeFtsTokenLine == actual.FirstOversizeFtsTokenLine, $"FirstOversizeFtsTokenLine mismatch for {context}");
        Assert.Equal(expected.ChunkSlices, actual.ChunkSlices);
    }

    private static void AssertChunksEqual(
        IReadOnlyList<ExpectedChunk> expected,
        IReadOnlyList<ChunkRecord> actual,
        int caseIndex,
        string source)
    {
        Assert.True(expected.Count == actual.Count, $"Chunk count mismatch for case {caseIndex}, source length {source.Length}");
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].FileId, actual[index].FileId);
            Assert.Equal(expected[index].ChunkIndex, actual[index].ChunkIndex);
            Assert.Equal(expected[index].StartLine, actual[index].StartLine);
            Assert.Equal(expected[index].EndLine, actual[index].EndLine);
            Assert.Equal(expected[index].Content, actual[index].Content);
        }
    }

    private static void AssertValidationIssueParity(
        string content,
        NormalizedContentFacts expectedFacts,
        NormalizedContentFacts actualFacts,
        int caseIndex,
        string source)
    {
        var rawBytes = Encoding.UTF8.GetBytes(content);
        var actual = FileIndexer.ValidateContent(
                "random.md",
                rawBytes,
                content,
                "markdown",
                FileContentInspection.Inspect(rawBytes),
                actualFacts)
            .Where(issue => issue.Kind is "conflict_markers" or "replacement_char" or "line_too_long" or "fts_token_too_long")
            .Select(IssueProjection.FromIssue)
            .ToArray();
        var expected = BuildExpectedValidationIssues(expectedFacts).ToArray();

        Assert.True(
            expected.SequenceEqual(actual),
            $"Validation issue order mismatch for case {caseIndex}, source length {source.Length}. Expected: {string.Join(" | ", expected)}; Actual: {string.Join(" | ", actual)}");
    }

    private static IEnumerable<IssueProjection> BuildExpectedValidationIssues(NormalizedContentFacts facts)
    {
        if (facts.ConflictMarkerLine > 0)
        {
            yield return new IssueProjection(
                "conflict_markers",
                facts.ConflictMarkerLine,
                "Git conflict markers detected; resolve the conflict before indexing symbols or references",
                null,
                null);
        }

        if (facts.ReplacementCharacterLines is { } replacementLines)
        {
            foreach (var line in replacementLines)
            {
                yield return new IssueProjection(
                    "replacement_char",
                    line,
                    $"U+FFFD source literal at line {line}",
                    FileIssue.OriginSourceLiteral,
                    FileIssue.SeverityInfo);
            }
        }

        if (facts.FirstOversizeLine > 0)
        {
            yield return new IssueProjection(
                "line_too_long",
                facts.FirstOversizeLine,
                $"Line {facts.FirstOversizeLine} exceeds {ChunkSplitter.MaxLineLength}-char cap; chunks/symbols/references skipped",
                null,
                null);
        }

        if (facts.FirstOversizeFtsTokenLine > 0)
        {
            yield return new IssueProjection(
                "fts_token_too_long",
                facts.FirstOversizeFtsTokenLine,
                $"Line {facts.FirstOversizeFtsTokenLine} contains an FTS5 unicode61 token longer than {DbReader.FtsUnicode61MaxTokenLength} characters; that token is not searchable through FTS",
                null,
                null);
        }
    }

    private static string BuildMarkerAtNormalizedBytePosition(
        int markerBytePosition,
        int astralRuneCount,
        bool useCrLfAndLeadingInvisible,
        int unpairedSurrogateCount)
    {
        var prefixByteCount = markerBytePosition - 1;
        var lineBreakByteCount = 1;
        var astralByteCount = astralRuneCount * 4;
        var unpairedByteCount = unpairedSurrogateCount * 3;
        var asciiCount = prefixByteCount - lineBreakByteCount - astralByteCount - unpairedByteCount;
        Assert.True(asciiCount >= 0);

        var builder = new StringBuilder();
        builder.Append(string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F680), astralRuneCount)));
        builder.Append('\uD800', unpairedSurrogateCount);
        builder.Append('a', asciiCount);
        builder.Append(useCrLfAndLeadingInvisible ? "\r\n\uFEFF" : "\n");
        builder.Append("<<<<<<< HEAD");
        return builder.ToString();
    }

    private static void AssertConflictParity(string source, int expectedLine)
    {
        var normalized = FileContentLoader.NormalizeForIndexing(source);
        Assert.Equal(expectedLine, normalized.Facts.ConflictMarkerLine);
        Assert.Equal(expectedLine > 0, FileIndexer.HasConflictMarkers(normalized.Content));

        var rawBytes = Encoding.UTF8.GetBytes(normalized.Content);
        var publicIssues = FileIndexer.ValidateContent("conflict.md", rawBytes, normalized.Content, "markdown");
        var conflictIssue = publicIssues.SingleOrDefault(issue => issue.Kind == "conflict_markers");
        Assert.Equal(expectedLine, conflictIssue?.Line ?? 0);
    }

    private readonly record struct OracleResult(
        string Content,
        bool Changed,
        NormalizedContentFacts Facts);

    private readonly record struct ExpectedChunk(
        long FileId,
        int ChunkIndex,
        int StartLine,
        int EndLine,
        string Content);

    private readonly record struct IssueProjection(
        string Kind,
        int Line,
        string Message,
        string? Origin,
        string? Severity)
    {
        internal static IssueProjection FromIssue(FileIssue issue)
            => new(issue.Kind, issue.Line, issue.Message, issue.Origin, issue.Severity);
    }
}
