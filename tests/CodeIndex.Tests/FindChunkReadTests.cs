using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public sealed class FindChunkReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LineChunks_BoundSortingAndPreserveScanSemantics_Issue5415(bool legacy)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_chunks_5415");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Pooling = false
        }.ToString());
        connection.Open();
        var writer = new DbWriter(connection);
        var chunkCount = DbReader.FindChunkPageSize + 1;
        var totalLines = chunkCount * 2 + 1;
        var id = writer.UpsertFile(new FileRecord { Path = "source.txt", Lang = "text", Lines = totalLines, Size = 4096 });
        writer.InsertChunks(Enumerable.Range(0, chunkCount).Reverse().Select(index => new ChunkRecord
        {
            FileId = id,
            ChunkIndex = 1000 + index,
            StartLine = index * 2 + 1,
            EndLine = index * 2 + 3,
            Content = "needle\r\ncontext\r\nneedle"
        }).Append(new ChunkRecord
        {
            FileId = id,
            ChunkIndex = 999,
            StartLine = 1,
            EndLine = 3,
            Content = "needle needle\r\nowned\r\nneedle"
        }).ToList());
        // A missing indexed source must not fall back to the live file.
        // 索引本文が欠けた場合も実ファイルへフォールバックしない。
        writer.UpsertFile(new FileRecord { Path = "missing.txt", Lang = "text", Lines = 1, Size = 6 });
        File.WriteAllText(Path.Combine(project.Root, "missing.txt"), "needle");
        File.WriteAllText(Path.Combine(project.Root, "source.txt"), "unindexed live changes");
        if (legacy)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP INDEX {DbReader.BoundedResourceReadChunkIndexName}; DROP INDEX {DbReader.BoundedResourceReadChunkEndIndexName}";
            drop.ExecuteNonQuery();
        }
        using (var readOnly = connection.CreateCommand())
        {
            readOnly.CommandText = "PRAGMA query_only = ON";
            readOnly.ExecuteNonQuery();
        }

        foreach (var resumed in new[] { false, true })
        {
            using var command = connection.CreateCommand();
            command.CommandText = DbReader.FindLineChunkPageSql(!legacy, resumed);
            command.Parameters.AddWithValue("@fileId", id);
            command.Parameters.AddWithValue("@startLine", 1);
            command.Parameters.AddWithValue("@chunkIndex", 999);
            using (var rows = command.ExecuteReader())
            {
                Assert.Equal(4, rows.FieldCount);
                Assert.Equal(new[] { "id", "start_line", "end_line", "chunk_index" },
                    Enumerable.Range(0, rows.FieldCount).Select(rows.GetName));
                var count = 0;
                while (rows.Read()) count++;
                Assert.Equal(DbReader.FindChunkPageSize, count);
            }
            command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            using var plan = command.ExecuteReader();
            var details = new List<string>();
            while (plan.Read()) details.Add(plan.GetString(3));
            if (legacy)
                Assert.Contains(details, detail => detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
            else
            {
                Assert.Contains(details, detail => detail.Contains(DbReader.BoundedResourceReadChunkIndexName, StringComparison.Ordinal));
                Assert.DoesNotContain(details, detail => detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
            }
        }

        using var reader = new DbReader(connection);
        foreach (var (query, regex, exact, candidates) in new[]
        {
            ("needle", false, false, false), ("needle", false, false, true),
            ("needle", false, true, false), ("needle", true, false, false)
        })
        {
            var full = reader.FindInFiles(query, 200, regex: regex, exact: exact, useIndexedLiteralCandidates: candidates,
                before: 1, after: 1);
            Assert.Equal(chunkCount + 2, full.Count);
            Assert.Equal(new[] { 1 }.Concat(Enumerable.Range(0, chunkCount + 1).Select(index => index * 2 + 1)),
                full.Select(row => row.Line));
            Assert.Equal(new[] { 1, 8 }, full.Take(2).Select(row => row.Column));
            Assert.Contains("owned", full[0].Snippet, StringComparison.Ordinal);
            Assert.All(full, row => Assert.Equal("source.txt", row.Path));
            Assert.False(full.Scan.Truncated);
            Assert.Equal(full.Count, reader.CountFindInFiles(query, regex: regex, exact: exact,
                useIndexedLiteralCandidates: candidates).Count);
            Assert.Empty(reader.FindInFiles(query, 200, regex: regex, pathPatterns: ["missing.txt"]));

            var paged = new List<FileFindResult>();
            FindScanSummary scan = default;
            for (var pageIndex = 0; pageIndex < full.Count; pageIndex++)
            {
                var page = reader.FindInFiles(query, 5, regex: regex, exact: exact, useIndexedLiteralCandidates: candidates,
                    before: 1, after: 1, captureContinuation: true, resumePath: scan.NextPath,
                    resumeLine: scan.NextLine, resumeFileOrdinal: scan.NextFileOrdinal,
                    resumeMatchOrdinal: scan.NextMatchOrdinal, resumeByteOffset: scan.NextByteOffset);
                paged.AddRange(page);
                scan = page.Scan;
                if (scan.NextPath is null) break;
            }
            Assert.Null(scan.NextPath);
            Assert.Equal(full.Select(Position), paged.Select(Position));

            var countSum = 0;
            scan = default;
            for (var pageIndex = 0; pageIndex < totalLines; pageIndex++)
            {
                var page = reader.CountFindInFiles(query, regex: regex, exact: exact, useIndexedLiteralCandidates: candidates,
                    maxLinesScanned: 7, resumePath: scan.NextPath, resumeLine: scan.NextLine,
                    resumeFileOrdinal: scan.NextFileOrdinal);
                Assert.InRange(page.Scan.LinesScanned, 0, 7);
                countSum += page.Count;
                scan = page.Scan;
                if (scan.NextPath is null) break;
                Assert.Equal("line_scan_limit", scan.TruncationReason);
            }
            Assert.Null(scan.NextPath);
            Assert.Equal(full.Count, countSum);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullSource_PreservesOrderedFailureAndEarlyStop_Issue5415(bool legacy)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_null_5415");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Pooling = false
        }.ToString());
        connection.Open();
        var writer = new DbWriter(connection);
        var id = writer.UpsertFile(new FileRecord { Path = "null.txt", Lang = "text", Lines = 2, Size = 6 });
        writer.InsertChunks([new ChunkRecord { FileId = id, ChunkIndex = 1, StartLine = 1, EndLine = 1, Content = "needle" }]);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO chunks(file_id, chunk_index, start_line, end_line, content) VALUES (@fileId, 0, 2, 2, NULL)";
            insert.Parameters.AddWithValue("@fileId", id);
            insert.ExecuteNonQuery();
            if (legacy)
            {
                insert.CommandText = $"DROP INDEX {DbReader.BoundedResourceReadChunkIndexName}";
                insert.ExecuteNonQuery();
            }
        }
        using var reader = new DbReader(connection);
        Assert.Equal(1, Assert.Single(reader.FindInFiles("needle", 1)).Line);
        Assert.Throws<InvalidOperationException>(() => reader.CountFindInFiles("needle"));
        Assert.Throws<InvalidOperationException>(() => reader.FindInFiles("missing", 10, regex: true));
    }

    private static string Position(FileFindResult row)
        => $"{row.Path}:{row.Line}:{row.Column}:{row.Length}:{row.StartLine}:{row.EndLine}:{row.Snippet}";
}
