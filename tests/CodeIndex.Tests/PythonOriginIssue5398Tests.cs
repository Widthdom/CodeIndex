using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class PythonOriginIssue5398Tests
{
    [Fact]
    public void LexicalOrigins_PreservePythonRegionsAndUtf16Coordinates()
    {
        (string Source, string[] Origins)[] cases =
        [
            ("import Needle\nNeedle.run() # Needle\n# Needle", ["code", "code", "comment", "comment"]),
            ("x = 'Needle'; y = \"Needle\"; Needle()", ["string_literal", "string_literal", "code"]),
            ("x = '''\nNeedle\n'''; Needle()\nx = \"\"\"Needle\"\"\"", ["string_literal", "code", "string_literal"]),
            ("r'\\\'Needle'; R\"Needle\"; b'Needle'; Br'Needle'; rb\"Needle\"; u'Needle'", Enumerable.Repeat("string_literal", 6).ToArray()),
            ("x = 'a\\\'Needle'; x = \"a\\\"Needle\"; Needle()", ["string_literal", "string_literal", "code"]),
            ("x = 'a\\\nNeedle'; Needle()", ["string_literal", "code"]),
            ("f'Needle {{Needle}} {Needle()} Needle'", ["string_literal", "string_literal", "code", "string_literal"]),
            ("rf'Needle \\{Needle()}'; Fr\"{Needle!r:Needle>{Needle}}\"", ["string_literal", "code", "code", "string_literal", "code"]),
            ("f\"{Needle('Needle', {'Needle': Needle}, f'{Needle()} Needle')}\"", ["code", "string_literal", "string_literal", "code", "code", "string_literal"]),
            ("f\"{Needle[\"Needle\"] # Needle\n} Needle\"", ["code", "string_literal", "comment", "string_literal"]),
            ("f'''Needle\n{Needle(\"\"\"Needle\nNeedle\"\"\")}\nNeedle'''", ["string_literal", "code", "string_literal", "string_literal", "string_literal"]),
            ("f'{Needle=}' f'{Needle = !s:>20}' f'{Needle != Needle}'", ["code", "code", "code", "code"]),
            ("f'\\N{LEFT CURLY BRACKET}Needle {Needle:{Needle}.{Needle}f}'", ["string_literal", "code", "code", "code"]),
            ("def f(): return\"Needle\"\nNeedle()", ["string_literal", "code"]),
            ("x = 'Needle'if'Needle'else'Needle'; Needle()", ["string_literal", "string_literal", "string_literal", "code"]),
            ("f'{value:\\N{Sewing Needle}} Needle'; Needle()", ["string_literal", "string_literal", "code"]),
            ("rf'{value:\\N{Needle}}' f'{value:\\\\N{Needle}}'", ["code", "code"]),
            ("f'{value:\\\nNeedle}'; Needle()", ["string_literal", "code"]),
            ("f'{Needle!r }' f'{Needle!r :>12}'; Needle()", ["code", "code", "code"]),
            ("f'{Needle= # Needle\n}'; Needle()", ["code", "comment", "code"]),
            ("f'{Needle!r # Needle\n}'; Needle()", ["code", "comment", "code"]),
            ("f'{Needle! # Needle\n r}'; Needle()", ["code", "comment", "code"]),
        ];
        foreach (var (source, expected) in cases)
        foreach (var newline in new[] { "\n", "\r\n" })
        foreach (var prefix in new[] { "", "x = 'ASCII'; ", "x = '日本語'; ", "x = '😀'; " })
        {
            var text = prefix + source.Replace("\n", newline, StringComparison.Ordinal);
            var lines = Lines(text);
            var context = new SearchMatchClassifier.PythonOriginContext(lines);
            var actual = new List<string>();
            foreach (var (line, value) in lines)
            foreach (Match match in Regex.Matches(value, "Needle"))
            {
                var facet = SearchMatchClassifier.Classify("tests/test_origins.py", "python", line, value,
                    match.Index + 1, match.Length, pythonContext: context);
                actual.Add(facet.Origin);
                Assert.Equal((line, match.Index + 1, 6), (facet.Line, facet.Column, facet.Length));
                Assert.Equal(facet.Origin == "string_literal", facet.TestFixture);
            }
            Assert.True(expected.SequenceEqual(actual), $"{text}: {string.Join(',', actual)}");
        }
    }

    [Fact]
    public void LexicalUnknowns_RetainStableReasonsAndBoundStateAndCancellation()
    {
        foreach (var (source, reason) in new[]
        {
            ("f'{Needle(]}'", "unbalanced_interpolation"),
            ("f'Needle }'", "unbalanced_interpolation"),
            ("f'{Needle!z}'", "unsupported_interpolation_conversion"),
            ("f'{Needle!r x}'", "unsupported_interpolation_expression"),
            ("f'{Needle:\n}'", "unterminated_ordinary_string"),
            ("x = 'Needle", "unterminated_ordinary_string"),
            ("t'Needle {value}'", "unsupported_python_string_prefix"),
            ("f'{" + new string('(', 65) + "Needle" + new string(')', 65) + "}'", "interpolation_nesting_limit"),
            ("f'{" + string.Concat(Enumerable.Repeat("f\"{", 33)) + "Needle", "interpolation_nesting_limit"),
            ("'''Needle\n", "indexed_prefix_unavailable"),
        })
        {
            var lines = Lines(source);
            var context = new SearchMatchClassifier.PythonOriginContext(lines);
            var target = lines.First(pair => pair.Value.Contains("Needle", StringComparison.Ordinal));
            Assert.Equal("unknown", context.GetOrigin(target.Key, target.Value, target.Value.IndexOf("Needle", StringComparison.Ordinal)));
            var unavailable = context.GetUnavailable(target.Key, target.Value);
            Assert.Equal(reason, unavailable.Reason);
            Assert.Null(unavailable.RetryOriginPasses);
            Assert.NotEmpty(unavailable.RecoveryGuidance!);
        }
        var missing = new SearchMatchClassifier.PythonOriginContext(new Dictionary<int, string> { [2] = "Needle()" });
        Assert.Equal("unknown", missing.GetOrigin(2, "Needle()", 0));
        var mismatch = new SearchMatchClassifier.PythonOriginContext(Lines("Needle()"));
        Assert.Equal("unknown", mismatch.GetOrigin(1, "# Needle", 2));
        Assert.Equal("indexed_text_mismatch", mismatch.GetUnavailable(1, "# Needle").Reason);
        var repeated = string.Concat(Enumerable.Repeat("Needle() ", 4096));
        var shared = new SearchMatchClassifier.PythonOriginContext(Lines(repeated));
        var view = new string(repeated.ToCharArray());
        foreach (Match match in Regex.Matches(view, "Needle"))
            Assert.Equal("code", shared.GetOrigin(1, view, match.Index));
        Assert.Equal(view.Length, shared.ComparedCharacters);

        var dense = string.Concat(Enumerable.Repeat("'' ", SearchMatchClassifier.PythonOriginContext.SpanLimit + 1)) + "Needle";
        var bounded = new SearchMatchClassifier.PythonOriginContext(Lines(dense));
        Assert.Equal("unknown", bounded.GetOrigin(1, dense, dense.Length - 6));
        Assert.Equal("python_span_budget_exhausted", bounded.GetUnavailable(1, dense).Reason);
        var huge = new string(' ', SearchMatchClassifier.CSharpContextCharacterLimit);
        var starts = new List<int>();
        SearchMatchClassifier.CSharpOriginWindow Read(int first)
        {
            starts.Add(first);
            return new(first == 1 ? new Dictionary<int, string> { [1] = huge, [2] = "Needle()" }
                : new Dictionary<int, string> { [2] = "Needle()" });
        }
        var onePass = new SearchMatchClassifier.PythonOriginContext(Read, 1);
        Assert.Equal("unknown", onePass.GetOrigin(2, "Needle()", 0));
        Assert.Equal("character_budget_exhausted", onePass.GetUnavailable(2, "Needle()").Reason);
        Assert.Equal(2, onePass.GetUnavailable(2, "Needle()").RetryOriginPasses);
        starts.Clear();
        var twoPasses = new SearchMatchClassifier.PythonOriginContext(Read, 2);
        Assert.Equal("code", twoPasses.GetOrigin(2, "Needle()", 0));
        Assert.Equal(new[] { 1, 2 }, starts);
        var stalled = new SearchMatchClassifier.PythonOriginContext(
            _ => new(new Dictionary<int, string>(), "character_budget_exhausted"), 16);
        Assert.Null(stalled.GetUnavailable(1, "Needle").RetryOriginPasses);
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => new SearchMatchClassifier.PythonOriginContext(start =>
        {
            cancellation.Cancel();
            return new(Lines("Needle()"));
        }, 1, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new SearchMatchClassifier.PythonOriginContext(Lines(repeated), cancellation.Token));
    }

    [Fact]
    public void ChunkBudget_ContinuesWithoutForgettingAnOpenString()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_python_chunks_5398");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var lines = Enumerable.Repeat("", 132).ToArray();
        lines[125] = "'''";
        lines[128] = "Needle";
        lines[129] = "'''";
        lines[130] = "Needle()";
        Seed(dbPath, lines, 1);
        using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var reader = new DbReader(db);
        var bounded = reader.FindInFiles("Needle", 10, regex: true, semanticFilters: new([], [], []));
        Assert.Equal(2, bounded.Scan.UnknownOriginMatches);
        Assert.All(bounded, row => Assert.Equal("chunk_budget_exhausted", row.MatchFacets![0].OriginUnavailable!.Reason));
        reader.OriginPasses = 2;
        var continued = reader.FindInFiles("Needle", 10, regex: true, semanticFilters: new([], [], []));
        Assert.Equal(new[] { "string_literal", "code" }, continued.Select(row => row.MatchFacets![0].Origin));
        Assert.Equal(0, continued.Scan.UnknownOriginMatches);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void IndexedQueries_ShareFacetsFiltersCountsAndZeroWidthPositions(string newline)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_python_5398");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var lines = new[] { "# Needle", "x = '''", "😀 日本語 Needle", "'''", "Needle() # Needle",
            "x = rf'Needle {{Needle}} {Needle(\"Needle\"):{Needle}}'", "Needle(); Needle()", "" };
        Seed(dbPath, lines, 2, newline);
        using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var reader = new DbReader(db);
        var searchRows = reader.Search("Needle", 50, exact: true, deduplicate: false);
        Assert.All(searchRows, row => Assert.Same(searchRows[0].PythonOrigins, row.PythonOrigins));
        var search = SearchSnippetFormatter.ToCompactResults(searchRows, "Needle", exposeLiteralHighlights: true)
            .SelectMany(row => row.MatchFacets).OrderBy(f => f.Line).ThenBy(f => f.Column).ToArray();
        var find = reader.FindInFiles("Needle", 50, regex: true, semanticFilters: new([], [], []));
        Assert.Equal(search.Select(Identity), find.Select(row => Identity(Assert.Single(row.MatchFacets!))));
        Assert.Equal(0, find.Scan.UnknownOriginMatches);
        foreach (var filters in new FindSemanticFilters[]
        {
            new(["code"], [], []), new(["comment"], [], []), new(["string_literal"], [], []),
            new([], ["string_literal", "comment"], []), new([], [], ["identifier"]),
            new([], [], [], ExcludeComments: true), new([], [], [], ExcludeStrings: true),
            new([], [], [], ExcludeFixtures: true),
        })
        {
            var expected = search.Where(filters.Accepts).ToArray();
            Assert.Equal(expected.Length, reader.CountFindInFiles("Needle", regex: true, semanticFilters: filters).Count);
            var zero = reader.FindInFiles("(?=Needle)", 50, regex: true, semanticFilters: filters);
            Assert.Equal(expected.Select(f => (f.Line, f.Column, f.Origin)), zero.Select(row => (row.Line, row.Column, row.MatchFacets![0].Origin)));
            Assert.All(zero, row => Assert.Equal((0, 0), (row.Length, row.MatchFacets![0].Length)));
        }
        var ends = reader.FindInFiles("$", 50, regex: true, semanticFilters: new([], [], []));
        Assert.Equal(0, ends.Scan.UnknownOriginMatches);
        Assert.Equal("string_literal", Assert.Single(ends.Where(r => r.Line == 3)).MatchFacets![0].Origin);
        Assert.Equal("comment", Assert.Single(ends.Where(r => r.Line == 5)).MatchFacets![0].Origin);
        Assert.Equal("code", Assert.Single(ends.Where(r => r.Line == 8)).MatchFacets![0].Origin);

        foreach (var command in new[] { "search", "find" })
        foreach (var origin in new[] { "code", "comment", "string_literal" })
        {
            var args = new[] { command, "Needle", "--path", "tests/test_origins.py", "--db", dbPath,
                "--origin", origin, "--json", "--count", command == "find" ? "--regex" : "--exact" };
            var (exit, output, error) = CaptureConsole(() => ProgramRunner.Run(args, JsonOptions, "test"));
            Assert.Equal(0, exit);
            Assert.Empty(error);
            using var result = JsonDocument.Parse(output);
            Assert.True(result.RootElement.GetProperty("origin_classification_complete").GetBoolean());
            Assert.True(result.RootElement.GetProperty("authoritative_count").GetBoolean());
            var expected = command == "find" ? search.Count(f => f.Origin == origin)
                : SearchSnippetFormatter.ToCompactResults(searchRows, "Needle", exposeLiteralHighlights: true)
                    .Count(row => row.MatchFacets.Any(f => f.Origin == origin));
            Assert.Equal(expected, result.RootElement.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public void Continuation_PreservesMultilineStateAcrossWindowsAndCursorPages()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_python_pages_5398");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var lines = Enumerable.Repeat("", 4104).ToArray();
        lines[0] = "Needle()";
        lines[4094] = "value = f'''Needle";
        lines[4096] = "{Needle( # Needle";
        lines[4097] = "'Needle')} Needle";
        lines[4098] = "'''";
        lines[4101] = "Needle(); Needle()";
        Seed(dbPath, lines);
        using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var reader = new DbReader(db);
        foreach (var passes in new[] { 1, 2 })
        {
            reader.OriginPasses = passes;
            var rows = reader.FindInFiles("Needle", 50, regex: true, semanticFilters: new(["code"], [], []));
            Assert.Equal(passes == 1 ? 1 : 4, rows.Count);
            Assert.Equal(passes == 2, rows.Scan.UnknownOriginMatches == 0);
            Assert.Equal(passes == 1 ? 2 : null, rows.Scan.RetryOriginPasses);
            foreach (var command in new[] { "search", "find" })
            {
                var (exit, output, _) = CaptureConsole(() => ProgramRunner.Run(
                    [command, "Needle", "--path", "tests/", "--db", dbPath, "--origin", "code", "--count", "--json",
                        "--origin-passes", passes.ToString(), command == "find" ? "--regex" : "--exact"], JsonOptions, "test"));
                Assert.Equal(passes == 1 ? CommandExitCodes.PartialResult : 0, exit);
                using var count = JsonDocument.Parse(output);
                Assert.Equal(passes == 2, count.RootElement.GetProperty("authoritative_count").GetBoolean());
                if (passes == 1) Assert.Contains("--origin-passes 2", count.RootElement.GetProperty("classification_recovery_guidance").GetString());
            }
        }
        var args = new[] { "find", "(?=Needle)", "--regex", "--origin", "code", "--origin-passes", "2", "--path", "tests/",
            "--db", dbPath, "--json", "--limit", "1", "--max-json-bytes", "6000", "--fields", "path,line,column,length,match_facets" };
        string? cursor = null;
        var seen = new List<(int, int)>();
        for (var page = 0; page < 6; page++)
        {
            var (exit, output, _) = CaptureConsole(() => ProgramRunner.Run(cursor is null ? args : [.. args, "--cursor", cursor], JsonOptions, "test"));
            Assert.Equal(0, exit);
            using var document = JsonDocument.Parse(output);
            var row = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(0, row.GetProperty("length").GetInt32());
            seen.Add((row.GetProperty("line").GetInt32(), row.GetProperty("column").GetInt32()));
            cursor = document.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString();
            if (cursor is null) break;
        }
        Assert.Null(cursor);
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Distinct().Count());
        var (ndjsonExit, ndjson, _) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["Needle", "--regex", "--origin", "code", "--origin-passes", "2", "--path", "tests/", "--db", dbPath, "--json"], JsonOptions));
        Assert.Equal(0, ndjsonExit);
        var records = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => JsonDocument.Parse(value)).ToArray();
        try
        {
            Assert.Equal(4, records.Count(record => record.RootElement.TryGetProperty("path", out _)));
            Assert.True(records[^1].RootElement.GetProperty("origin_classification_complete").GetBoolean());
        }
        finally { foreach (var record in records) record.Dispose(); }
    }

    [Fact]
    public void IndexedContext_RejectsConflictsGenerationChangesAndCancelledContinuation()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_python_bounds_5398");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var lines = Enumerable.Repeat("", 4100).ToArray();
        lines[4094] = "'''";
        lines[4096] = "Needle";
        lines[4097] = "'''";
        Seed(dbPath, lines);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var reader = new DbReader(connection) { OriginPasses = 2 };
        using var cancel = new CancellationTokenSource();
        reader.OriginWindowStartingForTesting = start => { if (start > 1) cancel.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => reader.FindInFiles("Needle", 10, regex: true,
            semanticFilters: new([], [], []), cancellationToken: cancel.Token));
        reader.OriginWindowStartingForTesting = start =>
        {
            if (start <= 1) return;
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE files SET size = size + 1";
            command.ExecuteNonQuery();
        };
        var changed = reader.FindInFiles("Needle", 10, regex: true, semanticFilters: new([], [], []));
        Assert.Equal("indexed_generation_changed", Assert.Single(Assert.Single(changed).MatchFacets!).OriginUnavailable!.Reason);
        reader.OriginWindowStartingForTesting = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO chunks(file_id, chunk_index, start_line, end_line, content) SELECT id, 999, 1, 1, 'conflict' FROM files";
            command.ExecuteNonQuery();
        }
        var conflict = reader.FindInFiles("Needle", 10, regex: true, semanticFilters: new([], [], []));
        Assert.Equal("indexed_text_mismatch", Assert.Single(Assert.Single(conflict).MatchFacets!).OriginUnavailable!.Reason);
    }

    private static Dictionary<int, string> Lines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select((text, i) => (text, i))
            .ToDictionary(pair => pair.i + 1, pair => pair.text);

    private static (int, int, int, string) Identity(SearchMatchFacet facet)
        => (facet.Line, facet.Column, facet.Length, facet.Origin);

    private static void Seed(string dbPath, string[] lines, int chunkSize = 80, string newline = "\n")
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        var id = writer.UpsertFile(new FileRecord
        {
            Path = "tests/test_origins.py", Lang = "python", Lines = lines.Length,
            Size = string.Join(newline, lines).Length, Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
        });
        writer.InsertChunks(Enumerable.Range(0, (lines.Length + chunkSize - 1) / chunkSize).Select(index => new ChunkRecord
        {
            FileId = id, ChunkIndex = index, StartLine = index * chunkSize + 1,
            EndLine = Math.Min(lines.Length, (index + 1) * chunkSize),
            Content = string.Join(newline, lines.Skip(index * chunkSize).Take(chunkSize)),
        }).ToList());
    }
}
