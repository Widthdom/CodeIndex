using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class OriginContinuationIssue5348Tests
{
    [Fact]
    public void LexicalWindows_PreserveMultilineStateCoordinatesAndHardLimits()
    {
        (string Open, string Middle, string Close, string Origin)[] cases =
        [
            ("/*", "😀 日本語 Needle", "*/", "comment"),
            ("var s = @\"", "😀 \"\"Needle\"\"", "\";", "string_literal"),
            ("var s = \"\"\"\"", "😀 \"\"\" Needle", "\"\"\"\";", "string_literal"),
            ("var s = $\"{", "日本語.Needle()", "}\";", "code"),
            ("var s = $@\"{", "Call($\"{Needle()}\", \"}\")", "}\";", "code"),
            ("var s = $$\"\"\"{{", "/* } */ 日本語.Needle()", "}}\"\"\";", "code"),
            ("var s = $@\"{value:", "Needle", "}\";", "string_literal"),
            ("var s = $\"{value:", "Needle", "}\";", "unknown"),
            ("var s = $@\"{", "Needle(]", "}\";", "unknown"),
            ("var s = $@\"{", "Needle()", "", "unknown"),
        ];
        foreach (var (open, middle, close, expected) in cases)
        {
            var lines = Enumerable.Repeat("", 8194).ToArray();
            lines[4094] = open;
            lines[4096] = middle;
            lines[4097] = close;
            lines[8192] = "Needle();";
            var starts = new List<int>();
            SearchMatchClassifier.CSharpOriginWindow Read(int start)
            {
                starts.Add(start);
                return new(Enumerable.Range(start, Math.Min(4096, lines.Length - start + 1))
                    .ToDictionary(line => line, line => lines[line - 1]));
            }
            var column = middle.IndexOf("Needle", StringComparison.Ordinal);
            var prefix = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", Read, 1);
            Assert.Equal("unknown", prefix.GetOrigin(4097, middle, column));
            starts.Clear();
            var resumed = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", Read, 3);
            var facet = SearchMatchClassifier.Classify("src/a.cs", "csharp", 4097, middle,
                column + 1, 6, csharpContext: resumed);
            Assert.True(expected == facet.Origin, $"{open} / {middle}: {facet.Origin}");
            Assert.Equal((4097, column + 1, 6), (facet.Line, facet.Column, facet.Length));
            if (expected != "unknown")
            {
                Assert.Equal(new[] { 1, 4097, 8193 }, starts);
                Assert.Equal("code", resumed.GetOrigin(8193, lines[8192], 0));
            }
            else
                Assert.Null(facet.OriginUnavailable!.RetryOriginPasses);
        }

        var huge = new string(' ', SearchMatchClassifier.CSharpContextCharacterLimit);
        var reads = 0;
        SearchMatchClassifier.CSharpOriginWindow Characters(int start)
        {
            reads++;
            return new(start == 1
                ? new Dictionary<int, string> { [1] = huge, [2] = "Needle();" }
                : new Dictionary<int, string> { [2] = "Needle();" });
        }
        var bounded = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", Characters, 1);
        Assert.Equal("character_budget_exhausted", bounded.GetUnavailable(2, "Needle();").Reason);
        Assert.Equal(2, bounded.GetUnavailable(2, "Needle();").RetryOriginPasses);
        reads = 0;
        var complete = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", Characters, 2);
        Assert.Equal(2, reads);
        Assert.Equal("code", complete.GetOrigin(2, "Needle();", 0));
        var stalled = new SearchMatchClassifier.CSharpOriginContext("src/a.cs",
            _ => new(new Dictionary<int, string>(), "character_budget_exhausted"), 16);
        Assert.Null(stalled.GetUnavailable(1, "Needle").RetryOriginPasses);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchMatchClassifier.CSharpOriginContext("a.cs", Characters, 17));
    }

    [Fact]
    public void Queries_KeepRowsCountsOriginsAndCursorPagesConsistent()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_origin_5348");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var lines = Enumerable.Repeat("", 8199).ToArray();
        lines[0] = "Needle();";
        lines[4094] = "/*";
        lines[4096] = "😀 Needle";
        lines[4097] = "*/";
        lines[4199] = "日本語.Needle();";
        lines[8190] = "var s = $$\"\"\"{{";
        lines[8192] = "Needle()";
        lines[8193] = "}}\"\"\";";
        Seed(dbPath, lines);
        using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var reader = new DbReader(db);
        foreach (var passes in new[] { 1, 2, 3 })
        {
            reader.OriginPasses = passes;
            var search = SearchSnippetFormatter.ToCompactResults(
                reader.Search("Needle", 30, exact: true, deduplicate: false), "Needle", exposeLiteralHighlights: true)
                .SelectMany(row => row.MatchFacets).OrderBy(facet => facet.Line).ToArray();
            var find = reader.FindInFiles("Needle", 30, regex: true, semanticFilters: new([], [], []));
            Assert.Equal(search.Select(f => (f.Line, f.Column, f.Length, f.Origin)),
                find.Select(row => Assert.Single(row.MatchFacets!)).Select(f => (f.Line, f.Column, f.Length, f.Origin)));
            foreach (var origin in new[] { "code", "comment", "unknown" })
            {
                var filters = new FindSemanticFilters([origin], [], []);
                var expected = search.Where(f => f.Origin == origin).ToArray();
                Assert.Equal(expected.Length, reader.CountFindInFiles("Needle", regex: true, semanticFilters: filters).Count);
                Assert.Equal(expected.Select(f => f.Line), reader.FindInFiles("Needle", 30, regex: true, semanticFilters: filters).Select(r => r.Line));
                foreach (var command in new[] { "search", "find" })
                {
                    var args = new[] { command, "Needle", "--path", "src/a.cs", "--db", dbPath, "--origin", origin,
                        "--origin-passes", passes.ToString(), "--json", "--count", command == "find" ? "--regex" : "--exact" };
                    var (_, output, error) = CaptureConsole(() => ProgramRunner.Run(args, JsonOptions, "test"));
                    Assert.Empty(error);
                    using var count = JsonDocument.Parse(output);
                    Assert.Equal(expected.Length, count.RootElement.GetProperty("count").GetInt32());
                    if (command == "find")
                    {
                        Assert.Equal(passes == 3, count.RootElement.GetProperty("origin_classification_complete").GetBoolean());
                        if (passes < 3)
                            Assert.Equal(passes + 1, count.RootElement.GetProperty("retry_origin_passes").GetInt32());
                    }
                }
            }
        }

        foreach (var (passes, origin, expectedCount) in new[] { (3, "code", 3), (1, "unknown", 3) })
        {
            var args = new[] { "find", "(?=Needle)", "--regex", "--path", "src/a.cs", "--db", dbPath,
                "--origin", origin, "--origin-passes", passes.ToString(), "--json", "--limit", "1", "--max-json-bytes", "8000",
                "--fields", "path,line,column,length,match_facets" };
            var positions = new HashSet<(int, int)>();
            string? cursor = null;
            string? firstCursor = null;
            for (var page = 0; page < 4; page++)
            {
                var (exit, output, error) = CaptureConsole(() => ProgramRunner.Run(
                    cursor is null ? args : [.. args, "--cursor", cursor], JsonOptions, "test"));
                Assert.Equal(origin == "unknown" ? CommandExitCodes.PartialResult : 0, exit);
                Assert.Empty(error);
                using var result = JsonDocument.Parse(output);
                var row = Assert.Single(result.RootElement.GetProperty("results").EnumerateArray());
                Assert.Equal(0, row.GetProperty("length").GetInt32());
                Assert.True(positions.Add((row.GetProperty("line").GetInt32(), row.GetProperty("column").GetInt32())));
                cursor = result.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString();
                firstCursor ??= cursor;
                if (cursor is null)
                    break;
            }
            Assert.Null(cursor);
            Assert.Equal(expectedCount, positions.Count);
            var (changed, _, _) = CaptureConsole(() => ProgramRunner.Run(
                [.. args, "--origin-passes", "2", "--cursor", firstCursor!], JsonOptions, "test"));
            Assert.Equal(CommandExitCodes.UsageError, changed);
        }
        foreach (var command in new[] { "search", "audit", "find" })
        {
            Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand(command), flag => flag.Name == "--origin-passes");
            foreach (var value in new[] { "0", "17", "-1", "bad", "999999999999999" })
            {
                var (exit, output, error) = CaptureConsole(() => ProgramRunner.Run(
                    [command, "Needle", "--db", dbPath, "--path", "src/a.cs", "--origin-passes=" + value, "--json"], JsonOptions, "test"));
                Assert.Equal(CommandExitCodes.UsageError, exit);
                Assert.Empty(error);
                using var failure = JsonDocument.Parse(output);
            }
        }
    }

    [Fact]
    public void Queries_DiscardChangedGenerationAndPreserveMissingMalformedAndCancelledUnknowns()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_origin_state_5348");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var lines = Enumerable.Repeat("", 4098).ToArray();
        lines[0] = "Needle();";
        lines[4096] = "Needle();";
        Seed(dbPath, lines);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var reader = new DbReader(db.Connection) { OriginPasses = 2 };
        using var cancel = new CancellationTokenSource();
        reader.OriginWindowStartingForTesting = start => { if (start > 1) cancel.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => reader.FindInFiles("Needle", 10, regex: true,
            cancellationToken: cancel.Token, semanticFilters: new(["code"], [], [])));
        reader.OriginWindowStartingForTesting = null;
        Assert.Equal(2, reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["code"], [], [])).Count);

        void Execute(string sql)
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        reader.OriginWindowStartingForTesting = start =>
        {
            if (start > 1)
                Execute("UPDATE chunks SET content = '/*' || content WHERE start_line = 1");
        };
        var changed = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["code"], [], []));
        Assert.Equal(0, changed.Count);
        Assert.Contains("indexed_generation_changed", changed.Scan.OriginIncompleteReasons!);
        Assert.Null(changed.Scan.RetryOriginPasses);
        reader.OriginWindowStartingForTesting = null;
        Assert.Equal(2, reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["comment"], [], [])).Count);

        Execute("DELETE FROM chunks WHERE start_line = 4001");
        var missing = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["unknown"], [], []));
        Assert.Equal(1, missing.Count);
        Assert.Contains("indexed_prefix_unavailable", missing.Scan.OriginIncompleteReasons!);
        Assert.Null(missing.Scan.RetryOriginPasses);
        Execute("UPDATE chunks SET content = '$\"{Call(}\";' WHERE start_line = 1");
        var malformed = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["unknown"], [], []));
        Assert.Equal(1, malformed.Count);
        Assert.Contains("unbalanced_interpolation", malformed.Scan.OriginIncompleteReasons!);
        Assert.Null(malformed.Scan.RetryOriginPasses);

        Execute("DELETE FROM chunks");
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/a.cs",
            Lang = "csharp",
            Lines = 129,
            Size = 129,
            Modified = DateTime.UtcNow,
            Checksum = "overlap",
        });
        writer.InsertChunks(Enumerable.Range(1, 128).Select(line => new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = line - 1,
            StartLine = line,
            EndLine = line,
            Content = line == 128 ? "/*" : "",
        }).Append(new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 128,
            StartLine = 128,
            EndLine = 129,
            Content = "// changed\nNeedle();",
        }).ToList());
        var conflicting = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["code"], [], []));
        Assert.Equal(0, conflicting.Count);
        Assert.Equal(1, conflicting.Scan.UnknownOriginMatches);
        Assert.Contains("indexed_text_mismatch", conflicting.Scan.OriginIncompleteReasons!);
        Assert.Null(conflicting.Scan.RetryOriginPasses);
        Assert.Single(reader.FindInFiles("Needle", 10, regex: true, semanticFilters: new(["unknown"], [], [])));
        var searchConflict = Assert.Single(SearchSnippetFormatter.ToCompactResults(
            reader.Search("Needle", 10, exact: true), "Needle", exposeLiteralHighlights: true));
        Assert.Equal("unknown", Assert.Single(searchConflict.MatchFacets).Origin);

        Execute("UPDATE chunks SET content = '/*\nNeedle();' WHERE chunk_index = 128");
        var consistent = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["comment"], [], []));
        Assert.Equal(1, consistent.Count);
        Assert.Equal(0, consistent.Scan.UnknownOriginMatches);

        Execute("UPDATE chunks SET content = '$@\"{' WHERE chunk_index = 125");
        Execute("UPDATE chunks SET content = 'Needle()' WHERE chunk_index = 126");
        Execute("UPDATE chunks SET content = '}\";' WHERE chunk_index = 127");
        Execute("UPDATE chunks SET content = '// changed\nNeedle();' WHERE chunk_index = 128");
        var completedInterpolation = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["code"], [], []));
        Assert.Equal(0, completedInterpolation.Count);
        Assert.Equal(2, completedInterpolation.Scan.UnknownOriginMatches);
        Assert.Contains("indexed_text_mismatch", completedInterpolation.Scan.OriginIncompleteReasons!);
        Execute("UPDATE chunks SET content = '}\";\nNeedle();' WHERE chunk_index = 128");
        var restoredInterpolation = reader.CountFindInFiles("Needle", regex: true, semanticFilters: new(["code"], [], []));
        Assert.Equal(2, restoredInterpolation.Count);
        Assert.Equal(0, restoredInterpolation.Scan.UnknownOriginMatches);
    }

    private static void Seed(string dbPath, string[] lines)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        var id = writer.UpsertFile(new FileRecord
        {
            Path = "src/a.cs",
            Lang = "csharp",
            Lines = lines.Length,
            Size = string.Join('\n', lines).Length,
            Modified = DateTime.UtcNow,
            Checksum = "fixture",
        });
        writer.InsertChunks(Enumerable.Range(0, (lines.Length + 79) / 80).Select(index => new ChunkRecord
        {
            FileId = id,
            ChunkIndex = index,
            StartLine = index * 80 + 1,
            EndLine = Math.Min(lines.Length, (index + 1) * 80),
            Content = string.Join('\n', lines.Skip(index * 80).Take(80)),
        }).ToList());
    }
}
