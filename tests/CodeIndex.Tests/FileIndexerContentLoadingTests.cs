using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class FileIndexerTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("a\nb\n", "a\nb\n", 2)]
    [InlineData("\n\n", "\n\n", 2)]
    [InlineData("a\r\n\uFEFFb\rc", "a\nb\nc", 3)]
    [InlineData("\uFEFF\u200B", "", 0)]
    [InlineData("a\n\uFEFF\u200Bb", "a\nb", 2)]
    public void FileContentLoader_Load_CanonicalizesContentAndLineCountBeforeChecksum(
        string rawContent,
        string expectedContent,
        int expectedLineCount)
    {
        var loaded = LoadFileContentForTest(Encoding.UTF8.GetBytes(rawContent));

        Assert.Equal(expectedContent, loaded.Content);
        Assert.Equal(expectedLineCount, loaded.LineCount);
        Assert.False(loaded.HasOversizeLine);
        Assert.Equal(FileIndexer.ComputeChecksum(Encoding.UTF8.GetBytes(expectedContent)), loaded.Checksum);
        Assert.Null(loaded.Warning);
    }

    [Theory]
    [InlineData("a", 1, false)]
    [InlineData("a\n", 1, false)]
    [InlineData("a\nb", 2, false)]
    [InlineData("\n\n", 2, false)]
    [InlineData(null, 1, true)]
    public void FileContentLoader_NormalizeForIndexing_LfOnlyFastPathPreservesLineSemantics(
        string? contentTemplate,
        int expectedLineCount,
        bool expectedOversizeLine)
    {
        var content = contentTemplate ?? new string('a', ChunkSplitter.MaxLineLength + 1);
        var normalized = FileContentLoader.NormalizeForIndexing(content);

        Assert.Same(content, normalized.Content);
        Assert.Equal(expectedLineCount, normalized.LineCount);
        Assert.Equal(expectedOversizeLine, normalized.HasOversizeLine);
        Assert.Equal(0, normalized.ConflictMarkerLine);
    }

    [Fact]
    public void FileContentLoader_Normalization_MidLineInvisibleCharactersStayOnFastPath()
    {
        var content = "a\uFEFFb\nc\u200Bd";

        var normalized = FileContentLoader.NormalizeForIndexing(content);
        var prepass = FileContentLoader.NormalizeContentForPrepass(content);

        Assert.Same(content, normalized.Content);
        Assert.Same(content, prepass);
        Assert.Equal(2, normalized.LineCount);
        Assert.False(normalized.HasOversizeLine);
        Assert.Equal(0, normalized.ConflictMarkerLine);
    }

    [Fact]
    public void FileContentLoader_Load_LfOnlyUtf8CanReuseRawChecksum()
    {
        var content = new string('a', 4097)
            + "\n"
            + char.ConvertFromUtf32(0x1F680)
            + new string('b', 4097)
            + "\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var normalized = FileContentLoader.NormalizeForIndexing(content);
        var inspection = FileContentInspection.Inspect(bytes);

        Assert.True(FileContentLoader.CanReuseRawBytesForNormalizedChecksum(content, null, inspection, normalized));

        var loaded = LoadFileContentForTest(bytes);
        var expected = RawSha256Hex(bytes);

        Assert.Equal(content, loaded.Content);
        Assert.Null(loaded.Warning);
        Assert.False(loaded.Inspection.IsUtf16);
        Assert.Equal(expected, loaded.Checksum);
    }

    [Fact]
    public void FileContentLoader_Load_AllowsConcurrentWriterShare_Issue4078()
    {
        var tempDir = TestProjectHelper.CreateTempProject("codeindex_loader_share");
        try
        {
            var path = Path.Combine(tempDir, "sample.cs");
            var bytes = Encoding.UTF8.GetBytes("class Sample {}\n");
            File.WriteAllBytes(path, bytes);

            using var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var loader = new FileContentLoader(FileIndexer.DefaultMaxFileSizeBytes);

            var loaded = loader.Load(path, "sample.cs", "sample.cs", CancellationToken.None);

            Assert.Equal("class Sample {}\n", loaded.Content);
            Assert.Equal(bytes.Length, loaded.SizeBytes);
            Assert.Equal(FileIndexer.ComputeChecksum(bytes), loaded.Checksum);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void FileContentLoader_Load_CarriesConflictMarkerLine()
    {
        var loaded = LoadFileContentForTest(Encoding.UTF8.GetBytes("a\r\n<<<<<<< HEAD\r\nb\r\n"));

        Assert.Equal("a\n<<<<<<< HEAD\nb\n", loaded.Content);
        Assert.Equal(2, loaded.ConflictMarkerLine);
    }

    [Fact]
    public void FileContentLoader_Load_CarriesConflictMarkerLineAfterLeadingInvisibleStripping()
    {
        var loaded = LoadFileContentForTest(Encoding.UTF8.GetBytes("a\n\uFEFF<<<<<<< HEAD\nb\n"));

        Assert.Equal("a\n<<<<<<< HEAD\nb\n", loaded.Content);
        Assert.Equal(2, loaded.ConflictMarkerLine);
    }

    [Fact]
    public void FileContentLoader_NormalizeForIndexing_DetectsConflictMarkerWithinNormalizedScanBudget()
    {
        var blankLineCount = FileIndexer.ConflictMarkerScanLimitBytes - 10;
        var content = new StringBuilder((blankLineCount * 2) + 32);
        for (var i = 0; i < blankLineCount; i++)
            content.Append("\r\n");
        content.Append("<<<<<<< HEAD\r\n");

        var normalized = FileContentLoader.NormalizeForIndexing(content.ToString());

        Assert.Equal(blankLineCount + 1, normalized.ConflictMarkerLine);
    }

    [Fact]
    public void FileContentLoader_IsGitLfsPointer_AcceptsAsciiPointerWithMixedLineEndings()
    {
        var pointer = GitLfsPointerText(new string('a', 64));

        Assert.True(FileContentLoader.IsGitLfsPointer(Encoding.ASCII.GetBytes(pointer)));
    }

    [Fact]
    public void FileContentLoader_IsGitLfsPointer_RejectsMalformedPointerLines()
    {
        var validPointer = GitLfsPointerText(new string('a', 64));
        var uppercaseHashPointer = GitLfsPointerText(new string('A', 64));

        Assert.False(FileContentLoader.IsGitLfsPointer(Encoding.ASCII.GetBytes(validPointer + "\n")));
        Assert.False(FileContentLoader.IsGitLfsPointer(Encoding.ASCII.GetBytes(uppercaseHashPointer)));
    }

    [Fact]
    public void FileContentLoader_Load_DetectsOversizeLineDuringCanonicalization()
    {
        var longLine = new string('a', ChunkSplitter.MaxLineLength + 1);
        var loaded = LoadFileContentForTest(Encoding.UTF8.GetBytes($"\uFEFF{longLine}\r\nb"));

        Assert.Equal($"{longLine}\nb", loaded.Content);
        Assert.Equal(2, loaded.LineCount);
        Assert.True(loaded.HasOversizeLine);
        Assert.Empty(ChunkSplitter.SplitNormalized(1, loaded.Content, loaded.HasOversizeLine));
        var issues = FileIndexer.ValidateContent(
            "sample.cs",
            loaded.RawBytes,
            loaded.Content,
            "csharp",
            loaded.Inspection,
            loaded.HasOversizeLine);
        Assert.Contains(issues, issue => issue.Kind == "line_too_long");
    }

    [Fact]
    public void ComputeChecksum_MixedLineEndings_NormalizesToLf()
    {
        // Direct-call coverage: mixed CRLF / CR / LF lines all collapse to LF before
        // hashing, matching the content-level normalization in BuildRecord. Pinning the
        // helper directly catches regressions even if BuildRecord shape changes.
        // Closes #1544.
        // direct call の網羅: CRLF / CR / LF が混在しても全て LF に畳まれてから
        // ハッシュ化され、BuildRecord 側の content 正規化と一致する。BuildRecord の
        // 形が変わっても helper 単体で回帰を検知できる。Closes #1544.
        var mixed = Encoding.UTF8.GetBytes("a\r\nb\rc\nd\r\n");
        var lfOnly = Encoding.UTF8.GetBytes("a\nb\nc\nd\n");
        Assert.Equal(
            FileIndexer.ComputeChecksum(lfOnly),
            FileIndexer.ComputeChecksum(mixed));
    }

    [Fact]
    public void ComputeChecksumFromNormalizedContent_MatchesUtf8ByteChecksumAcrossChunks()
    {
        var content = new string('a', 1023)
            + char.ConvertFromUtf32(0x1F680)
            + new string('b', 4097)
            + "\uD800";

        var expected = FileIndexer.ComputeChecksum(Encoding.UTF8.GetBytes(content));

        Assert.Equal(expected, FileContentLoader.ComputeChecksumFromNormalizedContent(content));
    }

    [Fact]
    public void ComputeChecksum_LongInputWithoutCr_MatchesRawByteSha256()
    {
        // For CR-free payloads (the common case), the checksum must still equal raw-byte
        // SHA256 — both as a correctness anchor for existing DBs whose stored checksums
        // were computed from raw bytes on LF-only sources, and to confirm the streaming
        // implementation handles inputs that span multiple AppendData chunks. Closes #1544.
        // CR を含まない payload (一般的なケース) では checksum が生バイト SHA256 と
        // 一致する必要がある。これは LF のみのソースで生バイトから算出された既存 DB の
        // checksum との互換性を保ち、また streaming 実装が AppendData の複数チャンクを
        // またぐ入力でも正しく動くことを示す。Closes #1544.
        var payload = new byte[16 * 1024];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 95 + 32); // printable ASCII (no CR / LF)
        var expected = RawSha256Hex(payload);
        Assert.Equal(expected, FileIndexer.ComputeChecksum(payload));
    }

    [Fact]
    public void ComputeChecksum_ReturnsLowercaseHex()
    {
        var checksum = FileIndexer.ComputeChecksum(Encoding.UTF8.GetBytes("ABC\n"));

        Assert.Equal(checksum.ToLowerInvariant(), checksum);
        Assert.DoesNotContain(checksum, c => c is >= 'A' and <= 'F');
    }

    private static string GitLfsPointerText(string hash)
        => "version https://git-lfs.github.com/spec/v1\r\n"
           + "ext-fixture metadata\r"
           + $"oid sha256:{hash}\n"
           + "size 123\n";
}
