using System.Buffers;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodeIndex.Database;

internal sealed record ResourceFileMetadata(
    long Id,
    string Path,
    string? Lang,
    long Size,
    int Lines,
    string? Checksum,
    DateTime? Modified);

internal sealed class FindContinuationException(string reason, string message) : Exception(message)
{
    internal string Reason { get; } = reason;
}

public partial class DbReader
{
    internal const int MaxBoundedFileReadUtf8Bytes = 4 * 1024 * 1024;
    internal const int MaxBoundedFileReadLines = 10_000;
    internal const int MaxBoundedFileReadChunks = 256;
    internal const int MaxBoundedFileReadScannedUtf8Bytes = 32 * 1024 * 1024;
    internal const string BoundedResourceReadChunkEndIndexName = "idx_chunks_file_end_start_nonnull";
    internal const string BoundedResourceReadChunkIndexName = "idx_chunks_file_start_chunk_nonnull";
    internal const string LegacyBoundedResourceReadFileIndexName = "idx_chunks_file";
    internal const int MaxLegacyResourceReadSqliteVmSteps = 250_000;
    private const string EmptyIndexedContentChecksum =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly AsyncLocal<TimeSpan?> FindRegexMatchTimeoutOverride = new();
    private static readonly AsyncLocal<Action?> FindLineScannedOverride = new();
    private static readonly AsyncLocal<int?> BoundedFileReadScanByteLimitOverride = new();
    private static readonly AsyncLocal<int?> LegacyResourceReadSqliteVmStepLimitOverride = new();

    internal static readonly string BoundedResourceReadPredecessorSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {BoundedResourceReadChunkIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.start_line <= @startLine
        ORDER BY c.start_line DESC, c.chunk_index DESC
        LIMIT @chunkLimit
        """;

    internal static readonly string BoundedResourceReadEndSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {BoundedResourceReadChunkEndIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.end_line >= @startLine
        ORDER BY c.end_line, c.start_line, c.chunk_index
        LIMIT @chunkLimit
        """;

    internal static readonly string BoundedResourceReadForwardSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {BoundedResourceReadChunkIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.start_line > @startLine
          AND c.start_line <= @endLine
        ORDER BY c.start_line, c.chunk_index
        LIMIT @chunkLimit
        """;


    internal static TimeSpan? FindRegexMatchTimeoutForTesting
    {
        get => FindRegexMatchTimeoutOverride.Value;
        set => FindRegexMatchTimeoutOverride.Value = value;
    }

    internal static Action? FindLineScannedForTesting
    {
        get => FindLineScannedOverride.Value;
        set => FindLineScannedOverride.Value = value;
    }

    internal static int? BoundedFileReadScanByteLimitForTesting
    {
        get => BoundedFileReadScanByteLimitOverride.Value;
        set => BoundedFileReadScanByteLimitOverride.Value = value;
    }

    internal static int? LegacyResourceReadSqliteVmStepLimitForTesting
    {
        get => LegacyResourceReadSqliteVmStepLimitOverride.Value;
        set => LegacyResourceReadSqliteVmStepLimitOverride.Value = value;
    }

    private static Regex CreateFindRegexMatcher(string query, bool exact)
        => RegexRegistry.CreateFindRegex(query, exact, ResolveFindRegexMatchTimeout());

    private static TimeSpan ResolveFindRegexMatchTimeout()
        => FindRegexMatchTimeoutForTesting is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : BoundedRegex.DefaultMatchTimeout;

    public FindResults FindInFiles(string query, int limit, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, int before = 0, int after = 0, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, int? focusLine = null, int? focusColumn = null, bool regex = false, int? maxCandidateFiles = null, int? maxLinesScanned = null, int offset = 0, bool useIndexedLiteralCandidates = false, string? resumePath = null, int? resumeLine = null, int? resumeFileOrdinal = null, int? resumeMatchOrdinal = null, int? resumeByteOffset = null, bool captureContinuation = false, CancellationToken cancellationToken = default)
    {
        var scanRequest = new IndexedFindScanRequest(
            query,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            focusLine,
            focusColumn,
            regex,
            maxCandidateFiles,
            maxLinesScanned,
            useIndexedLiteralCandidates,
            new FindResumePosition(
                resumePath,
                resumeLine,
                resumeFileOrdinal,
                resumeMatchOrdinal,
                resumeByteOffset),
            cancellationToken);
        return new IndexedFindPipeline(this).Find(new IndexedFindListRequest(
            scanRequest,
            limit,
            before,
            after,
            maxLineWidth,
            offset,
            captureContinuation));
    }

    public int CountFindCandidateFiles(string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        using var fileCmd = _conn.CreateCommand();
        var sql = "SELECT COUNT(*) FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        fileCmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.AddText(fileCmd, "@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);

        return SqliteCommandPolicy.ReadInt32Scalar(fileCmd, "find candidate file count");
    }

    public FindCountResult CountFindInFiles(string query, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int? focusLine = null, int? focusColumn = null, bool regex = false, int? maxCandidateFiles = null, int? maxLinesScanned = null, bool useIndexedLiteralCandidates = false, string? resumePath = null, int? resumeLine = null, int? resumeFileOrdinal = null, int? resumeMatchOrdinal = null, int? resumeByteOffset = null, CancellationToken cancellationToken = default)
    {
        return new IndexedFindPipeline(this).Count(new IndexedFindScanRequest(
            query,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            focusLine,
            focusColumn,
            regex,
            maxCandidateFiles,
            maxLinesScanned,
            useIndexedLiteralCandidates,
            new FindResumePosition(
                resumePath,
                resumeLine,
                resumeFileOrdinal,
                resumeMatchOrdinal,
                resumeByteOffset),
            cancellationToken));
    }

    /// <summary>
    /// Reconstruct one indexed file into an ordered line map.
    /// 1つのインデックス済みファイルを順序付き行マップへ再構成する。
    /// </summary>
    private bool TryLoadIndexedFileLines(string path, out string? lang, out int totalLines, out SortedDictionary<int, string> lineMap, int? startLine = null, int? endLine = null)
    {
        lang = null;
        totalLines = 0;
        lineMap = new SortedDictionary<int, string>();
        if (string.IsNullOrWhiteSpace(path))
            return false;

        using var fileCmd = _conn.CreateCommand();
        fileCmd.CommandText = "SELECT lang, lines FROM files WHERE path = @path";
        SqliteCommandPolicy.Add(fileCmd, "@path", path);

        using var fileReader = fileCmd.ExecuteTrackedReader();
        if (!fileReader.TrackedRead())
            return false;

        lang = GetNullableString(fileReader, 0);
        totalLines = fileReader.GetInt32(1);

        using var chunkCmd = _conn.CreateCommand();
        var chunkSql = @"
            SELECT c.start_line, c.end_line, c.content
            FROM chunks c
            JOIN files f ON c.file_id = f.id
            WHERE f.path = @path";
        if (startLine.HasValue)
            chunkSql += " AND c.end_line >= @startLine";
        if (endLine.HasValue)
            chunkSql += " AND c.start_line <= @endLine";
        chunkSql += " ORDER BY c.start_line, c.chunk_index";
        chunkCmd.CommandText = chunkSql;
        SqliteCommandPolicy.Add(chunkCmd, "@path", path);
        if (startLine.HasValue)
            SqliteCommandPolicy.Add(chunkCmd, "@startLine", startLine.Value);
        if (endLine.HasValue)
            SqliteCommandPolicy.Add(chunkCmd, "@endLine", endLine.Value);

        using var chunkReader = chunkCmd.ExecuteTrackedReader();
        while (chunkReader.TrackedRead())
        {
            var chunkStartLine = chunkReader.GetInt32(0);
            var chunkEndLine = chunkReader.GetInt32(1);
            var chunkLines = chunkReader.GetString(2).Split('\n');
            var lineCount = chunkEndLine - chunkStartLine + 1;

            for (int i = 0; i < chunkLines.Length && i < lineCount; i++)
            {
                var absoluteLine = chunkStartLine + i;
                if (!lineMap.ContainsKey(absoluteLine))
                    lineMap[absoluteLine] = chunkLines[i];
            }
        }

        return lineMap.Count > 0;
    }

    /// <summary>
    /// Reconstruct a bounded indexed-source prefix while preserving absolute line positions for semantic classification.
    /// semantic 分類向けに絶対行位置を維持しながら、bounded な indexed-source prefix を再構成する。
    /// </summary>
    internal IReadOnlyList<string?> GetIndexedSourceLinesForSemanticTokens(
        string path,
        int maxLines,
        int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(path) || maxLines <= 0 || maxCharacters <= 0)
            return [];

        var totalLines = 0;
        using (var fileCmd = _conn.CreateCommand())
        {
            fileCmd.CommandText = "SELECT lines FROM files WHERE path = @path";
            SqliteCommandPolicy.Add(fileCmd, "@path", path);
            using var fileReader = fileCmd.ExecuteTrackedReader();
            if (!fileReader.TrackedRead())
                return [];
            totalLines = fileReader.GetInt32(0);
        }
        if (totalLines <= 0)
            return [];

        var sourceLines = new string?[Math.Min(totalLines, maxLines)];
        using var chunkCmd = _conn.CreateCommand();
        chunkCmd.CommandText = """
            SELECT c.start_line, c.end_line, substr(c.content, 1, @maxChunkCharacters)
            FROM chunks c
            JOIN files f ON c.file_id = f.id
            WHERE f.path = @path
              AND c.start_line <= @maxLines
            ORDER BY c.start_line, c.chunk_index
            """;
        SqliteCommandPolicy.Add(chunkCmd, "@path", path);
        SqliteCommandPolicy.Add(chunkCmd, "@maxLines", sourceLines.Length);
        SqliteCommandPolicy.Add(
            chunkCmd,
            "@maxChunkCharacters",
            checked(maxCharacters + 1));

        var remainingCharacters = maxCharacters;
        var lastContiguousLine = 0;
        var budgetExhausted = false;
        using var chunkReader = chunkCmd.ExecuteTrackedReader();
        while (!budgetExhausted && chunkReader.TrackedRead())
        {
            var chunkStartLine = chunkReader.GetInt32(0);
            var chunkEndLine = chunkReader.GetInt32(1);
            var chunkLines = chunkReader.GetString(2).Split('\n');
            var lineCount = Math.Min(
                chunkLines.Length,
                chunkEndLine - chunkStartLine + 1);
            for (var index = 0; index < lineCount; index++)
            {
                var absoluteLine = chunkStartLine + index;
                if (absoluteLine <= 0)
                    continue;
                if (absoluteLine > sourceLines.Length)
                {
                    budgetExhausted = true;
                    break;
                }
                if (sourceLines[absoluteLine - 1] != null)
                    continue;

                var content = chunkLines[index];
                var requiredCharacters = checked(content.Length + 1);
                if (requiredCharacters > remainingCharacters)
                {
                    budgetExhausted = true;
                    break;
                }

                sourceLines[absoluteLine - 1] = content;
                remainingCharacters -= requiredCharacters;
                while (lastContiguousLine < sourceLines.Length &&
                    sourceLines[lastContiguousLine] != null)
                {
                    lastContiguousLine++;
                }
            }
        }

        if (lastContiguousLine == 0)
            return [];
        if (lastContiguousLine < sourceLines.Length)
            Array.Resize(ref sourceLines, lastContiguousLine);
        return sourceLines;
    }

    /// <summary>
    /// Reconstruct a file excerpt from indexed chunks.
    /// インデックス済みチャンクからファイル抜粋を再構成する。
    /// </summary>
    public FileExcerptResult? GetExcerpt(
        string path,
        int startLine,
        int endLine,
        int before = 0,
        int after = 0,
        int? maxLineWidth = null,
        int? focusLine = null,
        int? focusColumn = null,
        int focusLength = 1)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (startLine <= 0)
            startLine = 1;
        if (endLine < startLine)
            endLine = startLine;
        if (before < 0)
            before = 0;
        if (after < 0)
            after = 0;
        var requestedStartLine = startLine;
        var requestedEndLine = endLine;
        // `endLine` + `after` (and `startLine` - `before`) come from untrusted MCP callers in
        // some entry points and can overflow int when endLine is near `int.MaxValue`. Clamp via
        // long intermediates so the subsequent `Math.Max/Min` sees the real window (#1528).
        // 一部の MCP 経路では `endLine` + `after`（および `startLine` - `before`）が信頼できない
        // 入力で、`int.MaxValue` 近傍の endLine だと int 加算で overflow する。long 中間で実窓を
        // 確定させてから clamp する（#1528）。
        var expandedStart = (int)Math.Max(1L, (long)startLine - before);
        var expandedEndCeiling = (int)Math.Min(int.MaxValue, (long)endLine + after);
        if (!TryLoadIndexedFileLines(path, out var lang, out var totalLines, out var lineMap, expandedStart, expandedEndCeiling))
            return null;
        var expandedEnd = Math.Min(totalLines, expandedEndCeiling);
        if (expandedStart > expandedEnd)
            return null;

        var selectedLines = Enumerable.Range(expandedStart, expandedEnd - expandedStart + 1)
            .Where(lineMap.ContainsKey)
            .ToList();

        if (selectedLines.Count == 0)
            return null;

        var contentLines = selectedLines.Select(line => lineMap[line]).ToList();
        var focusLineIndex = focusLine.HasValue ? selectedLines.IndexOf(focusLine.Value) : -1;
        if (focusLineIndex >= 0 && focusColumn.HasValue && focusColumn.Value > contentLines[focusLineIndex].Length)
            return null;
        var excerptLines = new string[contentLines.Count];
        var contentLineSpans = new List<ExcerptContentLineSpan>(contentLines.Count);
        var contentTruncated = false;
        for (var i = 0; i < contentLines.Count; i++)
        {
            var clampedLine = maxLineWidth.HasValue
                ? LineWidthFormatter.ClampLine(
                    contentLines[i],
                    maxLineWidth.Value,
                    i == focusLineIndex ? focusColumn : null,
                    focusLength)
                : ClampedTextResult.Unclamped(contentLines[i]);

            excerptLines[i] = clampedLine.Text;
            contentTruncated |= clampedLine.Truncated;
            var visibleLength = Math.Max(0, clampedLine.OriginalVisibleEndColumn - clampedLine.OriginalVisibleStartColumn + 1);
            contentLineSpans.Add(new ExcerptContentLineSpan
            {
                ContentLine = i + 1,
                SourceLine = selectedLines[i],
                ContentStartColumn = clampedLine.TextVisibleStartColumn,
                ContentEndColumn = clampedLine.TextVisibleStartColumn + visibleLength,
                SourceStartColumn = clampedLine.OriginalVisibleStartColumn,
                SourceEndColumn = clampedLine.OriginalVisibleStartColumn + visibleLength,
            });
        }

        return new FileExcerptResult
        {
            Path = path,
            Lang = lang,
            StartLine = selectedLines[0],
            EndLine = selectedLines[^1],
            RequestedStartLine = requestedStartLine,
            RequestedEndLine = requestedEndLine,
            EffectiveStartLine = selectedLines[0],
            EffectiveEndLine = selectedLines[^1],
            TotalLines = totalLines,
            Content = string.Join("\n", excerptLines),
            ContentTruncated = contentTruncated,
            ContentTruncationReasons = contentTruncated ? ["line_width_cap"] : [],
            ContentRecovery = contentTruncated
                ? FileExcerptResult.CreateRecoveryHint(path, selectedLines[0], selectedLines[^1])
                : null,
            ContentLineSpans = contentLineSpans,
        };
    }

    internal static bool IsAffirmativelyEmptyIndexedFile(int lines, string? checksum)
        => lines == 0
           && string.Equals(checksum, EmptyIndexedContentChecksum, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Load only the exact resource metadata needed by resources/read.
    /// resources/read に必要な完全一致resource metadataだけを取得する。
    /// </summary>
    internal ResourceFileMetadata? GetResourceFileMetadata(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        _cancellation.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        var generatedFilter = !IncludeGenerated && _fileColumns.Contains("generated")
            ? "AND COALESCE(f.generated, 0) = 0"
            : string.Empty;
        command.CommandText = $"""
            SELECT f.id, f.path, f.lang,
                   COALESCE(f.size, -1), COALESCE(f.lines, -1),
                   {GetFileColumnSql("checksum")} AS checksum,
                   {GetFileColumnSql("modified")} AS modified
            FROM files f
            WHERE f.path = @path
              {generatedFilter}
            LIMIT 1
            """;
        SqliteCommandPolicy.AddText(command, "@path", path);

        using var reader = command.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        var metadata = new ResourceFileMetadata(
            reader.GetInt64(0),
            reader.GetString(1),
            GetNullableString(reader, 2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            GetNullableString(reader, 5),
            GetNullableDateTime(reader, 6));
        _cancellation.ThrowIfCancellationRequested();
        return metadata;
    }

    /// <summary>
    /// Read a line range without materializing complete chunk strings, bounded by UTF-8 bytes and lines.
    /// チャンク文字列全体を実体化せず、UTF-8 byte数と行数で制限して行範囲を読む。
    /// </summary>
    /// <remarks>
    /// Continuation offsets are zero-based UTF-8 byte offsets from the start of an absolute, one-based line.
    /// continuation offset は、絶対1始まり行の先頭から数えた0始まりUTF-8 byte offset。
    /// </remarks>
    internal BoundedFileReadResult GetBoundedFileContent(
        ResourceFileMetadata file,
        int startLine,
        int endLine,
        int maxUtf8Bytes,
        int maxLines,
        int? continuationLine = null,
        int continuationByteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(file);
        var reader = new BoundedIndexedContentReader(
            _conn,
            _cancellation,
            _hasChunksTable,
            _chunkIndexes,
            BoundedFileReadScanByteLimitOverride.Value,
            LegacyResourceReadSqliteVmStepLimitOverride.Value);
        return reader.Read(new BoundedIndexedContentRequest(
            file,
            startLine,
            endLine,
            maxUtf8Bytes,
            maxLines,
            continuationLine,
            continuationByteOffset));
    }


    /// <summary>
    /// Return the length of the focused excerpt line when it is part of the reconstructed range.
    /// 抜粋として再構成される範囲内に focus line が含まれる場合、その行長を返す。
    /// </summary>
    public int? GetExcerptFocusLineLength(
        string path,
        int startLine,
        int endLine,
        int before = 0,
        int after = 0,
        int? focusLine = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !focusLine.HasValue)
            return null;

        if (startLine <= 0)
            startLine = 1;
        if (endLine < startLine)
            endLine = startLine;
        if (before < 0)
            before = 0;
        if (after < 0)
            after = 0;

        var requestedStart = (int)Math.Max(1L, (long)startLine - before);
        var requestedEndCeiling = (int)Math.Min(int.MaxValue, (long)endLine + after);
        if (!TryLoadIndexedFileLines(path, out _, out var totalLines, out var lineMap, requestedStart, requestedEndCeiling))
            return null;
        var requestedEnd = Math.Min(totalLines, requestedEndCeiling);

        if (focusLine.Value < requestedStart || focusLine.Value > requestedEnd)
            return null;

        return lineMap.TryGetValue(focusLine.Value, out var line) ? line.Length : null;
    }

    /// <summary>
    /// Get one indexed file by exact path.
    /// 完全一致パスでインデックス済みファイルを1件取得する。
    /// </summary>
    public FileResult? GetFileByPath(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            WITH file_match AS (
                SELECT f.id, f.path, f.lang, f.size, f.lines,
                       {GetFileColumnSql("checksum")} AS checksum,
                       {GetFileColumnSql("modified")} AS modified,
                       {GetFileColumnSql("indexed_at")} AS indexed_at
                FROM files f
                WHERE f.path = @path
            )
            SELECT f.path, f.lang, f.size, f.lines,
                   COALESCE(symbol_counts.symbol_count, 0) AS symbol_count,
                   {FileReferenceCountSql} AS reference_count,
                   f.checksum,
                   f.modified,
                   f.indexed_at
            FROM file_match f
            LEFT JOIN (
                SELECT s.file_id, COUNT(*) AS symbol_count
                FROM symbols s
                JOIN file_match file_set ON file_set.id = s.file_id
                GROUP BY s.file_id
            ) AS symbol_counts ON symbol_counts.file_id = f.id
            {BuildFileReferenceCountJoinSql("file_match")}";
        SqliteCommandPolicy.Add(cmd, "@path", path);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        return new FileResult
        {
            Path = reader.GetString(0),
            Lang = GetNullableString(reader, 1),
            Size = reader.GetInt64(2),
            Lines = reader.GetInt32(3),
            SymbolCount = reader.GetInt32(4),
            ReferenceCount = reader.GetInt32(5),
            Checksum = GetNullableString(reader, 6),
            Modified = GetNullableDateTime(reader, 7),
            IndexedAt = GetNullableDateTime(reader, 8),
        };
    }

    /// <summary>
    /// Delegate to RepoMapBuilder for repo-level overview generation.
    /// RepoMapBuilderに委譲してリポジトリ俯瞰情報を生成する。
    /// </summary>
    public RepoMapResult GetRepoMap(int limit = 10, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, double minEntrypointConfidence = 0, int? moduleDepth = null, int? oversizedLineThreshold = null, long? oversizedByteThreshold = null, int offset = 0, string? requestedCollection = null, bool summaryProjection = false)
    {
        var builder = new RepoMapBuilder(_conn, _fileColumns, _hasReferencesTable, GetIndexedPathComparer);
        return builder.Build(
            limit,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            minEntrypointConfidence,
            GetWorkspaceFreshness,
            moduleDepth,
            oversizedLineThreshold,
            oversizedByteThreshold,
            offset,
            requestedCollection,
            summaryProjection);
    }

    private long ExecuteScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private StatusDbPragmaSettings GetDbPragmaSettings() => new()
    {
        JournalMode = ExecuteScalarString("PRAGMA journal_mode"),
        Synchronous = NormalizeSynchronousMode(ExecuteScalarString("PRAGMA synchronous")),
        WalAutocheckpoint = ExecuteNullableLong("PRAGMA wal_autocheckpoint"),
        BusyTimeoutMs = ExecuteNullableLong("PRAGMA busy_timeout"),
        PageCount = ExecuteNullableLong("PRAGMA page_count"),
        FreelistCount = ExecuteNullableLong("PRAGMA freelist_count"),
        PageSize = ExecuteNullableLong("PRAGMA page_size"),
        AutoVacuum = ExecuteNullableLong("PRAGMA auto_vacuum"),
    };

    private long? TryGetDatabaseFileSize()
    {
        var path = TryGetLocalDatabasePath();
        if (path == null)
            return null;

        return TryGetFileSize(path, missingValue: null);
    }

    private long? TryGetWalFileSize()
        => TryGetDatabaseSidecarFileSize("-wal");

    private long? TryGetShmFileSize()
        => TryGetDatabaseSidecarFileSize("-shm");

    private long? TryGetDatabaseSidecarFileSize(string suffix)
    {
        var path = TryGetLocalDatabasePath();
        if (path == null)
            return null;

        return TryGetFileSize(path + suffix, missingValue: 0);
    }

    private string? TryGetLocalDatabasePath()
    {
        var path = _conn.DataSource;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? DbConnectionFactory.TryGetLocalPath(path)
            : path;
    }

    private static long? TryGetFileSize(string path, long? missingValue)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : missingValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private StatusLastIndexRun? GetLastIndexRun()
    {
        var mode = TryGetMetaStringInternal(DbContext.LastIndexRunModeMetaKey);
        var startedAt = ParseMetaDateTime(TryGetMetaStringInternal(DbContext.LastIndexRunStartedAtMetaKey));
        var durationMs = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunDurationMsMetaKey));
        var filesScanned = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunFilesScannedMetaKey));
        var filesSkipped = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunFilesSkippedMetaKey));
        var parseErrors = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunParseErrorsMetaKey));
        var bytesRead = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunBytesReadMetaKey));
        var bytesReadSkippedFileCount = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunBytesReadSkippedFileCountMetaKey));
        var bytesReadIncomplete = ParseMetaBool(TryGetMetaStringInternal(DbContext.LastIndexRunBytesReadIncompleteMetaKey));
        var rowsUpserted = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunRowsUpsertedMetaKey));
        var rowsDeleted = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunRowsDeletedMetaKey));
        var peakMemoryMb = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunPeakMemoryMbMetaKey));
        var diagnostics = ParseMetaStringList(TryGetMetaStringInternal(DbContext.LastIndexRunDiagnosticsMetaKey));
        var diagnosticCount = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunDiagnosticCountMetaKey));
        var diagnosticsTruncated = ParseMetaBool(TryGetMetaStringInternal(DbContext.LastIndexRunDiagnosticsTruncatedMetaKey));
        var referenceExtractionCapHits = ParseReferenceExtractionCapHits(
            TryGetMetaStringInternal(DbContext.LastIndexRunReferenceExtractionCapHitsMetaKey));
        var rebuildReclaim = ParseRebuildReclaim(
            TryGetMetaStringInternal(DbContext.LastIndexRunRebuildReclaimMetaKey));
        if (mode == null && startedAt == null && durationMs == null && filesScanned == null && filesSkipped == null
            && parseErrors == null && bytesRead == null && bytesReadSkippedFileCount == null && bytesReadIncomplete == null
            && rowsUpserted == null && rowsDeleted == null && peakMemoryMb == null
            && diagnostics == null && diagnosticCount == null && diagnosticsTruncated == null
            && referenceExtractionCapHits == null && rebuildReclaim == null)
        {
            return null;
        }

        return new StatusLastIndexRun
        {
            Mode = mode,
            StartedAt = startedAt,
            DurationMs = durationMs,
            FilesScanned = filesScanned,
            FilesSkipped = filesSkipped,
            ParseErrors = parseErrors,
            BytesRead = bytesRead,
            BytesReadSkippedFileCount = bytesReadSkippedFileCount,
            BytesReadIncomplete = bytesReadIncomplete,
            RowsUpserted = rowsUpserted,
            RowsDeleted = rowsDeleted,
            PeakMemoryMb = peakMemoryMb,
            Diagnostics = diagnostics,
            DiagnosticCount = diagnosticCount,
            DiagnosticsTruncated = diagnosticsTruncated,
            ReferenceExtractionCapHits = referenceExtractionCapHits,
            RebuildReclaim = rebuildReclaim,
        };
    }

    private static StatusRebuildReclaim? ParseRebuildReclaim(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize(json, StatusMetadataJsonContext.Default.StatusRebuildReclaim);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReferenceExtractionCapHitSummary? ParseReferenceExtractionCapHits(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize(json, StatusMetadataJsonContext.Default.ReferenceExtractionCapHitSummary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private StatusFailedOrPartialIndexRun? GetLastFailedOrPartialIndexRun(bool batchInProgress)
    {
        var status = TryGetMetaStringInternal(DbContext.LastFailedIndexRunStatusMetaKey);
        var mode = TryGetMetaStringInternal(DbContext.LastFailedIndexRunModeMetaKey);
        var startedAt = ParseMetaDateTime(TryGetMetaStringInternal(DbContext.LastFailedIndexRunStartedAtMetaKey));
        var durationMs = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastFailedIndexRunDurationMsMetaKey));
        var filesProcessed = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastFailedIndexRunFilesProcessedMetaKey));
        var filesTotal = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastFailedIndexRunFilesTotalMetaKey));
        var errorCode = TryGetMetaStringInternal(DbContext.LastFailedIndexRunErrorCodeMetaKey);
        var reason = TryGetMetaStringInternal(DbContext.LastFailedIndexRunReasonMetaKey);
        var progressPersisted = ParseMetaBool(TryGetMetaStringInternal(DbContext.LastFailedIndexRunProgressPersistedMetaKey));
        var recoveryHint = TryGetMetaStringInternal(DbContext.LastFailedIndexRunRecoveryHintMetaKey);
        var fileErrors = ParseStatusIndexFileErrors(TryGetMetaStringInternal(DbContext.LastFailedIndexRunFileErrorsMetaKey));
        if (status == null && mode == null && startedAt == null && durationMs == null && filesProcessed == null
            && filesTotal == null && errorCode == null && reason == null && progressPersisted == null && recoveryHint == null
            && fileErrors == null)
        {
            return batchInProgress
                ? new StatusFailedOrPartialIndexRun
                {
                    Status = "partial",
                    Reason = "batch_in_progress",
                }
                : null;
        }

        return new StatusFailedOrPartialIndexRun
        {
            Status = status,
            Mode = mode,
            StartedAt = startedAt,
            DurationMs = durationMs,
            FilesProcessed = filesProcessed,
            FilesTotal = filesTotal,
            ErrorCode = errorCode,
            Reason = reason,
            ProgressPersisted = progressPersisted,
            RecoveryHint = recoveryHint,
            FileErrors = fileErrors,
        };
    }

    private string? ExecuteScalarString(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    private long? ExecuteNullableLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? null : Convert.ToInt64(raw);
    }

    private static string? NormalizeSynchronousMode(string? value) => value switch
    {
        "0" => "OFF",
        "1" => "NORMAL",
        "2" => "FULL",
        "3" => "EXTRA",
        null or "" => value,
        _ => value.ToUpperInvariant(),
    };

    /// <summary>
    /// Return a lightweight freshness hint for zero-result MCP responses.
    /// 0件MCPレスポンス向けの軽量な鮮度ヒントを返す。
    /// </summary>
    public FreshnessHintResult GetFreshnessHint()
    {
        var freshnessAvailable = _fileColumns.Contains("indexed_at");
        var fileCount = ExecuteScalar("SELECT COUNT(*) FROM files");
        var indexedAt = ExecuteNullableDateTime(
            freshnessAvailable ? "SELECT MAX(indexed_at) FROM files" : null);
        return new FreshnessHintResult
        {
            FileCount = fileCount,
            IndexedAt = indexedAt,
            FreshnessAvailable = freshnessAvailable,
            FreshnessDegradedReason = freshnessAvailable ? null : "files.indexed_at column missing in this index",
        };
    }

    internal (string Identity, string? StableAt) GetPaginationGeneration()
    {
        var freshness = GetFreshnessHint();
        var indexedHeadSha = TryGetMetaStringInternal(DbContext.IndexedHeadShaMetaKey);
        var indexedHeadTimestamp = TryGetMetaStringInternal(DbContext.IndexedHeadTimestampMetaKey);
        // files_resource_generation_* triggers advance this persisted counter inside the
        // same transaction as every indexed-file insert/update/delete. Unlike indexed_at,
        // it cannot collapse multiple committed indexing batches into the same second.
        // files_resource_generation_* trigger は indexed-file の insert/update/delete と
        // 同じ transaction 内でこの永続 counter を進めるため、同一秒内の複数 commit
        // batch が indexed_at 上で同一 generation に潰れることを防ぐ。
        var committedWriteGeneration =
            TryGetMetaStringInternal(DbContext.ResourceListGenerationMetaKey);
        var indexedAt = freshness.IndexedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var stableAt = indexedHeadTimestamp ?? indexedAt;
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{indexedHeadSha ?? "no-indexed-head"}\n"
            + $"{indexedHeadTimestamp ?? "no-indexed-head-timestamp"}\n"
            + $"{indexedAt ?? "no-indexed-at"}\n"
            + $"{freshness.FileCount}\n"
            + $"{committedWriteGeneration ?? "no-committed-write-generation"}");
        return (identity, stableAt);
    }

    internal string GetSymbolSelectorGenerationIdentity()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{GetPaginationGeneration().Identity}\n"
            + $"{TryGetMetaStringInternal(DbContext.IndexedProjectRootMetaKey) ?? "no-indexed-project-root"}");

    internal string GetFoldPaginationGenerationIdentity()
    {
        var userVersion = ExecuteScalar("PRAGMA user_version");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"stored-fold-ready-bit:{userVersion & DbContext.FoldReadyFlag}\n"
            + $"stored-fold-version:{TryGetMetaStringInternal("fold_key_version") ?? "no-fold-key-version"}\n"
            + $"stored-fold-fingerprint:{TryGetMetaStringInternal("fold_key_fingerprint") ?? "no-fold-key-fingerprint"}\n"
            + $"effective-fold-ready:{(_foldReady ? "1" : "0")}\n"
            + $"runtime-fold-version:{NameFold.Version}\n"
            + $"runtime-fold-fingerprint:{NameFold.Fingerprint()}\n"
            + $"fold-backfill-pending:{TryGetMetaStringInternal(DbWriter.FoldBackfillGraphRefreshPendingMetaKey) ?? "no-fold-backfill-pending"}");
    }

    internal string GetIssuePaginationGenerationIdentity()
    {
        var userVersion = ExecuteScalar("PRAGMA user_version");
        var issuesDataCurrent = _hasIssuesTable
            && (userVersion & DbContext.IssuesReadyFlag) != 0;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"stored-issues-ready-bit:{userVersion & DbContext.IssuesReadyFlag}\n"
            + $"issues-table-available:{(_hasIssuesPhysicalTable ? "1" : "0")}\n"
            + $"effective-issues-ready:{(issuesDataCurrent ? "1" : "0")}");
    }

    /// <summary>
    /// Re-check issue readiness after the caller has established its read snapshot.
    /// The constructor's cached readiness remains a conservative prerequisite so a reader
    /// opened while issues were degraded cannot promote itself without being reopened.
    /// 呼び出し側が read snapshot を確立した後で issue readiness を再確認する。
    /// degraded 時に開いた reader は再オープンなしに昇格させない。
    /// </summary>
    internal bool IsIssueDataCurrentInSnapshot()
    {
        var userVersion = ExecuteScalar("PRAGMA user_version");
        return _hasIssuesTable
            && (userVersion & DbContext.IssuesReadyFlag) != 0;
    }

    private (DateTime? IndexedAt, DateTime? LatestModified) GetWorkspaceFreshness()
    {
        return (
            ExecuteNullableDateTime(_fileColumns.Contains("indexed_at") ? "SELECT MAX(indexed_at) FROM files" : null),
            ExecuteNullableDateTime(_fileColumns.Contains("modified") ? "SELECT MAX(modified) FROM files" : null)
        );
    }

    private StringComparer GetIndexedPathComparer()
    {
        var pathCaseSensitive = ParseMetaBool(TryGetMetaStringInternal(DbContext.WorkspacePathCaseSensitiveMetaKey));
        return pathCaseSensitive == true
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    private DateTime? ExecuteNullableDateTime(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        if (value == null || value is DBNull)
            return null;

        return ParseDateTimeValue(value);
    }

    // Inline codeindex_meta lookup that returns null when the table or row is missing.
    // Matches the static TryGetMetaString in DbReader.cs but uses the live connection so
    // callers inside an open transaction read against the same snapshot. Issue #1509.
    // codeindex_meta の inline 参照。テーブル欠落・行欠落は null 返却。
    private string? TryGetMetaStringInternal(string key)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            SqliteCommandPolicy.Add(cmd, "@key", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // Parse an ISO-8601 timestamp persisted via SetMeta. Offsetless legacy values are
    // treated as UTC, while explicit offsets are honored before normalizing to UTC JSON.
    // SetMeta で保存された ISO-8601 timestamp を読む。offset の無い legacy 値は UTC 扱いし、
    // 明示 offset は尊重してから UTC の JSON 値へ正規化する。
    private static DateTimeOffset? ParseMetaDateTimeOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return value.ToUniversalTime();
        }
        return null;
    }

    private static DateTime? ParseMetaDateTime(string? raw)
        => ParseMetaDateTimeOffset(raw)?.UtcDateTime;

    // Parse a "true" / "false" meta string into a nullable bool. Returns null when the raw
    // value is missing or unrecognized so a partial / legacy stamp degrades gracefully.
    // bool.TryParse already accepts case-insensitive "True"/"False" via Boolean.TryParse,
    // matching how SetMeta(WorkspacePathCaseSensitiveMetaKey, value.ToString()) writes it.
    // "true"/"false" meta 文字列を nullable bool に。欠落・不明値は null フォールバック。
    private static bool? ParseMetaBool(string? raw)
        => string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var value)
            ? null
            : value;

    private static List<string>? ParseMetaStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return JsonStringListCodec.Deserialize(raw);
    }

    private static List<StatusIndexFileError>? ParseStatusIndexFileErrors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize(raw, StatusMetadataJsonContext.Default.ListStatusIndexFileError);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ParseMetaLong(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            || !long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            ? null
            : value;
}
