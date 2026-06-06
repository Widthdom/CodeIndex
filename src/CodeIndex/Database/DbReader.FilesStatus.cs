using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    public List<FileFindResult> FindInFiles(string query, int limit, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, int before = 0, int after = 0, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth, int? focusLine = null, int? focusColumn = null, bool regex = false)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || pathPatterns == null || pathPatterns.Count == 0)
            return [];

        before = Math.Max(0, before);
        after = Math.Max(0, after);
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);
        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regexMatcher = regex
            ? new Regex(query, exact ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500))
            : null;

        using var fileCmd = _conn.CreateCommand();
        var sql = "SELECT f.id, f.path, f.lang, f.lines FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {PathBucketOrder}, f.path";
        fileCmd.CommandText = sql;
        if (lang != null)
            fileCmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);

        var results = new List<FileFindResult>();
        using var fileReader = fileCmd.ExecuteTrackedReader();
        while (fileReader.TrackedRead())
        {
            if (results.Count >= limit)
                break;

            var fileId = fileReader.GetInt64(0);
            var path = fileReader.GetString(1);
            var fileLang = GetNullableString(fileReader, 2);
            var totalLines = fileReader.GetInt32(3);
            if (totalLines <= 0)
                continue;

            var searchQuery = exact && !regex ? ExactSourceSearchNormalizer.Normalize(query, fileLang) : query;
            var pendingMatches = new Queue<PendingFileFindMatch>();
            var snippetWindow = new Queue<IndexedLine>();
            var snippetLinesByNumber = new Dictionary<int, string>();
            var acceptedMatches = results.Count;

            foreach (var indexedLine in EnumerateIndexedFileLines(fileId))
            {
                if (indexedLine.Number > totalLines)
                    break;

                AddLineToFindWindow(indexedLine, snippetWindow, snippetLinesByNumber);

                if (acceptedMatches < limit && (!focusLine.HasValue || indexedLine.Number == focusLine.Value))
                {
                    foreach (var lineMatch in EnumerateFindLineMatches(
                        indexedLine.Text,
                        fileLang,
                        searchQuery,
                        comparison,
                        regexMatcher,
                        exact && !regex,
                        focusColumn))
                    {
                        if (acceptedMatches >= limit)
                            break;

                        pendingMatches.Enqueue(new PendingFileFindMatch(
                            indexedLine.Number,
                            lineMatch.Column,
                            lineMatch.Length,
                            Math.Max(1, indexedLine.Number - before),
                            Math.Min(totalLines, indexedLine.Number + after)));
                        acceptedMatches++;
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

                if (results.Count >= limit && pendingMatches.Count == 0)
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
        }

        return results;
    }

    public int CountFindCandidateFiles(string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        if (pathPatterns == null || pathPatterns.Count == 0)
            return 0;

        using var fileCmd = _conn.CreateCommand();
        var sql = "SELECT COUNT(*) FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        fileCmd.CommandText = sql;
        if (lang != null)
            fileCmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);

        return Convert.ToInt32(fileCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public QueryCountResult CountFindInFiles(string query, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int? focusLine = null, int? focusColumn = null, bool regex = false)
    {
        if (string.IsNullOrWhiteSpace(query) || pathPatterns == null || pathPatterns.Count == 0)
            return new QueryCountResult(0, 0);

        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regexMatcher = regex
            ? new Regex(query, exact ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500))
            : null;
        using var fileCmd = _conn.CreateCommand();
        var sql = "SELECT f.id, f.path, f.lang, f.lines FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {PathBucketOrder}, f.path";
        fileCmd.CommandText = sql;
        if (lang != null)
            fileCmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);

        var count = 0;
        var fileCount = 0;
        using var fileReader = fileCmd.ExecuteTrackedReader();
        while (fileReader.TrackedRead())
        {
            var fileId = fileReader.GetInt64(0);
            var fileLang = GetNullableString(fileReader, 2);
            var totalLines = fileReader.GetInt32(3);
            if (totalLines <= 0)
                continue;

            var searchQuery = exact && !regex ? ExactSourceSearchNormalizer.Normalize(query, fileLang) : query;
            var fileMatches = 0;
            foreach (var indexedLine in EnumerateIndexedFileLines(fileId))
            {
                if (indexedLine.Number > totalLines)
                    break;

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
        }

        return new QueryCountResult(count, fileCount);
    }

    private readonly record struct IndexedLine(int Number, string Text);

    private readonly record struct FindLineMatch(int Column, int Length);

    private readonly record struct PendingFileFindMatch(int LineNumber, int Column, int Length, int SnippetStart, int SnippetEnd);

    private IEnumerable<IndexedLine> EnumerateIndexedFileLines(long fileId)
    {
        using var chunkCmd = _conn.CreateCommand();
        chunkCmd.CommandText = @"
            SELECT c.start_line, c.end_line, c.content
            FROM chunks c
            WHERE c.file_id = @fileId
            ORDER BY c.start_line, c.chunk_index";
        chunkCmd.Parameters.AddWithValue("@fileId", fileId);

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
                if (TryCreateFindLineMatch(match.Index, Math.Max(1, match.Length), rawIndexMap, focusColumn, out var lineMatch))
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
        return !focusColumn.HasValue || (focusColumn.Value >= rawMatchColumn + 1 && focusColumn.Value <= rawMatchColumn + rawMatchLength);
    }

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
            var clampedSnippet = LineWidthFormatter.ClampLines(
                snippetLines,
                maxLineWidth,
                focusLineIndex: snippetLineNumbers.IndexOf(pending.LineNumber),
                focusColumn: pending.Column + 1,
                focusLength: pending.Length);

            results.Add(new FileFindResult
            {
                Path = path,
                Lang = fileLang,
                Line = pending.LineNumber,
                Column = pending.Column + 1,
                StartLine = snippetLineNumbers[0],
                EndLine = snippetLineNumbers[^1],
                Snippet = clampedSnippet.Text,
                SnippetTruncated = clampedSnippet.Truncated,
            });
        }
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
        fileCmd.Parameters.AddWithValue("@path", path);

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
        chunkCmd.Parameters.AddWithValue("@path", path);
        if (startLine.HasValue)
            chunkCmd.Parameters.AddWithValue("@startLine", startLine.Value);
        if (endLine.HasValue)
            chunkCmd.Parameters.AddWithValue("@endLine", endLine.Value);

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
        // `endLine` + `after` (and `startLine` - `before`) come from untrusted MCP callers in
        // some entry points and can overflow int when endLine is near `int.MaxValue`. Clamp via
        // long intermediates so the subsequent `Math.Max/Min` sees the real window (#1528).
        // 一部の MCP 経路では `endLine` + `after`（および `startLine` - `before`）が信頼できない
        // 入力で、`int.MaxValue` 近傍の endLine だと int 加算で overflow する。long 中間で実窓を
        // 確定させてから clamp する（#1528）。
        var requestedStart = (int)Math.Max(1L, (long)startLine - before);
        var requestedEndCeiling = (int)Math.Min(int.MaxValue, (long)endLine + after);
        if (!TryLoadIndexedFileLines(path, out var lang, out var totalLines, out var lineMap, requestedStart, requestedEndCeiling))
            return null;
        var requestedEnd = Math.Min(totalLines, requestedEndCeiling);

        var selectedLines = Enumerable.Range(requestedStart, requestedEnd - requestedStart + 1)
            .Where(lineMap.ContainsKey)
            .ToList();

        if (selectedLines.Count == 0)
            return null;

        var contentLines = selectedLines.Select(line => lineMap[line]).ToList();
        var focusLineIndex = focusLine.HasValue ? selectedLines.IndexOf(focusLine.Value) : -1;
        if (focusLineIndex >= 0 && focusColumn.HasValue && focusColumn.Value > contentLines[focusLineIndex].Length)
            return null;
        var clampedContent = maxLineWidth.HasValue
            ? LineWidthFormatter.ClampLines(
                contentLines,
                maxLineWidth.Value,
                focusLineIndex >= 0 ? focusLineIndex : null,
                focusLineIndex >= 0 ? focusColumn : null,
                focusLength)
            : new ClampedTextResult(string.Join("\n", contentLines), false);

        return new FileExcerptResult
        {
            Path = path,
            Lang = lang,
            StartLine = selectedLines[0],
            EndLine = selectedLines[^1],
            Content = clampedContent.Text,
            ContentTruncated = clampedContent.Truncated,
        };
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
        cmd.Parameters.AddWithValue("@path", path);

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
    /// Get database statistics.
    /// データベースの統計情報を取得する。
    /// </summary>
    public StatusResult GetStatus()
    {
        // Issue #180: wrap the multi-statement status read in one DEFERRED transaction so
        // every COUNT(*) / freshness / readiness query resolves against the same WAL
        // snapshot. Without this, a concurrent writer that commits between the first and
        // last statement can expose wildly inconsistent counts (e.g. `refs: 0` against a
        // steady-state 44k while an incremental update is mid-flight). DEFERRED avoids
        // acquiring a write lock — the transaction grabs a SHARED lock on the first SELECT
        // and holds one consistent snapshot until Commit releases it.
        // Issue #180: 複数 SELECT を 1 つの DEFERRED transaction で囲み、全 COUNT(*) /
        // freshness / readiness クエリを同じ WAL snapshot で解決する。これが無いと、
        // 並行 writer が途中で commit した際に「refs: 0 なのに files=836」のような不整合
        // が見える。DEFERRED は最初の SELECT で SHARED lock を取るのみで write lock を
        // 握らないため、別 writer を阻害しない。
        using var txn = _conn.BeginTransaction(deferred: true);
        var files = ExecuteScalar("SELECT COUNT(*) FROM files");
        var chunks = ExecuteScalar("SELECT COUNT(*) FROM chunks");
        var symbols = ExecuteScalar("SELECT COUNT(*) FROM symbols");
        var references = _hasReferencesTable ? ExecuteScalar("SELECT COUNT(*) FROM symbol_references") : 0L;
        var freshness = GetWorkspaceFreshness();
        var hasCSharpFiles = ScopeMayIncludeCSharpFiles("csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false, since: null);
        var csharpSymbolNameReady = !hasCSharpFiles || _csharpSymbolNameContractCurrent;
        // #435 codex review iter 3: mirror `csharp_symbol_name_ready` — the readiness flag
        // only applies when the workspace actually contains C# files, and the column +
        // stamp must match the current contract for the resolver edges to be trusted.
        // This surfaces the same flag we already emit from the CLI `index` JSON so that
        // `status --json` and MCP `status` expose a consistent trust signal (README /
        // CLAUDE.md contract).
        // #435 codex review iter 3: `csharp_symbol_name_ready` と同じ条件で expose する。
        // C# ファイルが 0 なら ready=true、そうでなければ列 + stamp の一致を要求する。
        var csharpMetadataTargetReady = !hasCSharpFiles || _csharpMetadataTargetReady;
        var csharpMetadataTargetDegradedReason = csharpMetadataTargetReady
            ? null
            : _csharpMetadataTargetDegradedReason;
        var sqlGraphContractSignal = GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignal = GetHotspotFamilySignal(lang: null);
        var languageReadiness = GetLanguageReadiness();
        var foldReadyReason = ResolveFoldReadyReason();
        var foldReady = _foldReady && foldReadyReason == null;

        // Language breakdown / 言語別内訳
        // Scope the reader in an inner block so it releases its statement handle before
        // we Commit() the enclosing txn — `SqliteTransaction.Commit()` fails if any
        // reader on the same connection is still open.
        // reader を内側ブロックに閉じ込め、txn.Commit() の前に statement handle を
        // 解放する。SqliteTransaction.Commit() は同じ connection 上で開いている reader
        // があると失敗する。
        var langs = new Dictionary<string, long>();
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT lang, COUNT(*) FROM files WHERE lang IS NOT NULL GROUP BY lang ORDER BY COUNT(*) DESC";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                langs[reader.GetString(0)] = reader.GetInt64(1);
        }

        var symbolsByLanguage = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(f.lang, 'unknown'), s.kind, COUNT(*)
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                GROUP BY COALESCE(f.lang, 'unknown'), s.kind
                ORDER BY COALESCE(f.lang, 'unknown'), COUNT(*) DESC, s.kind";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var lang = reader.GetString(0);
                var kind = reader.GetString(1);
                if (!symbolsByLanguage.TryGetValue(lang, out var kinds))
                {
                    kinds = new Dictionary<string, long>(StringComparer.Ordinal);
                    symbolsByLanguage[lang] = kinds;
                }

                kinds[kind] = reader.GetInt64(2);
            }
        }

        // #1509: pull persisted HEAD metadata while the SHARED snapshot is open so the
        // recorded SHA / branch / timestamp can't drift relative to the counts and freshness
        // reported by the same status call.
        // #1509: 同じ snapshot 内で HEAD metadata を引き、counts / freshness と整合させる。
        var indexedHeadSha = TryGetMetaStringInternal(DbContext.IndexedHeadShaMetaKey);
        var indexedHeadBranch = TryGetMetaStringInternal(DbContext.IndexedHeadBranchMetaKey);
        var indexedHeadTimestamp = ParseMetaDateTime(TryGetMetaStringInternal(DbContext.IndexedHeadTimestampMetaKey));
        var unknownExtensionFileCount = ParseMetaLong(TryGetMetaStringInternal(DbContext.UnknownExtensionFileCountMetaKey));
        var unknownExtensionFiles = ParseMetaStringList(TryGetMetaStringInternal(DbContext.UnknownExtensionFilePathsMetaKey));
        var unknownExtensionFilesTruncated = ParseMetaBool(TryGetMetaStringInternal(DbContext.UnknownExtensionFilesTruncatedMetaKey));
        var unknownExtensionFilePathLimit = ParseMetaLong(TryGetMetaStringInternal(DbContext.UnknownExtensionFilePathLimitMetaKey));
        if (unknownExtensionFiles != null)
        {
            unknownExtensionFilesTruncated ??= unknownExtensionFileCount.HasValue
                && unknownExtensionFileCount.Value > unknownExtensionFiles.Count;
            unknownExtensionFilePathLimit ??= unknownExtensionFiles.Count;
        }
        // #1546: workspace case-sensitivity stamp. Read inside the SHARED snapshot for
        // consistency with the other freshness signals; missing on legacy DBs.
        // #1546: case-sensitivity stamp も同 snapshot で読む。stamp 無し旧 DB は null。
        var pathCaseSensitive = ParseMetaBool(TryGetMetaStringInternal(DbContext.WorkspacePathCaseSensitiveMetaKey));
        var dbPragmaSettings = GetDbPragmaSettings();
        var dbSizeBytes = TryGetDatabaseFileSize();
        var walSizeBytes = TryGetWalFileSize();
        var lastIndexRun = GetLastIndexRun();
        var batchInProgress = string.Equals(
            TryGetMetaStringInternal(DbContext.BatchInProgressMetaKey),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var result = new StatusResult
        {
            Files = files,
            Chunks = chunks,
            Symbols = symbols,
            References = references,
            UnknownExtensionFileCount = unknownExtensionFileCount,
            UnknownExtensionFiles = unknownExtensionFiles,
            UnknownExtensionFilesTruncated = unknownExtensionFilesTruncated,
            UnknownExtensionFilePathLimit = unknownExtensionFilePathLimit,
            IndexedAt = freshness.IndexedAt,
            LastWorkspaceFreshenedAt = lastIndexRun?.StartedAt ?? indexedHeadTimestamp,
            LatestModified = freshness.LatestModified,
            IndexedHeadSha = indexedHeadSha,
            IndexedHeadBranch = indexedHeadBranch,
            IndexedHeadTimestamp = indexedHeadTimestamp,
            Languages = langs,
            SymbolsByLanguage = symbolsByLanguage.Count > 0 ? symbolsByLanguage : null,
            GraphTableAvailable = _hasReferencesTable,
            IssuesTableAvailable = _hasIssuesPhysicalTable,
            FileIssuesDataCurrent = _hasIssuesTable,
            MigrationInProgress = batchInProgress,
            HotspotFamilyReady = hotspotFamilySignal.Ready,
            HotspotFamilyDegradedReason = hotspotFamilySignal.DegradedReason,
            LanguageReadiness = languageReadiness.Count > 0 ? languageReadiness : null,
            CSharpSymbolNameReady = csharpSymbolNameReady,
            CSharpMetadataTargetReady = csharpMetadataTargetReady,
            CSharpMetadataTargetDegradedReason = csharpMetadataTargetDegradedReason,
            SqlGraphContractReady = sqlGraphContractSignal.Ready,
            SqlGraphContractDegradedReason = sqlGraphContractSignal.DegradedReason,
            FoldReady = foldReady,
            FoldReadyReason = foldReadyReason,
            IndexWriterVersion = _indexWriterVersion,
            IndexNewerThanReader = _indexNewerThanReader,
            IndexNewerThanReaderReason = _indexNewerThanReaderReason,
            PathCaseSensitive = pathCaseSensitive,
            DbPragmaSettings = dbPragmaSettings,
            DbSizeBytes = dbSizeBytes,
            WalSizeBytes = walSizeBytes,
            Process = StatusProcessMetrics.Capture(),
            LastIndexRun = lastIndexRun,
            ReadOnlyFallback = _readOnlyFallback,
            WalCheckpointAttempted = _walCheckpointAttempted,
            WalCheckpointSucceeded = _walCheckpointSucceeded,
        };
        // Commit the read-only snapshot explicitly so the SHARED lock is released promptly.
        // read-only なので rollback でも同じだが、明示 commit して SHARED lock を早期解放する。
        txn.Commit();
        return result;
    }

    /// <summary>
    /// Delegate to RepoMapBuilder for repo-level overview generation.
    /// RepoMapBuilderに委譲してリポジトリ俯瞰情報を生成する。
    /// </summary>
    public RepoMapResult GetRepoMap(int limit = 10, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, double minEntrypointConfidence = 0)
    {
        var builder = new RepoMapBuilder(_conn, _fileColumns, _hasReferencesTable);
        return builder.Build(limit, lang, pathPatterns, excludePathPatterns, excludeTests, minEntrypointConfidence, GetWorkspaceFreshness);
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
        PageCount = ExecuteNullableLong("PRAGMA page_count"),
        FreelistCount = ExecuteNullableLong("PRAGMA freelist_count"),
        PageSize = ExecuteNullableLong("PRAGMA page_size"),
    };

    private long? TryGetDatabaseFileSize()
    {
        var path = _conn.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private long? TryGetWalFileSize()
    {
        var path = _conn.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var info = new FileInfo(path + "-wal");
            return info.Exists ? info.Length : 0;
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
        var rowsUpserted = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunRowsUpsertedMetaKey));
        var rowsDeleted = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunRowsDeletedMetaKey));
        var peakMemoryMb = ParseMetaLong(TryGetMetaStringInternal(DbContext.LastIndexRunPeakMemoryMbMetaKey));
        if (mode == null && startedAt == null && durationMs == null && filesScanned == null && filesSkipped == null
            && parseErrors == null && bytesRead == null && rowsUpserted == null && rowsDeleted == null && peakMemoryMb == null)
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
            RowsUpserted = rowsUpserted,
            RowsDeleted = rowsDeleted,
            PeakMemoryMb = peakMemoryMb,
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

    private (DateTime? IndexedAt, DateTime? LatestModified) GetWorkspaceFreshness()
    {
        return (
            ExecuteNullableDateTime(_fileColumns.Contains("indexed_at") ? "SELECT MAX(indexed_at) FROM files" : null),
            ExecuteNullableDateTime(_fileColumns.Contains("modified") ? "SELECT MAX(modified) FROM files" : null)
        );
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
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // Parse an ISO-8601 UTC timestamp persisted via SetMeta. Returns null when the raw
    // value is missing or malformed so a partial / legacy stamp degrades gracefully.
    // The stamp is always written as a UTC "o" round-trip string, so RoundtripKind alone
    // preserves the offset / Kind without AssumeUniversal (which conflicts with RoundtripKind).
    // ISO-8601 UTC 文字列を DateTime にする。欠落・解析失敗は null。
    // stamp は常に UTC "o" 形式なので RoundtripKind だけで Kind / offset を維持する
    // （AssumeUniversal と併用すると ArgumentException）。
    private static DateTime? ParseMetaDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (DateTime.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var value))
        {
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }
        return null;
    }

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
