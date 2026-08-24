using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class FileIndexerTests
{
    [Fact]
    public void FileHandleSnapshot_MatchesManagedHandleMetadataOnSupportedPlatforms()
    {
        if (!OperatingSystem.IsWindows()
            && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var project = TestProjectHelper.CreateTempProjectScope(
            "cdidx_file_handle_snapshot_native");
        var path = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "subject.bin",
            Encoding.UTF8.GetBytes("snapshot parity\n"));
        File.SetLastWriteTimeUtc(
            path,
            new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                .AddTicks(7_654_321));
        using var stream = BoundedFile.OpenReadForIndexContent(path);

        Assert.True(FileIndexer.TryGetFileHandleSnapshot(
            stream.SafeFileHandle,
            out var snapshot));
        Assert.True(FileIndexer.TryGetFileIdentity(
            stream.SafeFileHandle,
            out var identity));
        Assert.Equal(stream.Length, snapshot.Length);
        Assert.Equal(
            File.GetLastWriteTimeUtc(stream.SafeFileHandle),
            snapshot.ModifiedUtc);
        Assert.Equal(DateTimeKind.Utc, snapshot.ModifiedUtc.Kind);
        Assert.Equal<FileIndexer.FileIdentity?>(identity, snapshot.Identity);
    }

    [Fact]
    public void FileHandleSnapshot_UnixMetadataConversionsPreserveDeviceAndTimestampContracts()
    {
        Assert.Equal(
            0x100000523467UL,
            FileIndexer.EncodeLinuxDeviceId(major: 0x1234, minor: 0x0567));

        Assert.True(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: 0,
            nanoseconds: 99,
            out var belowOneTick));
        Assert.Equal(DateTime.UnixEpoch, belowOneTick);

        Assert.True(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: 0,
            nanoseconds: 100,
            out var oneTick));
        Assert.Equal(DateTime.UnixEpoch.AddTicks(1), oneTick);

        Assert.True(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: 0,
            nanoseconds: 999_999_999,
            out var finalNanosecond));
        Assert.Equal(DateTime.UnixEpoch.AddTicks(9_999_999), finalNanosecond);
        Assert.Equal(DateTimeKind.Utc, finalNanosecond.Kind);

        Assert.False(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: 0,
            nanoseconds: -1,
            out _));
        Assert.False(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: 0,
            nanoseconds: 1_000_000_000,
            out _));
        Assert.False(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: long.MinValue,
            nanoseconds: 0,
            out _));
        Assert.False(FileIndexer.TryCreateUnixModifiedUtc(
            seconds: long.MaxValue,
            nanoseconds: 0,
            out _));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("load-bound")]
    [InlineData("raw-chunks-negative")]
    [InlineData("raw-chunks-positive")]
    [InlineData("csharp-negative")]
    [InlineData("csharp-positive")]
    [InlineData("unknown-header")]
    [InlineData("unknown-coverage")]
    public void FileContentLoader_StableReadPathsCaptureExactlyTwoHandleSnapshots(
        string readPathShape)
    {
        using var project = TestProjectHelper.CreateTempProjectScope(
            "cdidx_file_handle_snapshot_counts");
        var source = readPathShape switch
        {
            "csharp-negative" => "public class C { int M() => 0; }\n",
            "csharp-positive" => "public interface I { static abstract int M(); }\n",
            "unknown-header" => "#!/bin/sh\necho ok\n",
            "unknown-coverage" => "plain unknown-language coverage\n",
            _ => "class Fixture { }\n",
        };
        var path = TestProjectHelper.WriteTextFile(project.Root, "subject", source);
        var snapshotCount = 0;
        var loader = new FileContentLoader(
            FileIndexer.DefaultMaxFileSizeBytes,
            resolveFileReadPath: readPathShape == "load-bound"
                ? static candidate => Path.GetFullPath(candidate)
                : null,
            bindReadToFileSystemIdentity: readPathShape == "load-bound",
            fileHandleSnapshotCapturedForTesting: () => snapshotCount++);

        switch (readPathShape)
        {
            case "load":
            case "load-bound":
                Assert.Equal(source, loader.Load(
                    path,
                    "subject",
                    "subject",
                    CancellationToken.None).Content);
                break;
            case "raw-chunks-negative":
                Assert.False(loader.RawByteChunksMayMatch(
                    path,
                    "subject",
                    static _ => false,
                    CancellationToken.None));
                break;
            case "raw-chunks-positive":
                Assert.True(loader.RawByteChunksMayMatch(
                    path,
                    "subject",
                    static bytes => bytes.Length > 0,
                    CancellationToken.None));
                break;
            case "csharp-negative":
            case "csharp-positive":
                var (candidate, requiresRetry) = loader
                    .LoadCSharpStaticInterfaceCandidateContentForPrepass(
                        path,
                        "subject",
                        "subject",
                        retryOnMutation: true,
                        includeQualifiedMemberAccessCandidate: false,
                        includeChecksum: false,
                        CancellationToken.None);
                Assert.False(requiresRetry);
                Assert.Equal(readPathShape == "csharp-positive", candidate is not null);
                break;
            case "unknown-header":
            case "unknown-coverage":
                var unknown = loader.ProbeUnknownLanguage(
                    path,
                    "subject",
                    "subject",
                    CancellationToken.None);
                Assert.Equal(
                    readPathShape == "unknown-header"
                        ? FileIndexer.FileProbeStatus.Supported
                        : FileIndexer.FileProbeStatus.Unsupported,
                    unknown.LanguageDetection.Status);
                Assert.Equal(
                    readPathShape == "unknown-coverage",
                    unknown.IsCoverageCandidate);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(readPathShape),
                    readPathShape,
                    null);
        }

        Assert.Equal(2, snapshotCount);
    }

    [Fact]
    public void FileContentLoader_Load_SameMetadataAtomicReplacementCapturesFourSnapshotsAndReadsCurrentPath()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(
            "cdidx_file_handle_snapshot_atomic_replace");
        const string originalSource = "class OldType { }\n";
        const string replacementSource = "class NewType { }\n";
        Assert.Equal(
            Encoding.UTF8.GetByteCount(originalSource),
            Encoding.UTF8.GetByteCount(replacementSource));
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "subject.cs",
            originalSource);
        var replacementPath = TestProjectHelper.WriteTextFile(
            project.Root,
            "replacement.tmp",
            replacementSource);
        var sharedModifiedUtc = DateTime.UtcNow.AddMinutes(-2);
        File.SetLastWriteTimeUtc(path, sharedModifiedUtc);
        File.SetLastWriteTimeUtc(replacementPath, sharedModifiedUtc);
        var openCount = 0;
        var snapshotCount = 0;
        var loader = new FileContentLoader(
            FileIndexer.DefaultMaxFileSizeBytes,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                var stream = BoundedFile.OpenReadForIndexContent(candidate);
                if (openCount == 1)
                {
                    File.Replace(
                        replacementPath,
                        path,
                        destinationBackupFileName: null);
                    File.SetLastWriteTimeUtc(path, sharedModifiedUtc);
                }

                return stream;
            },
            fileHandleSnapshotCapturedForTesting: () => snapshotCount++);

        var loaded = loader.Load(
            path,
            "subject.cs",
            "subject.cs",
            CancellationToken.None);

        Assert.Equal(replacementSource, loaded.Content);
        Assert.Equal(2, openCount);
        Assert.Equal(4, snapshotCount);
    }

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
    [InlineData("public sealed class Box<T> { }\npublic bool IsGreater(int x) => x > 0;", 2, false)]
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
    public void FileContentLoader_NormalizeForIndexing_LfOnlyAngleContentDetectsConflictMarkerWithoutRewritingContent()
    {
        var content = "public sealed class Box<T> { }\n<<<<<<< HEAD\npublic sealed class Other<T> { }\n";

        var normalized = FileContentLoader.NormalizeForIndexing(content);

        Assert.Same(content, normalized.Content);
        Assert.Equal(3, normalized.LineCount);
        Assert.False(normalized.HasOversizeLine);
        Assert.Equal(2, normalized.ConflictMarkerLine);
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
    public void FileContentLoader_Load_PreservesLexicalPathForAuthorizedOpen_Issue4829()
    {
        var tempDir = TestProjectHelper.CreateTempProject("codeindex_loader_lexical_path");
        try
        {
            var lexicalPath = Path.Combine(tempDir, "linked.cs");
            var resolvedPath = Path.Combine(tempDir, "target.cs");
            File.WriteAllText(resolvedPath, "class Target {}\n");
            string? openedPath = null;
            var loader = new FileContentLoader(
                FileIndexer.DefaultMaxFileSizeBytes,
                path =>
                {
                    openedPath = path;
                    return BoundedFile.OpenReadForIndexContent(resolvedPath);
                },
                _ => resolvedPath);

            var loaded = loader.Load(
                lexicalPath,
                "linked.cs",
                "linked.cs",
                CancellationToken.None);

            Assert.Equal(lexicalPath, openedPath);
            Assert.Equal("class Target {}\n", loaded.Content);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void FileContentLoader_Load_RejectsInternalLinkRetargetedDuringOpen_Issue4829()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("codeindex_loader_internal_retarget");
        var externalRoot = TestProjectHelper.CreateTempProject("codeindex_loader_external_retarget");
        try
        {
            var internalTarget = Path.Combine(projectRoot, "inside.py");
            var externalTarget = Path.Combine(externalRoot, "outside.py");
            var linkPath = Path.Combine(projectRoot, "alias.py");
            File.WriteAllText(internalTarget, "inside\n");
            File.WriteAllText(externalTarget, "secret\n");
            var sharedModifiedUtc = DateTime.UtcNow.AddMinutes(-1);
            File.SetLastWriteTimeUtc(internalTarget, sharedModifiedUtc);
            File.SetLastWriteTimeUtc(externalTarget, sharedModifiedUtc);
            try
            {
                File.CreateSymbolicLink(linkPath, internalTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var retargeted = false;
            var snapshotCount = 0;
            var indexer = new FileIndexer(
                projectRoot,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: FileIndexer.SymlinkPolicy.Internal,
                openReadForIndexContent: path =>
                {
                    if (!retargeted)
                    {
                        File.Delete(path);
                        File.CreateSymbolicLink(path, externalTarget);
                        retargeted = true;
                    }

                    return BoundedFile.OpenReadForIndexContent(path);
                },
                fileHandleSnapshotCapturedForTesting: () => snapshotCount++);

            var exception = Assert.Throws<IOException>(() => indexer.BuildRecord(linkPath));

            Assert.True(retargeted);
            Assert.Equal(1, snapshotCount);
            Assert.Contains("identity changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalRoot);
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
