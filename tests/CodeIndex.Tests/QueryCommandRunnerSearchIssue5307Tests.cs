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
        Assert.Equal("unknown", SearchMatchClassifier.Classify("src/a.cs", "csharp", lines.Count,
            "needle", 1, 6, lineContext: lines).Origin);
        Assert.Equal("code", SearchMatchClassifier.Classify("src/a.cs", "csharp", lines.Count - 1,
            "needle", 1, 6, lineContext: lines).Origin);
        var exactBudget = new string(' ', SearchMatchClassifier.CSharpContextCharacterLimit - 6) + "needle";
        Assert.Equal("code", SearchMatchClassifier.Classify("src/a.cs", "csharp", 1,
            exactBudget, exactBudget.Length - 5, 6, lineContext: new Dictionary<int, string>()).Origin);
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
