using System.Text.Json;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public class QueryCommandRunnerSearchIssue4906Tests
{
    [Theory]
    [InlineData("Widget.*", "--regex")]
    [InlineData("Widget", "--all")]
    public void RunSearch_ScanOnlyFlagsPointHumanUsersToFindWithoutExecuting_Issue4906(
        string query,
        string scanFlag)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(
                ["search", query, scanFlag],
                JsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains($"{scanFlag} is not supported for search.", stderr, StringComparison.Ordinal);
        Assert.Contains("cdidx find --query", stderr, StringComparison.Ordinal);
        Assert.Contains("--all", stderr, StringComparison.Ordinal);
        Assert.Contains("displayed only; not executed", stderr, StringComparison.Ordinal);
        Assert.Contains("Usage: cdidx search", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--data-dir", stderr, StringComparison.Ordinal);
        if (scanFlag == "--regex")
            Assert.Contains("--regex", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_RegexJsonReturnsTypedShellSafeFindAlternative_Issue4906()
    {
        const string query = "a'b $value; .*";
        const string path = "src/space dir/**";
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(
                [
                    "search",
                    "--query", query,
                    "--regex",
                    "--path", path,
                    "--path", "tools/**",
                    "--lang", "cs",
                    "--exclude-path", "**/obj/**",
                    "--exclude-tests",
                    "--include-generated",
                    "--limit", "7",
                    "--snippet-lines", "3",
                    "--max-line-width", "120",
                    "--json",
                ],
                JsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(CommandErrorCodes.UsageError, root.GetProperty("error_code").GetString());
        Assert.Equal("search", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("automatic_execution").GetBoolean());
        Assert.Contains("not executed", root.GetProperty("alternative_reason").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("non_equivalent_options").EnumerateArray());
        Assert.Empty(root.GetProperty("alternative_blockers").EnumerateArray());

        var alternative = root.GetProperty("alternative_command");
        Assert.True(alternative.GetProperty("display_only").GetBoolean());
        Assert.False(alternative.GetProperty("executed").GetBoolean());
        var argv = alternative
            .GetProperty("argv")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(["cdidx", "find", "--query", query], argv.Take(4));
        Assert.Equal(1, argv.Count(value => value == "--regex"));
        Assert.Equal("csharp", ValueAfterIssue4906(argv, "--lang"));
        Assert.Equal("7", ValueAfterIssue4906(argv, "--limit"));
        Assert.Equal("3", ValueAfterIssue4906(argv, "--snippet-lines"));
        Assert.Equal("120", ValueAfterIssue4906(argv, "--max-line-width"));
        Assert.Contains(path, argv);
        Assert.Contains("tools/**", argv);
        Assert.Contains("**/obj/**", argv);
        Assert.Contains("--exclude-tests", argv);
        Assert.Contains("--include-generated", argv);
        Assert.Contains("--json", argv);
        Assert.DoesNotContain("--all", argv);
        Assert.DoesNotContain("--data-dir", argv);

        var posix = alternative.GetProperty("posix_sh").GetString()!;
        var powershell = alternative.GetProperty("powershell").GetString()!;
        Assert.Contains("'a'\\''b $value; .*'", posix, StringComparison.Ordinal);
        Assert.Contains("'src/space dir/**'", posix, StringComparison.Ordinal);
        Assert.Contains("'a''b $value; .*'", powershell, StringComparison.Ordinal);
        Assert.Contains("'src/space dir/**'", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_StructuredFormatDoesNotInventJsonFlag_Issue4906()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(
                ["search", "TODO", "--regex", "--path", "src/**", "--format", "csv"],
                JsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--format csv", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--format csv --json", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_FormatCountPreservesStructuredFindOutput_Issue4906()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(
                ["search", "TODO", "--regex", "--format", "count"],
                JsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(
            "cdidx find --query TODO --all --regex --format count",
            stderr,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--regex --count", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_OptionShapedQueryIsNotReinterpretedInFindAlternative_Issue4906()
    {
        var cases = new[]
        {
            new
            {
                Args = new[] { "search", "--query", "--regex", "--all", "--json" },
                Query = "--regex",
                Scope = "--all",
                ActualRegex = false,
            },
            new
            {
                Args = new[] { "search", "--query", "--all", "--regex", "--path", "src/**", "--json" },
                Query = "--all",
                Scope = "--path",
                ActualRegex = true,
            },
        };

        foreach (var testCase in cases)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                ProgramRunner.Run(testCase.Args, JsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var argv = document.RootElement
                .GetProperty("alternative_command")
                .GetProperty("argv")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
            Assert.Equal(testCase.Query, ValueAfterIssue4906(argv, "--query"));
            Assert.Contains(testCase.Scope, argv);
            Assert.Equal(testCase.ActualRegex ? 1 : 0, argv.Skip(4).Count(value => value == "--regex"));
        }

        var consumedOptionCases = new[]
        {
            new[] { "search", "--query", "--json", "--regex", "--path", "src/**" },
            new[] { "search", "--query", "--data-dir", "--regex", "--path", "src/**", "--json" },
        };
        foreach (var args in consumedOptionCases)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                ProgramRunner.Run(args, JsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var argv = document.RootElement
                .GetProperty("alternative_command")
                .GetProperty("argv")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();
            Assert.Equal(args[2], argv[3]);
            Assert.Equal(1, argv.Count(value => value == args[2]));
        }
    }

    [Fact]
    public void RunSearch_UnmappableOrUnsafeScanRequestsExplainWhyWithoutCommand_Issue4906()
    {
        var cases = new[]
        {
            new
            {
                Args = new[]
                {
                    "search", "TODO", "--regex", "--path", "src/**",
                    "--group-by", "file", "--count", "--json",
                },
                ExpectedOption = "--group-by",
                ExpectedBlocker = string.Empty,
            },
            new
            {
                Args = new[] { "search", "TODO", "--all", "--path", "src/**", "--json" },
                ExpectedOption = string.Empty,
                ExpectedBlocker = "either --path filters or --all",
            },
            new
            {
                Args = new[] { "search", "--query", "line1\nline2", "--regex", "--json" },
                ExpectedOption = string.Empty,
                ExpectedBlocker = "control characters",
            },
        };

        foreach (var testCase in cases)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                ProgramRunner.Run(testCase.Args, JsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("alternative_command").ValueKind);
            Assert.False(root.GetProperty("automatic_execution").GetBoolean());
            if (testCase.ExpectedOption.Length > 0)
            {
                Assert.Contains(
                    root.GetProperty("non_equivalent_options").EnumerateArray(),
                    item => item.GetString() == testCase.ExpectedOption);
            }
            if (testCase.ExpectedBlocker.Length > 0)
            {
                Assert.Contains(
                    root.GetProperty("alternative_blockers").EnumerateArray(),
                    item => item.GetString()!.Contains(testCase.ExpectedBlocker, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void RunSearch_FindAlternativeRejectsIncompatibleCompactSnippetOutput_Issue4906()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(
                [
                    "search", "TODO", "--regex", "--path", "src/**",
                    "--format", "compact", "--snippet-lines", "3",
                ],
                JsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("metadata").GetProperty("error");
        Assert.Equal(JsonValueKind.Null, error.GetProperty("alternative_command").ValueKind);
        Assert.Contains(
            error.GetProperty("alternative_blockers").EnumerateArray(),
            item => item.GetString()!.Contains(
                "compact cannot be combined with --snippet-lines",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RunSearch_FindAlternativeHonorsJsonByteBudget_Issue4906()
    {
        const int maxJsonBytes = 200;
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(
                [
                    "search", "TODO", "--regex", "--path", "src/**",
                    "--json", "--max-json-bytes", "200",
                ],
                JsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stdout) <= maxJsonBytes);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("too small for search recovery error JSON output", stderr, StringComparison.Ordinal);
        Assert.Contains("Increase --max-json-bytes", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_FindAlternativeRejectsFindValidationFailures_Issue4906()
    {
        var cases = new[]
        {
            new
            {
                Args = new[]
                {
                    "search", "TODO", "--regex", "--path", "src/**",
                    "--snippet-lines", "0", "--json",
                },
                ExpectedBlocker = "positive integer",
            },
            new
            {
                Args = new[]
                {
                    "search", new string('x', QueryLimits.MaxQueryLength + 1),
                    "--regex", "--json",
                },
                ExpectedBlocker = $"maximum {QueryLimits.MaxQueryLength} characters",
            },
        };

        foreach (var testCase in cases)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                ProgramRunner.Run(testCase.Args, JsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("alternative_command").ValueKind);
            Assert.Contains(
                root.GetProperty("alternative_blockers").EnumerateArray(),
                item => item.GetString()!.Contains(testCase.ExpectedBlocker, StringComparison.Ordinal));
        }
    }

    private static string ValueAfterIssue4906(IReadOnlyList<string> argv, string option)
    {
        var index = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == option)
            {
                index = i;
                break;
            }
        }
        Assert.InRange(index, 0, argv.Count - 2);
        return argv[index + 1];
    }
}
