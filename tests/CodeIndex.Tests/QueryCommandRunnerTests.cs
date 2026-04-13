using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for query-style CLI command execution.
/// クエリ系CLIコマンド実行のテスト。
/// </summary>
public class QueryCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ParseArgs_ParsesFiltersFlagsAndClampsSnippetLines()
    {
        var options = QueryCommandRunner.ParseArgs(
        [
            "RunSearch",
            "--db", "/tmp/query.db",
            "--no-json",
            "--limit", "7",
            "--lang", "csharp",
            "--kind", "function",
            "--fts",
            "--body",
            "--path", "src/**",
            "--exclude-path", "tests/**",
            "--exclude-path", "docs/**",
            "--exclude-tests",
            "--start", "12",
            "--end", "18",
            "--before", "2",
            "--after", "4",
            "--snippet-lines", "99",
        ], jsonDefault: true);

        Assert.Equal("/tmp/query.db", options.DbPath);
        Assert.False(options.Json);
        Assert.Equal(7, options.Limit);
        Assert.Equal("csharp", options.Lang);
        Assert.Equal("function", options.Kind);
        Assert.Equal("RunSearch", options.Query);
        Assert.True(options.RawFts);
        Assert.True(options.IncludeBody);
        Assert.Equal(new[] { "src/**" }, options.PathPatterns);
        Assert.Equal(["tests/**", "docs/**"], options.ExcludePaths);
        Assert.True(options.ExcludeTests);
        Assert.Equal(12, options.StartLine);
        Assert.Equal(18, options.EndLine);
        Assert.Equal(2, options.ContextBefore);
        Assert.Equal(4, options.ContextAfter);
        Assert.Equal(SearchSnippetFormatter.MaxSnippetLines, options.SnippetLines);
    }

    [Fact]
    public void ParseArgs_CountFlagParsed()
    {
        var options = QueryCommandRunner.ParseArgs(["myquery", "--count"], jsonDefault: false);
        Assert.True(options.CountOnly);
        Assert.Equal("myquery", options.Query);
    }

    [Fact]
    public void ParseArgs_InvalidNumbersAndUnknownOptionsFallbackAndReportErrors()
    {
        var (options, _, stderr) = CaptureConsole(() => QueryCommandRunner.ParseArgs(
        [
            "RunSearch",
            "--limit", "0",
            "--start", "0",
            "--end", "-1",
            "--before", "-2",
            "--after", "-3",
            "--snippet-lines", "0",
            "--mystery",
        ], jsonDefault: false));

        Assert.Equal(20, options.Limit);
        Assert.Null(options.StartLine);
        Assert.Null(options.EndLine);
        Assert.Equal(0, options.ContextBefore);
        Assert.Equal(0, options.ContextAfter);
        Assert.Equal(SearchSnippetFormatter.DefaultSnippetLines, options.SnippetLines);
        Assert.Contains("Error: --limit requires a positive integer", stderr);
        Assert.Contains("Error: --start requires a positive integer", stderr);
        Assert.Contains("Error: --end requires a positive integer", stderr);
        Assert.Contains("Error: --before requires a non-negative integer", stderr);
        Assert.Contains("Error: --after requires a non-negative integer", stderr);
        Assert.Contains("Error: --snippet-lines requires a positive integer", stderr);
        Assert.Contains("Warning: unknown option '--mystery' (ignored)", stderr);
    }

    [Fact]
    public void ParseArgs_NameFlagCollectsValuesAndRejectsMissingValue()
    {
        var ok = QueryCommandRunner.ParseArgs(
            ["first", "--name", "Alpha", "--name", "Beta", "extraPositional"],
            jsonDefault: false);
        Assert.Null(ok.ParseError);
        Assert.Equal("first", ok.Query);
        Assert.Equal(new[] { "Alpha", "Beta", "extraPositional" }, ok.ExtraNames);

        // --name swallowing a following flag as data is a silent trust failure; must be rejected.
        // --name が直後のフラグを値として飲み込むのは暗黙の誤動作。拒否する。
        var bad = QueryCommandRunner.ParseArgs(
            ["--name", "--lang", "csharp"],
            jsonDefault: false);
        Assert.NotNull(bad.ParseError);
        Assert.Contains("--name requires a value", bad.ParseError!);

        var badTail = QueryCommandRunner.ParseArgs(["--name"], jsonDefault: false);
        Assert.NotNull(badTail.ParseError);
    }

    [Fact]
    public void BuildSymbolQueryList_TreatsPipeAsLiteralNameCharacter()
    {
        // `|` is a legitimate character in operator symbols (C# `operator |`, etc.); it must not
        // be treated as OR syntax so those names stay searchable.
        // `|` は `operator |` など演算子名に出現する有効な文字。OR 構文として分割してはならない。
        var options = QueryCommandRunner.ParseArgs(["|"], jsonDefault: false);
        var (queries, hadInput) = QueryCommandRunner.BuildSymbolQueryList(options);
        Assert.True(hadInput);
        Assert.NotNull(queries);
        Assert.Equal(new[] { "|" }, queries!);

        var compound = QueryCommandRunner.ParseArgs(["operator|"], jsonDefault: false);
        var (compoundQueries, _) = QueryCommandRunner.BuildSymbolQueryList(compound);
        Assert.Equal(new[] { "operator|" }, compoundQueries!);
    }

    [Fact]
    public void BuildSymbolQueryList_FailsClosedOnlyWhenEveryInputIsEmptyString()
    {
        // --name "" is explicit input that normalizes to empty — must be flagged, not silently
        // broadened into an all-symbols dump.
        // --name "" は明示入力だが正規化で空になる。フラグを立てて全件ダンプを防ぐ。
        var rejected = QueryCommandRunner.ParseArgs(["--name", ""], jsonDefault: false);
        var (rejectedQueries, rejectedHadInput) = QueryCommandRunner.BuildSymbolQueryList(rejected);
        Assert.Null(rejectedQueries);
        Assert.True(rejectedHadInput);
    }

    [Fact]
    public void RunSymbols_EmptyAfterNormalizationFailsClosed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_empty_norm");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exit, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(["--name", "", "--db", dbPath], _jsonOptions));
            Assert.Equal(1, exit);
            Assert.Contains("empty after normalization", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_RejectsOversizedMultiNameBatches()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_oversize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var names = Enumerable.Range(0, QueryCommandRunner.MaxSymbolQueryNames + 5)
                .Select(i => $"Name{i}")
                .ToArray();
            var argv = names.Concat(new[] { "--db", dbPath }).ToArray();
            int exit;
            string stderr;
            (exit, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(argv, _jsonOptions));
            Assert.Equal(1, exit);
            Assert.Contains("too many symbol names", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_WithJsonOutputsCompactSnippetMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_search");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "line 1\nline 2\nline 3\nTarget();\nline 5\nline 6");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Target", "--db", dbPath, "--json", "--snippet-lines", "3"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/app.cs", json.GetProperty("path").GetString());
            Assert.Equal(1, json.GetProperty("chunk_start_line").GetInt32());
            Assert.Equal(6, json.GetProperty("chunk_end_line").GetInt32());
            Assert.Equal(3, json.GetProperty("snippet_start_line").GetInt32());
            Assert.Equal(5, json.GetProperty("snippet_end_line").GetInt32());
            Assert.Contains("Target();", json.GetProperty("snippet").GetString());
            Assert.Equal(4, json.GetProperty("match_lines")[0].GetInt32());
            Assert.Equal(1, json.GetProperty("highlights").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunReferences_UnsupportedLanguageWithoutMatches_PrintsGraphSupportHint()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_refs");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["MissingSymbol", "--db", dbPath, "--lang", "markdown"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Contains("No references found.", stderr);
            Assert.Contains("call-graph queries are not indexed for 'markdown'", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExcerpt_RequiresStartLine()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["src/app.cs"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: excerpt requires --start <line>", stderr);
    }

    [Fact]
    public void RunExcerpt_RejectsStartGreaterThanEnd()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
            ["src/app.cs", "--start", "5", "--end", "3"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--start (5) must be less than or equal to --end (3)", stderr);
    }

    [Fact]
    public void RunInspect_BlankQueryReturnsUsageError()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
            ["   "],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: inspect requires a symbol query argument", stderr);
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

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
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

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
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
    public void RunStatus_HumanReadableIncludesGitMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_status");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains($"Git HEAD: {expectedHead}", stdout);
            Assert.Contains("Git Dirty: True", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_MissingDatabaseReturnsGuidance()
    {
        var missingDbPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.db");

        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--db", missingDbPath],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Contains("Error: database not found at", stderr);
        // Verify full (absolute) path is shown, not just the basename / フルパス表示を検証
        Assert.Contains(Path.GetFullPath(missingDbPath), stderr);
        Assert.Contains("Run 'cdidx index <projectPath>' first to create the index.", stderr);
    }

    private static (T Result, string Stdout, string Stderr) CaptureConsole<T>(Func<T> action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var result = action();
                return (result, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    // --- TryParseIso8601Since tests / TryParseIso8601Sinceテスト ---

    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("2024-01-15T10:30")]              // minute precision / 分精度
    [InlineData("2024-01-15T10:30Z")]
    [InlineData("2024-01-15T10:30+09:00")]
    [InlineData("2024-01-15T10:30:00")]
    [InlineData("2024-01-15T10:30:00Z")]
    [InlineData("2024-01-15T10:30:00+09:00")]
    [InlineData("2024-01-15T10:30:00.000Z")]
    [InlineData("2024-01-15T10:30:00.123")]       // offsetless fractional / オフセットなし小数秒
    [InlineData("2024-01-15T10:30:00.1234567Z")]
    [InlineData("2024-01-15T10:30:00.1Z")]        // 1-digit fraction / 1桁小数
    public void TryParseIso8601Since_AcceptsValidIsoFormats(string input)
    {
        var ok = QueryCommandRunner.TryParseIso8601Since(input, out var result);
        Assert.True(ok, $"Expected '{input}' to be accepted as ISO 8601");
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Theory]
    [InlineData("01/02/2024")]        // ambiguous locale-dependent / ロケール依存の曖昧な形式
    [InlineData("1/2/2024")]
    [InlineData("02-Jan-2024")]
    [InlineData("Jan 15, 2024")]
    [InlineData("not-a-date")]
    [InlineData("yesterday")]
    [InlineData("")]
    public void TryParseIso8601Since_RejectsNonIsoFormats(string input)
    {
        var ok = QueryCommandRunner.TryParseIso8601Since(input, out _);
        Assert.False(ok, $"Expected '{input}' to be rejected as non-ISO 8601");
    }

    [Fact]
    public void TryParseIso8601Since_DateOnlyTreatedAsLocalTime()
    {
        // Offsetless dates are treated as local time, matching prior DateTime.TryParse behavior /
        // オフセットなしの日付はローカル時刻として扱う（従来のDateTime.TryParseの動作と一致）
        QueryCommandRunner.TryParseIso8601Since("2024-06-15", out var result);
        var expected = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 6, 15))).UtcDateTime;
        Assert.Equal(expected, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void TryParseIso8601Since_ExplicitUtcTimestamp()
    {
        QueryCommandRunner.TryParseIso8601Since("2024-06-15T12:00:00Z", out var result);
        Assert.Equal(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void TryParseIso8601Since_ConvertsTimezoneOffsetToUtc()
    {
        QueryCommandRunner.TryParseIso8601Since("2024-06-15T12:00:00+09:00", out var result);
        Assert.Equal(new DateTime(2024, 6, 15, 3, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ParseArgs_RejectsSinceWithAmbiguousDate()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["search", "foo", "--since", "01/02/2024"], jsonDefault: false);
        Assert.NotNull(options.ParseError);
        Assert.Contains("could not parse", options.ParseError);
    }

    [Fact]
    public void ParseArgs_AcceptsSinceWithIsoDate()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["search", "foo", "--since", "2024-01-02"], jsonDefault: false);
        Assert.Null(options.ParseError);
        Assert.NotNull(options.Since);
        // Offsetless date → local midnight → UTC / オフセットなし → ローカル深夜 → UTC
        var expected = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 1, 2))).UtcDateTime;
        Assert.Equal(expected, options.Since.Value);
    }

    [Fact]
    public void ParseArgs_RejectsBareSinceWithNoValue()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["search", "foo", "--since"], jsonDefault: false);
        Assert.NotNull(options.ParseError);
        Assert.Contains("--since requires a value", options.ParseError);
    }

    [Fact]
    public void ParseArgs_RejectsBareSinceForFiles()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["files", "--since"], jsonDefault: false);
        Assert.NotNull(options.ParseError);
        Assert.Contains("--since requires a value", options.ParseError);
    }

    [Fact]
    public void RunSymbols_ExactZero_JsonIncludesResultsMarkerAndHint()
    {
        // Codex #88 review (latest pass): CLI --json normally streams result rows; the exact-
        // zero hint envelope must carry a `results: []` marker so consumers can discriminate
        // a summary from a row DTO — same envelope shape as WriteDegradedGraphZeroResult.
        // Exercise three branches for `symbols`: exact-hit (row stream), exact-miss with
        // sibling (envelope with hint), and genuine miss (no stdout output at all).
        // CLI の envelope shape 固定: `results: []` marker と `exact_zero_hint` を期待。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_exact_zero_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/handlers.cs", "csharp",
                "public class HandleRequest { public void Run() {} }\npublic class HandleRequestAsync { public void Run() {} }\n");

            // Exact hit: row stream, no envelope.
            // exact hit: 通常の row 出力、envelope 無し。
            var (hitCode, hitStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["HandleRequest", "--exact", "--db", dbPath, "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, hitCode);
            var hitLines = hitStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.NotEmpty(hitLines);
            using var firstRow = JsonDocument.Parse(hitLines[0]);
            Assert.True(firstRow.RootElement.TryGetProperty("name", out _), "exact-hit output must be a row DTO (has 'name' property)");

            // Exact miss with substring sibling: single envelope line with results:[] + hint.
            // exact miss + substring 有り: envelope 1 行、results:[] と hint を含む。
            var (missCode, missStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["HandleReq", "--exact", "--db", dbPath, "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, missCode);
            var missLines = missStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Single(missLines);
            using var envelope = JsonDocument.Parse(missLines[0]);
            var root = envelope.RootElement;
            Assert.Equal(0, root.GetProperty("count").GetInt32());
            Assert.Equal(JsonValueKind.Array, root.GetProperty("results").ValueKind);
            Assert.Equal(0, root.GetProperty("results").GetArrayLength());
            var hint = root.GetProperty("exact_zero_hint");
            Assert.True(hint.GetProperty("relaxed_count").GetInt32() >= 2);
            var samples = hint.GetProperty("sample_names").EnumerateArray().Select(s => s.GetString()).ToList();
            Assert.Contains("HandleRequest", samples);
            Assert.Contains("HandleRequestAsync", samples);

            // Genuine miss: no stdout output (exit NotFound, no envelope to confuse consumers).
            // 本物の miss: stdout 何も出さない (consumer を混乱させない)。
            var (genuineCode, genuineStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["zzz_nonexistent_xyz_123", "--exact", "--db", dbPath, "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, genuineCode);
            Assert.True(string.IsNullOrWhiteSpace(genuineStdout), $"expected empty stdout, got: {genuineStdout}");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static JsonDocument ParseJsonOutput(string stdout)
    {
        var jsonLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last();
        return JsonDocument.Parse(jsonLine);
    }
}
