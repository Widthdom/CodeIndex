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
    private const int LegacyResourceReadProgressOperations = 100;
    private const int BoundedFileReadBufferSize = 4 * 1024;
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

    private static readonly string LegacyBoundedResourceReadPredecessorSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {LegacyBoundedResourceReadFileIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.start_line <= @startLine
        ORDER BY c.start_line DESC, c.chunk_index DESC
        LIMIT @chunkLimit
        """;

    private static readonly string LegacyBoundedResourceReadEndSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {LegacyBoundedResourceReadFileIndexName}
        WHERE c.file_id = @fileId
          AND c.content IS NOT NULL
          AND c.end_line >= @startLine
        ORDER BY c.end_line, c.start_line, c.chunk_index
        LIMIT @chunkLimit
        """;

    private static readonly string LegacyBoundedResourceReadForwardSql = $"""
        SELECT c.id, c.start_line, c.end_line, c.chunk_index
        FROM chunks c INDEXED BY {LegacyBoundedResourceReadFileIndexName}
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

    public FindResults FindInFiles(string query, int limit, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, int before = 0, int after = 0, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, int? focusLine = null, int? focusColumn = null, bool regex = false, int? maxCandidateFiles = null, int? maxLinesScanned = null, int offset = 0, bool useIndexedLiteralCandidates = false, string? resumePath = null, int? resumeLine = null, int? resumeFileOrdinal = null, int? resumeMatchOrdinal = null, int? resumeByteOffset = null, bool captureContinuation = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return new FindResults([], new FindScanSummary(0, 0, 0));
        ValidateFindContinuation(
            resumePath,
            resumeLine,
            resumeFileOrdinal,
            resumeMatchOrdinal,
            resumeByteOffset);

        before = Math.Max(0, before);
        after = Math.Max(0, after);
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);
        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regexMatcher = regex
            ? CreateFindRegexMatcher(query, exact)
            : null;

        var searchPlan = CreateFindSearchPlan(query, exact, regex, useIndexedLiteralCandidates);
        using var fileCmd = CreateFindFileCommand(
            searchPlan,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests);

        var candidateFiles = CountFindCandidateFiles(lang, pathPatterns, excludePathPatterns, excludeTests);
        var filesScanned = 0;
        var linesScanned = 0;
        var truncated = false;
        string? truncationReason = null;
        string? nextPath = null;
        int? nextLine = null;
        int? nextFileOrdinal = null;
        int? nextMatchOrdinal = null;
        int? nextByteOffset = null;
        var resultLimitReached = false;
        var matchesSkipped = 0;
        offset = Math.Max(0, offset);
        var resumePending = resumePath is not null;
        var resumeMatchPending = resumeMatchOrdinal.HasValue;
        var candidateFileOrdinal = -1;
        var results = new List<FileFindResult>();
        using var fileReader = fileCmd.ExecuteTrackedReader();
        while (fileReader.TrackedRead())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!captureContinuation && results.Count >= limit)
                break;
            candidateFileOrdinal++;
            var fileId = fileReader.GetInt64(0);
            var path = fileReader.GetString(1);
            var fileLang = GetNullableString(fileReader, 2);
            var totalLines = fileReader.GetInt32(3);
            if (resumePending)
            {
                if (resumeFileOrdinal.HasValue)
                {
                    if (candidateFileOrdinal < resumeFileOrdinal.Value)
                        continue;
                    if (candidateFileOrdinal != resumeFileOrdinal.Value
                        || !string.Equals(path, resumePath, StringComparison.Ordinal))
                    {
                        throw new FindContinuationException(
                            "cursor_malformed",
                            "find cursor file position does not match the current candidate order.");
                    }
                }
                else if (!string.Equals(path, resumePath, StringComparison.Ordinal))
                {
                    continue;
                }
                if (resumeLine > Math.Max(1, totalLines))
                {
                    throw new FindContinuationException(
                        "cursor_malformed",
                        "find cursor line position exceeds the selected file.");
                }
                resumePending = false;
            }
            if (maxCandidateFiles.HasValue && filesScanned >= maxCandidateFiles.Value)
            {
                truncated = true;
                truncationReason ??= "candidate_file_limit";
                nextPath = path;
                nextLine = 1;
                nextFileOrdinal = candidateFileOrdinal;
                nextByteOffset = 0;
                break;
            }

            var firstEligibleLine = string.Equals(path, resumePath, StringComparison.Ordinal)
                ? Math.Max(1, resumeLine ?? 1)
                : 1;
            var firstContextLine = Math.Max(1, firstEligibleLine - before);
            filesScanned++;
            if (totalLines <= 0)
                continue;

            var searchQuery = exact && !regex ? ExactSourceSearchNormalizer.Normalize(query, fileLang) : query;
            var pendingMatches = new Queue<PendingFileFindMatch>();
            var snippetWindow = new Queue<IndexedLine>();
            var snippetLinesByNumber = new Dictionary<int, string>();
            var acceptedMatches = results.Count;
            var stopScanning = false;

            foreach (var indexedLine in EnumerateIndexedFileLines(fileId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (indexedLine.Number > totalLines)
                    break;
                if (indexedLine.Number < firstContextLine)
                    continue;
                var eligibleForMatch = indexedLine.Number >= firstEligibleLine;
                if (maxLinesScanned.HasValue && linesScanned >= maxLinesScanned.Value)
                {
                    truncated = true;
                    truncationReason ??= "line_scan_limit";
                    if (nextPath is null)
                    {
                        nextPath = path;
                        nextLine = indexedLine.Number;
                        nextFileOrdinal = candidateFileOrdinal;
                        nextByteOffset = 0;
                    }
                    stopScanning = true;
                    break;
                }
                if (eligibleForMatch)
                {
                    linesScanned++;
                    FindLineScannedForTesting?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                AddLineToFindWindow(indexedLine, snippetWindow, snippetLinesByNumber);

                if (!resultLimitReached
                    && (matchesSkipped < offset
                        || captureContinuation && acceptedMatches <= limit
                        || !captureContinuation && acceptedMatches < limit)
                    && eligibleForMatch
                    && (!focusLine.HasValue || indexedLine.Number == focusLine.Value))
                {
                    var matchOrdinal = 0;
                    foreach (var lineMatch in EnumerateFindLineMatches(
                        indexedLine.Text,
                        fileLang,
                        searchQuery,
                        comparison,
                        regexMatcher,
                        exact && !regex,
                        focusColumn))
                    {
                        if (resumeMatchPending
                            && indexedLine.Number == firstEligibleLine)
                        {
                            if (matchOrdinal < resumeMatchOrdinal!.Value)
                            {
                                matchOrdinal++;
                                continue;
                            }
                            var resumeBoundaryByteOffset = Encoding.UTF8.GetByteCount(
                                indexedLine.Text.AsSpan(0, lineMatch.Column));
                            if (matchOrdinal != resumeMatchOrdinal.Value
                                || resumeBoundaryByteOffset != resumeByteOffset)
                            {
                                throw new FindContinuationException(
                                    "cursor_malformed",
                                    "find cursor match position is not a record boundary for the current query.");
                            }
                            resumeMatchPending = false;
                        }

                        if (matchesSkipped < offset)
                        {
                            matchesSkipped++;
                            matchOrdinal++;
                            continue;
                        }
                        if (acceptedMatches >= limit)
                        {
                            var byteOffset = Encoding.UTF8.GetByteCount(
                                indexedLine.Text.AsSpan(0, lineMatch.Column));
                            nextPath = path;
                            nextLine = indexedLine.Number;
                            nextFileOrdinal = candidateFileOrdinal;
                            nextMatchOrdinal = matchOrdinal;
                            nextByteOffset = byteOffset;
                            resultLimitReached = true;
                            stopScanning = true;
                            break;
                        }

                        pendingMatches.Enqueue(new PendingFileFindMatch(
                            indexedLine.Number,
                            lineMatch.Column,
                            lineMatch.Length,
                            Math.Max(1, indexedLine.Number - before),
                            Math.Min(totalLines, indexedLine.Number + after)));
                        acceptedMatches++;
                        matchOrdinal++;
                    }
                }

                FlushReadyFindMatches(
                    path,
                    fileLang,
                    pendingMatches,
                    snippetLinesByNumber,
                    results,
                    maxLineWidth,
                    indexedLine.Number);
                PruneFindWindow(indexedLine.Number, before, pendingMatches, snippetWindow, snippetLinesByNumber);

                if ((stopScanning || !captureContinuation && results.Count >= limit)
                    && pendingMatches.Count == 0)
                    break;
            }

            FlushReadyFindMatches(
                path,
                fileLang,
                pendingMatches,
                snippetLinesByNumber,
                results,
                maxLineWidth,
                int.MaxValue);
            if (stopScanning || !captureContinuation && results.Count >= limit)
                break;
        }
        if (resumePending || resumeMatchPending)
        {
            throw new FindContinuationException(
                "cursor_malformed",
                "find cursor position does not exist in the current result sequence.");
        }

        var capReached = truncated;
        return new FindResults(
            results,
            new FindScanSummary(
                candidateFiles,
                filesScanned,
                linesScanned,
                truncated,
                capReached,
                TimedOut: false,
                truncationReason,
                maxCandidateFiles,
                maxLinesScanned,
                searchPlan.Strategy,
                searchPlan.FallbackReason,
                nextPath,
                nextLine,
                nextFileOrdinal,
                nextMatchOrdinal,
                nextByteOffset,
                resultLimitReached));
    }

    private static void ValidateFindContinuation(
        string? resumePath,
        int? resumeLine,
        int? resumeFileOrdinal,
        int? resumeMatchOrdinal,
        int? resumeByteOffset)
    {
        if (resumePath is null)
        {
            if (resumeLine.HasValue
                || resumeFileOrdinal.HasValue
                || resumeMatchOrdinal.HasValue
                || resumeByteOffset.HasValue)
            {
                throw new FindContinuationException(
                    "cursor_malformed",
                    "find cursor continuation fields require a resume path.");
            }
            return;
        }

        if (!resumeLine.HasValue
            || resumeLine.Value <= 0
            || resumeFileOrdinal is < 0
            || resumeMatchOrdinal is < 0
            || resumeByteOffset is < 0
            || resumeMatchOrdinal.HasValue && !resumeByteOffset.HasValue
            || !resumeMatchOrdinal.HasValue && resumeByteOffset is not (null or 0))
        {
            throw new FindContinuationException(
                "cursor_malformed",
                "find cursor continuation position is invalid.");
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query))
            return new FindCountResult(0, 0, new FindScanSummary(0, 0, 0));
        ValidateFindContinuation(
            resumePath,
            resumeLine,
            resumeFileOrdinal,
            resumeMatchOrdinal,
            resumeByteOffset);
        if (resumeMatchOrdinal.HasValue || resumeByteOffset is not (null or 0))
        {
            throw new FindContinuationException(
                "cursor_malformed",
                "find count cursor must resume at a line boundary.");
        }

        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regexMatcher = regex
            ? CreateFindRegexMatcher(query, exact)
            : null;
        var searchPlan = CreateFindSearchPlan(query, exact, regex, useIndexedLiteralCandidates);
        using var fileCmd = CreateFindFileCommand(
            searchPlan,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests);

        var candidateFiles = CountFindCandidateFiles(lang, pathPatterns, excludePathPatterns, excludeTests);
        var filesScanned = 0;
        var linesScanned = 0;
        var truncated = false;
        string? truncationReason = null;
        string? nextPath = null;
        int? nextLine = null;
        int? nextFileOrdinal = null;
        var count = 0;
        var fileCount = 0;
        var resumePending = resumePath is not null;
        var candidateFileOrdinal = -1;
        using var fileReader = fileCmd.ExecuteTrackedReader();
        while (fileReader.TrackedRead())
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateFileOrdinal++;
            var path = fileReader.GetString(1);
            if (resumePending)
            {
                if (resumeFileOrdinal.HasValue)
                {
                    if (candidateFileOrdinal < resumeFileOrdinal.Value)
                        continue;
                    if (candidateFileOrdinal != resumeFileOrdinal.Value
                        || !string.Equals(path, resumePath, StringComparison.Ordinal))
                    {
                        throw new FindContinuationException(
                            "cursor_malformed",
                            "find count cursor file position does not match the current candidate order.");
                    }
                }
                else if (!string.Equals(path, resumePath, StringComparison.Ordinal))
                {
                    continue;
                }
                if (resumeLine > Math.Max(1, fileReader.GetInt32(3)))
                {
                    throw new FindContinuationException(
                        "cursor_malformed",
                        "find count cursor line position exceeds the selected file.");
                }
                resumePending = false;
            }
            if (maxCandidateFiles.HasValue && filesScanned >= maxCandidateFiles.Value)
            {
                truncated = true;
                truncationReason ??= "candidate_file_limit";
                nextPath = path;
                nextLine = 1;
                nextFileOrdinal = candidateFileOrdinal;
                break;
            }

            var fileId = fileReader.GetInt64(0);
            var fileLang = GetNullableString(fileReader, 2);
            var totalLines = fileReader.GetInt32(3);
            var firstEligibleLine = string.Equals(path, resumePath, StringComparison.Ordinal)
                ? Math.Max(1, resumeLine ?? 1)
                : 1;
            filesScanned++;
            if (totalLines <= 0)
                continue;

            var searchQuery = exact && !regex ? ExactSourceSearchNormalizer.Normalize(query, fileLang) : query;
            var fileMatches = 0;
            var stopScanning = false;
            foreach (var indexedLine in EnumerateIndexedFileLines(fileId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (indexedLine.Number > totalLines)
                    break;
                if (indexedLine.Number < firstEligibleLine)
                    continue;
                if (maxLinesScanned.HasValue && linesScanned >= maxLinesScanned.Value)
                {
                    truncated = true;
                    truncationReason ??= "line_scan_limit";
                    nextPath = path;
                    nextLine = indexedLine.Number;
                    nextFileOrdinal = candidateFileOrdinal;
                    stopScanning = true;
                    break;
                }
                linesScanned++;

                if (focusLine.HasValue && indexedLine.Number != focusLine.Value)
                    continue;

                foreach (var _ in EnumerateFindLineMatches(
                    indexedLine.Text,
                    fileLang,
                    searchQuery,
                    comparison,
                    regexMatcher,
                    exact && !regex,
                    focusColumn))
                {
                    fileMatches++;
                }
            }

            if (fileMatches > 0)
            {
                count += fileMatches;
                fileCount++;
            }
            if (stopScanning)
                break;
        }
        if (resumePending)
        {
            throw new FindContinuationException(
                "cursor_malformed",
                "find count cursor position does not exist in the current candidate sequence.");
        }

        var capReached = truncated;
        return new FindCountResult(
            count,
            fileCount,
            new FindScanSummary(
                candidateFiles,
                filesScanned,
                linesScanned,
                truncated,
                capReached,
                TimedOut: false,
                truncationReason,
                maxCandidateFiles,
                maxLinesScanned,
                searchPlan.Strategy,
                searchPlan.FallbackReason,
                nextPath,
                nextLine,
                nextFileOrdinal,
                NextByteOffset: nextPath is null ? null : 0));
    }

    private readonly record struct FindSearchPlan(
        string Strategy,
        string? FallbackReason,
        string? TrigramMatchExpression);

    private readonly record struct IndexedLine(int Number, string Text);

    private readonly record struct FindLineMatch(int Column, int Length);

    private readonly record struct PendingFileFindMatch(int LineNumber, int Column, int Length, int SnippetStart, int SnippetEnd);

    private FindSearchPlan CreateFindSearchPlan(
        string query,
        bool exact,
        bool regex,
        bool useIndexedLiteralCandidates)
    {
        if (!useIndexedLiteralCandidates)
            return new FindSearchPlan("line_scan", null, null);
        if (regex)
            return new FindSearchPlan("line_scan", "regex", null);
        if (exact)
            return new FindSearchPlan("line_scan", "exact_source_normalization", null);
        if (query.Length < 3)
            return new FindSearchPlan("line_scan", "query_too_short", null);
        if (query.Any(character => character < ' ' || character > '~'))
            return new FindSearchPlan("line_scan", "unsupported_query_characters", null);
        if (!HasTable(DbContext.FtsChunksTrigramTableName))
            return new FindSearchPlan("line_scan", "trigram_index_unavailable", null);
        if (DbWriter.IsFtsBulkLoadMarkerSet(GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey)))
            return new FindSearchPlan("line_scan", "trigram_index_rebuilding", null);
        if (!HasAllTrigramFtsSyncTriggers())
            return new FindSearchPlan("line_scan", "trigram_index_unsynchronized", null);

        var phrase = "\"" + query.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return new FindSearchPlan("indexed_trigram", null, phrase);
    }

    private bool HasAllTrigramFtsSyncTriggers()
    {
        using var command = _conn.CreateCommand();
        command.CommandText = DbContext.CountFtsChunksTrigramSyncTriggersSql;
        return SqliteCommandPolicy.ReadInt32Scalar(
            command,
            "find trigram FTS synchronization trigger count") == 3;
    }

    private SqliteCommand CreateFindFileCommand(
        FindSearchPlan searchPlan,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var fileCmd = _conn.CreateCommand();
        string sql;
        if (searchPlan.TrigramMatchExpression != null)
        {
            sql = $"""
                SELECT f.id, f.path, f.lang, f.lines
                FROM (
                    SELECT DISTINCT find_chunk.file_id
                    FROM {DbContext.FtsChunksTrigramTableName}
                    JOIN chunks find_chunk ON find_chunk.id = {DbContext.FtsChunksTrigramTableName}.rowid
                    WHERE {DbContext.FtsChunksTrigramTableName} MATCH @trigramQuery
                ) find_candidate
                JOIN files f ON f.id = find_candidate.file_id
                WHERE 1=1
                """;
        }
        else
        {
            sql = "SELECT f.id, f.path, f.lang, f.lines FROM files f WHERE 1=1";
        }
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {PathBucketOrder}, f.path";
        fileCmd.CommandText = sql;
        if (searchPlan.TrigramMatchExpression != null)
            SqliteCommandPolicy.AddText(fileCmd, "@trigramQuery", searchPlan.TrigramMatchExpression);
        if (lang != null)
            SqliteCommandPolicy.Add(fileCmd, "@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);
        return fileCmd;
    }

    private IEnumerable<IndexedLine> EnumerateIndexedFileLines(long fileId)
    {
        using var chunkCmd = _conn.CreateCommand();
        chunkCmd.CommandText = @"
            SELECT c.start_line, c.end_line, c.content
            FROM chunks c
            WHERE c.file_id = @fileId
            ORDER BY c.start_line, c.chunk_index";
        SqliteCommandPolicy.Add(chunkCmd, "@fileId", fileId);

        var lastEmittedLine = 0;
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
                if (absoluteLine <= lastEmittedLine)
                    continue;

                lastEmittedLine = absoluteLine;
                yield return new IndexedLine(absoluteLine, chunkLines[i]);
            }
        }
    }

    private static IEnumerable<FindLineMatch> EnumerateFindLineMatches(
        string lineText,
        string? fileLang,
        string searchQuery,
        StringComparison comparison,
        Regex? regexMatcher,
        bool normalizeExactSource,
        int? focusColumn)
    {
        int[]? rawIndexMap = null;
        var searchLine = normalizeExactSource
            ? ExactSourceSearchNormalizer.Normalize(lineText, fileLang, out rawIndexMap)
            : lineText;

        if (regexMatcher != null)
        {
            foreach (Match match in regexMatcher.Matches(searchLine))
            {
                if (!match.Success)
                    continue;
                if (TryCreateFindLineMatch(match.Index, match.Length, rawIndexMap, focusColumn, out var lineMatch))
                    yield return lineMatch;
            }

            yield break;
        }

        for (int searchStart = 0; searchStart < searchLine.Length;)
        {
            var matchColumn = searchLine.IndexOf(searchQuery, searchStart, comparison);
            if (matchColumn < 0)
                break;

            if (TryCreateFindLineMatch(matchColumn, searchQuery.Length, rawIndexMap, focusColumn, out var lineMatch))
                yield return lineMatch;
            searchStart = matchColumn + 1;
        }
    }

    private static bool TryCreateFindLineMatch(int matchColumn, int matchLength, int[]? rawIndexMap, int? focusColumn, out FindLineMatch lineMatch)
    {
        var rawMatchColumn = rawIndexMap == null ? matchColumn : rawIndexMap[matchColumn];
        var rawMatchLength = matchLength;
        if (rawIndexMap != null && matchLength > 0)
        {
            var rawMatchEndIndex = rawIndexMap[matchColumn + matchLength - 1];
            rawMatchLength = rawMatchEndIndex - rawMatchColumn + 1;
        }

        lineMatch = new FindLineMatch(rawMatchColumn, rawMatchLength);
        var focusEndColumn = rawMatchColumn + Math.Max(1, rawMatchLength);
        return !focusColumn.HasValue || (focusColumn.Value >= rawMatchColumn + 1 && focusColumn.Value <= focusEndColumn);
    }

    private static Regex CreateFindRegexMatcher(string query, bool exact)
        => RegexRegistry.CreateFindRegex(query, exact, ResolveFindRegexMatchTimeout());

    private static TimeSpan ResolveFindRegexMatchTimeout()
        => FindRegexMatchTimeoutForTesting is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : BoundedRegex.DefaultMatchTimeout;

    private static void AddLineToFindWindow(IndexedLine indexedLine, Queue<IndexedLine> snippetWindow, Dictionary<int, string> snippetLinesByNumber)
    {
        snippetWindow.Enqueue(indexedLine);
        snippetLinesByNumber[indexedLine.Number] = indexedLine.Text;
    }

    private static void FlushReadyFindMatches(
        string path,
        string? fileLang,
        Queue<PendingFileFindMatch> pendingMatches,
        Dictionary<int, string> snippetLinesByNumber,
        List<FileFindResult> results,
        int maxLineWidth,
        int availableThroughLine)
    {
        while (pendingMatches.Count > 0 && pendingMatches.Peek().SnippetEnd <= availableThroughLine)
        {
            var pending = pendingMatches.Dequeue();
            var snippetLineNumbers = Enumerable.Range(pending.SnippetStart, pending.SnippetEnd - pending.SnippetStart + 1)
                .Where(snippetLinesByNumber.ContainsKey)
                .ToList();
            if (snippetLineNumbers.Count == 0)
                continue;

            var snippetLines = snippetLineNumbers.Select(line => snippetLinesByNumber[line]).ToList();
            var (snippet, truncationContext) = ClampFindSnippetLines(
                snippetLines,
                maxLineWidth,
                focusLineIndex: snippetLineNumbers.IndexOf(pending.LineNumber),
                focusColumn: pending.Column + 1,
                focusLength: pending.Length);
            var matchLine = snippetLinesByNumber[pending.LineNumber];

            results.Add(new FileFindResult
            {
                Path = path,
                Lang = fileLang,
                Line = pending.LineNumber,
                Column = pending.Column + 1,
                Length = pending.Length,
                OriginalLineLength = matchLine.Length,
                StartLine = snippetLineNumbers[0],
                EndLine = snippetLineNumbers[^1],
                Snippet = snippet,
                SnippetTruncated = truncationContext.LineCount > 0,
                SnippetTruncationContext = truncationContext,
            });
        }
    }

    private static (string Text, FileFindSnippetTruncationContext Context) ClampFindSnippetLines(
        IReadOnlyList<string> lines,
        int maxLineWidth,
        int focusLineIndex,
        int focusColumn,
        int focusLength)
    {
        if (lines.Count == 0)
            return (string.Empty, new FileFindSnippetTruncationContext());

        var output = new string[lines.Count];
        var truncatedCharCounts = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var clamped = i == focusLineIndex
                ? LineWidthFormatter.ClampLine(lines[i], maxLineWidth, focusColumn, focusLength)
                : LineWidthFormatter.ClampLine(lines[i], maxLineWidth);
            output[i] = clamped.Text;
            if (clamped.Truncated)
                truncatedCharCounts.Add(clamped.TruncatedCharCount);
        }

        return (
            string.Join('\n', output),
            new FileFindSnippetTruncationContext
            {
                LineCount = truncatedCharCounts.Count,
                CharCounts = truncatedCharCounts,
                TotalChars = truncatedCharCounts.Sum(),
                Reason = truncatedCharCounts.Count > 0 ? "line_width" : null,
            });
    }

    private static void PruneFindWindow(
        int currentLine,
        int before,
        Queue<PendingFileFindMatch> pendingMatches,
        Queue<IndexedLine> snippetWindow,
        Dictionary<int, string> snippetLinesByNumber)
    {
        var minLineToKeep = currentLine - before;
        if (pendingMatches.Count > 0)
            minLineToKeep = Math.Min(minLineToKeep, pendingMatches.Peek().SnippetStart);

        while (snippetWindow.Count > 0 && snippetWindow.Peek().Number < minLineToKeep)
        {
            var removed = snippetWindow.Dequeue();
            snippetLinesByNumber.Remove(removed.Number);
        }
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
        var path = file.Path;
        if (string.IsNullOrWhiteSpace(path))
            return new BoundedFileReadResult { Status = BoundedFileReadStatus.FileNotFound };
        if (maxUtf8Bytes is <= 0 or > MaxBoundedFileReadUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes), maxUtf8Bytes,
                $"UTF-8 byte budget must be between 1 and {MaxBoundedFileReadUtf8Bytes}.");
        if (maxLines is <= 0 or > MaxBoundedFileReadLines)
            throw new ArgumentOutOfRangeException(nameof(maxLines), maxLines,
                $"Line budget must be between 1 and {MaxBoundedFileReadLines}.");
        if (continuationByteOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(continuationByteOffset), continuationByteOffset,
                "Continuation byte offset must be non-negative.");
        if (!continuationLine.HasValue && continuationByteOffset != 0)
            return new BoundedFileReadResult { Status = BoundedFileReadStatus.InvalidContinuation, Path = path };

        startLine = Math.Max(1, startLine);
        endLine = Math.Max(startLine, endLine);
        var nextLine = continuationLine ?? startLine;
        if (nextLine < startLine || nextLine > endLine)
            return new BoundedFileReadResult { Status = BoundedFileReadStatus.InvalidContinuation, Path = path };

        var fileId = file.Id;
        var lang = file.Lang;
        var totalLines = file.Lines;
        var fileSize = file.Size;

        BoundedFileReadResult CreateResult(BoundedFileReadStatus status, string? failureReason = null) => new()
        {
            Status = status,
            FailureReason = failureReason,
            Path = path,
            Lang = lang,
            TotalLines = totalLines,
            RequestedStartLine = startLine,
            RequestedEndLine = endLine,
            StartLine = nextLine,
            EndLine = nextLine,
        };

        if (fileSize < 0 || totalLines < 0 || (fileSize == 0 && totalLines > 0))
            return CreateResult(BoundedFileReadStatus.InvalidTopology, "resource_file_metadata_inconsistent");
        if (totalLines == 0)
        {
            if (_hasChunksTable && HasAnyResourceChunk(fileId))
                return CreateResult(BoundedFileReadStatus.InvalidTopology, "resource_file_metadata_inconsistent");
            if (IsAffirmativelyEmptyIndexedFile(totalLines, file.Checksum))
                return CreateResult(BoundedFileReadStatus.Empty);
            if (fileSize == 0 && file.Checksum != null)
                return CreateResult(BoundedFileReadStatus.InvalidTopology, "resource_file_metadata_inconsistent");
            return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_content_unavailable");
        }
        if (!_hasChunksTable)
            return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_content_unavailable");
        var hasBoundedChunkIndexes = _chunkIndexes.Contains(BoundedResourceReadChunkIndexName)
                                     && _chunkIndexes.Contains(BoundedResourceReadChunkEndIndexName);
        var useLegacyChunkQueries = !hasBoundedChunkIndexes
                                    && _chunkIndexes.Contains(LegacyBoundedResourceReadFileIndexName);
        if (!hasBoundedChunkIndexes && !useLegacyChunkQueries)
            return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_bounded_read_index_unavailable");
        if (!TryHasNullResourceChunkBoundary(fileId, useLegacyChunkQueries, out var hasNullBoundary))
            return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_bounded_read_index_unavailable");
        if (hasNullBoundary)
            return CreateResult(BoundedFileReadStatus.InvalidTopology, "resource_chunk_topology_invalid");

        var effectiveEndLine = Math.Min(endLine, totalLines);
        if (nextLine > effectiveEndLine)
            return CreateResult(BoundedFileReadStatus.InvalidContinuation);

        // Bound SQLite work as well as returned metadata. Two disjoint index seeks collect
        // at most one capped predecessor set and one capped forward set; overlap filtering
        // then happens only over those bounded rows in memory. This avoids a full file_id
        // scan for late ranges and gaps while retaining legacy chunks that span many lines.
        // 返却metadataだけでなくSQLiteの走査量も制限する。互いに素なpredecessor/forwardの
        // index seekを各上限件数まで取得し、重複範囲の判定はbounded rowだけmemory上で行う。
        // これにより後方rangeやgapでもfile_id全走査を避け、長いlegacy chunkも扱える。
        var scanEndLine = (int)Math.Min(effectiveEndLine, (long)nextLine + maxLines - 1L);
        var chunks = new List<BoundedFileChunk>();
        var chunkIds = new HashSet<long>();
        string? chunkTopologyFailure = null;
        var chunkIndexUnavailable = false;

        int ReadChunkCandidates(string commandText, bool includeEndLine)
        {
            int ExecuteQuery()
            {
                using var chunkCmd = _conn.CreateCommand();
                chunkCmd.CommandText = commandText;
                SqliteCommandPolicy.AddInt64(chunkCmd, "@fileId", fileId);
                SqliteCommandPolicy.AddInt32(chunkCmd, "@startLine", nextLine);
                if (includeEndLine)
                    SqliteCommandPolicy.AddInt32(chunkCmd, "@endLine", scanEndLine);
                SqliteCommandPolicy.AddInt32(chunkCmd, "@chunkLimit", MaxBoundedFileReadChunks + 1);

                var rawCandidateCount = 0;
                using var chunkReader = chunkCmd.ExecuteTrackedReader();
                while (chunkReader.TrackedRead())
                {
                    rawCandidateCount++;
                    _cancellation.ThrowIfCancellationRequested();
                    if (chunkReader.IsDBNull(1) || chunkReader.IsDBNull(2) || chunkReader.IsDBNull(3))
                    {
                        chunkTopologyFailure = "resource_chunk_topology_invalid";
                        break;
                    }

                    var chunk = new BoundedFileChunk(
                        chunkReader.GetInt64(0),
                        chunkReader.GetInt32(1),
                        chunkReader.GetInt32(2),
                        chunkReader.GetInt32(3));
                    if (chunk.StartLine <= 0 || chunk.EndLine < chunk.StartLine || chunk.ChunkIndex < 0)
                    {
                        chunkTopologyFailure = "resource_chunk_topology_invalid";
                        break;
                    }
                    if (chunk.EndLine < nextLine || chunk.StartLine > scanEndLine)
                        continue;

                    if (chunkIds.Add(chunk.RowId))
                        chunks.Add(chunk);
                    if (chunks.Count > MaxBoundedFileReadChunks)
                        break;
                }

                return rawCandidateCount;
            }

            if (!useLegacyChunkQueries)
                return ExecuteQuery();
            if (TryRunLegacyResourceMetadataQuery(ExecuteQuery, out var legacyCandidateCount))
                return legacyCandidateCount;

            chunkIndexUnavailable = true;
            return 0;
        }

        var predecessorCandidateCount = ReadChunkCandidates(
            useLegacyChunkQueries
                ? LegacyBoundedResourceReadPredecessorSql
                : BoundedResourceReadPredecessorSql,
            includeEndLine: false);
        if (chunkIndexUnavailable)
            return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_bounded_read_index_unavailable");

        bool HasChunkCoveringNextLine()
            => chunks.Any(chunk => chunk.StartLine <= nextLine && chunk.EndLine >= nextLine);

        if (chunkTopologyFailure == null
            && chunks.Count <= MaxBoundedFileReadChunks
            && !HasChunkCoveringNextLine()
            && predecessorCandidateCount > MaxBoundedFileReadChunks)
        {
            var endCandidateCount = ReadChunkCandidates(
                useLegacyChunkQueries
                    ? LegacyBoundedResourceReadEndSql
                    : BoundedResourceReadEndSql,
                includeEndLine: false);
            if (chunkIndexUnavailable)
                return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_bounded_read_index_unavailable");
            if (chunkTopologyFailure == null
                && !HasChunkCoveringNextLine()
                && endCandidateCount > MaxBoundedFileReadChunks)
            {
                return CreateResult(BoundedFileReadStatus.InvalidTopology, "chunk_candidate_scan_limit_exceeded");
            }
        }

        if (chunkTopologyFailure == null && chunks.Count <= MaxBoundedFileReadChunks)
        {
            ReadChunkCandidates(
                useLegacyChunkQueries
                    ? LegacyBoundedResourceReadForwardSql
                    : BoundedResourceReadForwardSql,
                includeEndLine: true);
        }

        if (chunkIndexUnavailable)
            return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_bounded_read_index_unavailable");
        if (chunkTopologyFailure != null)
            return CreateResult(BoundedFileReadStatus.InvalidTopology, chunkTopologyFailure);
        if (chunks.Count > MaxBoundedFileReadChunks)
            return CreateResult(BoundedFileReadStatus.InvalidTopology, "chunk_limit_exceeded");
        chunks.Sort(static (left, right) =>
        {
            var byStartLine = left.StartLine.CompareTo(right.StartLine);
            return byStartLine != 0 ? byStartLine : left.ChunkIndex.CompareTo(right.ChunkIndex);
        });
        if (chunks.Count == 0)
        {
            if (!TryHasAnyStoredResourceChunk(fileId, useLegacyChunkQueries, out var hasStoredChunk))
                return CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_bounded_read_index_unavailable");
            return hasStoredChunk
                ? CreateResult(BoundedFileReadStatus.IncompleteCoverage, "resource_chunk_coverage_incomplete")
                : CreateResult(BoundedFileReadStatus.ContentUnavailable, "resource_content_unavailable");
        }

        var state = new BoundedFileReadState(nextLine, continuationByteOffset);
        var scanByteLimit = BoundedFileReadScanByteLimitOverride.Value ?? MaxBoundedFileReadScannedUtf8Bytes;
        if (scanByteLimit <= 0 || scanByteLimit > MaxBoundedFileReadScannedUtf8Bytes)
            scanByteLimit = MaxBoundedFileReadScannedUtf8Bytes;
        var content = new StringBuilder(Math.Min(maxUtf8Bytes, BoundedFileReadBufferSize));
        var buffer = ArrayPool<byte>.Shared.Rent(BoundedFileReadBufferSize);
        try
        {
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                _cancellation.ThrowIfCancellationRequested();
                if (state.Stopped)
                    break;
                if (chunk.EndLine < state.NextLine)
                    continue;
                if (chunk.StartLine > state.NextLine)
                {
                    state.IncompleteCoverage = true;
                    break;
                }

                using var blob = new SqliteBlob(_conn, "chunks", "content", chunk.RowId, readOnly: true);
                ReadBoundedChunk(
                    blob,
                    chunk,
                    effectiveEndLine,
                    maxUtf8Bytes,
                    maxLines,
                    content,
                    buffer,
                    scanByteLimit,
                    _cancellation,
                    ref state);
            }
        }
        catch (BoundedFileScanLimitException)
        {
            state.ScanLimitExceeded = true;
        }
        catch (InvalidDataException)
        {
            state.InvalidTopologyReason = "invalid_utf8_content";
        }
        finally
        {
            // Indexed source may contain secrets. Clear the entire pooled array before reuse.
            // index済みsourceにsecretが含まれうるため、poolへ戻す前に配列全体を消去する。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (state.ScanLimitExceeded)
            return CreateResult(BoundedFileReadStatus.InvalidTopology, "scan_limit_exceeded");
        if (state.InvalidTopologyReason != null)
            return CreateResult(BoundedFileReadStatus.InvalidTopology, state.InvalidTopologyReason);
        if (state.InvalidContinuation)
            return CreateResult(BoundedFileReadStatus.InvalidContinuation, "invalid_continuation");
        if (state.IncompleteCoverage || (!state.Completed && state.TruncationReason == null))
            return CreateResult(BoundedFileReadStatus.IncompleteCoverage, "resource_chunk_coverage_incomplete");

        return new BoundedFileReadResult
        {
            Status = BoundedFileReadStatus.Success,
            Path = path,
            Lang = lang,
            TotalLines = totalLines,
            RequestedStartLine = startLine,
            RequestedEndLine = endLine,
            StartLine = state.FirstReturnedLine ?? nextLine,
            EndLine = state.LastReturnedLine ?? nextLine,
            Content = content.ToString(),
            Utf8Bytes = state.Utf8Bytes,
            Truncated = state.TruncationReason != null,
            TruncationReason = state.TruncationReason,
            NextLine = state.TruncationReason != null ? state.NextLine : null,
            NextByteOffset = state.TruncationReason != null ? state.NextByteOffset : null,
        };
    }

    private bool HasAnyResourceChunk(long fileId)
    {
        _cancellation.ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT 1 FROM chunks WHERE file_id = @fileId LIMIT 1";
        SqliteCommandPolicy.AddInt64(command, "@fileId", fileId);
        var hasChunk = command.ExecuteScalar() != null;
        _cancellation.ThrowIfCancellationRequested();
        return hasChunk;
    }

    private bool TryHasAnyStoredResourceChunk(long fileId, bool useLegacyChunkQueries, out bool hasChunk)
    {
        bool ExecuteQuery()
        {
            _cancellation.ThrowIfCancellationRequested();
            using var command = _conn.CreateCommand();
            var indexName = useLegacyChunkQueries
                ? LegacyBoundedResourceReadFileIndexName
                : BoundedResourceReadChunkIndexName;
            command.CommandText = $"""
                SELECT 1
                FROM chunks INDEXED BY {indexName}
                WHERE file_id = @fileId AND content IS NOT NULL
                LIMIT 1
                """;
            SqliteCommandPolicy.AddInt64(command, "@fileId", fileId);
            var result = command.ExecuteScalar() != null;
            _cancellation.ThrowIfCancellationRequested();
            return result;
        }

        if (!useLegacyChunkQueries)
        {
            hasChunk = ExecuteQuery();
            return true;
        }

        return TryRunLegacyResourceMetadataQuery(ExecuteQuery, out hasChunk);
    }

    private bool TryHasNullResourceChunkBoundary(long fileId, bool useLegacyChunkQueries, out bool hasNullBoundary)
    {
        bool ExecuteQuery()
        {
            bool Exists(string commandText)
            {
                using var command = _conn.CreateCommand();
                command.CommandText = commandText;
                SqliteCommandPolicy.AddInt64(command, "@fileId", fileId);
                return command.ExecuteScalar() != null;
            }

            var startIndexName = useLegacyChunkQueries
                ? LegacyBoundedResourceReadFileIndexName
                : BoundedResourceReadChunkIndexName;
            var endIndexName = useLegacyChunkQueries
                ? LegacyBoundedResourceReadFileIndexName
                : BoundedResourceReadChunkEndIndexName;
            _cancellation.ThrowIfCancellationRequested();
            var result = Exists($"""
                SELECT 1
                FROM chunks INDEXED BY {startIndexName}
                WHERE file_id = @fileId
                  AND content IS NOT NULL
                  AND start_line IS NULL
                LIMIT 1
                """) || Exists($"""
                SELECT 1
                FROM chunks INDEXED BY {endIndexName}
                WHERE file_id = @fileId
                  AND content IS NOT NULL
                  AND end_line IS NULL
                LIMIT 1
                """);
            _cancellation.ThrowIfCancellationRequested();
            return result;
        }

        if (!useLegacyChunkQueries)
        {
            hasNullBoundary = ExecuteQuery();
            return true;
        }

        return TryRunLegacyResourceMetadataQuery(ExecuteQuery, out hasNullBoundary);
    }

    private bool TryRunLegacyResourceMetadataQuery<T>(Func<T> action, out T result)
    {
        var maxVmSteps = LegacyResourceReadSqliteVmStepLimitOverride.Value
                         ?? MaxLegacyResourceReadSqliteVmSteps;
        maxVmSteps = Math.Clamp(maxVmSteps, 1, MaxLegacyResourceReadSqliteVmSteps);
        var callbackLimit = Math.Max(1, (maxVmSteps + LegacyResourceReadProgressOperations - 1)
                                        / LegacyResourceReadProgressOperations);
        var callbackCount = 0;
        var budgetExceeded = false;
        SQLitePCL.delegate_progress progress = _ =>
        {
            callbackCount++;
            if (callbackCount <= callbackLimit)
                return 0;

            budgetExceeded = true;
            return 1;
        };

        SQLitePCL.raw.sqlite3_progress_handler(
            _conn.Handle,
            LegacyResourceReadProgressOperations,
            progress,
            null!);
        try
        {
            result = action();
            return true;
        }
        catch (SqliteException exception) when (
            budgetExceeded
            && !_cancellation.IsCancellationRequested
            && exception.SqliteErrorCode == 9)
        {
            result = default!;
            return false;
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(_conn.Handle, 0, null!, null!);
            GC.KeepAlive(progress);
        }
    }

    private static void ReadBoundedChunk(
        SqliteBlob blob,
        BoundedFileChunk chunk,
        int effectiveEndLine,
        int maxUtf8Bytes,
        int maxLines,
        StringBuilder content,
        byte[] buffer,
        int maxScannedUtf8Bytes,
        CancellationToken cancellationToken,
        ref BoundedFileReadState state)
    {
        var bufferOffset = 0;
        var bufferedBytes = 0;
        var localLine = chunk.StartLine;
        var localByteOffset = 0;
        Span<byte> runeBytes = stackalloc byte[4];
        Span<char> runeChars = stackalloc char[2];

        while (TryReadBlobByte(blob, buffer, ref bufferOffset, ref bufferedBytes, maxScannedUtf8Bytes, cancellationToken, ref state, out var firstByte))
        {
            if (localLine > chunk.EndLine)
                break;

            if (localLine < state.NextLine)
            {
                if (firstByte == (byte)'\n')
                {
                    localLine++;
                    localByteOffset = 0;
                }
                else
                {
                    localByteOffset++;
                }
                continue;
            }

            if (localLine > state.NextLine)
            {
                state.InvalidContinuation = true;
                return;
            }

            if (firstByte == (byte)'\n')
            {
                if (state.NextByteOffset != localByteOffset)
                {
                    state.InvalidContinuation = true;
                    return;
                }

                if (!CompleteBoundedLine(
                    localLine,
                    localByteOffset,
                    effectiveEndLine,
                    maxUtf8Bytes,
                    maxLines,
                    content,
                    ref state))
                {
                    return;
                }

                localLine++;
                localByteOffset = 0;
                continue;
            }

            var runeByteCount = ReadUtf8Rune(
                blob,
                firstByte,
                buffer,
                ref bufferOffset,
                ref bufferedBytes,
                maxScannedUtf8Bytes,
                cancellationToken,
                ref state,
                runeBytes,
                out var rune);
            var scalarStartOffset = localByteOffset;
            localByteOffset += runeByteCount;

            if (scalarStartOffset < state.NextByteOffset)
            {
                if (localByteOffset > state.NextByteOffset)
                    state.InvalidContinuation = true;
                if (state.InvalidContinuation)
                    return;
                continue;
            }

            if (scalarStartOffset != state.NextByteOffset)
            {
                state.InvalidContinuation = true;
                return;
            }
            if (state.Utf8Bytes > maxUtf8Bytes - runeByteCount)
            {
                state.TruncationReason = "max_bytes";
                return;
            }
            if (!TryEnterBoundedLine(localLine, maxLines, ref state))
                return;

            var runeCharCount = rune.EncodeToUtf16(runeChars);
            content.Append(runeChars[..runeCharCount]);
            state.Utf8Bytes += runeByteCount;
            state.NextByteOffset = localByteOffset;
        }

        // Persisted chunks omit the separator after their final line. Treat blob EOF as
        // that line boundary and synthesize exactly one LF when the requested range continues.
        // 永続化chunkは最終行後のseparatorを持たないため、blob EOFを行境界として扱い、
        // 要求範囲が続く場合だけLFを1つ合成する。
        if (!state.Stopped && localLine <= chunk.EndLine && localLine == state.NextLine)
        {
            if (state.NextByteOffset != localByteOffset)
            {
                state.InvalidContinuation = true;
                return;
            }

            _ = CompleteBoundedLine(
                localLine,
                localByteOffset,
                effectiveEndLine,
                maxUtf8Bytes,
                maxLines,
                content,
                ref state);
        }
    }

    private static bool CompleteBoundedLine(
        int line,
        int lineByteLength,
        int effectiveEndLine,
        int maxUtf8Bytes,
        int maxLines,
        StringBuilder content,
        ref BoundedFileReadState state)
    {
        if (!TryEnterBoundedLine(line, maxLines, ref state))
            return false;

        if (line >= effectiveEndLine)
        {
            state.Completed = true;
            return false;
        }

        if (state.Utf8Bytes >= maxUtf8Bytes)
        {
            state.NextLine = line;
            state.NextByteOffset = lineByteLength;
            state.TruncationReason = "max_bytes";
            return false;
        }

        content.Append('\n');
        state.Utf8Bytes++;
        state.NextLine = line + 1;
        state.NextByteOffset = 0;
        if (state.ReturnedLineCount >= maxLines)
        {
            state.TruncationReason = "max_lines";
            return false;
        }

        return true;
    }

    private static bool TryEnterBoundedLine(int line, int maxLines, ref BoundedFileReadState state)
    {
        if (state.LastReturnedLine == line)
            return true;
        if (state.ReturnedLineCount >= maxLines)
        {
            state.TruncationReason = "max_lines";
            return false;
        }

        state.FirstReturnedLine ??= line;
        state.LastReturnedLine = line;
        state.ReturnedLineCount++;
        return true;
    }

    private static int ReadUtf8Rune(
        SqliteBlob blob,
        byte firstByte,
        byte[] buffer,
        ref int bufferOffset,
        ref int bufferedBytes,
        int maxScannedUtf8Bytes,
        CancellationToken cancellationToken,
        ref BoundedFileReadState state,
        Span<byte> runeBytes,
        out Rune rune)
    {
        var byteCount = firstByte switch
        {
            <= 0x7f => 1,
            >= 0xc2 and <= 0xdf => 2,
            >= 0xe0 and <= 0xef => 3,
            >= 0xf0 and <= 0xf4 => 4,
            _ => throw new InvalidDataException("Indexed chunk contains invalid UTF-8."),
        };

        runeBytes[0] = firstByte;
        for (var i = 1; i < byteCount; i++)
        {
            if (!TryReadBlobByte(blob, buffer, ref bufferOffset, ref bufferedBytes, maxScannedUtf8Bytes, cancellationToken, ref state, out runeBytes[i]))
                throw new InvalidDataException("Indexed chunk ends inside a UTF-8 scalar value.");
        }

        var status = Rune.DecodeFromUtf8(runeBytes[..byteCount], out rune, out var bytesConsumed);
        if (status != OperationStatus.Done || bytesConsumed != byteCount)
            throw new InvalidDataException("Indexed chunk contains invalid UTF-8.");
        return byteCount;
    }

    private static bool TryReadBlobByte(
        SqliteBlob blob,
        byte[] buffer,
        ref int bufferOffset,
        ref int bufferedBytes,
        int maxScannedUtf8Bytes,
        CancellationToken cancellationToken,
        ref BoundedFileReadState state,
        out byte value)
    {
        if (bufferOffset >= bufferedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blob.Position >= blob.Length)
            {
                value = 0;
                return false;
            }
            var remaining = maxScannedUtf8Bytes - state.ScannedUtf8Bytes;
            if (remaining <= 0)
                throw new BoundedFileScanLimitException();
            bufferedBytes = blob.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            bufferOffset = 0;
            if (bufferedBytes == 0)
            {
                value = 0;
                return false;
            }
        }

        value = buffer[bufferOffset++];
        state.ScannedUtf8Bytes++;
        return true;
    }

    private readonly record struct BoundedFileChunk(long RowId, int StartLine, int EndLine, int ChunkIndex);

    private sealed class BoundedFileScanLimitException : Exception;

    private struct BoundedFileReadState(int nextLine, int nextByteOffset)
    {
        public int NextLine = nextLine;
        public int NextByteOffset = nextByteOffset;
        public int Utf8Bytes;
        public int ScannedUtf8Bytes;
        public int ReturnedLineCount;
        public int? FirstReturnedLine;
        public int? LastReturnedLine;
        public string? TruncationReason;
        public string? InvalidTopologyReason;
        public bool Completed;
        public bool InvalidContinuation;
        public bool IncompleteCoverage;
        public bool ScanLimitExceeded;
        public readonly bool Stopped => Completed || InvalidContinuation || IncompleteCoverage || ScanLimitExceeded || TruncationReason != null;
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
        if (mode == null && startedAt == null && durationMs == null && filesScanned == null && filesSkipped == null
            && parseErrors == null && bytesRead == null && bytesReadSkippedFileCount == null && bytesReadIncomplete == null
            && rowsUpserted == null && rowsDeleted == null && peakMemoryMb == null
            && diagnostics == null && diagnosticCount == null && diagnosticsTruncated == null
            && referenceExtractionCapHits == null)
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
        };
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
