using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void ParseArgs_ParsesSearchGuardFlags_Issue2852()
    {
        var options = QueryCommandRunner.ParseArgs(
        [
            "RunSearch",
            "--require-before", "Length",
            "--require-after", "File.Move",
            "--reject-before", "NoSizeCap",
            "--reject-after", "FileMode.Create",
            "--guard-window", "12",
        ], jsonDefault: true);

        Assert.Equal("RunSearch", options.Query);
        Assert.Equal(4, options.GuardFilters.Count);
        Assert.Equal(SearchGuardRole.Require, options.GuardFilters[0].Role);
        Assert.Equal(SearchGuardDirection.Before, options.GuardFilters[0].Direction);
        Assert.Equal("Length", options.GuardFilters[0].Query);
        Assert.Equal(SearchGuardRole.Require, options.GuardFilters[1].Role);
        Assert.Equal(SearchGuardDirection.After, options.GuardFilters[1].Direction);
        Assert.Equal("File.Move", options.GuardFilters[1].Query);
        Assert.Equal(SearchGuardRole.Reject, options.GuardFilters[2].Role);
        Assert.Equal(SearchGuardDirection.Before, options.GuardFilters[2].Direction);
        Assert.Equal("NoSizeCap", options.GuardFilters[2].Query);
        Assert.Equal(SearchGuardRole.Reject, options.GuardFilters[3].Role);
        Assert.Equal(SearchGuardDirection.After, options.GuardFilters[3].Direction);
        Assert.Equal("FileMode.Create", options.GuardFilters[3].Query);
        Assert.Equal(12, options.GuardWindow);
    }

    [Fact]
    public void RunSearch_GuardFiltersReturnUnguardedCallSites_Issue2852()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_guard_filters");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.IO;

                public class App
                {
                    public void Guarded(string path)
                    {
                        var length = new FileInfo(path).Length;
                        var text = File.ReadAllText(path);
                    }

                    public void Unguarded(string path)
                    {
                        var text = File.ReadAllText(path);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["File.ReadAllText", "--db", dbPath, "--exact-substring", "--reject-before", "Length", "--guard-window", "2", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/app.cs", row.GetProperty("path").GetString());
            Assert.Equal(13, row.GetProperty("chunk_start_line").GetInt32());
            Assert.Equal("        var text = File.ReadAllText(path);", row.GetProperty("snippet").GetString());
            Assert.False(row.TryGetProperty("guard_evidence", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GuardFiltersRequireAllNonExactTokensOnFocusLine_Issue2852()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_guard_non_exact_terms");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.IO;

                public class App
                {
                    public void Guarded(string path)
                    {
                        var length = new FileInfo(path).Length;
                        var text = File.ReadAllText(path);
                    }

                    public void Unguarded(string path)
                    {
                        var text = File.ReadAllText(path);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["File ReadAllText", "--db", dbPath, "--reject-before", "Length", "--guard-window", "2", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/app.cs", row.GetProperty("path").GetString());
            Assert.Equal(13, row.GetProperty("chunk_start_line").GetInt32());
            Assert.Equal("        var text = File.ReadAllText(path);", row.GetProperty("snippet").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GuardRequireEvidenceAppearsInJson_Issue2852()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_guard_require_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.IO;

                public class App
                {
                    public void Atomic(string path, string tempPath)
                    {
                        using var stream = new FileStream(path, FileMode.Create);
                        File.Move(tempPath, path, overwrite: true);
                    }

                    public void NonAtomic(string path)
                    {
                        using var stream = new FileStream(path, FileMode.Create);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["FileMode.Create", "--db", dbPath, "--exact-substring", "--require-after", "File.Move", "--guard-window", "2", "--json=array"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(7, row.GetProperty("chunk_start_line").GetInt32());
            var evidence = Assert.Single(row.GetProperty("guard_evidence").EnumerateArray());
            Assert.Equal("require", evidence.GetProperty("role").GetString());
            Assert.Equal("after", evidence.GetProperty("direction").GetString());
            Assert.Equal("File.Move", evidence.GetProperty("query").GetString());
            Assert.Equal(8, evidence.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_GuardFiltersApplyToCount_Issue2852()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_guard_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                using System.IO;

                public class App
                {
                    public void Guarded(string path)
                    {
                        var length = new FileInfo(path).Length;
                        var text = File.ReadAllText(path);
                    }

                    public void Unguarded(string path)
                    {
                        var text = File.ReadAllText(path);
                    }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["File.ReadAllText", "--db", dbPath, "--exact-substring", "--reject-before", "Length", "--guard-window", "2", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SearchUsageLineListsGuardFlags_Issue2852()
    {
        var usage = ConsoleUi.GetUsageLine("search");

        Assert.NotNull(usage);
        Assert.Contains("--require-before <query>", usage);
        Assert.Contains("--require-after <query>", usage);
        Assert.Contains("--reject-before <query>", usage);
        Assert.Contains("--reject-after <query>", usage);
        Assert.Contains("--guard-window <n>", usage);
    }
}
