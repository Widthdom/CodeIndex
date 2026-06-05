using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunMap_ParseSectionsAndDepth_StoresSelectors()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--json", "--sections", "tree,languages", "--depth", "2"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.Json);
        Assert.Equal(["tree", "languages"], options.MapSections);
        Assert.True(options.ContextAfterExplicit);
        Assert.Equal(2, options.ContextAfter);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunMap_ParseCompact_ImpliesJsonAndPreservesExplicitLimit_Issue3009()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--compact", "--limit", "3"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.Json);
        Assert.True(options.Compact);
        Assert.True(options.LimitExplicit);
        Assert.Equal(3, options.Limit);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunMap_ParseInvalidMinEntrypointConfidence_TruncatesOversizedValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var options = QueryCommandRunner.ParseArgs(
            ["--min-entrypoint-confidence", value],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--min-entrypoint-confidence must be a number", options.ParseError);
        Assert.Contains("<truncated; original length", options.ParseError);
        Assert.DoesNotContain(value, options.ParseError);
    }

    [Fact]
    public void RunMap_ParseInvalidSections_FlattensControlCharacters_Issue3092()
    {
        var value = "tree,bad\nforged\tvalue";

        var options = QueryCommandRunner.ParseArgs(
            ["--sections", value],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--sections contains unsupported section 'bad forged value'", options.ParseError);
        Assert.DoesNotContain("bad\nforged\tvalue", options.ParseError);
    }

    [Fact]
    public void RunMap_CompactJson_CapsSectionsAndReportsTruncation_Issue3009()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_compact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < QueryCommandRunner.DefaultCompactSectionLimit + 2; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/module{i}/App{i}.cs",
                    "csharp",
                    $"namespace Module{i}; public class App{i} {{ public void Run() {{ }} }}\n");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--compact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var topFiles = json.GetProperty("top_files").EnumerateArray().ToList();
            var topFilesTruncation = json
                .GetProperty("truncation")
                .GetProperty("sections")
                .GetProperty("top_files");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, json.GetProperty("compact_limit").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, topFiles.Count);
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, topFilesTruncation.GetProperty("returned").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit + 1, topFilesTruncation.GetProperty("source_count").GetInt32());
            Assert.True(topFilesTruncation.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJsonIncludesWorkspaceMetadataForProjectDb()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_CSharpRawStringFixturesDoNotCreatePhantomEntrypoints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_raw_string");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/fixture.cs",
                "csharp",
                """"
                public class FixtureHost
                {
                    public void UsesRawFixture()
                    {
                        const string fixture = """
                            function main()
                            end

                            public class App
                            {
                            }
                            """;
                    }
                }
                """");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var entrypoints = document.RootElement.GetProperty("entrypoints");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(entrypoints.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_JavaModuleInfoUsesModuleDeclarationAsModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_java_module");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "com", "example", "app"));
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app {
                    requires java.base;
                    exports com.example.api;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "com", "example", "app", "App.java"),
                """
                package com.example.app;

                public class App
                {
                    public static void main(String[] args) {}
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var javaModule = Assert.Single(modules.Where(module => module.GetProperty("module").GetString() == "com.example.app"));
            Assert.Equal(2, javaModule.GetProperty("files").GetInt32());
            Assert.DoesNotContain(modules, module => module.GetProperty("module").GetString() == "com");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_JavaModuleInfoWithAllmanBraceUsesModuleDeclarationAsModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_java_module_allman");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "com", "example", "app"));
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app
                {
                    requires java.base;
                    exports com.example.api;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "com", "example", "app", "App.java"),
                """
                package com.example.app;

                public class App
                {
                    public static void main(String[] args) {}
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var javaModule = Assert.Single(modules.Where(module => module.GetProperty("module").GetString() == "com.example.app"));
            Assert.Equal(2, javaModule.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_NonJavaNamespaceDoesNotOverridePathBasedModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_non_java_namespace");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "App", "App.cs"),
                """
                namespace My.Company.App;

                public class App {}
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains(modules, module => module.GetProperty("module").GetString() == "src/App");
            Assert.DoesNotContain(modules, module => module.GetProperty("module").GetString() == "My.Company.App");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_PathFilteredJavaModuleFileKeepsModuleDeclarationAsModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_java_module_filtered");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "com", "example", "app"));
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app {
                    requires java.base;
                    exports com.example.api;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "com", "example", "app", "App.java"),
                """
                package com.example.app;

                public class App
                {
                    public static void main(String[] args) {}
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--path", "com/example/app/App.java"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();
            var topFiles = document.RootElement.GetProperty("top_files").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var javaModule = Assert.Single(modules);
            Assert.Equal("com.example.app", javaModule.GetProperty("module").GetString());
            Assert.Equal(1, javaModule.GetProperty("files").GetInt32());
            var topFile = Assert.Single(topFiles);
            Assert.Equal("com/example/app/App.java", topFile.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJsonIncludesWorkspaceMetadataForCustomDbUnderCdidx()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_custom_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunMap_NonexistentPathReturnsNotFound()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_notfound");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--path", "nonexistent/"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No files found", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_NonexistentPathJsonReturnsNotFoundWithPayload()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_notfound_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--path", "nonexistent/", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(0, document.RootElement.GetProperty("file_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_EmptyDbWithoutFiltersReturnsSuccess()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_empty_ok");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            // No files inserted — empty but valid index / ファイル未挿入 — 空だが有効なインデックス

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Files      : 0", stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_HumanLargestFilesFormatsSizesAndBytesFlagKeepsRawCounts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_size_units");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/big.cs", "csharp", "class Big {}\n");
            SetIndexedFileSize(dbPath, "src/big.cs", 5L * 1024 * 1024 * 1024);

            var (formattedExit, formattedStdout, formattedStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath],
                _jsonOptions));
            var (rawExit, rawStdout, rawStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--bytes"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, formattedExit);
            Assert.Equal(CommandExitCodes.Success, rawExit);
            Assert.Equal(string.Empty, formattedStderr);
            Assert.Equal(string.Empty, rawStderr);
            Assert.Contains("Largest files:", formattedStdout);
            Assert.Contains("src/big.cs", formattedStdout);
            Assert.Contains("5.0 GiB", formattedStdout);
            Assert.Contains("5368709120 bytes", rawStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
