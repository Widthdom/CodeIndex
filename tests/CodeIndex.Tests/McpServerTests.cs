using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for McpServer JSON-RPC message handling.
/// McpServerのJSON-RPCメッセージ処理のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public partial class McpServerTests : IDisposable
{
    [Fact]
    public void FetchLimitForEnvelope_ClampsHugeInternalLimit_Issue3964()
    {
        Assert.Equal(1, McpServer.FetchLimitForEnvelopeForTests(0));
        Assert.Equal(McpServer.MaxMcpEnvelopeFetchLimit, McpServer.FetchLimitForEnvelopeForTests(int.MaxValue));
    }

    private static readonly Dictionary<short, OpCode> SingleByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 1)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    private static readonly Dictionary<short, OpCode> MultiByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 2)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => (short)(opCode.Value & 0xff));

    private readonly string _dbPath;
    private readonly string _projectRoot;
    private readonly DbContext _db;
    private readonly McpServer _server;

    public McpServerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_test_{Guid.NewGuid():N}.db");
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_workspace");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();

        // Seed test data / テストデータを投入
        var writer = new DbWriter(_db.Connection);
        writer.SetMeta(DbContext.IndexedProjectRootMetaKey, _projectRoot);
        // Stamp graph + issues ready so reads trust the seeded references like a completed index run.
        // seed したデータを完了 index と同等に扱うため readiness を stamp しておく。
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
        writer.MarkCSharpSymbolNameContractReady();
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/app.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
            Checksum = "abc123",
        });
        writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 10,
            Content = "public class App { public void Run() { } }",
        }]);
        writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "class",
            Name = "App",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = "public class App { public void Run() { } }",
        },
        new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "Run",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = "public void Run() { }",
            ContainerKind = "class",
            ContainerName = "App",
            ContainerQualifiedName = "App",
        }]);

        _server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
    }


    [Fact]
    public void RunConcurrentFrameLoop_DoesNotUseSpinWaitPolling_Issue3509()
    {
        var method = typeof(McpServer).GetMethod("RunConcurrentFrameLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.False(MethodCallsType(method!, typeof(SpinWait)));
    }

    [Fact]
    public void PruneCompletedRequestTasks_RemovesCompletedTasks_Issue3837()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task>
        {
            Task.CompletedTask,
            pending.Task,
            Task.FromCanceled(cts.Token),
        };

        var removed = McpServer.PruneCompletedRequestTasks(tasks);

        Assert.Equal(2, removed);
        Assert.Same(pending.Task, Assert.Single(tasks));
    }

    [Fact]
    public void PruneCompletedRequestTasks_ObservesFaultedTasks_Issue3837()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = Task.FromException(new InvalidOperationException("boom"));
        var tasks = new List<Task> { pending.Task, faulted };
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        int removed;
        try
        {
            removed = McpServer.PruneCompletedRequestTasks(tasks);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(1, removed);
        Assert.Same(pending.Task, Assert.Single(tasks));
        Assert.Contains("In-flight request ended before EOF drain (InvalidOperationException)", stderr.ToString(), StringComparison.Ordinal);
    }

    private static bool MethodCallsType(MethodInfo method, Type declaringType)
    {
        var body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
            return false;

        var module = method.Module;
        for (var i = 0; i < body.Length;)
        {
            OpCode opCode;
            var value = body[i++];
            if (value == 0xfe)
            {
                if (i >= body.Length)
                    break;
                opCode = MultiByteOpCodes[(short)body[i++]];
            }
            else
            {
                opCode = SingleByteOpCodes[(short)value];
            }

            var operandStart = i;
            i += OperandSize(opCode.OperandType, body, i);
            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                && operandStart + 4 <= body.Length)
            {
                var token = BitConverter.ToInt32(body, operandStart);
                var member = module.ResolveMember(token);
                if (member?.DeclaringType == declaringType)
                    return true;
            }
        }

        return false;
    }

    private static int OperandSize(OperandType operandType, byte[] body, int offset) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (offset + 4 <= body.Length ? BitConverter.ToInt32(body, offset) * 4 : 0),
        _ => 0,
    };

    [Fact]
    public void ProcessFrame_UsesTraceParentFromMetaAsActivityParent()
    {
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{parentTraceId}-{parentSpanId}-01";
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CodeIndex.CodeIndexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["traceparent"] = traceParent,
                },
            },
        };

        var response = _server.ProcessFrame(request.ToJsonString());

        Assert.NotNull(response);
        var activity = Assert.Single(stopped.Where(activity => activity.OperationName == "mcp.request"));
        Assert.Equal(parentTraceId, activity.TraceId);
        Assert.Equal(parentSpanId, activity.ParentSpanId);
        Assert.Equal("tools/list", activity.GetTagItem("rpc.method"));
    }

    [Fact]
    public void ProcessFrame_IgnoresNonStringTraceParent()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["traceparent"] = 42,
                },
            },
        };

        var response = _server.ProcessFrame(request.ToJsonString());

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ProcessFrameAsync_MatchesSyncCompatibilityWrapper_Issue3770()
    {
        const string frame = """{"jsonrpc":"2.0","id":3770,"method":"tools/list"}""";

        var syncResponse = _server.ProcessFrame(frame);
        var asyncResponse = await _server.ProcessFrameAsync(frame);

        Assert.NotNull(syncResponse);
        Assert.NotNull(asyncResponse);
        using var syncDocument = JsonDocument.Parse(syncResponse);
        using var asyncDocument = JsonDocument.Parse(asyncResponse);
        Assert.Equal(
            syncDocument.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength(),
            asyncDocument.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task ProcessFrameAsync_TimedOutIsolatedActionReportsAndCleansUpDrain_Issue3722()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion())
        {
            RequestTimeout = TimeSpan.FromMilliseconds(20),
        };
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestDelayForTests = _ => blocker.Task;

        var response = await server.ProcessFrameAsync("""{"jsonrpc":"2.0","id":3722,"method":"ping"}""")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(response);
        using (var document = JsonDocument.Parse(response))
        {
            var error = document.RootElement.GetProperty("error");
            Assert.Equal("Request timed out", error.GetProperty("message").GetString());
            var data = error.GetProperty("data");
            Assert.Equal(OperationTimeoutCategories.McpRequest, data.GetProperty("timeout_category").GetString());
            Assert.True(data.GetProperty("isolated_action_draining").GetBoolean());
        }
        var draining = server.BuildRequestTimeoutDiagnosticsStatus();
        Assert.Equal(1, draining["isolated_action_draining_count"]!.GetValue<long>());
        Assert.Equal("draining", draining["last"]!["state"]!.GetValue<string>());

        blocker.SetResult();

        await WaitUntilAsync(
            () => server.BuildRequestTimeoutDiagnosticsStatus()["isolated_action_draining_count"]!.GetValue<long>() == 0,
            "timed-out isolated action to drain and unregister");
        var drained = server.BuildRequestTimeoutDiagnosticsStatus();
        Assert.Equal(1, drained["isolated_action_drained_count"]!.GetValue<long>());
        Assert.Equal("completed", drained["last"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task DrainInFlightTasksAsync_CancelsShutdownAfterBoundedDrainWindow_Issue3774()
    {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task> { stuck.Task };

        await _server.DrainInFlightTasksAsync(
            tasks,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        Assert.True(_server.ShutdownRequestedForTests);
        stuck.SetResult();
        await stuck.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }

    private void InsertIndexedFile(string path, string lang, string content, bool generated = false)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = normalized.Length,
            Lines = lines.Length,
            Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
            Checksum = Guid.NewGuid().ToString("N"),
            Generated = generated,
        });
        writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = lines.Length,
            Content = normalized,
        }]);

        var symbols = SymbolExtractor.Extract(fileId, lang, normalized);
        writer.InsertSymbols(symbols);
        writer.InsertReferences(ReferenceExtractor.Extract(fileId, lang, normalized, symbols));
    }

    private static JsonNode CallIndex(McpServer server, string path, Action<JsonObject>? configure = null)
    {
        var arguments = new JsonObject
        {
            ["path"] = path,
        };
        configure?.Invoke(arguments);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = arguments,
            },
        };

        return server.HandleMessage(request)!;
    }

    private static string BuildDenseReferenceCSharpSource(int callCount)
    {
        var calls = string.Join('\n', Enumerable.Range(0, callCount).Select(static _ => "            Target.Ping();"));
        return $$"""
namespace DenseReferences;

public static class Target
{
    public static void Ping()
    {
    }
}

public sealed class Caller
{
    public void Run()
    {
{{calls}}
    }
}
""";
    }

    private static Dictionary<string, int> ReadSymbolKindCounts(string dbPath)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, COUNT(*) FROM symbols GROUP BY kind";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private void MarkFoldReady()
    {
        var writer = new DbWriter(_db.Connection);
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
    }

    private void InsertValidationIssues(params FileIssue[] issues)
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/app.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
            Checksum = "issues",
        });
        writer.InsertIssues(fileId, issues);
    }
















    // --- Protocol tests / プロトコルテスト ---

    [Fact]
    public async Task StdioTransport_WriteFrameAsync_UsesLfTerminatorEvenWhenHostNewLineDiffers()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var transport = new StdioMcpTransport(input, output, bufferSize: 1024);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", CancellationToken.None);

        var bytes = output.ToArray();
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public async Task ProcessLineAsync_UsesLfTerminatorEvenWhenWriterNewLineIsCrLf()
    {
        using var writer = new StringWriter { NewLine = "\r\n" };

        await _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", writer);

        var response = writer.ToString();
        Assert.EndsWith("\n", response, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", response);
    }

    [Fact]
    public async Task ProcessLineAsync_PingReturnsStructuredHealth()
    {
        using var writer = new StringWriter();

        await _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", writer);

        using var document = JsonDocument.Parse(writer.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("uptime_s").GetInt64() >= 0);
        Assert.True(result.GetProperty("db_open").GetBoolean());
        Assert.True(result.GetProperty("transport_ready").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(result.GetProperty("last_request_at").GetString(), out _));
        Assert.True(DateTimeOffset.TryParse(result.GetProperty("last_db_check_at").GetString(), out _));
    }

    [Fact]
    public async Task ProcessLineAsync_ToolCallEmitsInvocationTelemetry()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":123,"method":"tools/call","params":{"name":"ping","arguments":{}}}""", writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal));
        Assert.Contains("[rid=123 cid=", line);
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal("mcp.tool.invocation", root.GetProperty("event").GetString());
        Assert.Equal("ping", root.GetProperty("tool").GetString());
        Assert.Equal("123", root.GetProperty("request_id").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("correlation_id", out var correlationId));
        Assert.False(string.IsNullOrWhiteSpace(correlationId.GetString()));
    }

    [Fact]
    public async Task ProcessLineAsync_UnknownToolName_TruncatesTelemetry_Issue3118()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var toolName = new string('l', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = new JsonObject
                {
                    ["x"] = 1,
                },
            },
        };

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync(request.ToJsonString(), writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        Assert.DoesNotContain(toolName, writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(toolName, error.ToString(), StringComparison.Ordinal);
        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal));
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal("mcp.tool.invocation", root.GetProperty("event").GetString());
        Assert.Equal(display.Text, root.GetProperty("tool").GetString());
        Assert.Equal(toolName.Length, root.GetProperty("tool_length").GetInt32());
        Assert.True(root.GetProperty("tool_truncated").GetBoolean());
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(-32602, root.GetProperty("error_code").GetInt32());
    }

    [Fact]
    public async Task ProcessLineAsync_UnknownArgumentName_TruncatesTelemetryKeyMetadata_Issue3117_Issue3105()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var argumentName = new string('k', AuditLogSink.MaxAuditArgumentKeyChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName, AuditLogSink.MaxAuditArgumentKeyChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = new JsonObject
                {
                    ["path"] = ".",
                    [argumentName] = true,
                },
            },
        };

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync(request.ToJsonString(), writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        Assert.DoesNotContain(argumentName, writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(argumentName, error.ToString(), StringComparison.Ordinal);
        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal));
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal("mcp.tool.invocation", root.GetProperty("event").GetString());
        Assert.Contains(root.GetProperty("arg_keys").EnumerateArray(), key => key.GetString() == display.Text);
        Assert.Equal(argumentName.Length, root.GetProperty("arg_key_lengths").GetProperty(display.Text).GetInt32());
        Assert.True(root.GetProperty("arg_keys_truncated").GetBoolean());
        Assert.Equal(1, root.GetProperty("arg_key_names_truncated_count").GetInt32());
    }

    [Fact]
    public async Task ProcessLineAsync_CapsTelemetryArgumentKeyCount_Issue3237_Issue3105()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();
        const int requestId = 3325;
        var arguments = new JsonObject();
        for (var i = 0; i < AuditLogSink.MaxAuditArgumentCount + 3; i++)
            arguments[$"arg{i}"] = i;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "does_not_exist",
                ["arguments"] = arguments,
            },
        };

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync(request.ToJsonString(), writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal)
                && l.Contains($"\"request_id\":\"{requestId}\"", StringComparison.Ordinal));
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal(AuditLogSink.MaxAuditArgumentCount, root.GetProperty("arg_keys").GetArrayLength());
        Assert.True(root.GetProperty("arg_keys_truncated").GetBoolean());
        Assert.Contains(root.GetProperty("arg_key_truncation_reasons").EnumerateArray(),
            reason => reason.GetString() == "arg_key_count_limit");
        Assert.Equal(3, root.GetProperty("arg_keys_omitted_count").GetInt32());
        Assert.DoesNotContain(root.GetProperty("arg_keys").EnumerateArray(),
            key => key.GetString() == $"arg{AuditLogSink.MaxAuditArgumentCount}");
    }

    [Fact]
    public async Task ProcessLineAsync_FallbackErrorIncludesCorrelationData()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":321,"method":"tools/call","params":{"name":42,"arguments":{}}}""", writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        var response = JsonNode.Parse(writer.ToString())!;
        var data = response["error"]!["data"]!;
        Assert.Equal("321", data["request_id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(data["correlation_id"]!.GetValue<string>()));
        Assert.Contains("[cdidx-mcp] [rid=321 cid=", error.ToString());
    }


    [Fact]
    public async Task RunAsync_InitializeEmitsInitializedNotificationAfterResponseOnlyOnce()
    {
        var transport = new QueueMcpTransport(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"test-client","version":"1.0"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"test-client","version":"1.0"}}}""");

        await _server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(3, transport.WrittenFrames.Count);
        Assert.Equal(1, JsonNode.Parse(transport.WrittenFrames[0])!["id"]!.GetValue<int>());
        Assert.Equal("notifications/initialized", JsonNode.Parse(transport.WrittenFrames[1])!["method"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(transport.WrittenFrames[2])!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_StartupLogSanitizesDbPathByDefault_Issue1469()
    {
        using var error = new StringWriter();
        var previousDebug = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            await Task.Run(() =>
            {
                lock (TestConsoleLock.Gate)
                {
                    var previousError = Console.Error;
                    try
                    {
                        Console.SetError(error);
#pragma warning disable xUnit1031
                        _server.RunAsync(new QueueMcpTransport(), CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                    }
                    finally
                    {
                        Console.SetError(previousError);
                    }
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previousDebug);
        }

        var log = error.ToString();
        Assert.Contains("db: " + Path.GetFileName(_dbPath), log);
        Assert.DoesNotContain(_dbPath, log);
    }




    [Fact]
    public void ErrorEnvelope_ClonesExtraDataWithoutSerializeParseRoundTrip_Issue3055()
    {
        var details = new JsonObject
        {
            ["ok"] = true,
        };
        var extra = new JsonObject
        {
            ["details"] = details,
            ["category"] = "shadow",
        };

        var data = McpErrorEnvelope.BuildData(
            "custom_category",
            "custom suggestion",
            retrySafe: true,
            extra);
        details["ok"] = false;

        Assert.Equal("custom_category", data["category"]!.GetValue<string>());
        Assert.True(data["details"]!["ok"]!.GetValue<bool>());
    }


    [Fact]
    public void RefreshClientRoots_CapsSessionStatusDiagnostics_Issue3076()
    {
        var longRoot = "file:///" + new string('r', McpServer.MaxClientRootUriChars + 50);
        var advertisedRoots = new JsonArray();
        for (var i = 0; i < McpServer.MaxClientRootCount + 3; i++)
        {
            advertisedRoots.Add(new JsonObject
            {
                ["uri"] = i == 0 ? longRoot : $"file:///tmp/cdidx-not-this-workspace/{i}",
            });
        }

        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("roots/list", method);
            return new JsonObject { ["roots"] = advertisedRoots.DeepClone() };
        };

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;
        var indexResponse = _server.HandleMessage(request)!;

        Assert.True(indexResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(McpServer.MaxClientRootCount + 3, _server.ClientRootsForTests.Length);
        Assert.Contains(longRoot, _server.ClientRootsForTests);

        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;
        var session = response["result"]!["structuredContent"]!["mcp_session"]!;

        Assert.True(session["roots_truncated"]!.GetValue<bool>());
        Assert.Equal(McpServer.MaxClientRootCount + 3, session["root_count"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxClientRootCount, session["roots"]!.AsArray().Count);
        Assert.DoesNotContain(longRoot, response.ToJsonString(), StringComparison.Ordinal);
    }




    [Fact]
    public void StatusAndPing_ReportAuditLogDiagnostics_Issue3547()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_audit_diag_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var sink = new AuditLogSink(auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
            sink.RecordRotationFailure("rotation_failure", new IOException("rotation failed"));
            using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, sink);

            var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var statusResponse = server.HandleMessage(status)!;
            var statusAudit = statusResponse["result"]!["structuredContent"]!["mcp_session"]!["audit_log"]!;

            Assert.True(statusAudit["enabled"]!.GetValue<bool>());
            Assert.Equal(auditPath, statusAudit["path"]!.GetValue<string>());
            Assert.False(statusAudit["include_values"]!.GetValue<bool>());
            Assert.True(statusAudit["rotation_degraded"]!.GetValue<bool>());
            Assert.Equal(1, statusAudit["rotation_failure_count"]!.GetValue<long>());
            Assert.Equal(0, statusAudit["dropped_record_count"]!.GetValue<long>());
            Assert.Equal("rotation_failure:io_error:IOException", statusAudit["last_rotation_failure"]!.GetValue<string>());

            var ping = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"ping"}""")!;
            var pingResponse = server.HandleMessage(ping)!;
            var health = pingResponse["result"]!;
            var healthAudit = health["audit_log"]!;

            Assert.Equal("degraded", health["status"]!.GetValue<string>());
            Assert.True(healthAudit["rotation_degraded"]!.GetValue<bool>());
            Assert.Equal(1, healthAudit["rotation_failure_count"]!.GetValue<long>());
        }
        finally
        {
            if (File.Exists(auditPath))
                File.Delete(auditPath);
        }
    }



    [Fact]
    public void UnknownNotification_ReturnsNoResponseAndLogsWarning()
    {
        using var writer = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            var previous = Console.Error;
            try
            {
                Console.SetError(writer);
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initalized"}""")!;

                var response = _server.HandleMessage(request);

                Assert.Null(response);
            }
            finally
            {
                Console.SetError(previous);
            }
        }

        Assert.Contains("Ignoring unknown notification", writer.ToString());
        Assert.Contains("notifications/initalized", writer.ToString());
    }

    [Fact]
    public void ResourcesList_ReturnsIndexedFilesAsResources()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        var resource = response["result"]!["resources"]!.AsArray()
            .Single(r => r!["name"]!.GetValue<string>() == "src/app.cs")!;
        Assert.Equal("cdidx://file/src/app.cs", resource["uri"]!.GetValue<string>());
        Assert.Equal("text/x-csharp", resource["mimeType"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-cursor")]
    public void ResourcesList_InvalidCursor_ReturnsInvalidParams_Issue3112(string cursor)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/list",
            ["params"] = new JsonObject
            {
                ["cursor"] = cursor,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        var data = response["error"]!["data"]!;
        Assert.Equal("invalid_argument", data["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, data["max_pagination_offset"]!.GetValue<int>());
    }

    [Fact]
    public void ResourcesList_CursorBeyondPaginationCap_ReturnsInvalidParams_Issue3112()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/list",
            ["params"] = new JsonObject
            {
                ["cursor"] = (McpServer.MaxMcpPaginationOffset + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        var data = response["error"]!["data"]!;
        Assert.Equal("invalid_argument", data["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, data["max_pagination_offset"]!.GetValue<int>());
    }

    [Fact]
    public void ResourcesList_AtPaginationCap_DoesNotEmitSelfInvalidNextCursor_Issue3112()
    {
        var writer = new DbWriter(_db.Connection);
        using var transaction = writer.BeginTransaction();
        for (var i = 0; i < McpServer.MaxMcpPaginationOffset + 200; i++)
        {
            writer.UpsertFile(new FileRecord
            {
                Path = $"zz/paged-{i:D5}.cs",
                Lang = "csharp",
                Size = 1,
                Lines = 1,
                Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
                Checksum = $"bulk-{i}",
            });
        }
        transaction.Commit();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/list",
            ["params"] = new JsonObject
            {
                ["cursor"] = McpServer.MaxMcpPaginationOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]!["nextCursor"]);
    }

    [Fact]
    public void ResourcesList_CursorReturnsDeepPage_Issue3781()
    {
        var writer = new DbWriter(_db.Connection);
        using var transaction = writer.BeginTransaction();
        for (var i = 0; i < 450; i++)
        {
            writer.UpsertFile(new FileRecord
            {
                Path = $"zz/deep-{i:D5}.cs",
                Lang = "csharp",
                Size = 1,
                Lines = 1,
                Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
                Checksum = $"deep-{i}",
            });
        }
        transaction.Commit();
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/list",
            ["params"] = new JsonObject
            {
                ["cursor"] = "200",
            },
        };

        var response = _server.HandleMessage(request)!;

        var resources = response["result"]!["resources"]!.AsArray();
        Assert.Equal("400", response["result"]!["nextCursor"]!.GetValue<string>());
        Assert.DoesNotContain(resources, resource => resource!["name"]!.GetValue<string>() == "zz/deep-00000.cs");
        Assert.Contains(resources, resource => resource!["name"]!.GetValue<string>() == "zz/deep-00199.cs");
    }

    [Fact]
    public void ResourcesList_DoesNotAdvertiseUrisTooLongToRead_Issue3122()
    {
        var longPath = "src/" + new string('x', McpBoundedText.MaxResourceUriChars) + ".cs";
        InsertIndexedFile(longPath, "csharp", "public class TooLongResource { }");
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{}}""")!;

        var response = _server.HandleMessage(request)!;

        var resources = response["result"]!["resources"]!.AsArray();
        Assert.DoesNotContain(resources, resource => resource!["name"]!.GetValue<string>() == longPath);
        Assert.All(resources, resource =>
            Assert.True(resource!["uri"]!.GetValue<string>().Length <= McpBoundedText.MaxResourceUriChars));
    }

    [Fact]
    public void ResourcesRead_ReturnsIndexedFileContent()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"cdidx://file/src/app.cs"}}""")!;
        var response = _server.HandleMessage(request)!;

        var content = response["result"]!["contents"]!.AsArray().Single()!;
        Assert.Equal("cdidx://file/src/app.cs", content["uri"]!.GetValue<string>());
        Assert.Equal("text/x-csharp", content["mimeType"]!.GetValue<string>());
        Assert.Contains("public class App", content["text"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_DecodesSpaceOnce_Issue3789()
    {
        InsertIndexedFile("src/space file.cs", "csharp", "public class SpaceFile { }");
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"cdidx://file/src/space%20file.cs"}}""")!;

        var response = _server.HandleMessage(request)!;

        var content = response["result"]!["contents"]!.AsArray().Single()!;
        Assert.Equal("cdidx://file/src/space%20file.cs", content["uri"]!.GetValue<string>());
        Assert.Contains("SpaceFile", content["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("cdidx://file/src%2fapp.cs")]
    [InlineData("cdidx://file/src%5capp.cs")]
    [InlineData("cdidx://file/src/%2e%2e/app.cs")]
    public void ResourcesRead_RejectsEncodedPathBoundaries_Issue3789(string uri)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/read",
            ["params"] = new JsonObject
            {
                ["uri"] = uri,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", response["error"]!["data"]!["category"]!.GetValue<string>());
        Assert.StartsWith("Invalid resource uri:", response["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResourcesRead_NonStringUri_ReturnsInvalidParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":42}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("missing_parameter", response["error"]!["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_UriTooLong_RejectsBeforeParse_Issue3122()
    {
        var uri = "cdidx://file/" + new string('x', McpBoundedText.MaxResourceUriChars + 25);
        var display = McpBoundedText.ForDisplay(uri, McpBoundedText.MaxResourceUriChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/read",
            ["params"] = new JsonObject
            {
                ["uri"] = uri,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(uri, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Resource uri is too long: {display.Text}", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["uri"]!.GetValue<string>());
        Assert.Equal(uri.Length, data["uri_length"]!.GetValue<int>());
        Assert.True(data["uri_truncated"]!.GetValue<bool>());
        Assert.Equal(McpBoundedText.MaxResourceUriChars, data["max_length"]!.GetValue<int>());
        Assert.Equal(uri.Length, data["actual_length"]!.GetValue<int>());
    }

    [Fact]
    public void ResourcesRead_NotFound_ReturnsBoundedUriData_Issue3122()
    {
        const string uri = "cdidx://file/src/missing.cs";
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"cdidx://file/src/missing.cs"}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal($"Resource not found: {uri}", response["error"]!["message"]!.GetValue<string>());
        Assert.Equal(uri, response["error"]!["data"]!["uri"]!.GetValue<string>());
        Assert.Equal("invalid_argument", response["error"]!["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void PromptsListAndGet_ReturnPromptMessages()
    {
        var list = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"prompts/list","params":{}}""")!;
        var listResponse = _server.HandleMessage(list)!;

        var names = listResponse["result"]!["prompts"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("summarize_file", names);
        Assert.Contains("find_unused", names);
        Assert.Contains("impact_of_changing", names);
        Assert.Contains("investigate_before_edit", names);
        Assert.Contains("find_existing_pattern", names);
        Assert.Contains("safe_symbol_change", names);
        Assert.Contains("debug_failure", names);

        var get = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"prompts/get","params":{"name":"impact_of_changing","arguments":{"symbol":"Run"}}}""")!;
        var getResponse = _server.HandleMessage(get)!;
        var message = getResponse["result"]!["messages"]!.AsArray().Single()!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Contains("impact_analysis", message["content"]!["text"]!.GetValue<string>());
        Assert.Contains("Run", message["content"]!["text"]!.GetValue<string>());

        var investigate = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"prompts/get","params":{"name":"investigate_before_edit","arguments":{"topic":"Run"}}}""")!;
        var investigateResponse = _server.HandleMessage(investigate)!;
        var investigateText = investigateResponse["result"]!["messages"]!.AsArray().Single()!["content"]!["text"]!.GetValue<string>();
        Assert.Contains("search", investigateText);
        Assert.Contains("definition", investigateText);
        Assert.Contains("references", investigateText);
        Assert.Contains("excerpt", investigateText);
    }

    [Fact]
    public void PromptsGet_PromptNameTooLong_TruncatesDiagnostics_Issue3121()
    {
        var name = new string('p', McpBoundedText.MaxPromptNameChars + 25);
        var display = McpBoundedText.ForDisplay(name, McpBoundedText.MaxPromptNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "prompts/get",
            ["params"] = new JsonObject
            {
                ["name"] = name,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(name, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Prompt name is too long: '{display.Text}'", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal("name", data["parameter"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxPromptNameChars, data["max_length"]!.GetValue<int>());
        Assert.Equal(name.Length, data["actual_length"]!.GetValue<int>());
        Assert.Equal(display.Text, data["value"]!.GetValue<string>());
        Assert.Equal(name.Length, data["value_length"]!.GetValue<int>());
        Assert.True(data["value_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void PromptsGet_ArgumentValueTooLong_RejectsBeforePromptInterpolation_Issue3121()
    {
        var path = new string('x', McpBoundedText.MaxPromptArgumentChars + 25);
        var display = McpBoundedText.ForDisplay(path, McpBoundedText.MaxPromptArgumentChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "prompts/get",
            ["params"] = new JsonObject
            {
                ["name"] = "summarize_file",
                ["arguments"] = new JsonObject
                {
                    ["path"] = path,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(path, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Prompt argument 'path' is too long: '{display.Text}'", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal("path", data["parameter"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxPromptArgumentChars, data["max_length"]!.GetValue<int>());
        Assert.Equal(path.Length, data["actual_length"]!.GetValue<int>());
        Assert.Equal(display.Text, data["value"]!.GetValue<string>());
        Assert.Equal(path.Length, data["value_length"]!.GetValue<int>());
        Assert.True(data["value_truncated"]!.GetValue<bool>());
    }





    [Fact]
    public void SupportedProtocolVersions_IsNewestFirstAndIncludesPreferred()
    {
        // The preferred version must be the newest entry; ordering matters for future
        // additions because clients may rely on the listed order as a "newest-first" hint.
        // 既定の優先バージョンは先頭でなければならない。クライアントが「先頭が最新」と
        // して扱う可能性があるため、順序の保証は明示的に必要。
        Assert.NotEmpty(McpServer.SupportedProtocolVersions);
        Assert.Equal("2025-03-26", McpServer.SupportedProtocolVersions[0]);
        Assert.Contains("2024-11-05", McpServer.SupportedProtocolVersions);
    }


    [Fact]
    public void NotificationsCancelled_ForActiveRequestId_ReturnsRequestCancelledError()
    {
        _server.RequestRegisteredForTests = id =>
        {
            var cancel = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1418}}""")!;
            Assert.Null(_server.HandleMessage(cancel));
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, error["code"]!.GetValue<int>());
        Assert.Equal("request_cancelled", error["data"]!["category"]!.GetValue<string>());
    }


    [Fact]
    public void ToolCall_ErrorIncludesCorrelationData()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"search","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("42", structured["request_id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(structured["correlation_id"]!.GetValue<string>()));
    }

    [Fact]
    public void ToolCall_ResponseIncludesCorrelationMeta()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var meta = response["result"]!["_meta"]!;
        Assert.Equal("\"abc\"", meta["request_id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(meta["correlation_id"]!.GetValue<string>()));
    }


    [Theory]
    [InlineData("search")]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("analyze_symbol")]
    [InlineData("impact_analysis")]
    public void ToolCall_RequiredQuery_DistinguishesMissingFromWhitespace(string toolName)
    {
        var missing = CallToolAndReadErrorMessage(toolName, new JsonObject());
        var blank = CallToolAndReadErrorMessage(toolName, new JsonObject { ["query"] = "   " });

        Assert.Equal("Missing required parameter: query", missing);
        Assert.Equal("Parameter \"query\" cannot be empty or whitespace-only", blank);
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("symbols")]
    public void ToolCall_InvalidSince_ReturnsInvalidArgument_Issue3194(string toolName)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = new JsonObject
                {
                    ["query"] = "App",
                    ["since"] = "not-a-timestamp",
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("Invalid 'since' timestamp", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, result["structuredContent"]!["category"]!.GetValue<string>());
    }



    [Theory]
    [InlineData("outline")]
    [InlineData("excerpt")]
    [InlineData("index")]
    public void ToolCall_RequiredPath_DistinguishesMissingFromWhitespace(string toolName)
    {
        var missing = CallToolAndReadErrorMessage(toolName, new JsonObject());
        var blank = CallToolAndReadErrorMessage(toolName, new JsonObject { ["path"] = "   " });

        Assert.Equal("Missing required parameter: path", missing);
        Assert.Equal("Parameter \"path\" cannot be empty or whitespace-only", blank);
    }

    [Theory]
    [InlineData("outline")]
    [InlineData("excerpt")]
    [InlineData("index")]
    public void ToolCall_RequiredPath_RejectsNonStringType_Issue3186(string toolName)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = BuildRequiredPathArguments(toolName, new JsonArray { "src/app.cs" }),
            },
        };

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains("Invalid type for argument 'path'", error["message"]!.GetValue<string>());
        Assert.Equal("path", error["data"]!["parameter"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("outline", "../outside.cs", "`..` path traversal")]
    [InlineData("excerpt", "/tmp/outside.cs", "workspace-relative")]
    [InlineData("index", "TOO_LONG", "must be no longer than")]
    [InlineData("outline", "TOO_LONG", "must be no longer than")]
    public void ToolCall_RequiredPath_RejectsInvalidPathValues_Issue3186(
        string toolName,
        string pathValue,
        string expectedText)
    {
        if (pathValue == "TOO_LONG")
            pathValue = new string('a', QueryCommandRunner.MaxQueryPathFilterLength + 1);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = BuildRequiredPathArguments(toolName, pathValue),
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>(), response.ToJsonString());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(expectedText, text, StringComparison.Ordinal);
        Assert.DoesNotContain("file not found in index", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Directory not found", text, StringComparison.Ordinal);
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, result["structuredContent"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void McpPathBoundary_ResolvesSymlinkTargetsBeforeContainment_Issue3753()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_mcp_boundary_root");
        var outside = TestProjectHelper.CreateTempProject("cdidx_mcp_boundary_outside");
        try
        {
            var link = Path.Combine(root, "link-outside");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.False(McpPathBoundary.IsPathWithinDirectory(root, link));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(outside);
        }
    }


    [Fact]
    public void ToolCall_FindInFilePath_DistinguishesMissingFromWhitespace()
    {
        var missing = CallToolAndReadErrorMessage("find_in_file", new JsonObject { ["query"] = "Run" });
        var blank = CallToolAndReadErrorMessage("find_in_file", new JsonObject
        {
            ["query"] = "Run",
            ["path"] = "   "
        });

        Assert.Equal("Missing required parameter: path", missing);
        Assert.Equal("Parameter \"path\" cannot be empty or whitespace-only", blank);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("description")]
    public void ToolCall_SuggestImprovementRequiredStrings_DistinguishMissingFromWhitespace(string propertyName)
    {
        var baseArguments = new JsonObject
        {
            ["category"] = "unexpected_error",
            ["description"] = "The tool should report this behavior more clearly."
        };
        baseArguments.Remove(propertyName);
        var missing = CallToolAndReadErrorMessage("suggest_improvement", baseArguments);

        var blankArguments = new JsonObject
        {
            ["category"] = "unexpected_error",
            ["description"] = "The tool should report this behavior more clearly.",
            [propertyName] = "   "
        };
        var blank = CallToolAndReadErrorMessage("suggest_improvement", blankArguments);

        Assert.Equal($"Missing required parameter: {propertyName}", missing);
        Assert.Equal($"Parameter \"{propertyName}\" cannot be empty or whitespace-only", blank);
    }



    [Fact]
    public void NegotiateProtocolVersion_TruncatesUnsupportedRequestedVersion_Issue3119()
    {
        var requested = new string('v', McpBoundedText.MaxProtocolVersionChars + 25);
        var display = McpBoundedText.ForDisplay(requested, McpBoundedText.MaxProtocolVersionChars);
        var initializeParams = new JsonObject
        {
            ["protocolVersion"] = requested,
        };

        var negotiated = McpServer.NegotiateProtocolVersion(initializeParams, out var requestedVersion);

        Assert.Null(negotiated);
        Assert.NotNull(requestedVersion);
        Assert.Equal(display.Text, requestedVersion.Value.Text);
        Assert.Equal(requested.Length, requestedVersion.Value.OriginalLength);
        Assert.True(requestedVersion.Value.Truncated);
    }







    [Theory]
    [InlineData("ping", """{}""")]
    [InlineData("status", """{}""")]
    [InlineData("search", """{"query":"Run","limit":5}""")]
    public void ToolCall_ResponseShape_HasStableMcpResultEnvelope(string toolName, string argumentsJson)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = JsonNode.Parse(argumentsJson),
            },
        };
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Null(response["error"]);

        var result = response["result"]!;
        Assert.NotNull(result["content"]);
        Assert.Equal("text", result["content"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.NotNull(result["structuredContent"]);
        Assert.NotNull(result["_meta"]!["request_id"]);
        Assert.NotNull(result["_meta"]!["correlation_id"]);
    }

    [Fact]
    public void ToolCall_TypeMismatch_ReturnsInvalidParams_Issue1417()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","limit":"not-an-int"}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", error["data"]!["category"]!.GetValue<string>());
        Assert.Equal("limit", error["data"]!["parameter"]!.GetValue<string>());
        Assert.Equal("integer", error["data"]!["expected"]!.GetValue<string>());
        Assert.Equal("string", error["data"]!["actual"]!.GetValue<string>());
    }


    // --- Authentication tests (#1559) / 認証テスト (#1559) ---

    [Fact]
    public void DefaultAuthenticator_AllowsRequestsWithoutToken()
    {
        // #1559: the historical stdio default must keep working without an auth token so
        // existing clients (Claude Code, Cursor, Windsurf) don't break when the upgrade
        // ships. The permissive default is wired by the parameterless ctor.
        // #1559: stdio 既定の従来動作はトークン無しで通る必要がある。permissive 既定は
        // 引数なしコンストラクタで wire される。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void TokenAuthenticator_MatchingToken_DispatchesNormally()
    {
        // #1559: when the server is configured with a token, the matching token in
        // params.auth.token authenticates the request and dispatch proceeds.
        // #1559: トークン設定済みサーバーに対し、params.auth.token に一致する値を
        // 添えれば認証成功し dispatch に進む。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"auth":{"token":"s3cret"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void TokenAuthenticator_MissingToken_ReturnsUnauthorized()
    {
        // #1559: a configured server must reject requests with no token. The wire response
        // carries only -32001 + "Unauthorized" (no detail) per the #1530 sanitization rule.
        // #1559: トークン設定済みサーバーはトークン未提示のリクエストを拒否する。ワイヤ応答は
        // #1530 サニタイズ方針に従い -32001 と "Unauthorized" のみ。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32001, error["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_WrongToken_ReturnsUnauthorized()
    {
        // #1559: an incorrect token must produce the same wire response shape as a missing
        // token so callers cannot mount a token-presence oracle on the response body.
        // #1559: 不一致トークンも未提示と同じワイヤ応答にすることで、応答本文を見て
        // トークン有無を判定するオラクル攻撃を防ぐ。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"auth":{"token":"wrong"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32001, error["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_ToolsCallWithToken_Dispatches()
    {
        // #1559: the auth check runs uniformly across initialize/tools/list/tools/call/ping
        // so a tool dispatch with a matching token still reaches the handler and returns the
        // tool result instead of an Unauthorized error.
        // #1559: 認証チェックは initialize/tools/list/tools/call/ping に統一されているため、
        // 一致トークン付きのツール呼び出しはハンドラまで届き Unauthorized ではなくツール結果を返す。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","auth":{"token":"s3cret"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void TokenAuthenticator_NotificationsBypassAuthCheck()
    {
        // Notifications (no id) produce no response so the auth check would have nothing to
        // signal on; the existing notification short-circuit must stay BEFORE the auth gate
        // so a token-protected server still tolerates `notifications/initialized` without
        // synthesising an error response.
        // 通知 (id 無し) は応答が無いので認証チェックがエラーを返す手段を持たない。通知の
        // ショートサーキットを認証ゲートより前に置き続け、token 保護サーバーでも
        // `notifications/initialized` を黙って受け入れられるようにする。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!;

        var response = server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void TokenAuthenticator_MalformedAuthShape_TreatedAsMissing()
    {
        // Defensive: an `auth` object whose `token` field is not a string (number, array,
        // object) must not crash the server. The token-authenticator catches the cast and
        // treats it as a missing token so the wire stays uniform.
        // 防御: `auth.token` が文字列でない（数値・配列・オブジェクト）入力でサーバーが
        // クラッシュしてはならない。token authenticator は cast 失敗を未提示扱いにし、
        // ワイヤ応答を統一する。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"auth":{"token":42}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32001, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", response["error"]!["message"]!.GetValue<string>());
    }


    [Fact]
    public void TokenAuthenticator_EmptyTokenInCtor_Rejected()
    {
        // An empty configured token would make every empty-string presentation succeed, so
        // the constructor must refuse it. RunMcp's factory already pre-filters on
        // whitespace, but the constructor is the public contract.
        // 空文字を期待トークンに設定すると空文字提示が全て通ってしまうため、コンストラクタで
        // 拒否する。RunMcp の factory は空白フィルタを掛けるが、コンストラクタが公開契約。
        Assert.Throws<ArgumentException>(() => new TokenMcpAuthenticator(string.Empty));
    }

    [Fact]
    public void TokenAuthenticator_OversizedTokenInCtor_RejectedBeforeHashing()
    {
        var oversized = new string('x', McpAuthenticationLimits.MaxTokenCharacters + 1);

        Assert.Throws<ArgumentException>(() => new TokenMcpAuthenticator(oversized));
    }

    [Fact]
    public void McpAuthenticationLimits_HashTokenUtf8ForTests_ClearsUsedTokenBytes_Issue3989()
    {
        var buffer = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        var destination = new byte[McpAuthenticationLimits.Sha256HashBytes];

        McpAuthenticationLimits.HashTokenUtf8ForTests("token", buffer, destination);

        Assert.Equal(McpAuthenticationLimits.HashTokenToArray("token"), destination);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, buffer[..5]);
        Assert.All(buffer[5..], value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void McpAuthenticatorFactory_NoEnv_ReturnsLocalStdio()
    {
        // FromEnvironment() must default to permissive stdio when the env var is unset or
        // empty, so unconfigured installs preserve the historical behaviour.
        // 環境変数が未設定 or 空文字の場合は permissive stdio に fallback し、未設定インストールの
        // 従来動作を維持する。
        var previous = Environment.GetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, null);
            Assert.IsType<LocalStdioAuthenticator>(McpAuthenticatorFactory.FromEnvironment());

            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, string.Empty);
            Assert.IsType<LocalStdioAuthenticator>(McpAuthenticatorFactory.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, previous);
        }
    }

    [Fact]
    public void McpAuthenticatorFactory_WhitespaceTokenIsRejected_Issue3505()
    {
        var previous = Environment.GetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, " token");

            var ex = Assert.Throws<FormatException>(McpAuthenticatorFactory.FromEnvironment);
            Assert.Contains(McpAuthenticatorFactory.AuthTokenEnvVar, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, previous);
        }
    }

    [Fact]
    public void McpEnvironment_InvalidTokenDiagnosticDoesNotEchoValue_Issue3676()
    {
        using var env = EnvironmentVariableScope.Capture(McpAuthenticatorFactory.AuthTokenEnvVar);
        const string invalidToken = "super secret token";
        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, invalidToken);

        var ex = Assert.Throws<FormatException>(() => McpEnvironment.GetOptionalToken(McpAuthenticatorFactory.AuthTokenEnvVar));

        Assert.Contains(McpAuthenticatorFactory.AuthTokenEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void McpEnvironment_ReadOptInSwitch_ClassifiesSamplingValues_Issue3676()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");

        env.Set("CDIDX_MCP_SAMPLING", null);
        Assert.Equal(McpEnvironmentSwitchState.Unset, McpEnvironment.ReadOptInSwitch("CDIDX_MCP_SAMPLING").State);

        env.Set("CDIDX_MCP_SAMPLING", " on ");
        Assert.True(McpEnvironment.ReadOptInSwitch("CDIDX_MCP_SAMPLING").IsEnabled);

        env.Set("CDIDX_MCP_SAMPLING", "off");
        Assert.True(McpEnvironment.ReadOptInSwitch("CDIDX_MCP_SAMPLING").IsDisabled);

        env.Set("CDIDX_MCP_SAMPLING", "maybe-with-secret-token");
        Assert.True(McpEnvironment.ReadOptInSwitch("CDIDX_MCP_SAMPLING").IsInvalid);
    }

    [Fact]
    public void McpServer_UnsafeDebugRequiresExactUnsafeValue_Issue3676()
    {
        using var env = EnvironmentVariableScope.Capture(McpServer.DebugEnvironmentVariable);

        env.Set(McpServer.DebugEnvironmentVariable, "unsafe");
        Assert.True(McpServer.IsUnsafeDebugEnabled());

        env.Set(McpServer.DebugEnvironmentVariable, "full");
        Assert.False(McpServer.IsUnsafeDebugEnabled());

        env.Set(McpServer.DebugEnvironmentVariable, "definitely-not-unsafe");
        Assert.False(McpServer.IsUnsafeDebugEnabled());
    }

    [Fact]
    public void TokenAuthenticator_ConfiguredWhitespaceTokenIsRejected_Issue3505()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TokenMcpAuthenticator("token "));
        Assert.Contains("whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenAuthenticator_NonStringMethod_ReturnsUnauthorized()
    {
        // #1559: a non-string `method` (e.g. `42`) must hit the auth gate and produce
        // -32001 "Unauthorized", not a -32603 "Internal error" leaked from a throwing
        // GetValue<string>() call on the way to dispatch. Otherwise the token-protected
        // server would tell unauthenticated callers that their malformed request reached
        // dispatch internals.
        // #1559: 非文字列 method（例: 42）は認証ゲートに到達して -32001 を返すべきで、
        // GetValue<string>() の例外から -32603 を漏らしてはならない。漏らすと token 保護下で
        // 未認証呼び出しに「dispatch 内部まで届いた」事実を伝えてしまう。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":42}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        Assert.Equal(-32001, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_MissingMethodWithToken_ReturnsMethodError()
    {
        // After auth passes, a request that omits `method` entirely still gets the
        // structured -32600 "missing method" error. This documents that the auth gate runs
        // first but does not swallow downstream method-shape validation when the caller is
        // authenticated.
        // 認証が通った後で `method` が欠落しているリクエストには従来通り -32600
        // "missing method" を返す。認証ゲートは先行するが、認証済み呼び出しに対しては
        // 既存の method 形式検証を残す。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"params":{"auth":{"token":"s3cret"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("missing method", response["error"]!["message"]!.GetValue<string>());
    }




    [Fact]
    public void TokenAuthenticator_WrongLengthToken_UniformWireResponse()
    {
        // The hash-based compare normalizes presented tokens to a fixed length before
        // FixedTimeEquals, so a wrong-length guess and a wrong equal-length guess produce
        // byte-identical wire responses. Verifies the two error bodies are exactly equal
        // (no length echoed back, no detail leaked).
        // ハッシュ比較により提示トークンは固定長に正規化されてから FixedTimeEquals に渡る
        // ので、長さ違いの推測と同長の不一致は同一のワイヤ応答になる。両エラーボディが
        // バイト単位で完全一致することを確認する。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var shortReq = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"x"}}}""")!;
        var sameLenReq = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"WRONG!"}}}""")!;

        var shortResp = server.HandleMessage(shortReq)!;
        var sameLenResp = server.HandleMessage(sameLenReq)!;

        ((JsonObject)shortResp["error"]!["data"]!).Remove("correlation_id");
        ((JsonObject)sameLenResp["error"]!["data"]!).Remove("correlation_id");
        ((JsonObject)shortResp["error"]!["data"]!).Remove("request_id");
        ((JsonObject)sameLenResp["error"]!["data"]!).Remove("request_id");
        Assert.Equal(shortResp.ToJsonString(), sameLenResp.ToJsonString());
        Assert.Equal(-32001, shortResp["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void TokenAuthenticator_OversizedPresentedToken_ReturnsUnauthorized()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var oversized = new string('x', McpAuthenticationLimits.MaxTokenCharacters + 1);
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"""
            + JsonSerializer.Serialize(oversized)
            + "}}}")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        Assert.Equal(-32001, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_MaxLengthToken_AcceptsMatchingToken_Issue3798()
    {
        var token = new string('t', McpAuthenticationLimits.MaxTokenCharacters);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator(token));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"""
            + JsonSerializer.Serialize(token)
            + "}}}")!;

        var response = server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void McpAuthenticatorFactory_TokenSet_ReturnsTokenAuthenticator()
    {
        // When CDIDX_MCP_AUTH_TOKEN holds a non-whitespace value, the factory must produce a
        // TokenMcpAuthenticator that enforces a matching token on the wire.
        // CDIDX_MCP_AUTH_TOKEN に空白以外の値があれば factory は TokenMcpAuthenticator を
        // 返し、ワイヤ上で一致トークンを強制する。
        var previous = Environment.GetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, "s3cret");
            var authenticator = McpAuthenticatorFactory.FromEnvironment();
            Assert.IsType<TokenMcpAuthenticator>(authenticator);

            var matching = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"s3cret"}}}""")!;
            Assert.True(authenticator.Authenticate(matching).IsAuthenticated);

            var bad = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"nope"}}}""")!;
            Assert.False(authenticator.Authenticate(bad).IsAuthenticated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, previous);
        }
    }












    [Fact]
    public void ProcessFrame_ResponseSerializationFailure_ReturnsMinimalErrorWithRequestId()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            _ => throw new JsonException("serializer failed"));

        var response = server.ProcessFrame("""{"jsonrpc":"2.0","id":42,"method":"tools/list"}""");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal(-32603, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("serializing MCP response", root.GetProperty("error").GetProperty("message").GetString());
        var data = root.GetProperty("error").GetProperty("data");
        Assert.Equal("42", data.GetProperty("request_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public void ProcessFrame_ResponseSerializationFailureForNonObjectRequest_ReturnsErrorWithNullId()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            _ => throw new JsonException("serializer failed"));

        var response = server.ProcessFrame("""["not","an","object"]""");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.Equal(-32603, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public void ProcessFrame_ResponseSerializationFailureForInvalidRequestId_ReturnsErrorWithNullId()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            _ => throw new JsonException("serializer failed"));

        var response = server.ProcessFrame("""{"jsonrpc":"2.0","id":false,"method":"tools/list"}""");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.Equal(-32603, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task ProcessLineAsync_ResponseWriteFailure_DoesNotThrow()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());

        await server.ProcessLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            new ThrowingTextWriter());
    }

    [Fact]
    public async Task ProcessLineAsync_ParseError_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync("not json", new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("\"code\":-32700", writer.ToString());
        Assert.Contains("JSON parse error", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_OversizedFrame_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(new string('x', 1_000_001), new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("Message too large", writer.ToString());
        Assert.Contains("Message too large", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_UnsupportedProtocol_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""",
                new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("Unsupported MCP protocolVersion", writer.ToString());
        Assert.Contains("Rejecting initialize", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_UnsupportedProtocol_TruncatesErrorLog_Issue3119()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var requested = new string('q', McpBoundedText.MaxProtocolVersionChars + 25);
        var display = McpBoundedText.ForDisplay(requested, McpBoundedText.MaxProtocolVersionChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = requested,
            },
        };
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(
                request.ToJsonString(),
                new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.DoesNotContain(requested, writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(requested, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(display.Text, writer.ToString());
        Assert.Contains(display.Text, error.ToString());
        Assert.Contains("Rejecting initialize", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_AuthFailure_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("secret"));
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("Unauthorized", writer.ToString());
        Assert.Contains("Auth failed", error.ToString());
    }

    [Fact]
    public async Task RunAsync_ParseErrorWriteFailure_LogsWriteFailureAndParseError()
    {
        var transport = new ShutdownProbeTransport("stdio", _ => throw new IOException("pipe closed"), "not json");
        using var server = new McpServer(_dbPath, "test");
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.RunAsync(transport, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var log = error.ToString();
        Assert.Contains("Error writing response", log);
        Assert.Contains("pipe closed", log);
        Assert.Contains("JSON parse error", log);
    }




    [Fact]
    public void SanitizeMcpIndexFailureMessage_CapsAndCollapsesText_Issue3202()
    {
        var raw = "first line\nsecond\tline " + new string('x', McpServer.MaxMcpIndexFailureMessageLength + 100);

        var message = McpServer.SanitizeMcpIndexFailureMessageForTesting(raw, out var truncated);

        Assert.True(truncated);
        Assert.True(message.Length <= McpServer.MaxMcpIndexFailureMessageLength);
        Assert.EndsWith("...(truncated)", message);
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("\t", message);
    }



    [Fact]
    public void Dispose_ReleasesSharedDbContext()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_dispose_{Guid.NewGuid():N}.db");
        try
        {
            using (var seed = new DbContext(dbPath))
            {
                seed.InitializeSchema();
            }

            var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            _ = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!);
            Assert.NotNull(GetSharedDbContextField(server));

            server.Dispose();

            Assert.Null(GetSharedDbContextField(server));
            Assert.Throws<ObjectDisposedException>(() => server.GetOrOpenSharedDb());
        }
        finally
        {
            DeleteFileRobust(dbPath);
        }
    }

    private static DbContext? GetSharedDbContextField(McpServer server)
    {
        var field = typeof(McpServer).GetField("_sharedDb",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (DbContext?)field!.GetValue(server);
    }

    [Fact]
    public async Task ProcessLineAsync_WhenResponseSerializationFails_ReturnsJsonRpcError()
    {
        using var server = new McpServer(
            _dbPath,
            ConsoleUi.LoadVersion(),
            false,
            _ => throw new InvalidOperationException("serialize boom"));
        using var stdout = new StringWriter();

        await server.ProcessLineAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", stdout);

        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        var error = root.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        // Issue #1530: raw ex.Message must not leak into the JSON-RPC response.
        // Only the exception type name and a stderr breadcrumb should surface.
        var message = error.GetProperty("message").GetString();
        Assert.Contains("InvalidOperationException", message);
        Assert.Contains("cdidx server stderr", message);
        Assert.DoesNotContain("serialize boom", message);
    }

    [Fact]
    public void Notification_Initialized_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void Notification_Cancelled_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled"}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void UnknownNotification_ReturnsNullAndLogsToStderr()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            try
            {
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/bogus","params":{"x":1}}""")!;
                var response = _server.HandleMessage(request);

                Assert.Null(response);
                Assert.Contains(
                    McpServer.BuildUnknownNotificationLog("notifications/bogus"),
                    errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    // --- Shutdown / concurrency tests (#1567) / shutdown と並列上限のテスト ---

    [Fact]
    public void Notification_Shutdown_ReturnsNullAndLogsToStderr()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            try
            {
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/shutdown"}""")!;
                var response = _server.HandleMessage(request);

                Assert.Null(response);
                Assert.Contains("notifications/shutdown", errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void Notification_Exit_ReturnsNullAndLogsToStderr()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            try
            {
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/exit"}""")!;
                var response = _server.HandleMessage(request);

                Assert.Null(response);
                Assert.Contains("notifications/exit", errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public async Task RunAsync_ShutdownNotification_DrainsAndExits()
    {
        // The loop must exit cleanly when the wire-level `notifications/shutdown` arrives
        // even if the transport has not been closed externally. WriteFrameAsync is still
        // called once with `null` because shutdown is a notification (#1567).
        // 外部からトランスポートが閉じられなくても `notifications/shutdown` でループが正常終了
        // することを確認する (#1567)。通知なので応答は null。
        var transport = new ShutdownProbeTransport(
            """{"jsonrpc":"2.0","method":"notifications/shutdown"}""");
        using var server = new McpServer(_dbPath, "test");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
        Assert.Null(transport.LastWritten);
    }

    [Fact]
    public async Task RunAsync_ShutdownNotification_PreemptsRemainingFrames()
    {
        // A `tools/list` request queued behind shutdown must not be served — shutdown
        // wins so the server can stop without taking on more work (#1567).
        // shutdown の後ろに積まれたフレームは処理しない (#1567)。
        var transport = new ShutdownProbeTransport(
            """{"jsonrpc":"2.0","method":"notifications/shutdown"}""",
            """{"jsonrpc":"2.0","id":99,"method":"tools/list"}""");
        using var server = new McpServer(_dbPath, "test");

        await server.RunAsync(transport, CancellationToken.None);

        // Exactly one write: the null for the shutdown notification. The queued tools/list
        // is never read because the loop breaks after observing `_running == false`.
        // shutdown 通知の null 応答 1 件のみで、後続の tools/list は read されない。
        Assert.Equal(1, transport.WriteCount);
        Assert.Null(transport.LastWritten);
    }

    [Fact]
    public async Task RunAsync_InvalidUtf8DecodeFailure_ReturnsParseError()
    {
        var transport = new InvalidUtf8ReadTransport("stdio");
        using var server = new McpServer(_dbPath, "test");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
        using var response = JsonDocument.Parse(transport.LastWritten!);
        var root = response.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Contains("invalid UTF-8", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task RunAsync_InvalidUtf8DecodeFailure_WaitsForPriorStdioResponse()
    {
        var transport = new InvalidUtf8ReadTransport(
            "stdio",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        using var firstResponseStarted = new ManualResetEventSlim(false);
        using var server = new McpServer(_dbPath, "test", false, response =>
        {
            firstResponseStarted.Set();
            Thread.Sleep(200);
            return response.ToJsonString();
        });

        await server.RunAsync(transport, CancellationToken.None);

        Assert.True(firstResponseStarted.IsSet);
        Assert.Equal(2, transport.WrittenFrames.Count);
        using var first = JsonDocument.Parse(transport.WrittenFrames[0]!);
        using var second = JsonDocument.Parse(transport.WrittenFrames[1]!);
        Assert.Equal(1, first.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32700, second.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task StdioTransport_Utf16BomInput_ThrowsDecodeFailure()
    {
        var utf16Json = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n"))
            .ToArray();
        await using var input = new MemoryStream(utf16Json);
        await using var output = new MemoryStream();
        await using var transport = new StdioMcpTransport(input, output, bufferSize: 1024);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => transport.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StdioTransport_ReadFrameAsync_RejectsOversizedLineWhileReading_Issue3506()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("abcdef\n"));
        await using var output = new MemoryStream();
        await using var transport = new StdioMcpTransport(
            input,
            output,
            bufferSize: 2,
            maxLineCharacters: 5,
            maxLineUtf8Bytes: 100);

        var ex = await Assert.ThrowsAsync<BoundedLineLengthException>(() => transport.ReadFrameAsync(CancellationToken.None));

        Assert.Equal(6, ex.CharactersRead);
        Assert.Equal(5, ex.MaxCharacters);
    }

    [Fact]
    public async Task RunAsync_StdioOversizedFrame_ReturnsMessageTooLarge_Issue3506()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("abcdef\n"));
        await using var output = new MemoryStream();
        await using var transport = new StdioMcpTransport(
            input,
            output,
            bufferSize: 2,
            maxLineCharacters: 5,
            maxLineUtf8Bytes: 100);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.RunAsync(transport, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var raw = Encoding.UTF8.GetString(output.ToArray());
        using var response = JsonDocument.Parse(raw);
        Assert.Equal(-32700, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("message_too_large", response.RootElement.GetProperty("error").GetProperty("data").GetProperty("category").GetString());
        Assert.Contains("Message too large", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdioTransport_WriteFrameAsync_FlushesBeforeReturning()
    {
        await using var input = new MemoryStream();
        await using var output = new FlushCountingStream();
        await using var transport = new StdioMcpTransport(input, output, bufferSize: 1024);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", CancellationToken.None);

        Assert.True(output.FlushCount > 0);
    }

    [Fact]
    public async Task RunAsync_StdioCancellationNotification_CancelsActiveRequest()
    {
        using var cancelWritten = new ManualResetEventSlim(false);
        var transport = new ShutdownProbeTransport(
            name: "stdio",
            onWrite: frame =>
            {
                if (frame is null)
                    cancelWritten.Set();
            },
            """{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1418}}""");
        using var server = new McpServer(_dbPath, "test");
        server.RequestRegisteredForTests = _ => Assert.True(cancelWritten.Wait(TimeSpan.FromSeconds(5)));

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Contains(transport.WrittenFrames, frame => frame?.Contains("\"request_cancelled\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task CancellationNotificationBeforeRequestRegistration_CancelsMatchingRequest()
    {
        using var server = new McpServer(_dbPath, "test");
        var cancel = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1418}}""")!;

        Assert.Null(server.HandleMessage(cancel));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = await server.HandleMessageAsync(request);

        var error = response!["error"]!;
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, error["code"]!.GetValue<int>());
        Assert.Equal("request_cancelled", error["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_IndexWithProgressToken_EmitsProgressNotificationBeforeResult()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "two.cs"), "public class Two { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1684,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject { ["progressToken"] = "issue-1684" },
                },
            };
            var transport = new ShutdownProbeTransport("stdio", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            var progressFrameIndex = transport.WrittenFrames.FindIndex(frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            var resultFrameIndex = transport.WrittenFrames.FindIndex(frame =>
                frame?.Contains("\"id\":1684", StringComparison.Ordinal) == true);
            Assert.True(progressFrameIndex >= 0);
            Assert.True(resultFrameIndex >= 0);
            Assert.True(progressFrameIndex < resultFrameIndex);

            var progress = JsonNode.Parse(transport.WrittenFrames[progressFrameIndex]!)!;
            Assert.Equal("issue-1684", progress["params"]!["progressToken"]!.GetValue<string>());
            Assert.Equal(2, progress["params"]!["total"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_IndexWithObjectProgressToken_EmitsBoundedClone()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3103,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject
                    {
                        ["progressToken"] = new JsonObject
                        {
                            ["request"] = "issue-3103",
                            ["attempt"] = 1,
                            ["scope"] = new JsonObject { ["tool"] = "index" },
                        },
                    },
                },
            };
            var transport = new ShutdownProbeTransport("stdio", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            var progressFrame = transport.WrittenFrames.First(frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true)!;
            var progress = JsonNode.Parse(progressFrame)!;
            var token = progress["params"]!["progressToken"]!;
            Assert.Equal("issue-3103", token["request"]!.GetValue<string>());
            Assert.Equal(1, token["attempt"]!.GetValue<int>());
            Assert.Equal("index", token["scope"]!["tool"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_IndexWithOversizedProgressToken_ReturnsResultWithoutProgress()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3104,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject
                    {
                        ["progressToken"] = new string('x', McpBoundedText.MaxProgressTokenStringChars + 1),
                    },
                },
            };
            var transport = new ShutdownProbeTransport("stdio", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            Assert.DoesNotContain(transport.WrittenFrames, frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            Assert.Contains(transport.WrittenFrames, frame =>
                frame?.Contains("\"id\":3104", StringComparison.Ordinal) == true
                && frame.Contains("\"structuredContent\"", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_IndexWithTooManyProgressTokenNodes_ReturnsResultWithoutProgress()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var progressToken = new JsonObject();
            for (var i = 0; i < McpBoundedText.MaxProgressTokenNodeCount; i++)
                progressToken[$"k{i}"] = null;
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3106,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject { ["progressToken"] = progressToken },
                },
            };
            var transport = new ShutdownProbeTransport("stdio", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            Assert.DoesNotContain(transport.WrittenFrames, frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            Assert.Contains(transport.WrittenFrames, frame =>
                frame?.Contains("\"id\":3106", StringComparison.Ordinal) == true
                && frame.Contains("\"structuredContent\"", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_IndexWithArrayProgressToken_ReturnsResultWithoutProgress()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3105,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject { ["progressToken"] = new JsonArray("unsupported") },
                },
            };
            var transport = new ShutdownProbeTransport("stdio", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            Assert.DoesNotContain(transport.WrittenFrames, frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            Assert.Contains(transport.WrittenFrames, frame =>
                frame?.Contains("\"id\":3105", StringComparison.Ordinal) == true
                && frame.Contains("\"structuredContent\"", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_NonStreamingIndexWithProgressToken_ReturnsFinalResultWithoutProgress()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1685,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject { ["progressToken"] = "non-streaming" },
                },
            };
            var transport = new ShutdownProbeTransport("http-like", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            Assert.DoesNotContain(transport.WrittenFrames, frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            Assert.Contains(transport.WrittenFrames, frame =>
                frame?.Contains("\"id\":1685", StringComparison.Ordinal) == true
                && frame.Contains("\"structuredContent\"", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void MaxConcurrency_DefaultExposesIssueBound()
    {
        Assert.Equal(McpServer.DefaultMaxConcurrency, _server.MaxConcurrency);
        Assert.Equal(8, _server.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_ExplicitOverride_TakesEffect()
    {
        using var server = new McpServer(
            _dbPath,
            "test",
            dbPathExplicit: false,
            serializeResponse: null,
            authenticator: null,
            toolFilter: null,
            maxConcurrency: 3);
        Assert.Equal(3, server.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_NonPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new McpServer(_dbPath, "test", false, null, null, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new McpServer(_dbPath, "test", false, null, null, null, -1));
    }

    /// <summary>
    /// In-memory <see cref="IMcpTransport"/> that replays a scripted sequence of frames
    /// and records the responses the server writes back. Used to drive `RunAsync` from
    /// tests without standing up a real stdio / HTTP transport (#1567).
    /// テスト用のインメモリ MCP トランスポート (#1567)。固定フレーム列を再生し、サーバーの
    /// 応答を記録する。
    /// </summary>
    private sealed class ShutdownProbeTransport : IMcpTransport
    {
        private readonly Queue<string?> _frames;
        private readonly string _name;
        private readonly Action<string?>? _onWrite;

        public ShutdownProbeTransport(params string?[] frames)
            : this("shutdown-probe", null, frames)
        {
        }

        public ShutdownProbeTransport(string name, Action<string?>? onWrite, params string?[] frames)
        {
            _name = name;
            _onWrite = onWrite;
            _frames = new Queue<string?>(frames);
            // Append EOS so the loop terminates if shutdown never fires for some reason.
            // shutdown が来なかった場合のフェイルセーフとして末尾に EOS を積む。
            _frames.Enqueue(null);
        }

        public string Name => _name;
        public string Endpoint => "in-memory";
        public int WriteCount { get; private set; }
        public string? LastWritten { get; private set; }
        public List<string?> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCount++;
            LastWritten = frame;
            WrittenFrames.Add(frame);
            _onWrite?.Invoke(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InvalidUtf8ReadTransport : IMcpTransport
    {
        private readonly Queue<string> _frames;

        public InvalidUtf8ReadTransport(string name, params string[] frames)
        {
            Name = name;
            _frames = new Queue<string>(frames);
        }

        public string Name { get; }
        public string Endpoint => "invalid-utf8";
        public int WriteCount { get; private set; }
        public string? LastWritten { get; private set; }
        public List<string?> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (_frames.Count > 0)
                return Task.FromResult<string?>(_frames.Dequeue());
            throw new DecoderFallbackException("Unable to translate bytes [ED][A0][80] at index 0 from specified code page to Unicode.");
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCount++;
            LastWritten = frame;
            WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingWriteTransport : IMcpTransport
    {
        private readonly Queue<string?> _frames;
        private readonly Exception _exception;

        public ThrowingWriteTransport(string name, params string?[] frames)
            : this(name, new IOException("pipe closed"), frames)
        {
        }

        public ThrowingWriteTransport(string name, Exception exception, params string?[] frames)
        {
            Name = name;
            _exception = exception;
            _frames = new Queue<string?>(frames);
            _frames.Enqueue(null);
        }

        public string Name { get; }
        public string Endpoint => "throwing-write";
        public int WriteCount { get; private set; }

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCount++;
            throw _exception;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_SequentialTransportWriteFailure_DoesNotThrow()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new ThrowingWriteTransport(
            "throwing-sequential",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task RunAsync_TransportTimeoutWriteFailure_DoesNotThrow_Issue3990()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new ThrowingWriteTransport(
            "throwing-timeout",
            new TimeoutException("HTTP MCP operation timed out; category=http_response_write; timeout_ms=15000."),
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task RunAsync_StdioTransportWriteFailure_DoesNotThrow()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new ThrowingWriteTransport(
            "stdio",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task HttpTransport_WriteOutOfBandFrameAsync_WithoutEventStream_IsBestEffort()
    {
        var port = AllocateLoopbackPort();
        await using var transport = new HttpMcpTransport(
            $"http://127.0.0.1:{port}/",
            "127.0.0.1",
            port,
            bearerToken: null);

        await transport.WriteOutOfBandFrameAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);

        Assert.False(transport.HasEventStreams);
    }

    [Fact]
    public void Ping_ReturnsEmptyResult()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":99,"method":"ping"}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(99, response["id"]!.GetValue<int>());
        Assert.NotNull(response["result"]);
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"unknown/method"}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Method not found", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void MissingMethod_ReturnsInvalidRequest()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void MissingMethodAndId_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0"}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    // --- tools/list tests / ツール一覧テスト ---












    [Fact]
    public void ToolCall_WithStructuredContent_DeclaresJsonMimeType()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var content = response["result"]!["content"]!.AsArray()[0]!;
        Assert.Equal("text", content["type"]!.GetValue<string>());
        Assert.Equal("application/json", content["mimeType"]!.GetValue<string>());
    }












    // --- tool enablement filter tests (#1561) ---

    [Fact]
    public void McpToolFilter_AllowAll_EnablesEveryKnownTool()
    {
        var filter = McpToolFilter.AllowAll();
        foreach (var name in McpToolFilter.KnownToolNames)
            Assert.True(filter.IsEnabled(name), $"{name} should be enabled in AllowAll");
    }

    [Fact]
    public void McpToolFilter_Parse_AllowListPinsVisibleSet()
    {
        var filter = McpToolFilter.Parse("search, references", null);

        Assert.True(filter.IsEnabled("search"));
        Assert.True(filter.IsEnabled("references"));
        Assert.False(filter.IsEnabled("index"));
        Assert.False(filter.IsEnabled("backfill_fold"));
        Assert.False(filter.IsEnabled("suggest_improvement"));
    }

    [Fact]
    public void McpToolFilter_Parse_DenyListRemovesIndividualTools()
    {
        var filter = McpToolFilter.Parse(null, "index, backfill_fold");

        Assert.True(filter.IsEnabled("search"));
        Assert.False(filter.IsEnabled("index"));
        Assert.False(filter.IsEnabled("backfill_fold"));
    }

    [Fact]
    public void McpToolFilter_Parse_OverlongAllowListFailsClosed_Issue2905()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var filter = McpToolFilter.Parse(new string('s', McpToolFilter.MaxToolFilterCsvLength + 1), null);

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(filter.IsEnabled(name), $"{name} should be disabled when an invalid allowlist is supplied");
                Assert.Contains(McpToolFilter.AllowEnvVarName, stderr.ToString());
                Assert.Contains("was rejected", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_TooManyDenyEntriesAreRejected_Issue2905()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var tooMany = string.Join(',', Enumerable.Repeat("index", McpToolFilter.MaxToolFilterCsvEntries + 1));
                var filter = McpToolFilter.Parse(null, tooMany);

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(filter.IsEnabled(name), $"{name} should be disabled when an invalid denylist is supplied");
                Assert.Contains(McpToolFilter.DenyEnvVarName, stderr.ToString());
                Assert.Contains("accepts at most", stderr.ToString());
                Assert.Contains("was rejected", stderr.ToString());
                Assert.Contains("failing closed", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_OverlongDenyListFailsClosed_Issue3829()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var filter = McpToolFilter.Parse(null, new string('d', McpToolFilter.MaxToolFilterCsvLength + 1));

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(filter.IsEnabled(name), $"{name} should be disabled when an overlong denylist is supplied");
                var warning = stderr.ToString();
                Assert.Contains(McpToolFilter.DenyEnvVarName, warning);
                Assert.Contains("was rejected", warning);
                Assert.Contains("failing closed", warning);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_AllowWinsOverDeny()
    {
        var filter = McpToolFilter.Parse("search,index", "index");

        Assert.True(filter.IsEnabled("search"));
        Assert.True(filter.IsEnabled("index"));
        Assert.False(filter.IsEnabled("references"));
    }

    [Fact]
    public void McpToolFilter_Parse_UnknownNamesWarnAndKeepFilterSemantics_Issue3406()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                // A typo in CDIDX_MCP_TOOLS_DENY simply does not match anything; the known set
                // stays enabled, but the operator now gets a bounded warning.
                // CDIDX_MCP_TOOLS_DENY の typo は何にも一致しないため既知ツールは有効のまま。
                // ただし、オペレータに bounded warning を出す。
                var denyFilter = McpToolFilter.Parse(null, "bogus_tool");
                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.True(denyFilter.IsEnabled(name), $"{name} should remain enabled when denylist names only unknown tools");

                // Allowlist semantics deliberately differ: an allowlist with no known names
                // fails closed and exposes nothing.
                // allowlist は厳格に扱い、既知名が 0 件なら fail closed で何も公開しない。
                var allowFilter = McpToolFilter.Parse("bogus_tool", null);
                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(allowFilter.IsEnabled(name), $"{name} should be disabled when allowlist only names unknown tools");

                var warning = stderr.ToString();
                Assert.Contains(McpToolFilter.DenyEnvVarName, warning);
                Assert.Contains(McpToolFilter.AllowEnvVarName, warning);
                Assert.Contains("unknown MCP tool name", warning);
                Assert.Contains("failing closed", warning);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_RedactsTokenLikeUnknownNames_Issue3676()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                const string tokenLikeValue = "0123456789abcdef0123456789abcdef";

                var filter = McpToolFilter.Parse(tokenLikeValue, null);

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(filter.IsEnabled(name), $"{name} should be disabled when allowlist only names unknown tools");
                var warning = stderr.ToString();
                Assert.Contains(McpToolFilter.AllowEnvVarName, warning);
                Assert.Contains("<redacted>", warning);
                Assert.DoesNotContain(tokenLikeValue, warning);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_EmptyAllowListWarnsAndFailsClosed_Issue3406()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var filter = McpToolFilter.Parse("   ", null);

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(filter.IsEnabled(name), $"{name} should be disabled when allowlist is explicitly empty");
                var warning = stderr.ToString();
                Assert.Contains(McpToolFilter.AllowEnvVarName, warning);
                Assert.Contains("empty", warning);
                Assert.Contains("failing closed", warning);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_EmptyDenyListWarnsAndKeepsDefaults_Issue3406()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var filter = McpToolFilter.Parse(null, "");

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.True(filter.IsEnabled(name), $"{name} should remain enabled when denylist is explicitly empty");
                var warning = stderr.ToString();
                Assert.Contains(McpToolFilter.DenyEnvVarName, warning);
                Assert.Contains("empty", warning);
                Assert.DoesNotContain("failing closed", warning);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_UnknownNameWarningIsBounded_Issue3406()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var unknownNames = Enumerable.Range(0, McpToolFilter.MaxToolFilterUnknownNamesReported + 2)
                    .Select(i => $"bogus_tool_{i}");

                var filter = McpToolFilter.Parse(string.Join(',', unknownNames.Prepend("search")), null);

                Assert.True(filter.IsEnabled("search"));
                Assert.False(filter.IsEnabled("references"));
                var warning = stderr.ToString();
                Assert.Contains("unknown MCP tool names", warning);
                Assert.Contains("more", warning);
                Assert.DoesNotContain($"bogus_tool_{McpToolFilter.MaxToolFilterUnknownNamesReported + 1}", warning);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_IsKnownTool_DistinguishesKnownFromUnknown()
    {
        Assert.True(McpToolFilter.IsKnownTool("search"));
        Assert.True(McpToolFilter.IsKnownTool("SEARCH"));
        Assert.False(McpToolFilter.IsKnownTool("bogus_tool"));
        Assert.False(McpToolFilter.IsKnownTool(null));
        Assert.False(McpToolFilter.IsKnownTool(string.Empty));
    }









    // --- tools/call tests / ツール呼び出しテスト ---








































































































    [Fact]
    public void Constructor_InvalidKeepAliveInterval_RedactsSecretLookingEnvValue_Issue3403()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
        var secret = "github_pat_" + "a".PadLeft(82, 'a');
        env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", $"Bearer {secret}");

        var stderr = ConsoleCapture.CaptureError(() => _ = new McpServer(_dbPath, ConsoleUi.LoadVersion()));

        Assert.Contains("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S='Bearer <redacted>'", stderr);
        Assert.DoesNotContain(secret, stderr);
    }

    [Fact]
    public void ResponseLimitSerializer_StopsBeforeFullStringMaterialization_Issue2860()
    {
        var payload = new JsonObject
        {
            ["value"] = new string('x', 10_000),
        };

        var withinLimit = _server.TrySerializeJsonNodeWithinByteLimitForTests(payload, 256, out var serialized, out var bytesWritten);

        Assert.False(withinLimit);
        Assert.Null(serialized);
        Assert.True(bytesWritten > 256);
        Assert.True(bytesWritten < 10_000);
    }





    [Fact]
    public void ResponseLimitSerializer_ReturnsCapturedJsonWhenWithinLimit_Issue2860()
    {
        var payload = new JsonObject
        {
            ["value"] = "ok",
        };

        var withinLimit = _server.TrySerializeJsonNodeWithinByteLimitForTests(payload, 256, out var serialized, out var bytesWritten);

        Assert.True(withinLimit);
        Assert.NotNull(serialized);
        Assert.Equal(Encoding.UTF8.GetByteCount(serialized), bytesWritten);
        using var parsed = JsonDocument.Parse(serialized);
        Assert.Equal("ok", parsed.RootElement.GetProperty("value").GetString());
    }












    [Fact]
    public void Constructor_InvalidKeepAliveEnvironment_DoesNotThrow()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
        env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", "Infinity");

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping"}""")!)!;

        Assert.Equal("ok", response["result"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Constructor_InvalidKeepAliveEnvironment_TruncatesWarning_Issue3091()
    {
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
            var originalErr = Console.Error;
            using var stderr = new StringWriter();
            var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);
            try
            {
                env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", value);
                Console.SetError(stderr);

                using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);

                var warning = stderr.ToString();
                Assert.Contains("Ignoring invalid CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", warning);
                Assert.Contains("<truncated; original length", warning);
                Assert.DoesNotContain(value, warning);
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
    }



    [Fact]
    public async Task ProcessFrameAsync_BatchResponseOverByteLimit_ReturnsStructuredError()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_RESPONSE_MAX_BYTES", "128");

        var frame = "["
            + string.Join(",", Enumerable.Range(1, 10).Select(id => $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}"""))
            + "]";
        var responseText = await _server.ProcessFrameAsync(frame);
        using var response = JsonDocument.Parse(responseText!);
        var root = response.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("response_too_large", data.GetProperty("reason").GetString());
        Assert.Equal(128, data.GetProperty("limit_bytes").GetInt32());
        Assert.True(data.GetProperty("actual_bytes").GetInt32() > 128);
    }
















    private static long InsertDependencyFile(DbWriter writer, string path, bool generated = false)
    {
        return writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
            Generated = generated,
        });
    }

    private static void InsertDependencySymbols(DbWriter writer, long fileId, IReadOnlyList<string> symbolNames)
    {
        writer.InsertSymbols(symbolNames.Select((symbolName, index) => new SymbolRecord
        {
            FileId = fileId,
            Kind = "class",
            Name = symbolName,
            Line = index + 1,
            StartLine = index + 1,
            EndLine = index + 1,
        }).ToArray());
    }

    private static void InsertDependencyReferences(DbWriter writer, long fileId, IReadOnlyList<string> symbolNames)
    {
        writer.InsertReferences(symbolNames.Select((symbolName, index) => new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "type_reference",
            Line = index + 1,
            Column = 1,
            Context = symbolName,
        }).ToArray());
    }















































































    [Fact]
    public void ApplyExcerptOutputBudget_TruncatesAtLineBoundary_Issue1605()
    {
        var payload = new JsonObject
        {
            ["content"] = "short\n" + new string('x', 200),
            ["contentTruncated"] = false,
            ["contentLineSpans"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentLine"] = 1,
                    ["sourceLine"] = 10,
                    ["contentStartColumn"] = 1,
                    ["contentEndColumn"] = 6,
                    ["sourceStartColumn"] = 3,
                    ["sourceEndColumn"] = 8,
                },
                new JsonObject
                {
                    ["contentLine"] = 2,
                    ["sourceLine"] = 11,
                    ["contentStartColumn"] = 1,
                    ["contentEndColumn"] = 201,
                    ["sourceStartColumn"] = 1,
                    ["sourceEndColumn"] = 201,
                },
            },
            ["semanticTokens"] = new JsonArray
            {
                new JsonObject
                {
                    ["startLine"] = 10,
                    ["startColumn"] = 3,
                    ["endLine"] = 10,
                    ["endColumn"] = 8,
                    ["type"] = "variable",
                    ["modifiers"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["startLine"] = 11,
                    ["startColumn"] = 1,
                    ["endLine"] = 11,
                    ["endColumn"] = 5,
                    ["type"] = "variable",
                    ["modifiers"] = new JsonArray(),
                },
            },
        };

        McpServer.ApplyExcerptOutputBudget(payload, 20);

        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.Equal("output_size_cap", payload["truncation_reason"]!.GetValue<string>());
        Assert.Equal("short", payload["content"]!.GetValue<string>());
        Assert.True(payload["contentTruncated"]!.GetValue<bool>());
        var spans = payload["contentLineSpans"]!.AsArray();
        var span = Assert.Single(spans);
        Assert.Equal(1, span!["contentLine"]!.GetValue<int>());
        Assert.Equal(10, span["sourceLine"]!.GetValue<int>());
        var tokens = payload["semanticTokens"]!.AsArray();
        var token = Assert.Single(tokens);
        Assert.Equal(10, token!["startLine"]!.GetValue<int>());
        Assert.Equal(8, token["endColumn"]!.GetValue<int>());
    }


















    [Fact]
    public void ToolsCall_Index_GeneratedCodePatternCountsProcessedAndSkipsExtraction_Issue3720()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_generated_pattern_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_generated_pattern_{Guid.NewGuid():N}.db");
        try
        {
            env.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, "generated/**");
            Directory.CreateDirectory(Path.Combine(fixtureDir, "generated"));
            File.WriteAllText(
                Path.Combine(fixtureDir, "generated", "Client.cs"),
                "public class GeneratedClient { public string Lookup() => \"generated\"; }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var response = CallIndex(server, fixtureDir);

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
            using var verifyDb = new DbContext(dbPath);
            Assert.Equal("1", verifyDb.GetMetaString(DbContext.LastIndexRunRowsUpsertedMetaKey));
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT f.generated,
                       (SELECT COUNT(*) FROM chunks c WHERE c.file_id = f.id AND c.content LIKE '%GeneratedClient%'),
                       (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id),
                       (SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id),
                       (SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id AND i.kind = @issueKind)
                FROM files f
                WHERE f.path = @path
                """;
            command.Parameters.AddWithValue("@issueKind", FileIndexer.GeneratedCodeExtractionSkippedIssueKind);
            command.Parameters.AddWithValue("@path", "generated/Client.cs");
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.True(reader.GetInt32(1) > 0);
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.Equal(1, reader.GetInt32(4));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_UnknownArgumentName_TruncatesDisplay_Issue3117()
    {
        var argumentName = new string('x', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = new JsonObject
                {
                    ["path"] = ".",
                    [argumentName] = true,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.DoesNotContain(argumentName, response.ToJsonString(), StringComparison.Ordinal);
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"Unknown argument '{display.Text}' for tool 'index'.", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(display.Text, structured["unknown_argument"]!.GetValue<string>());
        Assert.Equal(argumentName.Length, structured["unknown_argument_length"]!.GetValue<int>());
        Assert.True(structured["unknown_argument_truncated"]!.GetValue<bool>());
    }



    [Fact]
    public void SanitizeArgs_TruncatesArgumentKeysForAuditAndTelemetry_Issue3117_Issue3105()
    {
        var argumentName = new string('k', AuditLogSink.MaxAuditArgumentKeyChars + 1);
        var display = McpBoundedText.ForDisplay(argumentName, AuditLogSink.MaxAuditArgumentKeyChars);
        var args = new JsonObject
        {
            [argumentName] = "value",
        };

        var (keys, lengths, keyLengths, valuesEcho) = McpServer.SanitizeArgs(args, includeValues: false);

        Assert.Equal([display.Text], keys);
        var length = Assert.Single(lengths);
        Assert.Equal(display.Text, length.Key);
        Assert.Equal("value".Length, length.Value);
        var keyLength = Assert.Single(keyLengths);
        Assert.Equal(display.Text, keyLength.Key);
        Assert.Equal(argumentName.Length, keyLength.Value);
        Assert.Null(valuesEcho);
    }

    [Fact]
    public void SanitizeArgs_TruncatesArgumentKeysInValuesEcho_Issue3117_Issue3105()
    {
        var argumentName = new string('k', AuditLogSink.MaxAuditArgumentKeyChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName, AuditLogSink.MaxAuditArgumentKeyChars);
        var args = new JsonObject
        {
            [argumentName] = "value",
        };

        var (_, _, keyLengths, valuesEcho) = McpServer.SanitizeArgs(args, includeValues: true);

        Assert.NotNull(valuesEcho);
        var json = valuesEcho!.ToJsonString();
        Assert.DoesNotContain(argumentName, json, StringComparison.Ordinal);
        Assert.Equal("value", valuesEcho[display.Text]!.GetValue<string>());
        var keyLength = Assert.Single(keyLengths);
        Assert.Equal(display.Text, keyLength.Key);
        Assert.Equal(argumentName.Length, keyLength.Value);
    }

    [Fact]
    public void SanitizeArgs_DisambiguatesCollidingTruncatedKeys_Issue3117_Issue3105()
    {
        var sharedPrefix = new string('c', AuditLogSink.MaxAuditArgumentKeyChars + 25);
        var firstArgumentName = sharedPrefix + "a";
        var secondArgumentName = sharedPrefix + "b";
        var args = new JsonObject
        {
            [firstArgumentName] = "one",
            [secondArgumentName] = "two",
        };

        var (keys, lengths, keyLengths, valuesEcho) = McpServer.SanitizeArgs(args, includeValues: true);

        Assert.Equal(2, keys.Count);
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, lengths.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, keyLengths.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(valuesEcho);
        var json = valuesEcho!.ToJsonString();
        Assert.DoesNotContain(firstArgumentName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secondArgumentName, json, StringComparison.Ordinal);
        Assert.Equal("one", valuesEcho[keys[0]]!.GetValue<string>());
        Assert.Equal("two", valuesEcho[keys[1]]!.GetValue<string>());
    }


    [Fact]
    public void McpIndexRunLock_TryAcquire_OnPosix_WritesPrivateInfoFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_private_lock_{Guid.NewGuid():N}.db");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        var infoPath = lockPath + ".info";
        try
        {
            Assert.True(McpIndexRunLock.TryAcquire(dbPath, out var runLock, out var error), error);
            Assert.NotNull(runLock);
            using (runLock!)
            {
                Assert.True(File.Exists(infoPath));
                Assert.Equal(
                    DataDirectorySecurity.PrivateFileMode,
                    File.GetUnixFileMode(infoPath) & DataDirectorySecurity.PermissionBits);
            }
        }
        finally
        {
            TestProjectHelper.DeleteFile(infoPath);
            TestProjectHelper.DeleteFile(lockPath);
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void McpIndexRunLock_Dispose_WhenInfoCleanupFails_ReportsSanitizedDiagnostic_Issue3462()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_cleanup_diag_{Guid.NewGuid():N}.db");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        var infoPath = lockPath + ".info";
        var diagnostics = new List<LockCleanupDiagnostic>();
        try
        {
            McpIndexRunLock.CleanupDiagnosticSinkForTesting = diagnostics.Add;
            McpIndexRunLock.DeleteFileForTesting = path =>
            {
                if (string.Equals(path, infoPath, StringComparison.Ordinal))
                    throw new IOException($"sensitive cleanup path {path}");
                File.Delete(path);
            };

            Assert.True(McpIndexRunLock.TryAcquire(dbPath, out var runLock, out var error), error);
            using (runLock!)
            {
            }

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("mcp_index_lock", diagnostic.Component);
            Assert.Equal("metadata", diagnostic.Target);
            Assert.Equal("io_error", diagnostic.Reason);
            Assert.DoesNotContain("sensitive", diagnostic.ToLogMessage(), StringComparison.Ordinal);
            Assert.DoesNotContain(infoPath, diagnostic.ToLogMessage(), StringComparison.Ordinal);
        }
        finally
        {
            McpIndexRunLock.CleanupDiagnosticSinkForTesting = null;
            McpIndexRunLock.DeleteFileForTesting = File.Delete;
            TestProjectHelper.DeleteFile(infoPath);
            TestProjectHelper.DeleteFile(lockPath);
            DeleteFileRobust(dbPath);
        }
    }













































    // --- Security tests / セキュリティテスト ---




    // --- Database not found tests / DB未検出テスト ---

    [Fact]
    public void ToolsCall_Search_DbNotFound_ReturnsError()
    {
        using var server = new McpServer("/nonexistent/path/test.db", "0.1.1");
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"test"}}}""")!;
        var response = server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not found", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    // --- suggest_improvement tests / suggest_improvement テスト ---

    [Fact]
    public void SuggestImprovement_ValidInput_ReturnsSuccess()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_GITHUB_TOKEN");
        env.Set("CDIDX_GITHUB_TOKEN", null);
        // Use unique description to avoid dedup collision with other test runs
        // 他テスト実行との重複排除衝突を避けるため一意な description を使用
        var uniqueDesc = $"Arrow functions are not detected as symbols {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "symbol_extraction", ["language"] = "typescript", ["description"] = uniqueDesc }
            }
        };
        var request = (JsonNode)json;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("draft", structured["lifecycle_status"]!.GetValue<string>());
        Assert.NotNull(structured["hash"]);
        Assert.True(structured["stored_locally"]!.GetValue<bool>());
        Assert.False(structured["submitted_to_github"]!.GetValue<bool>());
        Assert.Equal("token_not_configured", structured["github_submission_reason"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(_dbPath)!), structured["cdidx_dir"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_CrashReport_ReturnsSuccess()
    {
        var uniqueDesc = $"NullReferenceException when searching with empty query {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "crash_report", ["description"] = uniqueDesc }
            }
        };
        var request = (JsonNode)json;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_RecordsClientAttributionFromInitialize()
    {
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"codex","version":"5.0"}}}""")!);
        var uniqueDesc = $"Attribution metadata regression {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                    ["toolInvocationContext"] = "Investigating suggestion triage",
                    ["evidencePaths"] = new JsonArray { "src/CodeIndex/Mcp/McpToolHandlers.cs" }
                }
            }
        };

        _server.HandleMessage((JsonNode)json);

        var cdidxDir = Path.GetDirectoryName(_dbPath)!;
        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var stored = new SuggestionStore(cdidxDir, dbName).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Equal("codex/5.0", stored.CreatedByAgent);
        Assert.Equal(_server.CurrentSessionId, stored.SessionId);
        Assert.Equal(ConsoleUi.LoadVersion(), stored.ClientVersion);
        Assert.Equal("codex", stored.McpClientName);
        Assert.Equal("5.0", stored.McpClientVersion);
        Assert.Equal("Investigating suggestion triage", stored.ToolInvocationContext);
        Assert.Equal(["src/CodeIndex/Mcp/McpToolHandlers.cs"], stored.EvidencePaths);
    }

    [Fact]
    public void SuggestImprovement_RedactedDescriptionReturnsStoredHash()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_GITHUB_TOKEN");
        env.Set("CDIDX_GITHUB_TOKEN", null);
        var secret = $"secret-{Guid.NewGuid():N}";
        var description = $"MCP redaction hash regression api_key={secret}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = description,
                }
            }
        };

        var response = _server.HandleMessage((JsonNode)json)!;

        var structured = response["result"]!["structuredContent"]!;
        var responseHash = structured["hash"]!.GetValue<string>();
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Hash == responseHash);
        Assert.Equal(stored.Hash, responseHash);
        Assert.Contains("api_key=[REDACTED:credential]", stored.Description);
        Assert.DoesNotContain(secret, stored.Description);
    }

    [Fact]
    public void SuggestImprovement_RejectsNonRelativeEvidencePath()
    {
        var uniqueDesc = $"Evidence path validation regression {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                    ["evidencePaths"] = new JsonArray { "/Users/example/project/src/File.cs" }
                }
            }
        };

        var response = _server.HandleMessage((JsonNode)json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var message = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("repository-relative", message);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingAvailable_StoresSampledMetadata()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = """{"title":"Improve TypeScript arrow symbol extraction","tags":["symbol_extraction","typescript","ranking"]}"""
                }
            };
        };
        var uniqueDesc = $"TypeScript arrow symbols need clearer extraction {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "symbol_extraction",
                    ["language"] = "typescript",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("sampled", structured["sampling_status"]!.GetValue<string>());
        Assert.Equal("Improve TypeScript arrow symbol extraction", structured["sampled_title"]!.GetValue<string>());
        Assert.Contains(structured["sampled_tags"]!.AsArray(), tag => tag!.GetValue<string>() == "typescript");
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Equal("Improve TypeScript arrow symbol extraction", stored.SampledTitle);
        Assert.Contains("symbol_extraction", stored.SampledTags!);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingReturnsSensitiveMetadata_RedactsBeforeResponseAndPersistence()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_GITHUB_TOKEN");
        env.Set("CDIDX_GITHUB_TOKEN", null);
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        var secret = $"sample-secret-{Guid.NewGuid():N}";
        string? capturedPrompt = null;
        _server.ClientRequestHandlerForTests = (method, parameters) =>
        {
            Assert.Equal("sampling/createMessage", method);
            capturedPrompt = parameters?["messages"]?[0]?["content"]?["text"]?.GetValue<string>();
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $$"""{"title":"Echoed api_key={{secret}}","tags":["github_token={{secret}}"]}"""
                }
            };
        };
        var description = $"Sampling metadata redaction regression api_key={secret}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = description,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var sampledTitle = structured["sampled_title"]!.GetValue<string>();
        var sampledTags = string.Join(" ", structured["sampled_tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Contains("api_key=[REDACTED:credential]", sampledTitle);
        Assert.Contains("redacted", sampledTags);
        Assert.NotNull(capturedPrompt);
        Assert.DoesNotContain(secret, capturedPrompt);
        Assert.DoesNotContain(secret, sampledTitle);
        Assert.DoesNotContain(secret, sampledTags);
        var responseHash = structured["hash"]!.GetValue<string>();
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Hash == responseHash);
        Assert.DoesNotContain(secret, stored.Description);
        Assert.DoesNotContain(secret, stored.SampledTitle!);
        Assert.DoesNotContain(secret, string.Join(" ", stored.SampledTags!));
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingResponseIsTooLarge_IgnoresSampledMetadata()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $$"""{"title":"{{new string('A', 9000)}}","tags":["security"]}"""
                }
            };
        };
        var uniqueDesc = $"Oversized sampling response regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("sampling_rejected", structured["sampling_status"]!.GetValue<string>());
        Assert.Contains("text length", structured["sampling_diagnostic"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(structured["sampled_title"]);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Null(stored.SampledTitle);
        Assert.Null(stored.SampledTags);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingResponseJsonIsMalformed_ReportsBoundedDiagnostic_Issue3816()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        const string secret = "SECRET_SAMPLING_JSON_3816";
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $$"""{"title":"broken {{secret}}","tags":["security"]"""
                }
            };
        };
        var uniqueDesc = $"Malformed sampling response regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("sampling_rejected", structured["sampling_status"]!.GetValue<string>());
        var diagnostic = structured["sampling_diagnostic"]!.GetValue<string>();
        Assert.Contains("JSON rejected", diagnostic, StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= 240);
        Assert.DoesNotContain(secret, diagnostic, StringComparison.Ordinal);
        Assert.Null(structured["sampled_title"]);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingResponseSchemaIsInvalid_ReportsDiagnostic_Issue3816()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = """{"title":{},"tags":"security"}"""
                }
            };
        };
        var uniqueDesc = $"Invalid sampling schema regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("sampling_rejected", structured["sampling_status"]!.GetValue<string>());
        var diagnostic = structured["sampling_diagnostic"]!.GetValue<string>();
        Assert.Contains("schema rejected", diagnostic, StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= 240);
        Assert.Null(structured["sampled_title"]);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingClientResponseJsonIsTooLarge_IgnoresSampledMetadata_Issue3098()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = new string('A', McpServer.MaxClientResponseJsonBytes + 1),
                },
            };
        };
        var uniqueDesc = $"Oversized sampling client response regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Null(structured["sampled_title"]);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Null(stored.SampledTitle);
        Assert.Null(stored.SampledTags);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingResponseJsonIsTooDeep_IgnoresSampledMetadata()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            var deepTail = new string('[', 40) + "null" + new string(']', 40);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $$"""{"title":"Deep sampling metadata","tags":["security"],"nested":{{deepTail}}}"""
                }
            };
        };
        var uniqueDesc = $"Deep sampling response regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Null(structured["sampled_title"]);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Null(stored.SampledTitle);
        Assert.Null(stored.SampledTags);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingAvailable_BoundsPromptAndSummarizesInvocationContext()
    {
        using var samplingEnv = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        samplingEnv.Set("CDIDX_MCP_SAMPLING", "1");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        string? capturedPrompt = null;
        _server.ClientRequestHandlerForTests = (method, parameters) =>
        {
            Assert.Equal("sampling/createMessage", method);
            capturedPrompt = parameters?["messages"]?[0]?["content"]?["text"]?.GetValue<string>();
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = """{"title":"Bound sampling prompt","tags":["security"]}"""
                }
            };
        };
        var uniqueDesc = new string('\u3042', 2000);
        var context = new string('\u3044', 1000);
        const string secretValue = "secret-token-1234567890";
        var toolInvocationContext = $"search request included token {secretValue} and detailed invocation payload";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                    ["context"] = context,
                    ["toolInvocationContext"] = toolInvocationContext,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("Bound sampling prompt", structured["sampled_title"]!.GetValue<string>());
        Assert.NotNull(capturedPrompt);
        Assert.True(Encoding.UTF8.GetByteCount(capturedPrompt) <= 4096);
        Assert.Contains("tool_invocation_context: provided;", capturedPrompt);
        Assert.Contains("raw content withheld", capturedPrompt);
        Assert.DoesNotContain(secretValue, capturedPrompt);
        Assert.Contains("[truncated]", capturedPrompt);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Equal(toolInvocationContext, stored.ToolInvocationContext);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingEnvUnset_DoesNotCallClientSampling_Issue3405()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        env.Set("CDIDX_MCP_SAMPLING", null);
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        var called = false;
        _server.ClientRequestHandlerForTests = (_, _) =>
        {
            called = true;
            return null;
        };
        var uniqueDesc = $"Sampling unset fail-closed regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.False(called);
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("disabled", structured["sampling_status"]!.GetValue<string>());
        Assert.Contains("requires explicit opt-in", structured["sampling_diagnostic"]!.GetValue<string>());
        Assert.Null(structured["sampled_title"]);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingEnvInvalid_DoesNotCallClientSampling_Issue3405()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        env.Set("CDIDX_MCP_SAMPLING", new string('x', 512));
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        var called = false;
        _server.ClientRequestHandlerForTests = (_, _) =>
        {
            called = true;
            return null;
        };
        var uniqueDesc = $"Sampling invalid env fail-closed regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var diagnostic = structured["sampling_diagnostic"]!.GetValue<string>();
        Assert.False(called);
        Assert.Equal("disabled", structured["sampling_status"]!.GetValue<string>());
        Assert.Contains("unrecognized value", diagnostic);
        Assert.True(diagnostic.Length < 200);
        Assert.DoesNotContain(new string('x', 80), diagnostic);
        Assert.Null(structured["sampled_title"]);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingDisabled_DoesNotCallClientSampling()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        env.Set("CDIDX_MCP_SAMPLING", "0");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        var called = false;
        _server.ClientRequestHandlerForTests = (_, _) =>
        {
            called = true;
            return null;
        };
        var uniqueDesc = $"Sampling opt-out regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        Assert.False(called);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("disabled", structured["sampling_status"]!.GetValue<string>());
        Assert.Contains("opt-out", structured["sampling_diagnostic"]!.GetValue<string>());
        Assert.Null(structured["sampled_title"]);
    }

    [Fact]
    public void Index_WhenClientRootsExcludePath_ReturnsError()
    {
        var requestedMethods = new List<string>();
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethods.Add(method);
            return new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = "file:///tmp/cdidx-not-this-workspace" })
            };
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Contains("roots/list", requestedMethods);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Index_WhenClientRootsAreEmpty_ReturnsError()
    {
        var requestedMethods = new List<string>();
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethods.Add(method);
            return new JsonObject { ["roots"] = new JsonArray() };
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Contains("roots/list", requestedMethods);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Index_WhenClientRootsHaveNoFileRoots_ReturnsError()
    {
        var requestedMethods = new List<string>();
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethods.Add(method);
            return new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = "https://example.com/workspace" })
            };
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Contains("roots/list", requestedMethods);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_DuplicateSubmission_ReturnsDuplicate()
    {
        var uniqueDesc = $"Add support for Zig language {Guid.NewGuid():N}";
        JsonNode MakeRequest(int id) => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "language_support", ["description"] = uniqueDesc }
            }
        };

        _server.HandleMessage(MakeRequest(1));
        var response2 = _server.HandleMessage(MakeRequest(2))!;

        var structured = response2["result"]!["structuredContent"]!;
        Assert.Equal("duplicate", structured["status"]!.GetValue<string>());
        Assert.Equal("draft", structured["lifecycle_status"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(_dbPath)!), structured["cdidx_dir"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_UnwritableCdidxDir_ReturnsActionableError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dir = TestProjectHelper.CreateTempProject("cdidx_mcp_readonly");
        var originalMode = File.GetUnixFileMode(dir);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using var server = new McpServer(Path.Combine(dir, "codeindex.db"), ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"other","description":"Permission probe regression"}}}""")!;

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var message = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("Cannot write to .cdidx directory", message);
            Assert.Contains(Path.GetFullPath(dir), message);
            Assert.Contains("check directory ownership, permissions, and read-only mounts", message);
        }
        finally
        {
            try { File.SetUnixFileMode(dir, originalMode); } catch { }
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void SuggestImprovement_WriteProbePreservesExistingProbeFile()
    {
        var cdidxDir = Path.GetDirectoryName(_dbPath)!;
        var existingProbe = Path.Combine(cdidxDir, ".write_probe");
        File.WriteAllText(existingProbe, "keep me");
        var uniqueDesc = $"Probe preservation regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.Equal("keep me", File.ReadAllText(existingProbe));
    }

    [Fact]
    public void SuggestImprovement_WriteProbeCleanupFailureWarnsWithoutFailing_Issue3023()
    {
        var cdidxDir = Path.GetDirectoryName(_dbPath)!;
        var uniqueDesc = $"Probe cleanup regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                },
            },
        };

        JsonNode response;
        using var stderr = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            var previousError = Console.Error;
            Console.SetError(stderr);
            try
            {
                McpServer.DeleteCdidxDirectoryWritableProbeForTesting = _ => throw new IOException("simulated probe cleanup failure");
                response = _server.HandleMessage(request)!;
            }
            finally
            {
                McpServer.DeleteCdidxDirectoryWritableProbeForTesting = null;
                Console.SetError(previousError);
            }
        }

        try
        {
            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Contains("Warning: failed to delete .cdidx writable probe", stderr.ToString());
            Assert.Contains("IOException", stderr.ToString());
        }
        finally
        {
            foreach (var leftover in Directory.GetFiles(cdidxDir, ".write_probe.*.tmp"))
                DeleteFileRobust(leftover);
        }
    }

    [Fact]
    public void SuggestImprovement_InvalidCategory_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"invalid_category","description":"Some description"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Invalid category", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    // Regression pin for issue #1582: typo'd category should surface a "Did you mean: ..." hint and
    // expose machine-readable similar values via `result.data.similar_values` for MCP clients.
    // #1582 回帰テスト: タイポしたカテゴリは "Did you mean: ..." ヒントを返し、MCP クライアント向けに
    // `result.data.similar_values` で類似候補を構造化して提供する。
    [Fact]
    public void SuggestImprovement_InvalidCategoryTypo_ReturnsDidYouMeanWithSimilarValues_Issue1582()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"symbol_extractoin","description":"Some description"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Invalid category", text);
        Assert.Contains("Did you mean: symbol_extraction", text);

        var data = result["data"]!.AsObject();
        var similar = data["similar_values"]!.AsArray();
        Assert.Contains(similar, n => n!.GetValue<string>() == "symbol_extraction");
    }

    [Fact]
    public void SuggestImprovement_MissingDescription_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"other"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("description", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_SourceCodeInDescription_ReturnsError()
    {
        // Build the JSON with actual newlines in description so SourceCodeDetector sees code lines
        // SourceCodeDetector がコード行を認識するよう、description に実際の改行を含む JSON を構築
        var desc = "public void Foo()\n{\n    var x = 1;\n    var y = 2;\n    var z = x + y;\n    Console.WriteLine(z);\n}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "other", ["description"] = desc }
            }
        };
        var response = _server.HandleMessage(json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("source code", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var rejection = response["result"]!["structuredContent"]!["source_code_rejection"]!;
        Assert.Equal("description", rejection["field"]!.GetValue<string>());
        Assert.Equal(SourceCodeDetector.ReasonStatementEnding, rejection["reason_code"]!.GetValue<string>());
        var reasonCounts = rejection["reason_code_counts"]!.AsObject();
        Assert.Equal(1, reasonCounts[SourceCodeDetector.ReasonStatementEnding]!.GetValue<int>());
        Assert.Equal(1, reasonCounts[SourceCodeDetector.ReasonIndentedCodeLines]!.GetValue<int>());
        Assert.Equal(1, reasonCounts[SourceCodeDetector.ReasonBlockStructure]!.GetValue<int>());
        Assert.Equal(1, reasonCounts[SourceCodeDetector.ReasonFunctionDefinition]!.GetValue<int>());
    }

    [Fact]
    public void SuggestImprovement_SourceCodeFenceRejectionIncludesBoundedReason_Issue3830()
    {
        const string leakedToken = "SHOULD_NOT_APPEAR_3830";
        var desc = "The tool should explain this failure:\n"
                 + "~~~csharp\n"
                 + leakedToken + "\n"
                 + "~~~";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "other", ["description"] = desc }
            }
        };
        var response = _server.HandleMessage(json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var rejection = response["result"]!["structuredContent"]!["source_code_rejection"]!;
        Assert.Equal("description", rejection["field"]!.GetValue<string>());
        Assert.Equal(SourceCodeDetector.ReasonFencedCodeBlock, rejection["reason_code"]!.GetValue<string>());
        var reasonCounts = rejection["reason_code_counts"]!.AsObject();
        Assert.Equal(1, reasonCounts[SourceCodeDetector.ReasonFencedCodeBlock]!.GetValue<int>());
        Assert.DoesNotContain(leakedToken, response.ToJsonString());
    }

    [Fact]
    public void SuggestImprovement_SourceCodeInContext_ReturnsError()
    {
        var ctx = "function foo() {\n    let x = 1;\n    let y = 2;\n    return x + y;\n}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "other", ["description"] = "Something is wrong", ["context"] = ctx }
            }
        };
        var response = _server.HandleMessage(json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("source code", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_BlockedInBatchQuery()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"suggest_improvement","arguments":{"category":"other","description":"test"}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        Assert.Contains("not allowed in batch_query", results[0]!["error"]!.GetValue<string>());
    }





    [Fact]
    public async Task ProcessFrameAsync_RequestTimeout_ReturnsStructuredTimeoutError()
    {
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(250),
        };
        using var delayStarted = new ManualResetEventSlim(false);
        using var releaseDelay = new ManualResetEventSlim(false);
        server.RequestDelayForTests = _ =>
        {
            delayStarted.Set();
            Assert.True(releaseDelay.Wait(TimeSpan.FromSeconds(5)));
            return Task.CompletedTask;
        };

        try
        {
            var responseTask = server.ProcessFrameAsync(
                """{"jsonrpc":"2.0","id":123,"method":"tools/call","params":{"name":"status"}}""");
            Assert.True(delayStarted.Wait(TimeSpan.FromSeconds(1)));
            server.RequestDelayForTests = null;

            var responseText = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
            var response = JsonNode.Parse(responseText!)!;
            var error = response["error"]!;
            Assert.Equal(-32603, error["code"]!.GetValue<int>());
            Assert.Equal("Request timed out", error["message"]!.GetValue<string>());
            Assert.Equal("timeout", error["data"]!["reason"]!.GetValue<string>());
            Assert.Equal(OperationTimeoutCategories.McpRequest, error["data"]!["timeout_category"]!.GetValue<string>());
            Assert.True(error["data"]!["elapsed_ms"]!.GetValue<long>() >= 1);
            Assert.True(error["data"]!["isolated_action_draining"]!.GetValue<bool>());
            Assert.Equal("internal_error", error["data"]!["category"]!.GetValue<string>());
            Assert.True(error["data"]!["retry_safe"]!.GetValue<bool>());
            Assert.Equal(123, response["id"]!.GetValue<int>());

            var requestTimeouts = server.BuildRequestTimeoutDiagnosticsStatus();
            Assert.Equal(1L, requestTimeouts["isolated_action_draining_count"]!.GetValue<long>());
            Assert.Equal(0L, requestTimeouts["isolated_action_drained_count"]!.GetValue<long>());
            Assert.Equal("123", requestTimeouts["last"]!["request_id"]!.GetValue<string>());
            Assert.Equal("draining", requestTimeouts["last"]!["state"]!.GetValue<string>());
            Assert.True(requestTimeouts["last"]!["elapsed_ms"]!.GetValue<long>() >= 1);
        }
        finally
        {
            releaseDelay.Set();
        }
    }

    [Fact]
    public async Task ProcessFrameAsync_BatchRequestTimeout_ReturnsStructuredTimeoutError()
    {
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(250),
        };
        using var delayStarted = new ManualResetEventSlim(false);
        using var releaseDelay = new ManualResetEventSlim(false);
        server.RequestDelayForTests = _ =>
        {
            delayStarted.Set();
            Assert.True(releaseDelay.Wait(TimeSpan.FromSeconds(5)));
            return Task.CompletedTask;
        };

        try
        {
            var responseTask = server.ProcessFrameAsync(
                """[{"jsonrpc":"2.0","id":123,"method":"tools/call","params":{"name":"status"}}]""");
            Assert.True(delayStarted.Wait(TimeSpan.FromSeconds(1)));
            server.RequestDelayForTests = null;

            var responseText = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
            var response = JsonNode.Parse(responseText!)!.AsArray().Single()!;
            var error = response["error"]!;
            Assert.Equal(-32603, error["code"]!.GetValue<int>());
            Assert.Equal("Request timed out", error["message"]!.GetValue<string>());
            Assert.Equal("timeout", error["data"]!["reason"]!.GetValue<string>());
            Assert.True(error["data"]!["isolated_action_draining"]!.GetValue<bool>());
            Assert.Equal(123, response["id"]!.GetValue<int>());
        }
        finally
        {
            releaseDelay.Set();
        }
    }

    [Fact]
    public async Task RunAsync_StdioEofDrainsInFlightRequestBeforeReturning()
    {
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false);
        server.RequestRegisteredForTests = _ => { };
        var transport = new QueuedFrameTransport(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Single(transport.WrittenFrames);
        var response = JsonNode.Parse(transport.WrittenFrames[0]!)!;
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Null(response["error"]);
    }

    private static string CreateLegacyDbWithoutIndexedAt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_legacy_{Guid.NewGuid():N}.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL UNIQUE,
                    lang TEXT,
                    size INTEGER,
                    lines INTEGER,
                    modified DATETIME
                );
                CREATE TABLE symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id INTEGER NOT NULL,
                    name TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO files (path, lang, size, lines, modified)
                VALUES ('src/legacy.cs', 'csharp', 42, 3, '2026-01-01T00:00:00Z');
                """;
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private static string CreateSqlGraphContractFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/target.sql",
            "sql",
            """
            CREATE FUNCTION dbo.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END;
            GO
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/caller.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                SELECT dbo.fn_Target();
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateMixedSqlGraphContractFixtureDb(string projectRoot)
    {
        var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/mixed.cs",
            "csharp",
            """
            public class MixedCalls
            {
                public void N() { }

                public void M()
                {
                    N();
                }
            }
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateSqlGraphContractZeroResultFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/a.cs",
            "csharp",
            """
            public class C
            {
                public void M() { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/b.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.Target
            AS
            BEGIN
                SELECT 1;
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static void DowngradeSqlGraphContractRows(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE symbol_references
            SET symbol_name = 'fn_Target',
                symbol_name_folded = 'fn_target',
                column_number = 1
            WHERE symbol_name = 'dbo.fn_Target';
            DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
            """;
        cmd.ExecuteNonQuery();
    }

    private static void DowngradeSqlGraphContractVersion(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _server.Dispose();
        _db.Dispose();
        DeleteDbPath();
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }

    private void DeleteDbPath()
    {
        DeleteFileRobust(_dbPath);
    }

    private static void DeleteFileRobust(string path)
    {
        SqliteConnection.ClearAllPools();
        TestProjectHelper.DeleteFile(path);
    }

    private void DropGraphExactFallbackIndexes()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbol_refs_name_nocase;
            DROP INDEX IF EXISTS idx_symbol_refs_container_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void DropSymbolExactFallbackIndex()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbols_name_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void ForceLegacyExactFallbackMode()
    {
        using var db = new DbContext(_dbPath);
        db.ClearReadyFlags();
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
    }

    // --- Rate limiter (issue #1560) / レート制限器（#1560） ---

    private sealed class FixedClock
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    private static FixedClock InstallRateLimiter(McpServer server, RateLimiterOptions options)
    {
        var clock = new FixedClock();
        server.OverrideRateLimiterForTests(new RateLimiter(options, clock.Read));
        return clock;
    }













    // --- Structured error envelope (#1581) / 構造化エラー envelope（#1581） ---

    private static void AssertEnvelope(JsonNode? data, string expectedCategory, bool expectedRetrySafe)
    {
        Assert.NotNull(data);
        Assert.Equal(expectedCategory, data!["category"]!.GetValue<string>());
        Assert.Equal(expectedRetrySafe, data["retry_safe"]!.GetValue<bool>());
        var suggestion = data["suggestion"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(suggestion));
    }

    [Fact]
    public void ErrorResponse_InvalidRequest_NotAnObject_CarriesEnvelope()
    {
        // #1581: every JSON-RPC error response must carry `data.{category, suggestion, retry_safe}`.
        // Sending a non-object JSON value (here, an array) trips the `-32600` branch. Clients
        // should be able to branch on `data.category == "invalid_request"` instead of the
        // free-text `message`.
        // #1581: すべての JSON-RPC エラー応答は `data.{category, suggestion, retry_safe}` を
        // 含む。`-32600` 経路（非オブジェクト）でも canonical envelope を返す。
        var response = _server.HandleMessage(JsonNode.Parse("[]")!)!;
        var error = response["error"]!;
        Assert.Equal(-32600, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "invalid_request", expectedRetrySafe: false);
    }

    [Fact]
    public void ErrorResponse_MethodNotFound_CarriesEnvelope()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"no/such/method"}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32601, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "method_not_found", expectedRetrySafe: false);
    }

    [Fact]
    public void ErrorResponse_MissingToolName_CarriesEnvelope()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "missing_parameter", expectedRetrySafe: false);
    }

    [Fact]
    public void ErrorResponse_UnknownTool_CarriesEnvelopeAndToolName()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"does_not_exist"}}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "tool_unknown", expectedRetrySafe: false);
        Assert.Equal("does_not_exist", error["data"]!["tool"]!.GetValue<string>());
    }

    [Fact]
    public void ErrorResponse_ToolDisabled_CarriesEnvelope()
    {
        // Tool-disabled (#1561) keeps the -32601 wire code but adds the #1581 envelope so
        // clients can distinguish operator-disabled tools from typos (tool_unknown).
        // tool_disabled（#1561）はワイヤコード -32601 を維持しつつ envelope を併載し、
        // typo（tool_unknown）と区別できるようにする。
        Environment.SetEnvironmentVariable("CDIDX_MCP_TOOLS_DENY", "status");
        try
        {
            using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
            var response = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;
            var error = response["error"]!;
            Assert.Equal(-32601, error["code"]!.GetValue<int>());
            AssertEnvelope(error["data"], "tool_disabled", expectedRetrySafe: false);
            Assert.Equal("status", error["data"]!["tool"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_MCP_TOOLS_DENY", null);
        }
    }

    [Fact]
    public void ErrorResponse_Unauthorized_CarriesEnvelope()
    {
        // Auth failures use server code -32001 with `permission_denied`; the wire message
        // stays generic so a token-protected server does not leak internals to unauth callers.
        // 認証失敗はサーバーコード -32001 と permission_denied で返し、生メッセージは汎用に保つ。
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false,
            new TokenMcpAuthenticator("secret-token"));
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32001, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "permission_denied", expectedRetrySafe: false);
    }

    [Fact]
    public void RateLimited_Error_AlsoCarriesCanonicalEnvelope()
    {
        // The pre-existing #1560 fields (`error_category`, `tool`, `caller`, `retry_after_ms`)
        // stay intact for backward compatibility; #1581 adds `category`, `suggestion`, and
        // `retry_safe` (true — back off and retry) under the same `error.data` object.
        // #1560 既存フィールドは維持しつつ、#1581 の canonical envelope を併載する。
        // rate_limited は retry_safe=true（バックオフして再試行）。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });
        var initialize = JsonNode.Parse("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"c","version":"1"}}}""")!;
        _server.HandleMessage(initialize);
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!);
        var throttled = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var data = throttled["error"]!["data"]!;
        // Legacy fields preserved / 既存フィールド維持
        Assert.Equal("rate_limited", data["error_category"]!.GetValue<string>());
        Assert.Equal("status", data["tool"]!.GetValue<string>());
        // Canonical envelope added / canonical envelope を併載
        AssertEnvelope(data, "rate_limited", expectedRetrySafe: true);
    }

    [Fact]
    public void RateLimited_ErrorAndLog_TruncatesCallerIdentity_Issue3120()
    {
        var caller = new string('r', McpBoundedText.MaxClientIdentityChars + 25);
        var display = McpBoundedText.ForDisplay(caller, McpBoundedText.MaxClientIdentityChars);

        var response = McpServer.CreateRateLimitedErrorResponse(null, "status", caller, retryAfterMs: 123);
        var log = McpServer.BuildRateLimitedLog("status", caller, retryAfterMs: 123);

        Assert.DoesNotContain(caller, response.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(caller, log, StringComparison.Ordinal);
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["caller"]!.GetValue<string>());
        Assert.Equal(caller.Length, data["caller_length"]!.GetValue<int>());
        Assert.True(data["caller_truncated"]!.GetValue<bool>());
        Assert.Contains(display.Text, log);
    }

    [Fact]
    public void RateLimited_ErrorAndLog_TruncatesToolName_Issue3118()
    {
        var tool = new string('t', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(tool, McpBoundedText.MaxToolNameChars);

        var response = McpServer.CreateRateLimitedErrorResponse(null, tool, "client", retryAfterMs: 123);
        var log = McpServer.BuildRateLimitedLog(tool, "client", retryAfterMs: 123);

        Assert.DoesNotContain(tool, response.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(tool, log, StringComparison.Ordinal);
        Assert.Contains(display.Text, response["error"]!["message"]!.GetValue<string>());
        Assert.Contains(display.Text, log);
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["tool"]!.GetValue<string>());
        Assert.Equal(tool.Length, data["tool_length"]!.GetValue<int>());
        Assert.True(data["tool_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolResult_DatabaseMissing_CarriesEnvelopeOnStructuredContent()
    {
        // Tool-result errors (MCP isError shape) mirror the JSON-RPC envelope by exposing
        // `category` / `suggestion` / `retry_safe` under `result.structuredContent`. The
        // `index_missing` category is retry_safe so clients can rebuild and retry.
        // ツール結果エラー（MCP isError 形式）も `result.structuredContent` に envelope を載せる。
        // index_missing は retry_safe=true（rebuild 後に再試行可能）。
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_test_missing_{Guid.NewGuid():N}.db");
        using var server = new McpServer(missingDb, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        Assert.Null(response["error"]);
        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        AssertEnvelope(result["structuredContent"], "index_missing", expectedRetrySafe: true);
    }




    [Fact]
    public void ErrorResponse_ProcessFrame_ParseError_CarriesEnvelope()
    {
        // ProcessFrame returns a serialized response string; parse it back to inspect the
        // envelope. Parse-error responses have `id: null` per JSON-RPC spec.
        // ProcessFrame は文字列を返すので、パースして envelope を検査する。Parse error は
        // 仕様により `id: null`。
        var raw = _server.ProcessFrame("not a json frame");
        Assert.NotNull(raw);
        var response = JsonNode.Parse(raw!)!;
        var error = response["error"]!;
        Assert.Equal(-32700, error["code"]!.GetValue<int>());
        AssertJsonNullId(response);
        AssertEnvelope(error["data"], "parse_error", expectedRetrySafe: false);
    }

    [Fact]
    public void ProcessFrame_OversizedUtf8Bytes_ReturnsParseErrorWithNullId()
    {
        var multibyte = new string('\u3042', (McpServer.MaxLineByteLength / 3) + 1);
        var raw = _server.ProcessFrame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"q\":\"" + multibyte + "\"}}");

        var response = JsonNode.Parse(raw!)!;
        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("message_too_large", response["error"]!["data"]!["category"]!.GetValue<string>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void ProcessFrame_OversizedStringRequestId_ReturnsInvalidRequestWithNullId_Issue3104()
    {
        var oversizedId = new string('x', McpServer.MaxRequestIdCharacterCount + 1);
        var raw = _server.ProcessFrame("{\"jsonrpc\":\"2.0\",\"id\":\"" + oversizedId + "\",\"method\":\"tools/list\"}");

        Assert.NotNull(raw);
        Assert.DoesNotContain(oversizedId, raw, StringComparison.Ordinal);
        var response = JsonNode.Parse(raw!)!;
        var error = response["error"]!;
        Assert.Equal(-32600, error["code"]!.GetValue<int>());
        Assert.Equal("invalid_request", error["data"]!["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxRequestIdCharacterCount, error["data"]!["max_request_id_chars"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxRequestIdByteLength, error["data"]!["max_request_id_bytes"]!.GetValue<int>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void ProcessFrame_OversizedNumericRequestId_ReturnsInvalidRequestWithNullId_Issue3104()
    {
        var oversizedId = new string('9', McpServer.MaxRequestIdCharacterCount + 1);
        var raw = _server.ProcessFrame("{\"jsonrpc\":\"2.0\",\"id\":" + oversizedId + ",\"method\":\"tools/list\"}");

        Assert.NotNull(raw);
        Assert.DoesNotContain(oversizedId, raw, StringComparison.Ordinal);
        var response = JsonNode.Parse(raw!)!;
        var error = response["error"]!;
        Assert.Equal(-32600, error["code"]!.GetValue<int>());
        Assert.Equal("invalid_request", error["data"]!["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxRequestIdCharacterCount, error["data"]!["max_request_id_chars"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxRequestIdByteLength, error["data"]!["max_request_id_bytes"]!.GetValue<int>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void ProcessFrame_TooDeepJson_ReturnsParseErrorWithNullId()
    {
        var raw = _server.ProcessFrame(new string('[', McpServer.MaxJsonDepth + 2) + "0" + new string(']', McpServer.MaxJsonDepth + 2));

        var response = JsonNode.Parse(raw!)!;
        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("parse_error", response["error"]!["data"]!["category"]!.GetValue<string>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void IsServerResponseFrame_TooDeepResponse_ReturnsFalse_Issue3012()
    {
        Assert.True(InvokeIsServerResponseFrame("""{"jsonrpc":"2.0","id":1,"result":{}}"""));

        var frame = BuildNestedJsonRpcResponse(McpServer.MaxJsonDepth + 1);

        Assert.False(InvokeIsServerResponseFrame(frame));
    }








    private static void AssertJsonNullId(JsonNode node)
    {
        var obj = Assert.IsType<JsonObject>(node);
        Assert.True(obj.ContainsKey("id"));
        Assert.Null(obj["id"]);
    }

    private static bool InvokeIsServerResponseFrame(string frame)
    {
        var method = typeof(McpServer).GetMethod("IsServerResponseFrame", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [frame])!;
    }

    private static string BuildNestedJsonRpcResponse(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"jsonrpc":"2.0","id":1,"result":""");
        AppendNestedObject(builder, nestedObjectCount);
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendNestedObject(StringBuilder builder, int nestedObjectCount)
    {
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append("""{"next":""");

        builder.Append('0');

        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append('}');
    }

    private static void WriteOversizedAsciiFile(string path)
    {
        const int targetBytes = 10 * 1024 * 1024 + 1;
        var chunk = new byte[8192];
        Array.Fill(chunk, (byte)'a');

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        int written = 0;
        while (written < targetBytes)
        {
            var toWrite = Math.Min(chunk.Length, targetBytes - written);
            stream.Write(chunk, 0, toWrite);
            written += toWrite;
        }
    }

    // Issue #1573: the MCP loop used to block on stdin until EOF with no signal-driven exit
    // path, so SIGINT/SIGTERM left the process hung. The fix wires CancellationToken through to
    // the transport's ReadFrameAsync; this test pins that contract by tripping the token while
    // ReadFrameAsync is blocked and asserting the loop drains cleanly and disposes the transport.
    // #1573: 旧実装は stdin EOF まで固まり、SIGINT/SIGTERM で吊り下がっていた。修正で
    // CancellationToken が ReadFrameAsync まで届くようになったため、ブロック中にトークンを
    // トリップしたときループが正常終了し transport が dispose されることを固定するテスト。
    [Fact]
    public async Task RunAsync_CancellationDrainsLoopAndDisposesTransport()
    {
        var transport = new CancellableFakeTransport();
        using var cts = new CancellationTokenSource();

        var runTask = _server.RunAsync(transport, cts.Token);

        await transport.WaitForReadAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        // The loop must observe cancellation and return on its own — without the fix this awaits
        // forever because ReadLineAsync ignored the (then-non-existent) token.
        // 修正前は ReadLineAsync が token を見ていないため永遠にブロックした。完了することを確認。
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runTask.IsCompletedSuccessfully, "RunAsync should exit cleanly when its CancellationToken is cancelled.");
        Assert.True(transport.ReadCalls >= 1);
    }

    // Cancellation must also unblock readers that were already pending before the signal arrived
    // and stop the loop without producing a spurious WriteFrameAsync call (which would otherwise
    // be observable as a half-completed request on the wire).
    // 信号到着前から待機中の reader も解除し、ループが余分な WriteFrameAsync を出さずに終了することを確認。
    [Fact]
    public async Task RunAsync_CancelledBeforeAnyResponseDoesNotWriteFrame()
    {
        var transport = new CancellableFakeTransport();
        using var cts = new CancellationTokenSource();

        var runTask = _server.RunAsync(transport, cts.Token);
        await transport.WaitForReadAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, transport.WriteCalls);
    }

    [Fact]
    public async Task DrainInFlightTasksAsync_CancelledTokenSkipsEofDelay_Issue3400()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stopwatch = Stopwatch.StartNew();

        await _server.DrainInFlightTasksAsync(
            [pending.Task],
            McpServer.DefaultEofDrainTimeout,
            McpServer.DefaultEofPostCancelDrainTimeout,
            cts.Token);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"EOF drain cancellation took {stopwatch.Elapsed}.");
    }

    private sealed class QueuedFrameTransport : IMcpTransport
    {
        private readonly Queue<string?> _frames;

        public QueuedFrameTransport(params string[] frames)
        {
            _frames = new Queue<string?>(frames.Cast<string?>().Append(null));
        }

        public string Name => "stdio";
        public string Endpoint => "memory://queued";
        public List<string?> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
            => Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// In-memory IMcpTransport whose ReadFrameAsync blocks until the supplied CancellationToken
    /// trips. Records read/write counts so tests can assert the loop actually entered the read
    /// before cancellation arrived (and never wrote a response after).
    /// CancellationToken がトリップするまで ReadFrameAsync をブロックするインメモリ実装。
    /// ループが read に入ってからキャンセルが来たことと、その後 write が発生していないことを検証する。
    /// </summary>
    private sealed class CancellableFakeTransport : IMcpTransport
    {
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "fake";
        public string Endpoint => "memory://test";
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            ReadCalls++;
            _readEntered.TrySetResult();
            // Honour the token: a never-completing Task<string?> + token-driven cancellation
            // mirrors how stdio's ReadLineAsync(CancellationToken) behaves on SIGINT/SIGTERM.
            // token に従って待機。stdio の ReadLineAsync(CancellationToken) と同じ動作を再現する。
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }

        public Task WaitForReadAsync(TimeSpan timeout) => _readEntered.Task.WaitAsync(timeout);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueMcpTransport : IMcpTransport, IOutOfBandMcpTransport
    {
        private readonly Queue<string> _frames;

        public QueueMcpTransport(params string[] frames)
        {
            _frames = new Queue<string>(frames);
        }

        public string Name => "memory";
        public string Endpoint => "memory://test";
        public List<string> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
            => Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            if (frame is not null)
                WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public Task WriteOutOfBandFrameAsync(string frame, CancellationToken cancellationToken)
        {
            WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The shutdown helper is the heart of the #1573 fix: cancelling the CTS through Console.CancelKeyPress
    // (and PosixSignal.SIGTERM on Unix) must trip the loop. This test exercises the cross-platform
    // Ctrl+C path by raising the .NET CancelKeyPress event directly via reflection — the test cannot
    // send real signals to the test process without crashing the xUnit runner.
    // #1573 の中核。Console.CancelKeyPress 経由 (Unix では PosixSignal.SIGTERM 経由) でループを
    // 止められることが要件。実信号は xUnit runner ごと落とすため、リフレクションで CancelKeyPress
    // を直接発火させてクロスプラットフォーム経路を検証する。
    [Fact]
    public void RegisterShutdownHandlers_ConsoleCancelKeyPress_CancelsToken()
    {
        using var cts = new CancellationTokenSource();
        using var registration = McpServer.RegisterShutdownHandlers(cts);

        Assert.False(cts.IsCancellationRequested);
        RaiseConsoleCancelKeyPress();
        Assert.True(cts.IsCancellationRequested);
    }

    // After the registration is disposed, a subsequent Ctrl+C must not touch a stale CTS — the
    // typical RunMcpHttp shape disposes the CTS right after the registration, so a late signal
    // would otherwise hit ObjectDisposedException and crash the host.
    // registration を dispose した後の Ctrl+C は使用済み CTS に触れてはならない。RunMcpHttp は
    // registration の直後に CTS を dispose するため、late signal で ObjectDisposedException で
    // host が落ちないことを担保する。
    [Fact]
    public void RegisterShutdownHandlers_AfterDispose_DoesNotInvokeHandler()
    {
        using var cts = new CancellationTokenSource();
        var registration = McpServer.RegisterShutdownHandlers(cts);
        registration.Dispose();

        RaiseConsoleCancelKeyPress();

        Assert.False(cts.IsCancellationRequested);
    }

    private static JsonObject BuildRequiredPathArguments(string toolName, string pathValue)
        => BuildRequiredPathArguments(toolName, JsonValue.Create(pathValue)!);

    private static JsonObject BuildRequiredPathArguments(string toolName, JsonNode pathValue)
    {
        var arguments = new JsonObject
        {
            ["path"] = pathValue,
        };
        if (toolName == "excerpt")
            arguments["startLine"] = 1;
        return arguments;
    }

    private string CallToolAndReadErrorMessage(string toolName, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            }
        };
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        return response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void RaiseConsoleCancelKeyPress()
    {
        // Console.CancelKeyPress is exposed as a public event but its backing delegate field is
        // private. Reflection is the only test-time path; .NET intentionally does not let user
        // code synthesise ConsoleCancelEventArgs (its constructor is internal). We construct the
        // args via the same internal ctor the runtime uses for real Ctrl+C events. The backing
        // field is null when no handlers are attached — that itself proves the handler was
        // removed, so callers must not assume a non-null delegate after dispose.
        // Console.CancelKeyPress は public event だが backing field は private で、
        // ConsoleCancelEventArgs の ctor も internal。reflection が唯一のテスト経路。
        // ハンドラ未登録の状態ではフィールドは null になり、それ自体が解除済みの証拠なので、
        // 呼び出し側は dispose 後に non-null を仮定してはならない。
        var field = typeof(Console).GetField("s_cancelCallbacks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var del = (ConsoleCancelEventHandler?)field!.GetValue(null);
        if (del == null)
            return;
        var argsType = typeof(ConsoleCancelEventArgs);
        var argsCtor = argsType.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, binder: null, types: new[] { typeof(ConsoleSpecialKey) }, modifiers: null);
        Assert.NotNull(argsCtor);
        var args = (ConsoleCancelEventArgs)argsCtor!.Invoke(new object[] { ConsoleSpecialKey.ControlC });
        del!.Invoke(null!, args);
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(char value) =>
            throw new IOException("pipe closed");

        public override Task WriteAsync(string? value) =>
            throw new IOException("pipe closed");

        public override Task WriteLineAsync(string? value) =>
            throw new IOException("pipe closed");
    }

    private sealed class AssertingTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly Action _beforeWrite;

        public AssertingTextWriter(TextWriter inner, Action beforeWrite)
        {
            _inner = inner;
            _beforeWrite = beforeWrite;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override async Task WriteAsync(string? value)
        {
            _beforeWrite();
            await _inner.WriteAsync(value).ConfigureAwait(false);
        }

        public override async Task WriteAsync(char value)
        {
            _beforeWrite();
            await _inner.WriteAsync(value).ConfigureAwait(false);
        }

        public override Task FlushAsync() => _inner.FlushAsync();
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
