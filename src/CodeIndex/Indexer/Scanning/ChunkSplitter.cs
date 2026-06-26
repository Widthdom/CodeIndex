using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Splits file content into overlapping chunks for indexing.
/// ファイル内容を重複を持つチャンクに分割してインデックス用にする。
/// </summary>
public static class ChunkSplitter
{
    // Lines per chunk / 1チャンクあたりの行数
    private const int ChunkSize = 80;

    // Overlap with previous chunk / 前チャンクとの重複行数
    private const int Overlap = 10;

    // Per-line byte cap (in chars). Lines longer than this trigger the
    // oversize-line skip path: chunks/symbols/references for that file
    // are skipped and ValidateContent emits a `line_too_long` FileIssue
    // so downstream regex-based extraction cannot stall on minified or
    // base64-encoded payloads packed into a single physical line. Closes #1542.
    // 行ごとのバイト上限 (char 数)。これを超える行は oversize-line スキップ
    // 経路に入り、当該ファイルの chunks / symbols / references をスキップし、
    // ValidateContent から `line_too_long` FileIssue を発行する。これにより
    // 1 行に詰め込まれた minified / base64 ペイロードに対して下流の正規表現
    // 抽出が停止しないようにする。Closes #1542.
    public const int MaxLineLength = 64 * 1024;

    /// <summary>
    /// Returns true when <paramref name="content"/> contains any single line
    /// whose length exceeds <see cref="MaxLineLength"/>. Used by ChunkSplitter,
    /// SymbolExtractor, ReferenceExtractor, and ValidateContent to share a
    /// single cap so the indexer never feeds an unbounded line into regex-based
    /// extraction. Assumes `\n` is the only line separator (callers normalize
    /// CRLF first). Closes #1542.
    /// </summary>
    public static bool HasOversizeLine(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return false;
        int lineLen = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                lineLen = 0;
                continue;
            }
            lineLen++;
            if (lineLen > MaxLineLength)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Split file content into chunks.
    /// ファイル内容をチャンクに分割する。
    /// </summary>
    /// <param name="fileId">The file ID in the database / データベース上のファイルID</param>
    /// <param name="content">Full file content / ファイル全体の内容</param>
    /// <returns>List of chunk records / チャンクレコードのリスト</returns>
    public static List<ChunkRecord> Split(long fileId, string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        // Defensive CRLF normalization and line-leading invisible stripping —
        // BuildRecord already normalizes, but this method is public and may be
        // called directly with raw content. Mid-line markers are preserved.
        // 防御的 CRLF 正規化と行頭不可視文字剥離 — BuildRecord で正規化済みだが、
        // 本メソッドは public で直接呼ばれうる。行頭以外はそのまま残す。
        // Closes #183/#2117.
        content = FileIndexer.NormalizeContentForPrepass(content);
        // Re-check for empty after invisible/CRLF strip so marker-only input yields no chunks,
        // matching the no-chunks contract for empty files.
        // 不可視文字/CRLF剥離後に再度空判定し、markerのみの入力が空ファイルと同じく0チャンクになるようにする。
        if (content.Length == 0)
            return [];

        return SplitNormalized(fileId, content, HasOversizeLine(content));
    }

    internal static List<ChunkRecord> SplitNormalized(long fileId, string content, bool hasOversizeLine, int? lineCount = null)
    {
        if (string.IsNullOrEmpty(content))
            return [];
        // Skip oversize-line files (e.g. 1 MB minified `.min.js`, base64 blobs):
        // returning no chunks prevents a single multi-MB Content column from
        // being persisted, and parallel guards in SymbolExtractor / ReferenceExtractor
        // keep the regex-based extractors from stalling on the same input.
        // ValidateContent emits a `line_too_long` FileIssue so the skip is
        // observable through the existing issues channel. Closes #1542.
        // oversize-line ファイル (例: 1 MB minified .min.js、base64 ペイロード) を
        // スキップする。チャンクを返さないことで複数 MB の Content カラムが
        // 永続化されるのを防ぎ、SymbolExtractor / ReferenceExtractor 側の同等
        // ガードと合わせて、正規表現抽出が同じ入力で停止しないようにする。
        // スキップは ValidateContent からの `line_too_long` FileIssue として
        // 既存の issues 経路で観測できる。Closes #1542.
        if (hasOversizeLine)
            return [];

        return SplitNormalizedCore(fileId, content, lineCount);
    }

    private static List<ChunkRecord> SplitNormalizedCore(long fileId, string content, int? lineCount)
    {
        // Track line start offsets instead of materializing every line string. Large
        // source files can still be valid and under the file-size cap, and chunking
        // should only allocate the persisted chunk bodies rather than a duplicate
        // full-file string[] plus per-chunk slice arrays. A trailing newline is not
        // treated as an extra phantom line, matching the previous Split('\n') path.
        // 各行の string 配列を作らず、行開始 offset だけを保持する。大きな source file
        // でも file-size 上限内なら有効なため、chunking では永続化する chunk 本文以外に
        // ファイル全体分の string[] や chunk ごとの slice 配列を作らない。末尾改行を
        // 余分な空行として扱わない点は従来の Split('\n') 経路と同じ。
        var lineStarts = GetLineStartOffsets(content, lineCount);
        int step = ChunkSize - Overlap;
        var estimatedChunkCount = Math.Max(1, (lineStarts.Count + step - 1) / step);
        var chunks = new List<ChunkRecord>(estimatedChunkCount);
        int chunkIndex = 0;
        var effectiveContentLength = content.EndsWith('\n') ? content.Length - 1 : content.Length;

        for (int start = 0; start < lineStarts.Count; start += step)
        {
            int end = Math.Min(start + ChunkSize, lineStarts.Count);
            var startOffset = lineStarts[start];
            var endOffset = end < lineStarts.Count
                ? lineStarts[end] - 1
                : effectiveContentLength;
            var chunkContent = content.Substring(startOffset, endOffset - startOffset);

            chunks.Add(new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = chunkIndex,
                StartLine = start + 1,       // 1-based / 1始まり
                EndLine = end,               // 1-based inclusive / 1始まり（含む）
                Content = chunkContent,
            });

            chunkIndex++;

            // Stop if we've reached the end / 末尾に到達したら終了
            if (end >= lineStarts.Count)
                break;
        }

        return chunks;
    }

    private static List<int> GetLineStartOffsets(string content, int? lineCount)
    {
        var lineStarts = lineCount is > 0
            ? new List<int>(lineCount.Value)
            : [];
        lineStarts.Add(0);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n' && i + 1 < content.Length)
                lineStarts.Add(i + 1);
        }

        return lineStarts;
    }
}
