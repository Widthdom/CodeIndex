using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_SameSymbolGuardsBoundNeighborsAndNestedFunctions_Issue5300()
    {
        var root = TestProjectHelper.CreateTempProject("same_symbol_guards");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(root);
            TestProjectHelper.InsertFreshIndexedFile(root, dbPath, "src/a.cs", "csharp", """
                class Example
                {
                    void Protected()
                    {
                        Clear();
                        Return();
                    }
                    void Unprotected()
                    {
                        Return();
                    }
                    void Outer()
                    {
                        void Inner()
                        {
                            Clear();
                            Return();
                        }
                        Return();
                    }
                    void OneLine() { Clear(); Return(); }
                    void CommentGuard()
                    {
                        // Clear is lexical evidence, not proof of cleanup.
                        Return();
                    }
                    void StringGuard()
                    {
                        var text = "Clear => delegate"; int delegateCount = 0;
                        Return();
                    }
                    void Expression() =>
                        Return();
                }
                """);
            StampSameSymbolGuardFixture(dbPath);
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var reader = new DbReader(db.Connection);
            var reject = new SearchGuardFilter(SearchGuardRole.Reject, SearchGuardDirection.Before, "Clear");
            var require = reject with { Role = SearchGuardRole.Require };
            var unprotected = reader.Search("Return", 100, guardFilters: [reject], guardWindow: 20, guardScope: SearchGuardScope.SameSymbol);
            Assert.Equal(new[] { 10, 19, 21, 33 }, unprotected.Select(row => row.StartLine).Order().ToArray());
            var protectedRows = reader.Search("Return", 100, guardFilters: [require], guardWindow: 20, guardScope: SearchGuardScope.SameSymbol);
            Assert.Equal(new[] { 6, 17, 25, 30 }, protectedRows.Select(row => row.StartLine).Order().ToArray());
            foreach (var row in unprotected.Concat(protectedRows))
            {
                var check = Assert.Single(row.GuardChecks!);
                Assert.True(check.ScopeAvailable);
                Assert.Equal("same_symbol", check.Scope);
                Assert.True(check.WindowStartLine >= check.SymbolStartLine);
                Assert.True(check.WindowEndLine <= check.SymbolEndLine);
            }
            Assert.Empty(reader.Search("Absent", 100, guardFilters: [reject], guardScope: SearchGuardScope.SameSymbol));
            Assert.Empty(reader.Search("Return", 100, guardFilters: [require], guardWindow: 0, guardScope: SearchGuardScope.SameSymbol));
            Assert.Equal(8, reader.Search("Return", 100, guardFilters: [reject], guardWindow: 0, guardScope: SearchGuardScope.SameSymbol).Count);

            var args = new[] { "Return", "--db", dbPath, "--reject-before", "Clear", "--guard-window", "20", "--guard-scope", "same-symbol", "--json=array" };
            var (exit, output, error) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, _jsonOptions));
            Assert.Equal(0, exit);
            Assert.Empty(error);
            using var json = ParseJsonOutput(output);
            Assert.Equal(4, json.RootElement.GetArrayLength());
            Assert.All(json.RootElement.EnumerateArray(), row => Assert.True(row.GetProperty("guard_checks")[0].GetProperty("scope_available").GetBoolean()));
            var (countExit, countOutput, _) = CaptureConsole(() => QueryCommandRunner.RunSearch([.. args[..^1], "--format", "count", "--json"], _jsonOptions));
            Assert.Equal(0, countExit);
            using var count = ParseJsonOutput(countOutput);
            Assert.Equal(1, count.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("same-symbol", count.RootElement.GetProperty("query_context").GetProperty("guard_scope").GetString());
            var recipePath = TestProjectHelper.WriteTextFile(root, "recipes.json", """
                [{"name":"symbol-guard-test","description":"Scope regression","queries":[{"name":"returns","query":"Return","description":"Return sites"}]}]
                """);
            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);
            var (auditExit, auditOutput, _) = CaptureConsole(() => QueryCommandRunner.RunAudit(
                ["symbol-guard-test", "--db", dbPath, "--reject-before", "Clear", "--guard-window", "20", "--guard-scope", "same-symbol", "--json"], _jsonOptions));
            Assert.Equal(0, auditExit);
            using var audit = ParseJsonOutput(auditOutput);
            Assert.Contains("same_symbol", auditOutput);
            Assert.Contains("scope_available", auditOutput);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RunSearch_SameSymbolGuardsFailExplicitlyForUnavailableRanges_Issue5300()
    {
        var root = TestProjectHelper.CreateTempProject("same_symbol_unavailable");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(root);
            var cases = new[]
            {
                ("lambda.cs", "csharp", "class C\n{\n void M()\n {\n  Action a = () => { Clear(); Return(); };\n }\n}"),
                ("anonymous.cs", "csharp", "class C\n{\n void M()\n {\n  Action a = delegate { Clear(); Return(); };\n }\n}"),
                ("expression.cs", "csharp", "class C\n{\n void M() => Use(() => Return());\n}"),
                ("adjacent.cs", "csharp", "class C\n{\n void A() { Clear(); } void B() { Return(); }\n}"),
                ("partial.cs", "csharp", "class C\n{\n void M()\n {\n  Return();"),
                ("outside.cs", "csharp", "// Return\nclass C {}"),
                ("other.py", "python", "def m():\n    Return()"),
            };
            foreach (var (path, lang, content) in cases)
                TestProjectHelper.InsertFreshIndexedFile(root, dbPath, path, lang, content);
            StampSameSymbolGuardFixture(dbPath);
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var reader = new DbReader(db.Connection);
            var filters = new[] { new SearchGuardFilter(SearchGuardRole.Reject, SearchGuardDirection.Before, "Clear") };
            foreach (var (path, _, _) in cases)
            {
                var exception = Assert.Throws<CodeIndexException>(() => reader.Search("Return", 10,
                    pathPatterns: [path], guardFilters: filters, guardScope: SearchGuardScope.SameSymbol));
                Assert.StartsWith("same_symbol_scope_unavailable:", exception.Message);
            }
            var args = new[] { "Return", "--db", dbPath, "--path", "lambda.cs", "--reject-before", "Clear", "--guard-scope", "same-symbol", "--json" };
            var (exit, output, _) = CaptureConsole(() => QueryCommandRunner.RunSearch(args, _jsonOptions));
            Assert.NotEqual(0, exit);
            Assert.Contains("same_symbol_scope_unavailable", output);
            using var json = ParseJsonOutput(output);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    private static void StampSameSymbolGuardFixture(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(DbContext.SymbolKindFilterMetaKey, SymbolKindFilter.Empty.Signature);
    }

    [Fact]
    public void RunSearch_SameSymbolGuardsRejectStaleMissingAndBudgetEvidence_Issue5300()
    {
        var root = TestProjectHelper.CreateTempProject("same_symbol_contract");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(root);
            const string source = "class C\n{\n void M()\n {\n  Return();\n  Clear();\n }\n}";
            TestProjectHelper.InsertFreshIndexedFile(root, dbPath, "src/a.cs", "csharp", source);
            StampSameSymbolGuardFixture(dbPath);
            var filters = new[] { new SearchGuardFilter(SearchGuardRole.Require, SearchGuardDirection.After, "Clear") };
            List<SearchResult> Search(string query = "Return")
            {
                using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
                using var reader = new DbReader(db.Connection);
                return reader.Search(query, 10, guardFilters: filters, guardScope: SearchGuardScope.SameSymbol);
            }
            Assert.Single(Search());
            // Indexing decodes UTF-16 before checksumming; freshness must use the same content contract.
            File.WriteAllText(Path.Combine(root, "src/a.cs"), source, System.Text.Encoding.Unicode);
            Assert.Single(Search());
            TestProjectHelper.WriteTextFile(root, "src/a.cs", source);
            TestProjectHelper.AppendTextFile(root, "src/a.cs", "\n// drift");
            Assert.Contains("source_stale", Assert.Throws<CodeIndexException>(() => Search()).Message);
            TestProjectHelper.WriteTextFile(root, "src/a.cs", source);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db.Connection).SetMeta(DbContext.IndexCompletenessMetaKey, "incomplete");
            Assert.Contains("index_incomplete", Assert.Throws<CodeIndexException>(() => Search("Absent")).Message);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexCompletenessMetaKey, "complete");
                writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("csharp"), "0");
            }
            Assert.Contains("symbol_ranges_stale", Assert.Throws<CodeIndexException>(() => Search("Absent")).Message);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                new DbWriter(db.Connection).StampSymbolExtractorVersions(["csharp"]);
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM symbols WHERE kind = 'function'";
                cmd.ExecuteNonQuery();
            }
            Assert.Contains("enclosing_symbol_missing", Assert.Throws<CodeIndexException>(() => Search()).Message);
            var large = "class C\n{\n void M()\n {\n" + new string('\n', 2048) + "Return();\nClear();\n }\n}";
            TestProjectHelper.InsertFreshIndexedFile(root, dbPath, "src/a.cs", "csharp", large);
            Assert.Contains("symbol_source_budget_exceeded", Assert.Throws<CodeIndexException>(() => Search()).Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }
}
