using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class JsonEnvelopeWrapperIssue4863Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void FindAll_RegexPageWalkEnumeratesCorpusBeyondDefaultLineCap_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_regex_page_walk_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var expectedLines = new[] { 1, 50_001, 100_001, 150_001, 200_001, 250_001 };
            var matchLines = expectedLines.ToHashSet();
            var content = new StringBuilder(capacity: 600_000);
            for (var line = 1; line <= 250_005; line++)
            {
                content.Append(matchLines.Contains(line) ? "Needle" : "x");
                if (line < 250_005)
                    content.Append('\n');
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Large.txt", "text", content.ToString());

            var args = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--regex",
                "--json=ndjson", "--limit", "2",
            };

            var actualLines = new List<int>();
            string? cursor = null;
            var pageCount = 0;
            do
            {
                var page = RunFindPage(args, cursor);
                pageCount++;
                Assert.True(
                    pageCount <= 3,
                    $"find cursor page walk did not make forward progress; cursor={cursor ?? "<null>"}.");
                actualLines.AddRange(page.Rows.Select(row => row.GetProperty("line").GetInt32()));
                cursor = page.NextCursor;
                Assert.Equal(page.HasMore, cursor is not null);
            }
            while (cursor is not null);

            Assert.Equal(3, pageCount);
            Assert.Equal(expectedLines, actualLines);
            Assert.Equal(actualLines.Count, actualLines.Distinct().Count());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CursorResumesSameLineUnicodeAndLargeLineAtRecordBoundary_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_unicode_record_cursor_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longPrefix = new string('x', 20_000);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Unicode.txt",
                "text",
                $"{longPrefix}猫 猫 猫\n猫\n");
            var args = new[]
            {
                "find", "猫", "--db", dbPath, "--all", "--regex",
                "--json=ndjson", "--limit", "1", "--max-line-width", "80",
            };

            var locations = new List<(int Line, int Column)>();
            string? cursor = null;
            var pageCount = 0;
            do
            {
                var page = RunFindPage(args, cursor);
                pageCount++;
                Assert.True(
                    pageCount <= 4,
                    $"find cursor page walk did not make forward progress; locations={string.Join(",", locations)}; cursor={cursor ?? "<null>"}.");
                var row = Assert.Single(page.Rows);
                locations.Add((
                    row.GetProperty("line").GetInt32(),
                    row.GetProperty("column").GetInt32()));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            Assert.Equal(
                new[]
                {
                    (Line: 1, Column: 20_001),
                    (Line: 1, Column: 20_003),
                    (Line: 1, Column: 20_005),
                    (Line: 2, Column: 1),
                },
                locations);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_HighOrdinalUnicodeCursorResumesWithoutWalkingUtf8Prefixes_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_high_ordinal_cursor_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            const int resumeMatchOrdinal = 10_000;
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/HighOrdinal.txt",
                "text",
                string.Join(' ', Enumerable.Repeat("猫", resumeMatchOrdinal + 2)));

            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var reader = new DbReader(db);
            var page = reader.FindInFiles(
                "猫",
                limit: 1,
                exact: true,
                resumePath: "src/HighOrdinal.txt",
                resumeLine: 1,
                resumeFileOrdinal: 0,
                resumeMatchOrdinal: resumeMatchOrdinal,
                resumeByteOffset: resumeMatchOrdinal * 4,
                captureContinuation: true);

            var row = Assert.Single(page.Results);
            Assert.Equal(1, row.Line);
            Assert.Equal((resumeMatchOrdinal * 2) + 1, row.Column);
            Assert.Equal(resumeMatchOrdinal + 1, page.Scan.NextMatchOrdinal);
            Assert.Equal((resumeMatchOrdinal + 1) * 4, page.Scan.NextByteOffset);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CursorFailuresAreTypedForMalformedMismatchAndStaleState_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_cursor_errors_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Rows.txt",
                "text",
                "Needle one\nNeedle two\nNeedle three\n");
            var args = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact",
                "--json=ndjson", "--limit", "1",
            };
            var cursor = Assert.IsType<string>(RunFindPage(args, cursor: null).NextCursor);

            AssertCursorFailure(
                args.Concat(["--cursor", "response:v2:not-base64"]).ToArray(),
                "cursor_malformed");
            AssertCursorFailure(
                args.Concat([
                    "--cursor",
                    MutateCursor(cursor, payload => payload["resume_line"] = 999_999),
                ]).ToArray(),
                "cursor_malformed");
            AssertCursorFailure(
                args.Concat([
                    "--cursor",
                    MutateCursor(cursor, payload =>
                    {
                        payload.Remove("resume_match_ordinal");
                        payload["resume_byte_offset"] = 123;
                    }),
                ]).ToArray(),
                "cursor_malformed");
            AssertCursorFailure(
                args.Select(value => value == "Needle" ? "Different" : value)
                    .Concat(["--cursor", cursor])
                    .ToArray(),
                "cursor_mismatch");

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Changed.txt",
                "text",
                "Needle after cursor creation\n");
            AssertCursorFailure(
                args.Concat(["--cursor", cursor]).ToArray(),
                "cursor_stale");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CursorIsBoundToRowOrCountScanMode_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_cursor_scan_mode_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Rows.txt",
                "text",
                "Needle one\nNeedle two\nNeedle three\n");
            var rowArgs = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact",
                "--json=ndjson", "--limit", "1",
            };
            var rowCursor = Assert.IsType<string>(RunFindPage(rowArgs, cursor: null).NextCursor);
            var countArgs = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact", "--count",
                "--json", "--line-scan-limit", "1", "--allow-partial",
            };
            var (countExitCode, countStdout, countStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(countArgs, _jsonOptions, "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            using var countDocument = JsonDocument.Parse(countStdout);
            var countCursor = Assert.IsType<string>(
                countDocument.RootElement.GetProperty("next_cursor").GetString());

            AssertCursorFailure(
                rowArgs.Concat(["--cursor", countCursor]).ToArray(),
                "cursor_mismatch");

            var (resumeExitCode, resumeStdout, resumeStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    countArgs.Concat(["--cursor", rowCursor]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.UsageError, resumeExitCode);
            Assert.Equal(string.Empty, resumeStderr);
            using var resumeDocument = JsonDocument.Parse(resumeStdout);
            Assert.Equal(
                "cursor_mismatch",
                resumeDocument.RootElement.GetProperty("category").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindContinuationIsExposedInHelpAndFlagSchema_Issue4863()
    {
        var cursorFlag = Assert.Single(
            CliFlagSchema.GetCompletionFlagsForCommand("find"),
            flag => flag.Name == "--cursor");

        Assert.Contains("match boundaries", cursorFlag.Description, StringComparison.Ordinal);
        var (printed, stdout, stderr) = ConsoleCapture.Capture(() =>
            ConsoleUi.PrintCommandUsage("find") ? 1 : 0);
        Assert.Equal(1, printed);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("--cursor <next_cursor>", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void FindAll_ByteBudgetCursorWalkHasNoDuplicatesOrGaps_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_byte_budget_cursor_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var suffix = new string('x', 2_000);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Budget.txt",
                "text",
                string.Join('\n', Enumerable.Range(1, 6).Select(line => $"Needle {line} {suffix}")));
            var args = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact",
                "--json=ndjson", "--limit", "4", "--max-json-bytes", "4096",
            };

            var lines = new List<int>();
            string? cursor = null;
            var pageCount = 0;
            do
            {
                var page = RunFindPage(args, cursor);
                pageCount++;
                Assert.True(pageCount <= 6, "byte-budget cursor page walk did not make forward progress.");
                lines.AddRange(page.Rows.Select(row => row.GetProperty("line").GetInt32()));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            Assert.True(pageCount > 1);
            Assert.Equal(Enumerable.Range(1, 6), lines);
            Assert.Equal(lines.Count, lines.Distinct().Count());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CancelledResumeCanRetryTheSameCursor_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_cancelled_resume_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Cancelled.txt",
                "text",
                "Needle one\nNeedle two\nNeedle three\n");
            var args = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact",
                "--json=ndjson", "--limit", "1",
            };
            var cursor = Assert.IsType<string>(RunFindPage(args, cursor: null).NextCursor);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelledArgs = args.Concat(["--cursor", cursor]).ToArray();
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    cancelledArgs,
                    _jsonOptions,
                    "1.0.0-test",
                    cancellationToken: cancellation.Token));

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("cancelled", stderr, StringComparison.OrdinalIgnoreCase);

            var retried = RunFindPage(args, cursor);
            var row = Assert.Single(retried.Rows);
            Assert.Equal(2, row.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_MidScanCancellationReturnsSignalExitWithoutCursor_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_mid_scan_cancel_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Cancelled.txt",
                "text",
                "Needle one\nNeedle two\n");
            using var cancellation = new CancellationTokenSource();

            (int ExitCode, string StdOut, string StdErr) result;
            try
            {
                DbReader.FindLineScannedForTesting = cancellation.Cancel;
                result = ConsoleCapture.Capture(() =>
                    ProgramRunner.Run(
                        [
                            "find", "Needle", "--db", dbPath, "--all", "--exact",
                            "--json=ndjson", "--limit", "1",
                        ],
                        _jsonOptions,
                        "1.0.0-test",
                        cancellationToken: cancellation.Token));
            }
            finally
            {
                DbReader.FindLineScannedForTesting = null;
            }

            Assert.Equal(CommandExitCodes.CancelledBySignal, result.ExitCode);
            Assert.Equal(string.Empty, result.StdOut);
            Assert.Contains("cancelled", result.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("next_cursor", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_RegexTimeoutDoesNotIssueContinuationCursor_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_timeout_cursor_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Timeout.txt",
                "text",
                new string('a', 4_096) + "!\n");

            try
            {
                DbReader.FindRegexMatchTimeoutForTesting = TimeSpan.FromMilliseconds(1);
                var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                    ProgramRunner.Run(
                        [
                            "find", "^(a+)+$", "--db", dbPath, "--all", "--regex",
                            "--json=ndjson", "--limit", "1",
                        ],
                        _jsonOptions,
                        "1.0.0-test"));

                Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.DoesNotContain("next_cursor", stdout, StringComparison.Ordinal);
                using var document = JsonDocument.Parse(stdout);
                Assert.Equal("regex_timeout", document.RootElement.GetProperty("category").GetString());
            }
            finally
            {
                DbReader.FindRegexMatchTimeoutForTesting = null;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CountPagesResumeAfterLineScanCaps_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_count_cursor_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Count.txt",
                "text",
                string.Join('\n', Enumerable.Range(1, 12).Select(line => line is 1 or 4 or 7 or 10 ? "Needle" : "x")));
            var args = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact", "--count",
                "--json", "--line-scan-limit", "4", "--allow-partial",
            };

            var counts = new List<int>();
            string? cursor = null;
            do
            {
                var pageArgs = cursor is null ? args : args.Concat(["--cursor", cursor]).ToArray();
                var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                    ProgramRunner.Run(pageArgs, _jsonOptions, "1.0.0-test"));
                Assert.True(
                    exitCode == CommandExitCodes.Success,
                    $"Expected count page success but received {exitCode}; cursor={cursor ?? "<null>"}; stdout={stdout}; stderr={stderr}");
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                counts.Add(root.GetProperty("count").GetInt32());
                Assert.False(root.GetProperty("authoritative_count").GetBoolean());
                cursor = root.GetProperty("next_cursor").GetString();
                Assert.Equal(root.GetProperty("has_more").GetBoolean(), cursor is not null);
                Assert.True(counts.Count <= 3, "count cursor page walk did not make forward progress.");
            }
            while (cursor is not null);

            Assert.Equal(new[] { 2, 1, 1 }, counts);
            Assert.Equal(4, counts.Sum());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--fields", "path")]
    [InlineData("--max-json-bytes", "4096")]
    public void FindAll_CountRejectsBoundedRowControls_Issue4863(
        string control,
        string value)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_count_bounded_control_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Count.txt", "text", "Needle\n");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    [
                        "find", "Needle", "--db", dbPath, "--all", "--exact",
                        "--count", "--json", control, value,
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains(
                "cannot be combined with --count",
                stdout + stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CountCursorRejectsLineBeyondSelectedFile_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_count_line_boundary_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Count.txt",
                "text",
                "Needle\nNeedle\nNeedle\n");
            var args = new[]
            {
                "find", "Needle", "--db", dbPath, "--all", "--exact", "--count",
                "--json", "--line-scan-limit", "1", "--allow-partial",
            };
            var (firstExitCode, firstStdout, firstStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = JsonDocument.Parse(firstStdout);
            var cursor = Assert.IsType<string>(
                firstDocument.RootElement.GetProperty("next_cursor").GetString());
            var malformedCursor = MutateCursor(
                cursor,
                payload => payload["resume_line"] = 999_999);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    args.Concat(["--cursor", malformedCursor]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(
                "cursor_malformed",
                document.RootElement.GetProperty("category").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FindAll_CountMissingCursorValuePreservesCountModeAndReturnsTypedError_Issue4863()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("find_count_missing_cursor_4863");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Count.txt", "text", "Needle\n");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    [
                        "find", "Needle", "--db", dbPath, "--all", "--json",
                        "--cursor", "--count",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Contains("find count", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal("cursor_malformed", document.RootElement.GetProperty("category").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private FindPage RunFindPage(string[] args, string? cursor)
    {
        var pageArgs = cursor is null
            ? args
            : args.Concat(["--cursor", cursor]).ToArray();
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(pageArgs, _jsonOptions, "1.0.0-test"));

        Assert.True(
            exitCode == CommandExitCodes.Success,
            $"Expected success but received exit code {exitCode}; cursor={cursor ?? "<null>"}; stdout={stdout}; stderr={stderr}");
        Assert.Equal(string.Empty, stderr);
        var records = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        if (records.Length > 1)
        {
            var terminal = records[^1];
            Assert.True(terminal.GetProperty("terminal_record").GetBoolean());
            var nextCursor = terminal.GetProperty("next_cursor").GetString();
            return new FindPage(
                records[..^1],
                terminal.GetProperty("has_more").GetBoolean(),
                nextCursor);
        }

        var root = Assert.Single(records);
        var metadata = root.GetProperty("metadata");
        return new FindPage(
            root.GetProperty("results").EnumerateArray().Select(row => row.Clone()).ToArray(),
            metadata.GetProperty("has_more").GetBoolean(),
            metadata.GetProperty("next_cursor").GetString());
    }

    private void AssertCursorFailure(string[] args, string expectedCategory)
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        if (string.IsNullOrEmpty(stdout))
        {
            Assert.Contains(expectedCategory, stderr, StringComparison.Ordinal);
            return;
        }

        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(
            expectedCategory,
            document.RootElement
                .GetProperty("metadata")
                .GetProperty("error")
                .GetProperty("category")
                .GetString());
    }

    private static string MutateCursor(
        string cursor,
        Action<JsonObject> mutate)
    {
        const string prefix = "response:v2:";
        Assert.StartsWith(prefix, cursor, StringComparison.Ordinal);
        var encoded = cursor[prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = (encoded.Length % 4) switch
        {
            2 => encoded + "==",
            3 => encoded + "=",
            _ => encoded,
        };
        var payload = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))));
        mutate(payload);
        var mutated = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return prefix + mutated;
    }

    private sealed record FindPage(
        IReadOnlyList<JsonElement> Rows,
        bool HasMore,
        string? NextCursor);
}
