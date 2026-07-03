using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    /// <summary>
    /// Build a FileRecord and return file content (avoids reading the file twice).
    /// FileRecordを構築しファイル内容も返す（二重読み込み防止）。
    /// </summary>
    public (FileRecord record, string content, string? warning) BuildRecord(string absolutePath, CancellationToken cancellationToken = default)
    {
        var loaded = BuildLoadedRecordWithRawBytes(absolutePath, cancellationToken);
        return (loaded.Record, loaded.Content, loaded.Warning);
    }

    /// <summary>
    /// Build a FileRecord and return both decoded content and raw bytes.
    /// Callers can run encoding validation without a second file read.
    /// FileRecordを構築し、デコード済み内容とraw bytesを返す。
    /// 呼び出し側は再読込なしでエンコーディング検証できる。
    /// </summary>
    public (FileRecord record, string content, byte[] rawBytes, string? warning) BuildRecordWithRawBytes(string absolutePath, CancellationToken cancellationToken = default)
    {
        var loaded = BuildLoadedRecordWithRawBytes(absolutePath, cancellationToken);
        return (loaded.Record, loaded.Content, loaded.RawBytes, loaded.Warning);
    }

    internal LoadedFileRecord BuildLoadedRecordWithRawBytes(string absolutePath, CancellationToken cancellationToken = default)
    {
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var relativePath = GetRelativePathFromProjectRoot(_projectRoot, absolutePath);
        return BuildLoadedRecordWithRawBytes(absolutePath, relativePath, cancellationToken);
    }

    internal LoadedFileRecord BuildLoadedRecordWithRawBytes(string absolutePath, string relativePath, CancellationToken cancellationToken = default)
        => BuildLoadedRecordWithRawBytes(absolutePath, relativePath, knownLanguage: null, cancellationToken);

    internal LoadedFileRecord BuildLoadedRecordWithRawBytes(
        string absolutePath,
        string relativePath,
        string? knownLanguage,
        CancellationToken cancellationToken = default)
        => BuildLoadedRecordWithRawBytes(absolutePath, relativePath, knownLanguage, detectGeneratedCode: true, cancellationToken);

    internal LoadedFileRecord BuildLoadedRecordWithRawBytes(
        string absolutePath,
        string relativePath,
        string? knownLanguage,
        bool detectGeneratedCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var indexability = GetFileIndexabilityForIndexing(absolutePath);
        if (indexability != FileProbeStatus.Supported)
            throw new InvalidOperationException("Only regular files can be indexed");

        var normalizedRelativePath = NormalizeIndexPath(relativePath);

        var loaded = _contentLoader.Load(
            absolutePath,
            normalizedRelativePath,
            relativePath,
            cancellationToken);
        var record = new FileRecord
        {
            Path = normalizedRelativePath,
            Lang = string.IsNullOrEmpty(knownLanguage)
                ? TryDetectLanguageForIndexing(absolutePath, loaded.Content).Language
                : knownLanguage,
            Size = loaded.SizeBytes,
            Lines = loaded.LineCount,
            Checksum = loaded.Checksum,
            Modified = loaded.ModifiedUtc,
            Generated = detectGeneratedCode && IsGeneratedCodeFile(normalizedRelativePath, loaded.Content),
        };

        return new LoadedFileRecord(
            record,
            loaded.Content,
            loaded.RawBytes,
            loaded.HasOversizeLine,
            loaded.ConflictMarkerLine,
            loaded.Warning,
            loaded.Inspection);
    }

    internal string LoadNormalizedContentForPrepass(string absolutePath, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var indexability = GetFileIndexabilityForIndexing(absolutePath);
        if (indexability != FileProbeStatus.Supported)
            throw new InvalidOperationException("Only regular files can be indexed");

        var normalizedRelativePath = NormalizeIndexPath(relativePath);
        return _contentLoader.LoadNormalizedContentForPrepass(
            absolutePath,
            normalizedRelativePath,
            relativePath,
            cancellationToken);
    }

    internal bool RawFileMayContainCSharpStaticInterfaceContract(
        string absolutePath,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var indexability = GetFileIndexabilityForIndexing(absolutePath);
        if (indexability != FileProbeStatus.Supported)
            throw new InvalidOperationException("Only regular files can be indexed");

        var normalizedRelativePath = NormalizeIndexPath(relativePath);
        var probe = CSharpStaticInterfacePrepass.CreateRawByteContractProbe();
        return _contentLoader.RawByteChunksMayMatch(
            absolutePath,
            normalizedRelativePath,
            probe.AppendAndCheck,
            cancellationToken);
    }

    public FileRecord BuildSkippedFileRecord(string absolutePath)
    {
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var relativePath = GetRelativePathFromProjectRoot(_projectRoot, absolutePath);
        return BuildSkippedFileRecord(absolutePath, relativePath);
    }

    internal FileRecord BuildSkippedFileRecord(string absolutePath, string relativePath)
        => BuildSkippedFileRecord(absolutePath, relativePath, knownLanguage: null);

    internal FileRecord BuildSkippedFileRecord(string absolutePath, string relativePath, string? knownLanguage)
    {
        if (!IsFilePathSyntaxIndexable(absolutePath))
            throw new InvalidOperationException("Cannot index a file path that contains NUL or control characters.");

        var normalizedRelativePath = NormalizeIndexPath(relativePath);
        var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
        var info = new FileInfo(ioPath);
        return new FileRecord
        {
            Path = normalizedRelativePath,
            Lang = string.IsNullOrEmpty(knownLanguage)
                ? TryDetectLanguageForIndexing(absolutePath).Language
                : knownLanguage,
            Size = info.Exists ? info.Length : 0,
            Lines = 0,
            Checksum = null,
            Modified = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            Generated = HasGeneratedCodeFileName(normalizedRelativePath),
        };
    }
}
