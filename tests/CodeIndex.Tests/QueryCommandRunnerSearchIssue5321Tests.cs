using CodeIndex.Database;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void SearchMatchClassifier_InterpolationRecoveryAndUnavailability_Issue5321()
    {
        (string Source, string Origin)[] cases =
        [
            ("var s = $\"{value}\"; needle();", "code"),
            ("var s = $\"😀{value}\"; 日本語.needle();", "code"),
            ("var s = $\"{needle()}\";", "code"),
            ("var s = $@\"\n{value}\n\"; needle();", "code"),
            ("var s = @$\"\n{needle()}\n\";", "code"),
            ("var s = $$\"\"\"\n{{value}}\n\"\"\"; needle();", "code"),
            ("var s = $$\"\"\"{{{needle()}}}\"\"\";", "code"),
            ("var s = $$\"\"\"\"\n{{value}} \"\"\" needle\n\"\"\"\";", "string_literal"),
            ("var s = $$\"\"\"\"\n{{value}} \"\"\" text\n\"\"\"\"; needle();", "code"),
            ("var s = $\"{{escaped}} {new { X = Call(\"}\", '\\'') }}\"; needle();", "code"),
            ("var s = $\"{Call($\"{value}\")}\"; needle();", "code"),
            ("var s = $\"{Call(\"needle\")}\";", "string_literal"),
            ("var s = $\"{ /* needle */ value }\";", "comment"),
            ("var s = $\"{ /* } \" */ value }\"; needle();", "code"),
            ("var s = $\"{value:needle}\";", "string_literal"),
            ("var s = $\"{value:000}\"; needle();", "code"),
            ("var s = $\"{value:000\n}\"; needle();", "unknown"),
            ("var s = $@\"{value:000\n}\"; needle();", "code"),
            ("var s = $$\"\"\"\n{{value:000\n}}\n\"\"\"; needle();", "code"),
            ("var s = $\"{value}\"; /*\nneedle\n*/", "comment"),
            ("var s = $\"{value}\"; var t = @\"\nneedle\n\";", "string_literal"),
            ("var s = $\"{value}\"; // 😀\n日本語.needle();", "code"),
            ("var s = $\"{Call(}\";\nneedle();", "unknown"),
            ("var s = $\"{value\";\nneedle();", "unknown"),
            ("var s = $\"{value} missing\nneedle();", "unknown"),
            ("var s = $\"{value:{format}}\"; needle();", "unknown"),
            ("var s = $\"{" + new string('(', 65) + "needle" + new string(')', 65) + "}\";", "unknown"),
        ];
        foreach (var (source, expected) in cases)
        {
            var context = source.Split('\n').Select((text, index) => (text, index))
                .ToDictionary(p => p.index + 1, p => p.text);
            var match = context.Single(p => p.Value.Contains("needle", StringComparison.Ordinal));
            var column = match.Value.IndexOf("needle", StringComparison.Ordinal) + 1;
            var origins = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", context);
            var facet = SearchMatchClassifier.Classify("src/a.cs", "csharp", match.Key, match.Value,
                column, 6, csharpContext: origins);
            Assert.True(expected == facet.Origin, $"{source}: expected {expected}, got {facet.Origin}");
            Assert.Equal(column, facet.Column);
            if (expected == "unknown")
            {
                Assert.NotNull(facet.OriginUnavailable);
                Assert.NotEmpty(facet.OriginUnavailable.Reason);
                Assert.Equal("remaining_file", facet.OriginUnavailable.Extent);
                Assert.Equal(1, facet.OriginUnavailable.StartLine);
            }
            else
                Assert.Null(facet.OriginUnavailable);
        }
        foreach (var source in new[]
        {
            "CreateToolDefinition($\"{value,10}\", \"needle\", new JsonObject());",
            "CreateToolDefinition(\"name\", $\"{StringOrArraySchema(\"needle\")}\", new JsonObject());",
        })
        {
            var context = new Dictionary<int, string> { [1] = source };
            Assert.Equal("schema_description", SearchMatchClassifier.Classify(
                "src/CodeIndex/Mcp/McpToolCatalog.cs", "csharp", 1, source,
                source.IndexOf("needle", StringComparison.Ordinal) + 1, 6, lineContext: context).Origin);
        }
    }

    [Fact]
    public void RunSearch_InterpolationClosingChunkIsIndependentOfPage_Issue5321()
    {
        var project = TestProjectHelper.CreateTempProject("cdidx_interpolation_pages_5321");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(project);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var id = writer.UpsertFile(new FileRecord
                {
                    Path = "src/a.cs", Lang = "csharp", Lines = 3, Size = 100,
                    Modified = DateTime.UtcNow, Checksum = "fixture",
                });
                writer.InsertChunks([
                    new ChunkRecord { FileId = id, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "var s = $@\"{needle()}" },
                    new ChunkRecord { FileId = id, ChunkIndex = 1, StartLine = 2, EndLine = 3, Content = "literal\n\"; needle();" },
                ]);
                var reader = new DbReader(db.Connection);
                foreach (var limit in new[] { 1, 10 })
                    foreach (var tokenBoundary in new[] { false, true })
                    {
                        var rows = reader.Search("needle", limit, exact: true, deduplicate: false, tokenBoundary: tokenBoundary);
                        Assert.Equal(Math.Min(limit, 2), rows.Count);
                        Assert.All(SearchSnippetFormatter.ToCompactResults(rows, "needle", exposeLiteralHighlights: true),
                            row => Assert.All(row.MatchFacets, facet => Assert.Equal("code", facet.Origin)));
                    }
            }
            var (exit, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["needle", "--db", dbPath, "--origin", "code", "--exact-substring", "--count", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, exit);
            Assert.Empty(stderr);
            using var json = ParseJsonOutput(stdout);
            Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(project);
        }
    }
}
