namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static bool ContainsIndexBlockingNullByte(byte[] rawBytes)
        => FileContentLoader.ContainsIndexBlockingNullByte(rawBytes);

    internal static bool TryDetectUtf16Encoding(
        byte[] rawBytes,
        bool allowHeuristic,
        out bool bigEndian,
        out bool hasBom)
        => FileContentLoader.TryDetectUtf16Encoding(rawBytes, allowHeuristic, out bigEndian, out hasBom);

    internal sealed class BinaryFileSkippedException(
        string relativePath,
        long nullByteOffset,
        string message) : InvalidOperationException(message)
    {
        public string RelativePath { get; } = relativePath;
        public long NullByteOffset { get; } = nullByteOffset;
    }

    internal sealed class FileTooLargeSkippedException(
        string relativePath,
        long actualBytes,
        long limitBytes,
        string message) : InvalidOperationException(message)
    {
        public string RelativePath { get; } = relativePath;
        public long ActualBytes { get; } = actualBytes;
        public long LimitBytes { get; } = limitBytes;
    }

    /// <summary>
    /// Compute SHA256 checksum from file bytes after collapsing CRLF / CR to LF.
    /// Matches the line-ending normalization that BuildRecord applies to the decoded
    /// content so cross-OS clones (Windows with core.autocrlf=true vs Linux/macOS) of the
    /// same logical file produce the same checksum, while BOM bytes pass through unchanged
    /// so BOM add / remove still triggers incremental re-index. Streams through
    /// IncrementalHash with a fixed buffer so large files do not require an extra full
    /// normalized-byte copy. Closes #1544.
    /// CRLF / CR を LF に潰してから SHA256 を算出する。BuildRecord がデコード後 content に
    /// 適用するのと同じ改行正規化を生バイト側でも行うので、Windows (core.autocrlf=true) と
    /// Linux/macOS で同じ論理内容を clone した場合に checksum が一致する。BOM はそのまま
    /// ハッシュ対象に残るので、BOM 追加 / 削除はインクリメンタル再索引で引き続き検知される。
    /// IncrementalHash に固定バッファで投入する streaming 実装なので、大ファイルでも
    /// 正規化後バイトのフルコピーを追加で確保しない。Closes #1544.
    /// </summary>
    internal static string ComputeChecksum(byte[] bytes)
        => FileContentLoader.ComputeChecksum(bytes);

    internal static bool TryComputeChecksum(string filePath, long maxBytes, out string checksum)
        => FileContentLoader.TryComputeChecksum(filePath, maxBytes, out checksum);
}
