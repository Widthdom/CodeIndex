using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void SearchMatchClassifier_MultilineStateBoundsAndCoordinates_Issue5307()
    {
        (string Source, string Origin)[] cases =
        [
            ("/*\nneedle\n*/", "comment"),
            ("/*\n*/ needle();", "code"),
            ("var x = @\"\nneedle\n\";", "string_literal"),
            ("var x = @\"\n\"\"needle\"\"\n\";", "string_literal"),
            ("var x = @\"\n\"; needle();", "code"),
            ("var x = $@\"\nneedle\n\";", "string_literal"),
            ("var x = @$\"\nneedle\n\";", "string_literal"),
            ("var x = \"\"\"\nneedle\n\"\"\";", "string_literal"),
            ("var x = \"\"\"\"\n\"\"\" needle\n\"\"\"\";", "string_literal"),
            ("var x = $$\"\"\"\nneedle\n\"\"\";", "string_literal"),
            ("var x = \"\"\"\n\"\"\"; needle();", "code"),
            ("var x = @\"\n// /* needle\n\";", "string_literal"),
            ("/* @\" \"\"\"\nneedle\n*/", "comment"),
            ("var x = \"\\\\\"; needle();", "code"),
            ("var x = '\\''; needle();", "code"),
            ("var x = @\"Usage:\nneedle\n\";", "help_text"),
            ("var x = new Regex(@\"\nneedle\n\");", "regex_literal"),
            ("var x = @\"\n* needle\n\";", "string_literal"),
            ("var x = @\"\"\"\nneedle\n\";", "string_literal"),
            ("var x = $@\"\n{{needle}}\n\";", "string_literal"),
            ("var x = $$\"\"\"\n{needle}\n\"\"\";", "string_literal"),
            ("var x = $@\"\n{Call(\"x\")} needle\n\";", "unknown"),
            ("var x = $$\"\"\"\n{{Call(\"x\")}} needle\n\"\"\";", "unknown"),
            ("var x = $\"{Call(\"x\")} needle\";", "unknown"),
            ("var x = $\"" + new string('{', 65536) + "needle\";", "string_literal"),
        ];
        foreach (var (source, expected) in cases)
        {
            var context = source.Split('\n').Select((text, i) => (text, i))
                .ToDictionary(pair => pair.i + 1, pair => pair.text);
            var match = context.Single(pair => pair.Value.Contains("needle", StringComparison.Ordinal));
            var column = match.Value.IndexOf("needle", StringComparison.Ordinal) + 1;
            var facet = SearchMatchClassifier.Classify("src/a.cs", "csharp", match.Key, match.Value,
                column, 6, lineContext: context);
            Assert.True(expected == facet.Origin, $"{source}: expected {expected}, got {facet.Origin}");
            Assert.Equal(match.Key, facet.Line);
            Assert.Equal(column, facet.Column);
            Assert.Equal(6, facet.Length);
        }
        foreach (var context in new[]
        {
            new Dictionary<int, string> { [2] = "needle();" },
            new Dictionary<int, string> { [1] = "/*", [3] = "needle();" },
            new Dictionary<int, string> { [1] = new(' ', SearchMatchClassifier.CSharpContextCharacterLimit), [2] = "needle();" },
        })
        {
            var line = context.Keys.Max();
            Assert.Equal("unknown", SearchMatchClassifier.Classify("src/a.cs", "csharp", line,
                context[line], 1, 6, lineContext: context).Origin);
        }
        var lines = Enumerable.Range(1, SearchMatchClassifier.CSharpContextLineLimit + 1)
            .ToDictionary(i => i, _ => "");
        lines[lines.Count] = "needle";
        lines[lines.Count - 1] = "needle";
        Assert.Equal("unknown", SearchMatchClassifier.Classify("src/a.cs", "csharp", lines.Count,
            "needle", 1, 6, lineContext: lines).Origin);
        Assert.Equal("code", SearchMatchClassifier.Classify("src/a.cs", "csharp", lines.Count - 1,
            "needle", 1, 6, lineContext: lines).Origin);
        var exactBudget = new string(' ', SearchMatchClassifier.CSharpContextCharacterLimit - 6) + "needle";
        Assert.Equal("code", SearchMatchClassifier.Classify("src/a.cs", "csharp", 1,
            exactBudget, exactBudget.Length - 5, 6, lineContext: new Dictionary<int, string> { [1] = exactBudget }).Origin);
        var schema = new Dictionary<int, string>
        {
            [1] = "[\"description\"] = @\"",
            [2] = "needle",
            [3] = "\";",
        };
        Assert.Equal("schema_description", SearchMatchClassifier.Classify(
            "src/CodeIndex/Mcp/McpToolCatalog.cs", "csharp", 2, "needle", 1, 6, lineContext: schema).Origin);
        schema[1] = "[\"description\"] = \"\"\"\"";
        Assert.Equal("schema_description", SearchMatchClassifier.Classify(
            "src/CodeIndex/Mcp/McpToolCatalog.cs", "csharp", 2, "needle", 1, 6, lineContext: schema).Origin);

        foreach (var (source, expected) in new[]
        {
            ("var s = $\"{Call()}\";", "unknown"),
            ("var s = $@\"{Call()}\";", "unknown"),
            ("var s = $$\"\"\"{{Call()}}\"\"\";", "unknown"),
            ("var s = $\"{{Call()}}\";", "string_literal"),
            ("var s = $@\"{{Call()}}\";", "string_literal"),
            ("var s = $$\"\"\"{Call()}\"\"\";", "string_literal"),
        })
        {
            var brace = source.IndexOf('{');
            var origins = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", new Dictionary<int, string> { [1] = source });
            Assert.Equal(expected, origins.GetOrigin(1, source, brace));
        }
    }

    [Fact]
    public void SearchMatchClassifier_SharesBoundedPrefixAndLazySchemaLabels_Issue5307()
    {
        var source = string.Join('\n', Enumerable.Repeat("var s = \"literal\";" + new string(' ', 200), 2000)) +
            "\nneedle(); needle(); needle(); needle(); needle();";
        var result = new SearchResult
        {
            Path = "src/CodeIndex/Mcp/McpToolCatalog.cs",
            Lang = "csharp",
            StartLine = 1,
            EndLine = 2001,
            Content = source,
        };
        // Warm up the formatter before measuring its complete shared-prefix path.
        SearchSnippetFormatter.ToCompactResult(new SearchResult { Path = "a.cs", Lang = "csharp", StartLine = 1, Content = "needle();" }, "needle");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var compact = SearchSnippetFormatter.ToCompactResult(result, "needle", maxLines: 1, exposeLiteralHighlights: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16 * 1024 * 1024, $"Shared prefix allocated {allocated} bytes.");
        Assert.Equal(5, compact.MatchFacets.Count);
        Assert.All(compact.MatchFacets, facet => Assert.Equal("code", facet.Origin));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SearchMatchClassifier.CSharpOriginContext(
            result.Path, new Dictionary<int, string> { [1] = source }, cancelled.Token));
    }

    [Fact]
    public void SearchMatchClassifier_ChunkBudgetSharesOriginsAcrossRows_Issue5307()
    {
        var project = TestProjectHelper.CreateTempProject("cdidx_csharp_chunk_budget_5307");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(project);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(db.Connection);
            var count = SearchMatchClassifier.CSharpContextChunkLimit + 2;
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/Chunks.cs",
                Lang = "csharp",
                Lines = count,
                Size = count * 40,
                Modified = DateTime.UtcNow,
                Checksum = "fixture",
            });
            writer.InsertChunks(Enumerable.Range(0, count).Select(i => new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = i,
                StartLine = i + 1,
                EndLine = i + 1,
                Content = $"info.ArgumentList.Add(value); // {i}",
            }).ToList());
            var reader = new DbReader(db.Connection);
            var rows = reader.Search("ArgumentList", count + 1, exact: true, deduplicate: false, tokenBoundary: true);
            Assert.Equal(count, rows.Count);
            Assert.NotNull(rows[0].CSharpOrigins);
            Assert.All(rows, row => Assert.Same(rows[0].CSharpOrigins, row.CSharpOrigins));
            var compact = SearchSnippetFormatter.ToCompactResults(rows, "ArgumentList", exposeLiteralHighlights: true).ToArray();
            Assert.Equal(SearchMatchClassifier.CSharpContextChunkLimit,
                compact.Count(row => Assert.Single(row.MatchFacets).Origin == "code"));
            Assert.Equal(2, compact.Count(row => Assert.Single(row.MatchFacets).Origin == "unknown"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(project);
        }
    }

    [Fact]
    public void RunSearch_MultilineOriginsRowsCountsAndRecipes_Issue5307()
    {
        var project = TestProjectHelper.CreateTempProject("cdidx_csharp_origin_5307");
        try
        {
            var db = TestProjectHelper.CreateProjectDb(project);
            TestProjectHelper.InsertIndexedFile(db, "src/Comment.cs", "csharp", "/*\ninfo.ArgumentList.Add(value);\n*/\n");
            TestProjectHelper.InsertIndexedFile(db, "src/String.cs", "csharp", "var text = @\"\ninfo.ArgumentList.Add(value);\n\";\n");
            TestProjectHelper.InsertIndexedFile(db, "src/Raw.cs", "csharp", "var text = \"\"\"\ninfo.ArgumentList.Add(value);\n\"\"\";\n");
            TestProjectHelper.InsertIndexedFile(db, "src/Code.cs", "csharp", "/* example */\ninfo.ArgumentList.Add(value);\n");
            using (var context = new DbContext(DbOpenIntent.WriteIndex, db))
            {
                var writer = new DbWriter(context.Connection);
                foreach (var gap in new[] { false, true })
                {
                    var id = writer.UpsertFile(new FileRecord
                    {
                        Path = gap ? "src/Gap.cs" : "src/Split.cs",
                        Lang = "csharp",
                        Lines = 3,
                        Size = 40,
                        Modified = DateTime.UtcNow,
                        Checksum = "fixture",
                    });
                    if (!gap)
                        writer.InsertChunks([new ChunkRecord { FileId = id, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "/*" }]);
                    writer.InsertChunks([new ChunkRecord { FileId = id, ChunkIndex = 1, StartLine = 2, EndLine = 3, Content = "info.ArgumentList.Add(value);\n*/" }]);
                }
            }
            foreach (var tokenBoundary in new[] { false, true })
                foreach (var (origin, expected) in new[] { ("code", 1), ("comment", 2), ("string_literal", 2), ("unknown", 1) })
                    foreach (var count in new[] { false, true })
                    {
                        var (exit, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                            ["ArgumentList", "--db", db, tokenBoundary ? "--token-boundary" : "--exact-substring",
                            "--origin", origin, "--snippet-lines", "4", count ? "--json" : "--json=array", .. count ? new[] { "--count" } : []], _jsonOptions));
                        Assert.True(exit == CommandExitCodes.Success, $"{tokenBoundary}/{origin}/{count}: {stdout} {stderr}");
                        Assert.Empty(stderr);
                        using var json = ParseJsonOutput(stdout);
                        if (count)
                            Assert.Equal(expected, json.RootElement.GetProperty("count").GetInt32());
                        else
                        {
                            var rows = json.RootElement.EnumerateArray().ToArray();
                            Assert.Equal(expected, rows.Length);
                            foreach (var row in rows)
                            {
                                var facet = Assert.Single(row.GetProperty("match_facets").EnumerateArray());
                                Assert.Equal(origin, facet.GetProperty("origin").GetString());
                                Assert.Equal(2, facet.GetProperty("line").GetInt32());
                                Assert.Equal(6, facet.GetProperty("column").GetInt32());
                                Assert.Equal(12, facet.GetProperty("length").GetInt32());
                            }
                        }
                    }
            var (recipeExit, output, _) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "dogfood-risk-patterns", "--db", db, "--json", "--limit", "100"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, recipeExit);
            using var recipe = ParseJsonOutput(output);
            var query = recipe.RootElement.GetProperty("queries").EnumerateArray()
                .Single(q => q.GetProperty("name").GetString() == "process-argument-list");
            Assert.Equal(1, query.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(project);
        }
    }
}
