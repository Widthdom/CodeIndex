using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerFindIssue5324Tests
{
    [Fact]
    public void FilteredRegex_UsesExactSpansAndFiltersBeforeCountsAndPages()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_5324");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "tests/Mixed.cs", "csharp",
            "// Needle\nvar text = \"Needle\"; Needle(); Needle();\n/* Needle */\n");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        connection.Open();
        using var reader = new DbReader(connection);
        var code = new FindSemanticFilters(["code"], [], []);
        var rows = reader.FindInFiles("Needle", 20, regex: true, semanticFilters: code);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("code", Assert.Single(row.MatchFacets!).Origin));
        Assert.Equal(new[] { 22, 32 }, rows.Select(row => row.Column));
        Assert.Equal(2, reader.CountFindInFiles("Needle", regex: true, semanticFilters: code).Count);
        Assert.Single(reader.FindInFiles("Needle", 1, regex: true, offset: 1, semanticFilters: code));
        Assert.Equal(32, reader.FindInFiles("Needle", 1, regex: true, offset: 1, semanticFilters: code)[0].Column);
        var zero = reader.FindInFiles("(?=Needle)", 20, regex: true, semanticFilters: code);
        Assert.Equal(2, zero.Count);
        Assert.All(zero, row =>
        {
            Assert.Equal(0, row.Length);
            Assert.Equal(0, Assert.Single(row.MatchFacets!).Length);
        });
        Assert.Equal(rows.Select(row => row.Column), zero.Select(row => row.Column));
        var strings = reader.FindInFiles("Needle", 20, regex: true,
            semanticFilters: new(["string_literal"], [], []));
        Assert.True(Assert.Single(Assert.Single(strings).MatchFacets!).TestFixture);
        Assert.Equal(4, reader.CountFindInFiles("Needle", regex: true,
            semanticFilters: new([], [], [], ExcludeFixtures: true)).Count);
        Assert.Equal(2, reader.CountFindInFiles("Needle", regex: true,
            semanticFilters: new([], ["comment", "string_literal"], [])).Count);
        Assert.Equal(2, reader.CountFindInFiles("Needle", regex: true,
            semanticFilters: new([], [], ["identifier"])).Count);
        Assert.Equal(5, reader.CountFindInFiles("Needle").Count);
        Assert.All(reader.FindInFiles("Needle", 20), row => Assert.Null(row.MatchFacets));

        var (exit, output, error) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["Needle", "--regex", "--origin", "code", "--path", "tests/", "--db", db, "--json", "--count"], JsonOptions));
        Assert.Equal(0, exit);
        Assert.Empty(error);
        using var count = JsonDocument.Parse(output);
        Assert.Equal(2, count.RootElement.GetProperty("count").GetInt32());
        Assert.True(count.RootElement.GetProperty("origin_classification_complete").GetBoolean());
    }

    [Fact]
    public void UnknownOrigins_RemainPartialEvenWhenExcludedAndCapsCanResume()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_unknown_5324");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/a.cs", "csharp", "Needle();\nNeedle();\n");
        TestProjectHelper.InsertIndexedFile(db, "src/b.txt", "text", "Needle\n");
        var args = new[] { "Needle", "--regex", "--origin", "code", "--all", "--db", db, "--json", "--count" };
        var (exit, output, _) = CaptureConsole(() => QueryCommandRunner.RunFind(args, JsonOptions));
        Assert.Equal(CommandExitCodes.PartialResult, exit);
        using var unknown = JsonDocument.Parse(output);
        Assert.Equal(2, unknown.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, unknown.RootElement.GetProperty("unknown_origin_matches").GetInt32());
        Assert.False(unknown.RootElement.GetProperty("authoritative_count").GetBoolean());
        Assert.False(unknown.RootElement.GetProperty("origin_classification_complete").GetBoolean());

        var (capExit, capOutput, _) = CaptureConsole(() => QueryCommandRunner.RunFind(
            [.. args, "--line-scan-limit", "1"], JsonOptions));
        Assert.Equal(CommandExitCodes.PartialResult, capExit);
        using var capped = JsonDocument.Parse(capOutput);
        var cursor = capped.RootElement.GetProperty("next_cursor").GetString()!;
        var (resumeExit, resumeOutput, _) = CaptureConsole(() => QueryCommandRunner.RunFind(
            [.. args, "--cursor", cursor], JsonOptions));
        Assert.Equal(CommandExitCodes.PartialResult, resumeExit);
        using var resumed = JsonDocument.Parse(resumeOutput);
        Assert.Equal(1, resumed.RootElement.GetProperty("count").GetInt32());
        var (changedExit, _, _) = CaptureConsole(() => QueryCommandRunner.RunFind(
            [.. args, "--exclude-origin", "comment", "--cursor", cursor], JsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, changedExit);
        Assert.Equal(0, CaptureConsole(() => QueryCommandRunner.RunFind([.. args, "--allow-partial"], JsonOptions)).Result);
    }

    [Fact]
    public void FilteredRegex_BoundedJsonPreservesPaginationAndUnknownAuthority()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_pages_5324");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/a.cs", "csharp",
            "// Needle\nvar s = \"Needle\"; Needle(); Needle();\nNeedle();\n");
        var args = new[] { "find", "(?=Needle)", "--regex", "--origin", "code", "--path", "src/", "--db", db,
            "--json", "--limit", "1", "--max-json-bytes", "6000", "--fields", "path,line,column,length,match_facets,result_kinds" };
        var locations = new List<(int Line, int Column)>();
        string? cursor = null;
        for (var page = 0; page < 4; page++)
        {
            var pageArgs = cursor is null ? args : [.. args, "--cursor", cursor];
            var (exit, output, error) = CaptureConsole(() => ProgramRunner.Run(pageArgs, JsonOptions, "test"));
            Assert.Equal(0, exit);
            Assert.Empty(error);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(output) <= 6000);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var row = Assert.Single(root.GetProperty("results").EnumerateArray());
            Assert.Equal(0, row.GetProperty("length").GetInt32());
            Assert.Equal("code", row.GetProperty("match_facets")[0].GetProperty("origin").GetString());
            locations.Add((row.GetProperty("line").GetInt32(), row.GetProperty("column").GetInt32()));
            cursor = root.GetProperty("metadata").GetProperty("next_cursor").GetString();
            if (cursor is null)
                break;
        }
        Assert.Null(cursor);
        Assert.Equal(3, locations.Count);
        Assert.Equal(3, locations.Distinct().Count());

        TestProjectHelper.InsertIndexedFile(db, "src/unknown.txt", "text", "Needle\n");
        var (unknownExit, unknownOutput, _) = CaptureConsole(() => ProgramRunner.Run(
            ["find", "Needle", "--regex", "--origin", "comment", "--path", "src/unknown.txt", "--db", db,
                "--json", "--max-json-bytes", "6000", "--fields", "path"], JsonOptions, "test"));
        Assert.Equal(CommandExitCodes.PartialResult, unknownExit);
        using var unknown = JsonDocument.Parse(unknownOutput);
        Assert.False(unknown.RootElement.GetProperty("metadata").GetProperty("total_count_authoritative").GetBoolean());
        Assert.Contains("origin_classification", unknownOutput, StringComparison.Ordinal);

        TestProjectHelper.InsertIndexedFile(db, "src/triple.txt", "text", "Needle Needle Needle\n");
        cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var unknownArgs = new[] { "find", "Needle", "--regex", "--origin", "unknown", "--path", "src/triple.txt",
                "--db", db, "--json", "--limit", "1", "--max-json-bytes", "6000" };
            var (exit, output, _) = CaptureConsole(() => ProgramRunner.Run(
                cursor is null ? unknownArgs : [.. unknownArgs, "--cursor", cursor], JsonOptions, "test"));
            Assert.Equal(CommandExitCodes.PartialResult, exit);
            using var document = JsonDocument.Parse(output);
            var metadata = document.RootElement.GetProperty("metadata");
            var terminal = metadata.GetProperty("stream_terminal");
            // The next-match lookahead is observed, but pre-cursor matches must not be recounted.
            Assert.Equal(page == 2 ? 1 : 2, terminal.GetProperty("unknown_origin_matches").GetInt32());
            Assert.False(metadata.GetProperty("total_count_authoritative").GetBoolean());
            cursor = metadata.GetProperty("next_cursor").GetString();
            if (page < 2)
                Assert.NotNull(cursor);
        }
        Assert.Null(cursor);
    }

    [Fact]
    public void FilteredRegex_SharedMultilineContextAndClassificationBudgetsAreIndependentOfPages()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_context_5324");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "tests/Context.cs", "csharp",
            "/*\nNeedle\n*/\nvar s = @\"\nNeedle\n\";\nNeedle();\n" + new string('\n', 4090) + "Needle();\n");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        connection.Open();
        using var reader = new DbReader(connection);
        var code = new FindSemanticFilters(["code"], [], []);
        var result = reader.FindInFiles("Needle", 20, regex: true, semanticFilters: code);
        Assert.Equal(7, Assert.Single(result).Line);
        Assert.Equal(1, result.Scan.UnknownOriginMatches);
        var comments = reader.FindInFiles("Needle", 20, regex: true, semanticFilters: new(["comment"], [], []));
        Assert.Equal(2, Assert.Single(comments).Line);
        var strings = reader.FindInFiles("Needle", 20, regex: true, semanticFilters: new(["string_literal"], [], []));
        Assert.Equal(5, Assert.Single(strings).Line);
        Assert.True(Assert.Single(strings[0].MatchFacets!).TestFixture);
        Assert.Equal(1, reader.CountFindInFiles("Needle", regex: true, semanticFilters: code).Count);
    }

    [Fact]
    public void FilteredRegex_CliSchemaAndUnsupportedCombinationsAgree()
    {
        foreach (var flag in new[] { "--origin", "--match-origin", "--exclude-origin", "--result-kind",
                     "--exclude-comments", "--exclude-strings", "--exclude-fixtures" })
            Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("find"), value => value.Name == flag);
        foreach (var options in new[] { new[] { "--origin", "code" }, new[] { "--regex", "--result-kind", "call_site" },
                     new[] { "--regex", "--origin", "code", "--json=array" } })
        {
            var (exit, _, error) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["Needle", "--all", .. options], JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void FilteredRegex_CancellationAndTimeoutRemainBounded()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_cancel_5324");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/a.cs", "csharp", "Needle();\nNeedle();\n");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        connection.Open();
        using var reader = new DbReader(connection);
        using var cancel = new CancellationTokenSource();
        try
        {
            DbReader.FindLineScannedForTesting = () => cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => reader.FindInFiles("Needle", 20, regex: true,
                cancellationToken: cancel.Token, semanticFilters: new(["code"], [], [])));
        }
        finally
        {
            DbReader.FindLineScannedForTesting = null;
        }
        Assert.Equal(2, reader.FindInFiles("Needle", 20, regex: true, semanticFilters: new(["code"], [], [])).Count);
        TestProjectHelper.InsertIndexedFile(db, "src/slow.cs", "csharp", new string('a', 100000) + "!");
        try
        {
            DbReader.FindRegexMatchTimeoutForTesting = TimeSpan.FromMilliseconds(1);
            Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => reader.FindInFiles(
                "(a+)+$", 20, regex: true, semanticFilters: new(["code"], [], [])));
        }
        finally
        {
            DbReader.FindRegexMatchTimeoutForTesting = null;
        }
    }
}
