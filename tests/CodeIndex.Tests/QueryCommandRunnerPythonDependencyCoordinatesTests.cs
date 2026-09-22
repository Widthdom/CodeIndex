using CodeIndex.Cli;
using CodeIndex.Database;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunDeps_PythonSourceContextWorkScalesWithReferences_Issue5401()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_python_context_work_5401");
        const int importCount = 8;
        const int callCount = 128;
        var lines = new List<string>();
        for (var i = 0; i < importCount; i++)
        {
            lines.Add($"import target{i} as m{i}");
            TestProjectHelper.WriteTextFile(project.Root, $"target{i}.py", $"def action{i}():\n    pass\n");
        }
        lines.Add("def caller():");
        for (var i = 0; i < callCount; i++)
        {
            lines.Add($"    m{i % importCount}.action{i % importCount}()");
            lines.AddRange(Enumerable.Repeat("    value = 0", 5));
        }
        TestProjectHelper.WriteTextFile(project.Root, "caller.py", string.Join('\n', lines) + '\n');
        TestProjectHelper.WriteTextFile(project.Root, "narrow.py", "import target0 as m\ndef narrow():\n    m.action0()\n");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);

        using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        var sourceReads = 0;
        db.Connection.CreateFunction("source_line_at", (string? text, long? start, long? line) =>
        {
            sourceReads++;
            return DbContext.GetTextInLineRange(text, start, line, line);
        });
        using var reader = new DbReader(db);
        foreach (var cycles in new[] { false, true })
        {
            sourceReads = 0;
            var edges = cycles
                ? reader.GetFileDependencyCycleCandidates(20, out _, lang: "python")
                : reader.GetFileDependencies(limit: 20, lang: "python");
            Assert.Equal(importCount + 1, edges.Count);
            Assert.All(edges.Where(edge => edge.SourcePath == "caller.py"), edge => Assert.Equal(callCount / importCount, edge.ReferenceCount));
            Assert.Equal(1, Assert.Single(edges, edge => edge.SourcePath == "narrow.py").ReferenceCount);
            Assert.Equal(callCount + 1, edges.Sum(edge => edge.ReferenceCount));
            // Ordinary deps evaluates its grouping key and projected context; cycles
            // must share one materialized context across imports and both stages.
            Assert.InRange(sourceReads, 1, (callCount + 1) * (cycles ? 1 : 2));
        }
        sourceReads = 0;
        Assert.Single(reader.GetFileDependencyCycleCandidates(20, out _, lang: "python", pathPatterns: ["narrow.py"]));
        Assert.Equal(1, sourceReads);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("    ")]
    [InlineData("        ")]
    [InlineData("\t")]
    [InlineData("\t  ")]
    public void RunDeps_PythonContextCoordinatesPreserveBothDirections_Issue5401(string indent)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_python_dependencies_5401");
        var expected = new Dictionary<(string Source, string Target), int>();
        var sources = new Dictionary<string, string[]>();
        var bodyIndent = indent.Length == 0 ? "    " : indent;
        foreach (var (style, left, right) in new[]
        {
            ("direct", "alpha", "beta"),
            ("alias", "a", "long_function_name"),
            ("relative", "long_function_name", "b"),
            ("module", "alpha", "beta"),
        })
        {
            foreach (var (file, target) in new[] { (left, right), (right, left) })
            {
                var import = style switch
                {
                    "alias" => $"from {style}.{target} import {target} as alias_{target}",
                    "relative" => $"from .{target} import {target}",
                    "module" => $"import {style}.{target} as m",
                    _ => $"from {style}.{target} import {target}",
                };
                var call = style switch { "alias" => "alias_" + target, "module" => "m." + target, _ => target };
                var path = $"{style}/{file}.py";
                // The unindented case is a module-level control. Both occurrences must
                // retain their original columns, even after strings and a tab prefix.
                string[] lines = [import, $"def {file}():", bodyIndent + "pass", $"{indent}{call}(); s = '😀'; {call}() # {call}()"];
                sources.Add(path, lines);
                TestProjectHelper.WriteTextFile(project.Root, path, string.Join('\n', lines) + '\n');
                expected.Add((path, $"{style}/{target}.py"), 2);
            }
        }

        // Same leaf, different receivers on one line: counts expose accidental
        // rebinding of the first call to the second after indentation is removed.
        sources.Add("members/caller.py", ["import members.first as x", "import members.second as y", "def entry():", bodyIndent + "pass", $"{indent}x.work(); y.work(); y.work()"]);
        foreach (var target in new[] { "first", "second" })
        {
            sources.Add($"members/{target}.py", ["from members.caller import entry", "def work():", bodyIndent + "pass", indent + "entry()"]);
            expected.Add(($"members/{target}.py", "members/caller.py"), 1);
            expected.Add(("members/caller.py", $"members/{target}.py"), target == "first" ? 1 : 2);
        }
        foreach (var (path, lines) in sources.Where(pair => pair.Key.StartsWith("members/", StringComparison.Ordinal)))
            TestProjectHelper.WriteTextFile(project.Root, path, string.Join("\r\n", lines) + "\r\n");

        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);
        using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT f.path, r.line, r.column_number, r.symbol_name, rl.context
                FROM symbol_references r JOIN files f ON f.id = r.file_id
                JOIN reference_lines rl ON rl.id = r.reference_line_id
                WHERE r.reference_kind = 'call' ORDER BY f.path, r.line, r.column_number
                """;
            using var rows = command.ExecuteReader();
            var count = 0;
            while (rows.Read())
            {
                var source = sources[rows.GetString(0)][rows.GetInt32(1) - 1];
                var column = rows.GetInt32(2);
                var name = rows.GetString(3);
                Assert.True(source.AsSpan(column - 1).StartsWith(name, StringComparison.Ordinal));
                Assert.Equal(source.Trim(), rows.GetString(4));
                count++;
            }
            Assert.Equal(expected.Values.Sum(), count);
        }

        // Queries must use the persisted snapshot, even if live files have changed.
        foreach (var path in sources.Keys)
            File.WriteAllText(Path.Combine(project.Root, path), "# changed after indexing\n");

        var ordinary = CaptureConsole(() => QueryCommandRunner.RunDeps(["--db", dbPath, "--json", "--lang", "python"], _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, ordinary.Result);
        using var document = ParseJsonOutput(ordinary.Stdout);
        var edges = document.RootElement.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, edges.Length);
        foreach (var edge in edges)
        {
            var key = (edge.GetProperty("source_path").GetString()!, edge.GetProperty("target_path").GetString()!);
            Assert.True(expected.TryGetValue(key, out var count), edge.GetRawText());
            Assert.True(count == edge.GetProperty("reference_count").GetInt32(), edge.GetRawText());
        }
        var cycles = RunIdentityCycles(dbPath, "--lang", "python");
        Assert.Equal(expected.Count, cycles.GetProperty("graph_edge_count").GetInt32());
        Assert.Equal(5, cycles.GetProperty("total_cycle_count").GetInt32());
        Assert.True(cycles.GetProperty("analysis_complete").GetBoolean());
        var nodes = cycles.GetProperty("cycles").EnumerateArray()
            .SelectMany(cycle => cycle.GetProperty("nodes").EnumerateArray().Select(node => node.GetString()))
            .OrderBy(path => path, StringComparer.Ordinal);
        Assert.Equal(sources.Keys.OrderBy(path => path, StringComparer.Ordinal), nodes);

        // Missing rows and legacy schemas cannot supply absolute source context.
        // Retain direct/aliased imports, but never guess a missing module receiver.
        foreach (var mutation in new[] { "DELETE FROM chunks", "DROP TABLE chunks" })
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText = mutation;
                command.ExecuteNonQuery();
            }
            using var reader = new DbReader(db);
            var fallbackExpected = expected.Keys.Where(key =>
                !key.Source.StartsWith("module/", StringComparison.Ordinal) && key.Source != "members/caller.py")
                .OrderBy(key => key.Source, StringComparer.Ordinal).ThenBy(key => key.Target, StringComparer.Ordinal).ToArray();
            foreach (var fallback in new[]
            {
                reader.GetFileDependencies(limit: 20, lang: "python"),
                reader.GetFileDependencyCycleCandidates(20, out _, lang: "python"),
            })
                Assert.Equal(fallbackExpected, fallback.Select(edge => (edge.SourcePath, edge.TargetPath))
                    .OrderBy(key => key.SourcePath, StringComparer.Ordinal).ThenBy(key => key.TargetPath, StringComparer.Ordinal));
        }
    }
}
