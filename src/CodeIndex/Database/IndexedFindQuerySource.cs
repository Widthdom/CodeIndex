using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class IndexedFindQuerySource(DbReader owner)
    {
        private readonly DbReader _owner = owner;

        internal FindSearchPlan CreateSearchPlan(IndexedFindScanRequest request)
        {
            if (!request.UseIndexedLiteralCandidates)
                return new FindSearchPlan("line_scan", null, null);
            if (request.Regex)
                return new FindSearchPlan("line_scan", "regex", null);
            if (request.Exact)
                return new FindSearchPlan("line_scan", "exact_source_normalization", null);
            if (request.Query.Length < 3)
                return new FindSearchPlan("line_scan", "query_too_short", null);
            if (request.Query.Any(character => character < ' ' || character > '~'))
                return new FindSearchPlan("line_scan", "unsupported_query_characters", null);
            if (!_owner.HasTable(DbContext.FtsChunksTrigramTableName))
                return new FindSearchPlan("line_scan", "trigram_index_unavailable", null);
            if (DbWriter.IsFtsBulkLoadMarkerSet(_owner.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey)))
                return new FindSearchPlan("line_scan", "trigram_index_rebuilding", null);
            if (!HasAllTrigramFtsSyncTriggers())
                return new FindSearchPlan("line_scan", "trigram_index_unsynchronized", null);

            var phrase = "\"" + request.Query.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            return new FindSearchPlan("indexed_trigram", null, phrase);
        }

        private bool HasAllTrigramFtsSyncTriggers()
        {
            using var command = _owner._conn.CreateCommand();
            command.CommandText = DbContext.CountFtsChunksTrigramSyncTriggersSql;
            return SqliteCommandPolicy.ReadInt32Scalar(
                command,
                "find trigram FTS synchronization trigger count") == 3;
        }

        internal SqliteCommand CreateFileCommand(
            FindSearchPlan searchPlan,
            IndexedFindScanRequest request)
        {
            var command = _owner._conn.CreateCommand();
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
            if (request.Lang != null)
                sql += " AND f.lang = @lang";
            AppendPathFilters(
                ref sql,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests);
            sql += $" ORDER BY {PathBucketOrder}, f.path";
            command.CommandText = sql;
            if (searchPlan.TrigramMatchExpression != null)
                SqliteCommandPolicy.AddText(command, "@trigramQuery", searchPlan.TrigramMatchExpression);
            if (request.Lang != null)
                SqliteCommandPolicy.Add(command, "@lang", request.Lang);
            AddPathFilterParameters(
                command,
                request.PathPatterns,
                request.ExcludePathPatterns);
            return command;
        }

        internal IEnumerable<IndexedLine> EnumerateIndexedFileLines(long fileId)
        {
            using var command = _owner._conn.CreateCommand();
            command.CommandText = @"
                SELECT c.start_line, c.end_line, c.content
                FROM chunks c
                WHERE c.file_id = @fileId
                ORDER BY c.start_line, c.chunk_index";
            SqliteCommandPolicy.Add(command, "@fileId", fileId);

            var lastEmittedLine = 0;
            using var reader = command.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var chunkStartLine = reader.GetInt32(0);
                var chunkEndLine = reader.GetInt32(1);
                var chunkLines = reader.GetString(2).Split('\n');
                var lineCount = chunkEndLine - chunkStartLine + 1;

                for (var index = 0; index < chunkLines.Length && index < lineCount; index++)
                {
                    var absoluteLine = chunkStartLine + index;
                    if (absoluteLine <= lastEmittedLine)
                        continue;

                    lastEmittedLine = absoluteLine;
                    yield return new IndexedLine(absoluteLine, chunkLines[index]);
                }
            }
        }

        internal static IEnumerable<FindLineMatch> EnumerateLineMatches(
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
                    if (TryCreateLineMatch(
                            match.Index,
                            match.Length,
                            rawIndexMap,
                            focusColumn,
                            out var lineMatch))
                    {
                        yield return lineMatch;
                    }
                }
                yield break;
            }

            for (var searchStart = 0; searchStart < searchLine.Length;)
            {
                var matchColumn = searchLine.IndexOf(searchQuery, searchStart, comparison);
                if (matchColumn < 0)
                    break;

                if (TryCreateLineMatch(
                        matchColumn,
                        searchQuery.Length,
                        rawIndexMap,
                        focusColumn,
                        out var lineMatch))
                {
                    yield return lineMatch;
                }
                searchStart = matchColumn + 1;
            }
        }

        private static bool TryCreateLineMatch(
            int matchColumn,
            int matchLength,
            int[]? rawIndexMap,
            int? focusColumn,
            out FindLineMatch lineMatch)
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
            return !focusColumn.HasValue
                   || focusColumn.Value >= rawMatchColumn + 1
                   && focusColumn.Value <= focusEndColumn;
        }
    }
}
