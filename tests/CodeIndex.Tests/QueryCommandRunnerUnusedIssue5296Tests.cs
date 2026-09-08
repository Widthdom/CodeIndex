using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerUnusedIssue5296Tests(ITestOutputHelper output)
{
    [Fact]
    public void CliBudgetAndEnvelope_DoNotRestartIncompleteAnalysisOrHideItsState()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unused_envelope_5296");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        foreach (var flags in new string[][] { ["--analysis-timeout-ms", "10000"], ["--analysis-timeout-ms=10000"] })
        {
            var (code, _, error) = CaptureConsole(() => QueryCommandRunner.RunUnused(["--db", dbPath, "--json", .. flags], JsonOptions));
            Assert.True(code != CommandExitCodes.UsageError, error);
        }
        foreach (var flags in new string[][] { ["--analysis-timeout-ms"], ["--analysis-timeout-ms=0"],
                     ["--analysis-timeout-ms=600001"], ["--analysis-timeout-ms=-1"], ["--analysis-timeout-ms=nan"] })
        {
            var (code, _, _) = CaptureConsole(() => QueryCommandRunner.RunUnused(["--db", dbPath, "--json", .. flags], JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, code);
        }
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("unused"), flag => flag.Name == "--analysis-timeout-ms");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("unused"), flag => flag.Name == "--progress");
        Assert.DoesNotContain(CliFlagSchema.GetCompletionFlagsForCommand("symbols"), flag => flag.Name == "--analysis-timeout-ms");
        var calls = 0;
        using var envelopeHeartbeat = new ManualResetEventSlim();
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
        {
            var previousError = Console.Error;
            Console.SetError(new HeartbeatWriter(previousError, envelopeHeartbeat));
            try
            {
                return JsonEnvelopeWrapper.RunWrapped("unused",
                    ["--db", dbPath, "--json", "--max-json-bytes", "2000"], "test", JsonOptions, _ =>
                    {
                        calls++;
                        using var progress = new ConsoleUi.UnusedProgress(Console.Error);
                        Assert.True(envelopeHeartbeat.Wait(TimeSpan.FromSeconds(10)));
                        progress.Finish("time_budget_exceeded");
                        Console.WriteLine("{\"analysis_complete\":false,\"analysis_state\":\"time_budget_exceeded\",\"total_count_authoritative\":false,\"results\":[]}");
                        return 11;
                    });
            }
            finally { Console.SetError(previousError); }
        });
        Assert.Equal(11, exitCode);
        Assert.Equal(1, calls);
        Assert.Contains("unused: time_budget_exceeded", stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("metadata").GetProperty("analysis_complete").GetBoolean());
        Assert.False(json.RootElement.GetProperty("metadata").GetProperty("total_count_authoritative").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.True(Encoding.UTF8.GetByteCount(stdout) <= 2000);
    }

    [Fact]
    public void ManyPartialDeclarations_ReconstructOncePerStatementAndRemainReadOnly()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unused_5296");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        const int parts = 16;
        const int members = 12;
        for (var part = 0; part < parts; part++)
        {
            var declarations = string.Join('\n', Enumerable.Range(0, members)
                .Select(i => $"    private int Value{part}_{i} = 1;"));
            var uses = string.Join(" + ", Enumerable.Range(0, members).Select(i => $"Value{(part + 1) % parts}_{i}"));
            TestProjectHelper.InsertIndexedFile(dbPath, $"src/Part{part:D2}.cs", "csharp", $$"""
                namespace Demo;
                partial class Host
                {
                {{declarations}}
                    public int Read{{part}}() => {{uses}};
                }
                """);
        }
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM symbol_references; ANALYZE; DELETE FROM sqlite_stat1; ANALYZE sqlite_schema;";
        cmd.ExecuteNonQuery();
        new DbWriter(db.Connection).MarkGraphReady();
        cmd.CommandText = "SELECT total_changes()";
        var changes = cmd.ExecuteScalar();
        cmd.CommandText = "PRAGMA query_only = ON";
        cmd.ExecuteNonQuery();
        var reconstructions = 0;
        db.Connection.CreateFunction("csharp_text_in_line_range",
            (string? text, long? start, long? first, long? last) =>
            {
                reconstructions++;
                return DbContext.GetTextInLineRange(text, start, first, last);
            });
        long vmCallbacks = 0;
        SQLitePCL.delegate_progress progress = _ => { vmCallbacks++; return 0; };
        using var reader = new DbReader(db);
        SQLitePCL.raw.sqlite3_progress_handler(db.Connection.Handle, 1000, progress, null!);
        var stopwatch = Stopwatch.StartNew();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        DbDebug.BeginProfile();
        try
        {
            Assert.Empty(reader.GetUnusedSymbols(10, "field", "csharp", null, null, false));
        }
        finally
        {
            var profiles = DbDebug.EndProfile();
            foreach (var profile in profiles.Where(profile => profile.Sql.Contains("partial_peer", StringComparison.Ordinal)))
                output.WriteLine(string.Join('\n', profile.QueryPlan.Select(row => row.Detail)));
            SQLitePCL.raw.sqlite3_progress_handler(db.Connection.Handle, 0, null!, null!);
            GC.KeepAlive(progress);
        }
        output.WriteLine($"parts={parts}, members={parts * members}, elapsed_ms={stopwatch.ElapsedMilliseconds}, cpu_ms={(Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds}, reconstructions={reconstructions}, vm_steps_upper_bound={(vmCallbacks + 1) * 1000}");
        Assert.InRange(reconstructions, 1, parts * 4);
        Assert.InRange(vmCallbacks, 1, 2000);
        cmd.CommandText = "SELECT total_changes()";
        Assert.Equal(changes, cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA query_only";
        Assert.Equal(1L, cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA query_only = OFF";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "UPDATE chunks SET content = replace(content, 'Value0_0', '/*Value0_0*/0') WHERE file_id = (SELECT id FROM files WHERE path = 'src/Part15.cs')";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA query_only = ON";
        cmd.ExecuteNonQuery();
        var refreshed = reader.GetUnusedSymbols(10, "field", "csharp", ["src/Part00.cs"], null, false);
        Assert.Equal("Value0_0", Assert.Single(refreshed).Name);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeSqliteWork_StopsWithTruthfulJsonAndLiveProgress(bool callerCancellation, bool userDefinedFunction)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unused_cancel_5296");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var reader = new DbReader(db);
        using var cancellation = new CancellationTokenSource();
        using var heartbeat = new ManualResetEventSlim();
        var enteredSqlite = false;
        var options = new QueryCommandOptions { Json = true, Progress = true, MaxJsonBytes = 2000 };
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
        {
            var previousError = Console.Error;
            Console.SetError(new HeartbeatWriter(previousError, heartbeat));
            try
            {
                return QueryCommandRunner.RunBoundedUnusedAnalysis(reader, options, JsonOptions,
                    callerCancellation ? 10000 : 50, cancellation.Token, () =>
                    {
                        SQLitePCL.delegate_progress progress = _ =>
                        {
                            if (userDefinedFunction)
                                return 0;
                            enteredSqlite = true;
                            if (callerCancellation)
                            {
                                Assert.True(heartbeat.Wait(TimeSpan.FromSeconds(10)));
                                cancellation.Cancel();
                            }
                            else
                                Assert.True(reader.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                            return 0;
                        };
                        SQLitePCL.raw.sqlite3_progress_handler(db.Connection.Handle, 100, progress, null!);
                        try
                        {
                            if (userDefinedFunction)
                            {
                                db.Connection.CreateFunction<int>("cancel_unused_5296", () =>
                                {
                                    enteredSqlite = true;
                                    Assert.True(heartbeat.Wait(TimeSpan.FromSeconds(10)));
                                    cancellation.Cancel();
                                    throw new OperationCanceledException(cancellation.Token);
                                });
                            }
                            using var command = db.Connection.CreateCommand();
                            command.CommandText = userDefinedFunction
                                ? "SELECT cancel_unused_5296()"
                                : "WITH RECURSIVE n(v) AS (VALUES(0) UNION ALL SELECT v+1 FROM n WHERE v<1000000000) SELECT sum(v) FROM n";
                            command.ExecuteScalar();
                            return 0;
                        }
                        finally
                        {
                            SQLitePCL.raw.sqlite3_progress_handler(db.Connection.Handle, 0, null!, null!);
                            GC.KeepAlive(progress);
                        }
                    });
            }
            finally { Console.SetError(previousError); }
        });
        Assert.True(enteredSqlite);
        Assert.Equal(callerCancellation ? CommandExitCodes.CancelledBySignal : 11, exitCode);
        using var json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("analysis_complete").GetBoolean());
        Assert.False(json.RootElement.GetProperty("total_count_authoritative").GetBoolean());
        Assert.Equal(callerCancellation ? "cancelled" : "time_budget_exceeded", json.RootElement.GetProperty("analysis_state").GetString());
        Assert.Empty(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.True(Encoding.UTF8.GetByteCount(stdout) <= 2000);
        Assert.Contains("unused: running", stderr);
        Assert.DoesNotContain(project.Root, stderr);
        Assert.False(reader.Cancellation.IsCancellationRequested);
        using var verify = db.Connection.CreateCommand();
        verify.CommandText = "SELECT 1";
        Assert.Equal(1L, verify.ExecuteScalar());
    }

    private sealed class HeartbeatWriter(TextWriter inner, ManualResetEventSlim heartbeat) : TextWriter
    {
        private int _running;
        public override Encoding Encoding => inner.Encoding;
        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            if (value?.Contains("running", StringComparison.Ordinal) == true && ++_running >= 2)
                heartbeat.Set();
        }
        public override void Flush() => inner.Flush();
    }
}
