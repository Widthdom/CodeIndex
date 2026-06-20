using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class DbDebugTests
{
    private static string CaptureStderr(Action action)
        => ConsoleCapture.CaptureError(action);

    private static string BuildUnionAllQuery(int count)
        => string.Join(" UNION ALL ", Enumerable.Range(0, count).Select(i => $"SELECT {i} AS value"));

    private static string QuoteSqlIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [Fact]
    public void ExecuteTrackedReader_EmitsActivityAndSlowQueryLog()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_SLOW_QUERY_MS");
        env.Set("CDIDX_SLOW_QUERY_MS", "0");
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CodeIndex.CodeIndexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";

        var stderr = CaptureStderr(() =>
        {
            using var reader = cmd.ExecuteTrackedReader();
            Assert.True(reader.TrackedRead());
            Assert.Equal(1, reader.GetInt32(0));
        });

        Assert.Contains("slow_query", stderr);
        var activity = Assert.Single(stopped.Where(activity => activity.OperationName == "db.query"));
        Assert.Equal("sqlite", activity.GetTagItem("db.system"));
        Assert.Equal("SELECT", activity.GetTagItem("db.operation"));
    }

    [Fact]
    public void ExecuteTrackedReader_SlowQueryThresholdIgnoresCurrentCulturePositiveSign_Issue3404()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_SLOW_QUERY_MS");
        using var _ = new CultureScope(TestCultures.BuildCaretPositiveSignCulture());
        env.Set("CDIDX_SLOW_QUERY_MS", "^0");

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";

        var stderr = CaptureStderr(() =>
        {
            using var reader = cmd.ExecuteTrackedReader();
            Assert.True(reader.TrackedRead());
            Assert.Equal(1, reader.GetInt32(0));
        });

        Assert.DoesNotContain("slow_query", stderr);
    }

    [Fact]
    public void ExecuteTrackedReader_ProfileCapsQueryPlanRows()
    {
        DbDebug.ResetForTesting();
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildUnionAllQuery(120);

            DbDebug.BeginProfile();
            using (var reader = cmd.ExecuteTrackedReader())
            {
                while (reader.TrackedRead()) { }
            }

            var entry = Assert.Single(DbDebug.EndProfile());
            Assert.True(entry.QueryPlan.Count <= DbDebug.MaxQueryPlanRows + 1);
            Assert.Contains(entry.QueryPlan, row => row.Detail.Contains("truncated after", StringComparison.Ordinal));
        }
        finally
        {
            DbDebug.EndProfile();
        }
    }

    [Fact]
    public void ExecuteTrackedReader_ProfileTruncatesLongQueryPlanDetails()
    {
        DbDebug.ResetForTesting();
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            var tableName = "t_" + new string('a', DbDebug.MaxQueryPlanDetailChars * 2);
            var quotedTableName = QuoteSqlIdentifier(tableName);
            using (var create = conn.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {quotedTableName} (id INTEGER)";
                create.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id FROM {quotedTableName}";

            DbDebug.BeginProfile();
            using (var reader = cmd.ExecuteTrackedReader())
            {
                while (reader.TrackedRead()) { }
            }

            var entry = Assert.Single(DbDebug.EndProfile());
            var detail = Assert.Single(entry.QueryPlan).Detail;
            Assert.True(detail.Length <= DbDebug.MaxQueryPlanDetailChars, $"Detail was {detail.Length} chars.");
            Assert.EndsWith("...<truncated>", detail, StringComparison.Ordinal);
        }
        finally
        {
            DbDebug.EndProfile();
        }
    }

    [Fact]
    public void QueryProfileEntry_SlowQueryGlobalToolLogTruncatesSql()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_slow_query_log_{Guid.NewGuid():N}");
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            GlobalToolLog.LogFormatEnvironmentVariable);
        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
            env.Set(GlobalToolLog.LogFormatEnvironmentVariable, "text");

            using var session = GlobalToolLog.TryStartForTesting(["status"], "1.10.0");
            Assert.NotNull(session);
            var sql = "SELECT " + new string('x', 5000) + Environment.NewLine + "FROM very_large_query";
            var entry = new QueryProfileEntry(sql, []);
            entry.AddElapsed(TimeSpan.FromMilliseconds(10));
            entry.MarkCompletedIfSlow(0);
            session!.Dispose();

            var logPath = Assert.Single(Directory.GetFiles(logDir, "stderr-*.log"));
            var content = File.ReadAllText(logPath);
            var slowLine = Assert.Single(content.Split('\n').Where(line => line.Contains("slow_query", StringComparison.Ordinal)));
            Assert.Contains("sql=SELECT ", slowLine);
            Assert.Contains("...<truncated>", slowLine);
            Assert.DoesNotContain(new string('x', 1000), content);
            var sqlText = slowLine[(slowLine.IndexOf(" sql=", StringComparison.Ordinal) + " sql=".Length)..].TrimEnd('\r');
            Assert.True(sqlText.Length <= DbDebug.MaxSlowQuerySqlChars, $"SQL text was {sqlText.Length} chars.");
            Assert.DoesNotContain('\r', sqlText);
            Assert.DoesNotContain('\n', sqlText);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void FormatSqlForSlowQueryLog_RedactsLiteralsAndSensitiveText_Issue3416()
    {
        var path = "/Users/example/private/project/secret_module.cs";
        var searchText = "literal user search text";
        var secret = "0123456789abcdef0123456789abcdef";
        var sql = $"SELECT * FROM chunks WHERE path = '{path}' AND content MATCH '{searchText}' AND api_token = '{secret}' AND rank > 42 AND payload = X'0123abcd'";

        var formatted = DbDebug.FormatSqlForSlowQueryLog(sql);

        Assert.Contains("path = '<redacted>'", formatted);
        Assert.Contains("content MATCH '<redacted>'", formatted);
        Assert.Contains("api_token = '<redacted>'", formatted);
        Assert.Contains("rank > <number>", formatted);
        Assert.Contains("payload = X'<redacted>'", formatted);
        Assert.DoesNotContain(path, formatted);
        Assert.DoesNotContain(searchText, formatted);
        Assert.DoesNotContain(secret, formatted);
        Assert.DoesNotContain("0123abcd", formatted);
    }

    [Fact]
    public void DumpToStderr_NoOp_WhenDisabled()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", null);
        DbDebug.ResetContext();
        var output = CaptureStderr(() => DbDebug.DumpToStderr(new InvalidOperationException("boom")));
        Assert.Empty(output);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    public void IsEnabled_AcceptsTruthyDebugValues(string value)
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", value);
        try
        {
            DbDebug.ResetForTesting();
            var output = CaptureStderr(() => Assert.True(DbDebug.IsEnabled));
            Assert.Empty(output);
        }
        finally
        {
            DbDebug.ResetForTesting();
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("no")]
    [InlineData("off")]
    public void IsEnabled_AcceptsFalsyDebugValues(string value)
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", value);
        try
        {
            DbDebug.ResetForTesting();
            var output = CaptureStderr(() => Assert.False(DbDebug.IsEnabled));
            Assert.Empty(output);
        }
        finally
        {
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void IsEnabled_InvalidDebugValue_WarnsOnceAndFallsBackToOff()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "maybe");
        try
        {
            DbDebug.ResetForTesting();
            var first = CaptureStderr(() => Assert.False(DbDebug.IsEnabled));
            var second = CaptureStderr(() => Assert.False(DbDebug.IsEnabled));

            Assert.Contains("CDIDX_DEBUG value 'maybe' is not recognized", first);
            Assert.Contains("Falling back to off", first);
            Assert.Empty(second);
        }
        finally
        {
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void IsEnabled_InvalidDebugValue_RedactsSecretLookingValue_Issue3403()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        const string secret = "0123456789abcdef0123456789abcdef";
        env.Set("CDIDX_DEBUG", $"token={secret}");
        try
        {
            DbDebug.ResetForTesting();
            var output = CaptureStderr(() => Assert.False(DbDebug.IsEnabled));

            Assert.Contains("CDIDX_DEBUG value 'token=<redacted>' is not recognized", output);
            Assert.DoesNotContain(secret, output);
        }
        finally
        {
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void IsEnabled_InvalidDebugValue_RedactsThroughSharedStderrSink_Issue3683()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        const string secret = "fedcba9876543210fedcba9876543210";
        env.Set("CDIDX_DEBUG", $"password={secret}");
        try
        {
            DbDebug.ResetForTesting();
            using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);

            Assert.False(DbDebug.IsEnabled);

            var stdout = capture.Out!.ToString()!;
            var stderr = capture.Error!.ToString()!;
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("CDIDX_DEBUG value 'password=<redacted>' is not recognized", stderr);
            Assert.DoesNotContain(secret, stderr);
        }
        finally
        {
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void IsEnabled_InvalidDebugValue_RedactsPathAndUrlValue_Issue3403()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        const string path = "/Users/example/private/project";
        const string url = "https://example.test/private/project/config.json";
        const string queryUrl = "https://example.test?query=user-content";
        env.Set("CDIDX_DEBUG", $"path={path} url={url} query={queryUrl}");
        try
        {
            DbDebug.ResetForTesting();
            var output = CaptureStderr(() => Assert.False(DbDebug.IsEnabled));

            Assert.Contains("CDIDX_DEBUG value 'path=<redacted> url=https://example.test<redacted> query=https://example.test<redacted>' is not recognized", output);
            Assert.DoesNotContain(path, output);
            Assert.DoesNotContain("/private/project/config.json", output);
            Assert.DoesNotContain("query=user-content", output);
        }
        finally
        {
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void DumpToStderr_RedactsTextByDefault()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "1");
        try
        {
            DbDebug.ResetContext();
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var init = conn.CreateCommand())
            {
                init.CommandText = "CREATE TABLE t (id INTEGER, content TEXT); INSERT INTO t VALUES (1, 'SECRET_SOURCE_CODE_TOKEN'), (2, NULL);";
                init.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, content FROM t WHERE id >= @min ORDER BY id";
            cmd.Parameters.AddWithValue("@min", 1);

            Exception? caught = null;
            try
            {
                using var reader = cmd.ExecuteTrackedReader();
                while (reader.TrackedRead())
                    _ = reader.GetString(1);
            }
            catch (Exception ex) { caught = ex; }

            Assert.NotNull(caught);
            var output = CaptureStderr(() => DbDebug.DumpToStderr(caught!));
            Assert.Contains("CDIDX_DEBUG", output);
            Assert.Contains("redacted", output);
            Assert.Contains("SELECT id, content FROM t", output);
            Assert.Contains("@min", output);
            Assert.Contains("[content] = <NULL>", output);
            // Row 1's string content must NOT leak verbatim in redacted mode.
            Assert.DoesNotContain("SECRET_SOURCE_CODE_TOKEN", output);
        }
        finally
        {
            DbDebug.ResetContext();
        }
    }

    [Fact]
    public void DumpToStderr_RedactedMode_UsesPathShapeForPathValues()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "1");
        try
        {
            DbDebug.ResetContext();
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var init = conn.CreateCommand())
            {
                init.CommandText = "CREATE TABLE t (file_path TEXT, content TEXT); INSERT INTO t VALUES ('/home/user/private/src/secret_module.cs', 'SECRET_SOURCE_CODE_TOKEN');";
                init.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_path, content FROM t WHERE file_path = @path";
            cmd.Parameters.AddWithValue("@path", "/home/user/private/src/secret_module.cs");
            using (var reader = cmd.ExecuteTrackedReader())
            {
                while (reader.TrackedRead()) { }
            }

            var output = CaptureStderr(() => DbDebug.DumpToStderr(new InvalidOperationException("boom")));
            Assert.Contains("@path = <path segments=5>", output);
            Assert.Contains("[file_path] = <path segments=5>", output);
            Assert.Contains("[content] = <str len=24 sha256=", output);
            Assert.DoesNotContain("/home/user/private/src/secret_module.cs", output);
            Assert.DoesNotContain("SECRET_SOURCE_CODE_TOKEN", output);
        }
        finally
        {
            DbDebug.ResetContext();
        }
    }

    [Fact]
    public void DumpToStderr_RedactedMode_HashesLargeStringsWithBoundedShape()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "1");
        try
        {
            DbDebug.ResetContext();
            var largeValue = new string('x', 100_000) + "tail";
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @value AS content";
            cmd.Parameters.AddWithValue("@value", largeValue);
            using (var reader = cmd.ExecuteTrackedReader())
            {
                Assert.True(reader.TrackedRead());
                _ = reader.GetString(0);
            }

            var output = CaptureStderr(() => DbDebug.DumpToStderr(new InvalidOperationException("boom")));
            Assert.Contains($"@value = <str len={largeValue.Length} sha256=", output);
            Assert.Contains($"[content] = <str len={largeValue.Length} sha256=", output);
            Assert.DoesNotContain(new string('x', 1000), output);
            Assert.DoesNotContain("tail", output);
        }
        finally
        {
            DbDebug.ResetContext();
        }
    }

    [Fact]
    public void DumpToStderr_UnsafeMode_IncludesRawContent()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "unsafe");
        DbDebug.EnableUnsafeForProcess();
        try
        {
            DbDebug.ResetContext();
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var init = conn.CreateCommand())
            {
                init.CommandText = "CREATE TABLE t (id INTEGER, content TEXT); INSERT INTO t VALUES (1, 'RAW_TOKEN');";
                init.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, content FROM t";
            using (var reader = cmd.ExecuteTrackedReader())
            {
                while (reader.TrackedRead()) { }
            }
            var output = CaptureStderr(() => DbDebug.DumpToStderr(new InvalidOperationException("boom")));
            Assert.Contains("unsafe", output);
            Assert.Contains("RAW_TOKEN", output);
        }
        finally
        {
            DbDebug.ResetContext();
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void DumpToStderr_UnsafeEnvAlone_DowngradesToRedactedAndWarns()
    {
        // Issue #1530: a stale `CDIDX_DEBUG=unsafe` in a shell profile or CI env
        // must not silently expose indexed text. Without an explicit per-process
        // opt-in (`--debug-unsafe` on the command line) the helper falls back to
        // redacted mode and emits a one-shot stderr warning. The capture has to
        // wrap the tracking calls too because the downgrade warning fires the
        // first time ResolveMode runs (here: during ExecuteTrackedReader).
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "unsafe");
        DbDebug.ResetForTesting();
        try
        {
            DbDebug.ResetContext();
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var init = conn.CreateCommand())
            {
                init.CommandText = "CREATE TABLE t (id INTEGER, content TEXT); INSERT INTO t VALUES (1, 'SECRET_LITERAL');";
                init.ExecuteNonQuery();
            }

            var output = CaptureStderr(() =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, content FROM t";
                using (var reader = cmd.ExecuteTrackedReader())
                {
                    while (reader.TrackedRead()) { }
                }
                DbDebug.DumpToStderr(new InvalidOperationException("boom"));
            });

            Assert.Contains("CDIDX_DEBUG=unsafe was ignored", output);
            Assert.Contains("--debug-unsafe", output);
            Assert.Contains("Mode: redacted", output);
            // Raw text must not leak when only the env var is set.
            Assert.DoesNotContain("SECRET_LITERAL", output);
        }
        finally
        {
            DbDebug.ResetContext();
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void DumpToStderr_UnsafeDowngradeWarning_EmittedOnlyOnce()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "unsafe");
        DbDebug.ResetForTesting();
        try
        {
            DbDebug.ResetContext();
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var init = conn.CreateCommand())
            {
                init.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);";
                init.ExecuteNonQuery();
            }

            string Run()
            {
                return CaptureStderr(() =>
                {
                    DbDebug.ResetContext();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT id FROM t";
                    using (var reader = cmd.ExecuteTrackedReader())
                    {
                        while (reader.TrackedRead()) { }
                    }
                    DbDebug.DumpToStderr(new InvalidOperationException("boom"));
                });
            }

            var first = Run();
            var second = Run();

            Assert.Contains("CDIDX_DEBUG=unsafe was ignored", first);
            Assert.DoesNotContain("CDIDX_DEBUG=unsafe was ignored", second);
        }
        finally
        {
            DbDebug.ResetContext();
            DbDebug.ResetForTesting();
        }
    }

    [Fact]
    public void DumpToStderr_DoesNotDumpStaleStateAfterReset()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_DEBUG");
        env.Set("CDIDX_DEBUG", "1");
        try
        {
            DbDebug.ResetContext();
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using (var init = conn.CreateCommand())
            {
                init.CommandText = "CREATE TABLE prev (id INTEGER); INSERT INTO prev VALUES (42);";
                init.ExecuteNonQuery();
            }
            // Request A: populate tracked state / リクエスト A で状態を埋める
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM prev";
                using var reader = cmd.ExecuteTrackedReader();
                while (reader.TrackedRead()) { _ = reader.GetInt32(0); }
            }

            // Request boundary / リクエスト境界
            DbDebug.ResetContext();

            // Request B: unrelated non-reader exception / リクエスト B で無関係な例外
            var output = CaptureStderr(() => DbDebug.DumpToStderr(new IOException("disk unplugged")));
            Assert.Empty(output);
        }
        finally
        {
            DbDebug.ResetContext();
        }
    }
}
