using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class FindMultilineTests
{
    [Fact]
    public void Windows_PreserveCoordinatesBoundsAndChunkOwnership_Issue5399()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_windows_5399");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        using var connection = Open(db);
        var writer = new DbWriter(connection);
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var path = newline.Length == 1 ? "lf.txt" : "crlf.txt";
            string[] lines = ["😀A", "B", "C", "A", "B", "C", "END"];
            var id = writer.UpsertFile(new FileRecord { Path = path, Lang = "text", Lines = lines.Length, Size = 30 });
            var chunks = new List<ChunkRecord>();
            for (var start = 0; start < lines.Length; start += 2)
            {
                var selected = lines.Skip(start).Take(3).ToArray();
                chunks.Add(new ChunkRecord
                {
                    FileId = id,
                    ChunkIndex = start,
                    StartLine = start + 1,
                    EndLine = start + selected.Length,
                    Content = string.Join(newline, selected)
                });
            }
            chunks.Add(new ChunkRecord
            {
                FileId = id,
                ChunkIndex = 99,
                StartLine = 1,
                EndLine = 3,
                Content = string.Join(newline, lines.Take(3))
            });
            writer.InsertChunks(chunks);
        }
        TestProjectHelper.InsertIndexedFile(db, "next.txt", "text", "START");
        using var reader = new DbReader(connection);
        var rows = reader.FindInFiles("A\\nB\\nC", 20, regex: true, window: new(3));
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(row.Line + 2, row.MatchEndLine);
            Assert.Equal(2, row.MatchEndColumn);
            Assert.Equal(5, row.Length);
            Assert.Equal(row.Line, row.StartLine);
            Assert.Equal(row.Line, row.EndLine);
            Assert.Equal(row.Line == 1 ? 3 : 1, row.Column);
        });
        Assert.Equal(4, reader.CountFindInFiles("A\\nB\\nC", regex: true, window: new(3)).Count);
        Assert.Empty(reader.FindInFiles("A\\nB\\nC", 20, regex: true));
        var beyond = reader.FindInFiles("A\\nB\\nC", 20, regex: true, window: new(2));
        Assert.Empty(beyond);
        Assert.False(beyond.Scan.Truncated);
        Assert.Empty(reader.FindInFiles("END\\nSTART", 20, regex: true, window: new(8)));
        Assert.Equal(4, reader.FindInFiles("(?m)^B$", 20, regex: true, window: new(3)).Count);
        Assert.Empty(reader.FindInFiles("A.*C", 20, regex: true, window: new(3)));
        Assert.Equal(4, reader.FindInFiles("(?s)A.*C", 20, regex: true, window: new(3)).Count);
        Assert.All(reader.FindInFiles("(?=B)", 20, regex: true, window: new(3)), row =>
        {
            Assert.Equal(0, row.Length);
            Assert.Equal(row.Line, row.MatchEndLine);
            Assert.Equal(row.Column, row.MatchEndColumn);
        });
        Assert.Equal(4, reader.FindInFiles("A\\nB|B", 20, regex: true, window: new(3)).Count);
        TestProjectHelper.InsertIndexedFile(db, "max.txt", "text", "START\n" + new string('\n', 62) + "END\nAFTER");
        var max = reader.FindInFiles("START\\n{63}END", 20, pathPatterns: ["max.txt"], regex: true, window: new(64));
        Assert.Equal(64, Assert.Single(max).MatchEndLine);
        Assert.Empty(reader.FindInFiles("START\\n{63}END\\nAFTER", 20, pathPatterns: ["max.txt"], regex: true, window: new(64)));
    }

    [Fact]
    public void PagesAndCounts_ReplayCrossingSpansAndSurrogatePositions_Issue5399()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_window_pages_5399");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "a.txt", "text", "😀A\nB A\nB\nA\nB");
        using var connection = Open(db);
        using var reader = new DbReader(connection);
        foreach (var pattern in new[] { "A\\nB|B", "(?=.)", "(?m)$" })
        {
            var full = reader.FindInFiles(pattern, 200, regex: true, window: new(2));
            var paged = new List<FileFindResult>();
            FindScanSummary scan = default;
            for (var i = 0; i < 50; i++)
            {
                var page = reader.FindInFiles(pattern, 1, regex: true, window: new(2), captureContinuation: true,
                    resumePath: scan.NextPath, resumeLine: scan.NextLine, resumeFileOrdinal: scan.NextFileOrdinal,
                    resumeMatchOrdinal: scan.NextMatchOrdinal, resumeByteOffset: scan.NextByteOffset);
                paged.AddRange(page);
                scan = page.Scan;
                if (scan.NextPath is null) break;
            }
            Assert.Null(scan.NextPath);
            Assert.Equal(full.Select(Position), paged.Select(Position));
            var total = 0;
            scan = default;
            for (var i = 0; i < 50; i++)
            {
                var page = reader.CountFindInFiles(pattern, regex: true, window: new(2), maxLinesScanned: 1,
                    resumePath: scan.NextPath, resumeLine: scan.NextLine, resumeFileOrdinal: scan.NextFileOrdinal,
                    resumeByteOffset: scan.NextByteOffset);
                total += page.Count;
                scan = page.Scan;
                if (scan.NextPath is null) break;
            }
            Assert.Null(scan.NextPath);
            Assert.Equal(full.Count, total);
        }
    }

    [Fact]
    public void Cli_ProjectsSpansPreservesAuthorityAndRejectsIncompatibleCursors_Issue5399()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_window_cli_5399");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "a.txt", "text", "A\nB A\nB\nA\nB");
        string[] args = ["find", "A\\nB", "--regex", "--multiline", "--window-lines", "2",
            "--path", "a.txt", "--db", db, "--json", "--limit", "1", "--fields", "path", "--max-json-bytes", "6000"];
        var output = Run(args);
        var row = Assert.Single(output["results"]!.AsArray())!;
        Assert.Equal(2, row["match_end_line"]!.GetValue<int>());
        Assert.Equal(2, row["match_end_column"]!.GetValue<int>());
        Assert.Equal(1, row["line"]!.GetValue<int>());
        Assert.False(output["metadata"]!["stream_terminal"]!["unbounded_absence_authoritative"]!.GetValue<bool>());
        var cursor = output["metadata"]!["next_cursor"]!.GetValue<string>();
        var resumed = Run([.. args, "--cursor", cursor]);
        Assert.Equal(2, resumed["results"]![0]!["line"]!.GetValue<int>());
        Assert.False(resumed["metadata"]!["total_count_authoritative"]!.GetValue<bool>());
        foreach (var extra in new[] { new[] { "--window-lines", "3" }, new[] { "--window-bytes", "128" },
            new[] { "--exact" }, new[] { "--exclude-tests" }, new[] { "--lang", "csharp" } })
        {
            var (exit, error, stderr) = CaptureConsole(() => ProgramRunner.Run([.. args, .. extra, "--cursor", cursor], JsonOptions, "test"));
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.Contains("cursor", error + stderr, StringComparison.OrdinalIgnoreCase);
        }
        var (tinyExit, tiny, _) = CaptureConsole(() => ProgramRunner.Run([.. args, "--max-json-bytes", "100"], JsonOptions, "test"));
        Assert.Equal(CommandExitCodes.UsageError, tinyExit);
        Assert.Contains(CommandErrorCodes.ResponseBudgetTooSmall, tiny, StringComparison.Ordinal);
        var compact = Run([.. args, "--compact"]);
        Assert.NotNull(compact["results"]![0]!["match_end_line"]);
        TestProjectHelper.InsertIndexedFile(db, "budget.txt", "text", string.Join('\n',
            Enumerable.Repeat(new string('x', 500) + "A\nB", 20)));
        cursor = null;
        var found = new List<int>();
        var byteLimited = false;
        for (var i = 0; i < 30; i++)
        {
            string[] budgetArgs = ["find", "A\\nB", "--regex", "--multiline", "--path", "budget.txt", "--db", db,
                "--json", "--fields", "path,snippet", "--limit", "20", "--max-json-bytes", cursor is null ? "6000" : "20000",
                .. cursor is null ? Array.Empty<string>() : new[] { "--cursor", cursor }];
            var budgetPage = Run(budgetArgs);
            byteLimited |= budgetPage["metadata"]!["byte_limit_reached"]?.GetValue<bool>() == true;
            if (cursor is not null)
            {
                Assert.False(budgetPage["metadata"]!["stream_terminal"]!["authoritative_rows"]!.GetValue<bool>());
                Assert.False(budgetPage["metadata"]!["total_count_authoritative"]!.GetValue<bool>());
            }
            found.AddRange(budgetPage["results"]!.AsArray().Select(item => item!["line"]!.GetValue<int>()));
            cursor = budgetPage["metadata"]!["next_cursor"]?.GetValue<string>();
            if (cursor is null) break;
            var terminal = budgetPage["metadata"]!["stream_terminal"]!;
            Assert.True(terminal["has_more"]!.GetValue<bool>());
            Assert.False(terminal["authoritative_rows"]!.GetValue<bool>());
        }
        Assert.Null(cursor);
        Assert.True(byteLimited);
        Assert.Equal(Enumerable.Range(0, 20).Select(index => index * 2 + 1), found);
        using (var connection = Open(db))
        {
            var writer = new DbWriter(connection);
            var id = writer.UpsertFile(new FileRecord { Path = "z-budget-gap.txt", Lang = "text", Lines = 2, Size = 2 });
            writer.InsertChunks([new ChunkRecord { FileId = id, ChunkIndex = 0, StartLine = 2, EndLine = 2, Content = "X" }]);
        }
        var (stoppedExit, stoppedJson, _) = CaptureConsole(() => ProgramRunner.Run(
            ["find", "A\\nB", "--regex", "--multiline", "--path", "budget.txt", "--path", "z-budget-gap.txt",
                "--db", db, "--json", "--fields", "path,snippet", "--limit", "200", "--max-json-bytes", "6000"], JsonOptions, "test"));
        Assert.Equal(CommandExitCodes.PartialResult, stoppedExit);
        var stopped = JsonNode.Parse(stoppedJson)!["metadata"]!;
        Assert.True(stopped["byte_limit_reached"]?.GetValue<bool>() == true, stoppedJson);
        Assert.False(stopped["has_more"]!.GetValue<bool>());
        Assert.Null(stopped["next_cursor"]);
        var stoppedTerminal = stopped["stream_terminal"]!;
        Assert.Null(stoppedTerminal["next_cursor"]);
        Assert.False(stoppedTerminal["done"]!.GetValue<bool>());
        Assert.Equal("multiline_source_gap", stoppedTerminal["scan_truncation_reason"]!.GetValue<string>());
        Assert.NotEqual("max_json_bytes", stoppedTerminal["truncation_reason"]?.GetValue<string>());
        Assert.Contains("refresh", stoppedTerminal["recovery_guidance"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        cursor = Run(args)["metadata"]!["next_cursor"]!.GetValue<string>();
        TestProjectHelper.InsertIndexedFile(db, "changed.txt", "text", "changed");
        var (staleExit, stale, staleError) = CaptureConsole(() => ProgramRunner.Run([.. args, "--cursor", cursor], JsonOptions, "test"));
        Assert.Equal(CommandExitCodes.UsageError, staleExit);
        Assert.Contains("stale", stale + staleError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_CountCapsAndErrorsRemainTruthful_Issue5399()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_window_limits_5399");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "a.txt", "text", "A\nB A\nB\nA\nB");
        string[] args = ["find", "A\\nB", "--regex", "--multiline", "--window-lines", "2", "--all", "--db", db, "--json", "--count"];
        var full = Run(args);
        Assert.Equal(3, full["count"]!.GetValue<int>());
        Assert.True(full["authoritative_count"]!.GetValue<bool>());
        var sum = 0;
        string? cursor = null;
        for (var i = 0; i < 10; i++)
        {
            string[] pageArgs = [.. args, "--line-scan-limit", "1", .. cursor is null ? Array.Empty<string>() : new[] { "--cursor", cursor }];
            var (exit, json, _) = CaptureConsole(() => ProgramRunner.Run(pageArgs, JsonOptions, "test"));
            Assert.Contains(exit, new[] { 0, CommandExitCodes.PartialResult });
            var page = JsonNode.Parse(json)!;
            if (cursor is not null) Assert.False(page["authoritative_count"]!.GetValue<bool>());
            sum += page["count"]!.GetValue<int>();
            cursor = page["next_cursor"]?.GetValue<string>();
            if (cursor is null) break;
        }
        Assert.Null(cursor);
        Assert.Equal(3, sum);
        var (capExit, cappedJson, _) = CaptureConsole(() => ProgramRunner.Run([.. args, "--window-bytes", "2"], JsonOptions, "test"));
        Assert.Equal(CommandExitCodes.PartialResult, capExit);
        var capped = JsonNode.Parse(cappedJson)!;
        Assert.False(capped["authoritative_count"]!.GetValue<bool>());
        Assert.Equal("multiline_window_bytes", capped["scan_truncation_reason"]!.GetValue<string>());
        Assert.Null(capped["next_cursor"]);
        Assert.NotNull(capped["recovery_guidance"]);
        Assert.Equal(0, CaptureConsole(() => ProgramRunner.Run([.. args, "--window-bytes", "2", "--allow-partial"], JsonOptions, "test")).Result);
        foreach (var extra in new[] { new[] { "--window-lines", "65" }, new[] { "--window-bytes", "262145" },
            new[] { "--origin", "code" }, new[] { "--focus-line", "1" }, new[] { "--before", "1" },
            new[] { "--max-line-width", "0" } })
            Assert.Equal(CommandExitCodes.UsageError, CaptureConsole(() => ProgramRunner.Run([.. args, .. extra], JsonOptions, "test")).Result);
        Assert.Equal(CommandExitCodes.UsageError, CaptureConsole(() => ProgramRunner.Run(
            ["find", "A", "--path", "a.txt", "--multiline", "--db", db, "--json"], JsonOptions, "test")).Result);
    }

    [Fact]
    public void LargeSourceTimeoutAndCancellation_AreBounded_Issue5399()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_window_resources_5399");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        using var connection = Open(db);
        var writer = new DbWriter(connection);
        foreach (var (path, text, lines) in new[] { ("huge.txt", new string('a', FindWindowOptions.ChunkBytes + 1), 1),
            ("slow.txt", new string('a', 50_000) + "!", 1), ("cancel.txt", "A\nB", 2) })
        {
            var id = writer.UpsertFile(new FileRecord { Path = path, Lang = "text", Lines = lines, Size = text.Length });
            writer.InsertChunks([new ChunkRecord { FileId = id, ChunkIndex = 0, StartLine = 1, EndLine = lines, Content = text }]);
        }
        using var reader = new DbReader(connection);
        var huge = reader.FindInFiles("a", 20, pathPatterns: ["huge.txt"], regex: true, window: new());
        Assert.Empty(huge);
        Assert.Equal("multiline_chunk_bytes", huge.Scan.TruncationReason);
        try
        {
            DbReader.FindRegexMatchTimeoutForTesting = TimeSpan.FromMilliseconds(1);
            Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() =>
                reader.CountFindInFiles("(a+)+$", pathPatterns: ["slow.txt"], regex: true, window: new()));
        }
        finally { DbReader.FindRegexMatchTimeoutForTesting = null; }
        using var cancel = new CancellationTokenSource();
        try
        {
            DbReader.FindLineScannedForTesting = cancel.Cancel;
            Assert.Throws<OperationCanceledException>(() => reader.FindInFiles("A\\nB", 20,
                pathPatterns: ["cancel.txt"], regex: true, window: new(), cancellationToken: cancel.Token));
        }
        finally { DbReader.FindLineScannedForTesting = null; }

        var boundedText = new string('x', 1023);
        var largeId = writer.UpsertFile(new FileRecord { Path = "many.txt", Lang = "text", Lines = 1024, Size = 1048576 });
        writer.InsertChunks(Enumerable.Range(0, 16).Select(index => new ChunkRecord
        {
            FileId = largeId,
            ChunkIndex = index,
            StartLine = index * 64 + 1,
            EndLine = (index + 1) * 64,
            Content = string.Join('\n', Enumerable.Repeat(boundedText, 64)),
        }).ToList());
        var workCapped = reader.CountFindInFiles("missing", pathPatterns: ["many.txt"], regex: true, window: new(64, 262144));
        Assert.Equal("multiline_query_bytes", workCapped.Scan.TruncationReason);
        Assert.Null(workCapped.Scan.NextPath);
        Assert.True(workCapped.Scan.LinesScanned < 1024);

        using (var plan = connection.CreateCommand())
        {
            plan.CommandText = "EXPLAIN QUERY PLAN " + DbReader.FindWindowChunkSql;
            plan.Parameters.AddWithValue("@fileId", largeId);
            plan.Parameters.AddWithValue("@firstLine", 1);
            using var details = plan.ExecuteReader();
            var descriptions = new List<string>();
            while (details.Read()) descriptions.Add(details.GetString(3));
            Assert.Contains(descriptions, text => text.Contains(DbReader.BoundedResourceReadChunkIndexName, StringComparison.Ordinal));
            Assert.DoesNotContain(descriptions, text => text.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
        }
        var legacyId = writer.UpsertFile(new FileRecord
        {
            Path = "legacy-many.txt",
            Lang = "text",
            Lines = DbReader.MaxLegacyWindowChunks + 1,
            Size = DbReader.MaxLegacyWindowChunks * 2 + 1
        });
        writer.InsertChunks(Enumerable.Range(0, DbReader.MaxLegacyWindowChunks + 1).Select(index => new ChunkRecord
        {
            FileId = legacyId,
            ChunkIndex = index,
            StartLine = index + 1,
            EndLine = index + 1,
            Content = "X",
        }).ToList());
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX " + DbReader.BoundedResourceReadChunkIndexName;
            drop.ExecuteNonQuery();
        }
        using var legacyReader = new DbReader(connection);
        Assert.Equal(1, legacyReader.CountFindInFiles("A\\nB", pathPatterns: ["cancel.txt"], regex: true, window: new()).Count);
        var legacyCap = legacyReader.CountFindInFiles("X", pathPatterns: ["legacy-many.txt"], regex: true, window: new());
        Assert.Equal("multiline_source_index", legacyCap.Scan.TruncationReason);
        Assert.Null(legacyCap.Scan.NextPath);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string Position(FileFindResult row)
        => $"{row.Path}:{row.Line}:{row.Column}:{row.MatchEndLine}:{row.MatchEndColumn}:{row.Length}";

    private static JsonNode Run(string[] args)
    {
        var (exit, output, error) = CaptureConsole(() => ProgramRunner.Run(args, JsonOptions, "test"));
        Assert.True(exit == 0, output + error);
        return JsonNode.Parse(output)!;
    }
}
