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

public partial class McpServerTests
{
    [Fact]
    public void BuildUnsupportedProtocolMessage_MentionsRequestedAndSupported()
    {
        var msg = McpServer.BuildUnsupportedProtocolMessage("2099-01-01");
        Assert.Contains("2099-01-01", msg);
        foreach (var supported in McpServer.SupportedProtocolVersions)
            Assert.Contains(supported, msg);
    }

    [Fact]
    public void BuildUnsupportedProtocolLog_IsActionable()
    {
        var log = McpServer.BuildUnsupportedProtocolLog("2099-01-01");
        Assert.Contains("Rejecting initialize", log);
        Assert.Contains("2099-01-01", log);
        Assert.Contains("Upgrade the server or pin a supported version", log);
    }

    [Fact]
    public void BuildAuthFailureLog_IsActionable()
    {
        // The stderr log keeps the actionable detail (method, reason, recovery hint) so an
        // operator can diagnose without combing through the wire transcript. The wire stays
        // sanitized per #1530.
        // stderr ログには診断用詳細 (method/reason/復旧ヒント) を残す。ワイヤ応答は #1530
        // 方針でサニタイズしたまま保つ。
        var log = McpServer.BuildAuthFailureLog("tools/call", "missing auth token");

        Assert.Contains("Auth failed", log);
        Assert.Contains("tools/call", log);
        Assert.Contains("missing auth token", log);
        Assert.Contains("CDIDX_MCP_AUTH_TOKEN", log);
        Assert.Contains("params.auth.token", log);
    }

    [Fact]
    public void BuildAuthFailureLog_SanitizesControlCharsInMethod()
    {
        // The stderr log interpolates the caller-controlled `method`; if we don't strip
        // control characters, an attacker can send `"method":"evil\n[forged]"` and split
        // the diagnostic across two lines (log forging). Sanitization replaces \n/\r/etc.
        // with `?` and clamps method length so a single auth failure never spans lines.
        // stderr ログには caller 由来の `method` が埋め込まれる。制御文字を除去しないと
        // `"method":"evil\n[forged]"` で 1 件のログを 2 行に分割されてしまう（ログ偽造）。
        // 制御文字を `?` に置換し、長さも切り詰める。
        var log = McpServer.BuildAuthFailureLog("evil\n[forged]\rfoo\t", "missing auth token");

        Assert.DoesNotContain('\n', log);
        Assert.DoesNotContain('\r', log);
        Assert.DoesNotContain('\t', log);
        Assert.Contains("evil?[forged]?foo?", log);
        Assert.Contains("missing auth token", log);
    }

    [Fact]
    public void BuildAuthFailureLog_ClampsLongMethod()
    {
        // The log clamps method to a fixed cap to keep a single auth-failure line readable
        // and to bound the cost of stderr writes when a hostile client sends a giant method.
        // method を一定長に切り詰めることでログ行を読みやすく保ち、巨大 method による
        // stderr 書き込みコストも抑える。
        var huge = new string('A', 5000);

        var log = McpServer.BuildAuthFailureLog(huge, "missing auth token");

        Assert.DoesNotContain(new string('A', 5000), log);
        Assert.Contains("…", log);
    }

    [Fact]
    public void BuildAuthFailureLog_NullMethod_LabeledNone()
    {
        // After the safe method-extraction change, `method` may be null when the request
        // omits it or sets it to a non-string. The log must still be readable rather than
        // showing a literal "null".
        // 安全な method 抽出により method が null になり得る（欠落 or 非文字列）。ログは
        // 読みやすい表記にしておき、リテラル "null" を出さない。
        var log = McpServer.BuildAuthFailureLog(null, "missing auth token");

        Assert.Contains("Auth failed for method (none)", log);
    }

    [Fact]
    public void BuildOversizedMessageLog_IsActionable()
    {
        var message = McpServer.BuildOversizedMessageLog(1_234_567, 1_500_000);

        Assert.Contains("Message too large", message);
        Assert.Contains("chars", message);
        Assert.Contains("bytes", message);
        Assert.Contains("Split the request into smaller JSON-RPC messages", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildJsonParseErrorLog_IsActionable()
    {
        var message = McpServer.BuildJsonParseErrorLog("Expected ':'");

        Assert.Contains("JSON parse error", message);
        Assert.Contains("Send one UTF-8 JSON-RPC object per line", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildJsonParseErrorLog_BoundsDiagnosticDetail_Issue3711()
    {
        var longDetail = "Expected ':' " + new string('x', JsonFrameParser.MaxParseDiagnosticChars + 100);

        var message = McpServer.BuildJsonParseErrorLog(longDetail);

        Assert.Contains("JSON parse error", message);
        Assert.Contains("<truncated; original length", message);
        Assert.DoesNotContain(new string('x', JsonFrameParser.MaxParseDiagnosticChars + 1), message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInvalidUtf8ErrorLog_IsActionable()
    {
        var message = McpServer.BuildInvalidUtf8ErrorLog("invalid byte sequence");

        Assert.Contains("invalid UTF-8", message);
        Assert.Contains("Send one UTF-8 JSON-RPC object per line", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildResponseSerializationErrorLog_IdentifiesResponseSerializationStage()
    {
        var message = McpServer.BuildResponseSerializationErrorLog("serializer failed");

        Assert.Contains("Error serializing response", message);
        Assert.Contains("serializer failed", message);
        Assert.Contains("minimal JSON-RPC error response", message);
    }

    [Fact]
    public void BuildResponseWriteErrorLog_IdentifiesResponseWriteStage()
    {
        var message = McpServer.BuildResponseWriteErrorLog("pipe closed");

        Assert.Contains("Error writing response", message);
        Assert.Contains("pipe closed", message);
        Assert.Contains("client connection", message);
    }

    [Fact]
    public void BuildToolErrorLog_IsActionable()
    {
        var message = McpServer.BuildToolErrorLog("search", new InvalidOperationException("bad db"));

        Assert.Contains("Tool error (search): InvalidOperationException", message);
        Assert.Contains("Fix the tool arguments", message);
        Assert.Contains("refresh the index if needed", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_OmitsExceptionMessage()
    {
        // Issue #1530: the JSON-RPC tool result must not echo `ex.Message`,
        // because SQLite errors and other content-bearing exceptions can quote
        // bound parameter values or matched text. Only the tool name and
        // exception type should reach the wire; full detail stays in stderr.
        var ex = new InvalidOperationException("near 'SECRET_LITERAL': syntax error");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            message = McpServer.BuildSanitizedToolErrorMessage("search", ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Equal("Tool 'search' failed. See cdidx server stderr for details.", message);
        Assert.Contains("server stderr", message);
        Assert.DoesNotContain("SECRET_LITERAL", message);
        Assert.DoesNotContain("syntax error", message);
        Assert.DoesNotContain(nameof(InvalidOperationException), message);
    }

    [Fact]
    public void BuildSanitizedLoopErrorMessage_OmitsExceptionMessage()
    {
        // Same protection as BuildSanitizedToolErrorMessage but for the
        // outer JSON-RPC loop catch-all (#1530).
        var ex = new InvalidOperationException("PRAGMA failed: secret table 'leaky_table' missing");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            message = McpServer.BuildSanitizedLoopErrorMessage(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Equal("Internal MCP error. See cdidx server stderr for details.", message);
        Assert.Contains("server stderr", message);
        Assert.DoesNotContain("leaky_table", message);
        Assert.DoesNotContain("PRAGMA failed", message);
        Assert.DoesNotContain(nameof(InvalidOperationException), message);
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_UnsafeDebugIncludesExceptionType()
    {
        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            var ex = new InvalidOperationException("near 'SECRET_LITERAL': syntax error");

            var message = McpServer.BuildSanitizedToolErrorMessage("search", ex);

            Assert.Contains("Error executing search", message);
            Assert.Contains(nameof(InvalidOperationException), message);
            Assert.DoesNotContain("SECRET_LITERAL", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_CodeIndexException_EchoesStructuredFields()
    {
        // Issue #1580: CodeIndexException carries author-controlled Code / Category /
        // Path / Hint values, so the MCP catch-all must surface them so clients can
        // branch on Code without parsing free-form messages. The free-form `Message`
        // text built by CodeIndexException itself (which already includes the path
        // suffix) still must not be echoed verbatim, to keep #1530 closed for the
        // database message body.
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection.",
            path: "/var/cdidx/state.db",
            hint: "Close other cdidx invocations.");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            message = McpServer.BuildSanitizedToolErrorMessage("search", ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Contains("Error executing search", message);
        Assert.Contains(nameof(CodeIndexException), message);
        Assert.Contains("[E002_DB_LOCKED/database]", message);
        Assert.Contains("path='/var/cdidx/state.db'", message);
        Assert.Contains("hint='Close other cdidx invocations.'", message);
        Assert.Contains("server stderr", message);
    }

    [Fact]
    public void BuildSanitizedLoopErrorMessage_CodeIndexException_EchoesStructuredFields()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection.",
            path: "/var/cdidx/state.db",
            hint: "Close other cdidx invocations.");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            message = McpServer.BuildSanitizedLoopErrorMessage(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Contains("Internal error", message);
        Assert.Contains(nameof(CodeIndexException), message);
        Assert.Contains("[E002_DB_LOCKED/database]", message);
        Assert.Contains("path='/var/cdidx/state.db'", message);
        Assert.Contains("hint='Close other cdidx invocations.'", message);
        Assert.Contains("server stderr", message);
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_CodeIndexException_NoPathNoHint_OmitsFragments()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: "Generic failure.");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            message = McpServer.BuildSanitizedToolErrorMessage("status", ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Contains("[E008_DB_ERROR/database]", message);
        Assert.DoesNotContain("path=", message);
        Assert.DoesNotContain("hint=", message);
    }

    [Fact]
    public void BuildSanitizedIndexFileFailureMessage_OmitsRawExceptionMessage_Issue3202()
    {
        var message = McpServer.BuildSanitizedIndexFileFailureMessageForTesting(
            "index_file",
            nameof(InvalidOperationException),
            out var truncated);

        Assert.Equal("File indexing failed during index_file (InvalidOperationException). See cdidx server stderr for details.", message);
        Assert.False(truncated);
        Assert.DoesNotContain("SECRET_LITERAL", message);
        Assert.DoesNotContain("/private/path", message);
    }

    [Fact]
    public void BoundedJsonUtf8Stream_RejectsUnsupportedReadAndSeekOperations_Issue3681()
    {
        using var stream = new BoundedJsonUtf8Stream(16, captureSerialized: true, bytes => new InvalidOperationException(bytes.ToString()));

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Read([], 0, 0));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public void BoundedJsonUtf8Stream_CapturesPartialBytesBeforeLimitException_Issue3681()
    {
        using var stream = new BoundedJsonUtf8Stream(5, captureSerialized: true, bytes => new InvalidOperationException(bytes.ToString()));

        stream.Write(Encoding.UTF8.GetBytes("abc"));
        var ex = Assert.Throws<InvalidOperationException>(() => stream.Write(Encoding.UTF8.GetBytes("def")));

        Assert.Equal("6", ex.Message);
        Assert.Equal(6, stream.BytesWritten);
        Assert.Equal("abcde", stream.GetCapturedString());
    }

    [Fact]
    public void ClientResponsePayload_RejectsOversizedResultBeforeClone_Issue3098()
    {
        var payload = new JsonObject
        {
            ["value"] = new string('x', McpServer.MaxClientResponseJsonBytes + 1),
        };

        var withinLimit = _server.TryCloneClientResponsePayloadForTests(payload, out var clone, out var bytesWritten);

        Assert.False(withinLimit);
        Assert.Null(clone);
        Assert.True(bytesWritten > McpServer.MaxClientResponseJsonBytes);
        Assert.True(bytesWritten < McpServer.MaxClientResponseJsonBytes + 100);
    }

    [Fact]
    public void ClientResponsePayload_RejectsOversizedErrorBeforeMessageMaterialization_Issue3098()
    {
        var oversized = new string('e', McpServer.MaxClientResponseJsonBytes + 1);
        var error = new JsonObject
        {
            ["code"] = -32000,
            ["message"] = oversized,
        };

        var withinLimit = _server.TrySerializeClientResponseErrorForTests(error, out var serialized, out var bytesWritten);
        var log = McpServer.BuildClientResponseTooLargeLog("error", bytesWritten);

        Assert.False(withinLimit);
        Assert.Null(serialized);
        Assert.True(bytesWritten > McpServer.MaxClientResponseJsonBytes);
        Assert.True(bytesWritten < McpServer.MaxClientResponseJsonBytes + 100);
        Assert.DoesNotContain(oversized, log, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildToolErrorLog_SuppressesRawExceptionMessage_Issue3370()
    {
        const string secret = "SECRET_TOOL_LOG_3370";

        var log = McpServer.BuildToolErrorLog("search", new InvalidOperationException($"near '{secret}': syntax error"));

        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("syntax error", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMcpIndexExceptionDiagnostic_RedactsSkippedSizingPathsAndMessages_Issue3695()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_diag_root_{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "src", "Hidden.cs");
        var secret = "0123456789abcdef0123456789abcdef";
        var ex = new UnauthorizedAccessException($"denied {filePath} token={secret}");

        var diagnostic = McpServer.BuildMcpIndexExceptionDiagnosticForTesting(
            "file_size_bytes_skipped",
            "skipped_file_sizing",
            "measure_file_size",
            projectRoot,
            filePath,
            ex);

        Assert.Equal("file_size_bytes_skipped", diagnostic["code"]!.GetValue<string>());
        Assert.Equal("skipped_file_sizing", diagnostic["category"]!.GetValue<string>());
        Assert.Equal("src/Hidden.cs", diagnostic["path"]!.GetValue<string>());
        Assert.Equal("measure_file_size", diagnostic["stage"]!.GetValue<string>());
        Assert.Equal(nameof(UnauthorizedAccessException), diagnostic["exception_type"]!.GetValue<string>());
        var message = diagnostic["message"]!.GetValue<string>();
        Assert.DoesNotContain(projectRoot, message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        Assert.Contains("<redacted>", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCallerSwapRejectionLog_IsActionable()
    {
        var log = McpServer.BuildCallerSwapRejectionLog("first-client", "second-client");
        Assert.Contains("Ignoring re-initialize", log);
        Assert.Contains("first-client", log);
        Assert.Contains("second-client", log);
    }

    [Fact]
    public void BuildCallerSwapRejectionLog_TruncatesClientInfoIdentities_Issue3120()
    {
        var current = new string('c', McpBoundedText.MaxClientIdentityChars + 25);
        var attempted = new string('a', McpBoundedText.MaxClientIdentityChars + 25);
        var currentDisplay = McpBoundedText.ForDisplay(current, McpBoundedText.MaxClientIdentityChars);
        var attemptedDisplay = McpBoundedText.ForDisplay(attempted, McpBoundedText.MaxClientIdentityChars);

        var log = McpServer.BuildCallerSwapRejectionLog(current, attempted);

        Assert.DoesNotContain(current, log, StringComparison.Ordinal);
        Assert.DoesNotContain(attempted, log, StringComparison.Ordinal);
        Assert.Contains(currentDisplay.Text, log);
        Assert.Contains(attemptedDisplay.Text, log);
    }

    [Fact]
    public void BuildRateLimitedLog_IsActionable()
    {
        var log = McpServer.BuildRateLimitedLog("search", "client-a", 250);
        Assert.Contains("Rate limit exceeded", log);
        Assert.Contains("search", log);
        Assert.Contains("client-a", log);
        Assert.Contains("250", log);
        Assert.Contains("CDIDX_MCP_RATE_LIMIT_RPS", log);
    }

    [Fact]
    public void ClassifyException_MapsCancelledToRequestCancelled()
    {
        var c = McpErrorEnvelope.ClassifyException(new OperationCanceledException());
        Assert.Equal("request_cancelled", c.Category);
        Assert.True(c.RetrySafe);
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, c.JsonRpcCode);
    }

    [Fact]
    public void ClassifyException_MapsSqliteSchemaErrorsToIndexStale()
    {
        // SqliteException whose message names a missing table/column maps to `index_stale`
        // so clients know `cdidx index --rebuild` is the path to recovery (retry_safe=true).
        // テーブル / カラム不在を訴える SqliteException は index_stale にマッピングし、
        // rebuild で復旧可能（retry_safe=true）であることをクライアントに伝える。
        SqliteException sqlite;
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM no_such_table_xyz";
            cmd.ExecuteReader();
            throw new InvalidOperationException("expected SqliteException");
        }
        catch (SqliteException ex)
        {
            sqlite = ex;
        }

        var c = McpErrorEnvelope.ClassifyException(sqlite);
        Assert.Equal("index_stale", c.Category);
        Assert.True(c.RetrySafe);
        Assert.Equal(McpErrorEnvelope.CodeIndexStale, c.JsonRpcCode);
    }

    [Fact]
    public void ClassifyException_DefaultsToInternalError()
    {
        var c = McpErrorEnvelope.ClassifyException(new InvalidOperationException("boom"));
        Assert.Equal("internal_error", c.Category);
        Assert.False(c.RetrySafe);
        Assert.Equal(-32603, c.JsonRpcCode);
    }

    [Fact]
    public void BuildData_ExtraDataCannotShadowCanonicalKeys()
    {
        // Defense-in-depth: if a category-specific call passes `extraData` with the same key
        // names, the canonical contract still wins so clients always see a coherent envelope.
        // canonical キー（category / suggestion / retry_safe）は extraData で上書きできない。
        var extra = new JsonObject
        {
            ["category"] = "spoofed",
            ["suggestion"] = "spoofed",
            ["retry_safe"] = true,
            ["tool"] = "search",
        };
        var data = McpErrorEnvelope.BuildData("invalid_argument", "real suggestion", retrySafe: false, extra);
        Assert.Equal("invalid_argument", data["category"]!.GetValue<string>());
        Assert.Equal("real suggestion", data["suggestion"]!.GetValue<string>());
        Assert.False(data["retry_safe"]!.GetValue<bool>());
        Assert.Equal("search", data["tool"]!.GetValue<string>());
    }

    [Fact]
    public void BuildData_RegexTimeoutCarriesStructuredPayload_Issue3559()
    {
        var extra = new JsonObject
        {
            ["error_code"] = CommandErrorCodes.RegexMatchTimeout,
            ["timeout_ms"] = 500.0,
        };

        var data = McpErrorEnvelope.BuildData(
            McpErrorEnvelope.CategoryRegexTimeout,
            "Simplify the pattern.",
            retrySafe: true,
            extra);

        Assert.Equal(McpErrorEnvelope.CategoryRegexTimeout, data["category"]!.GetValue<string>());
        Assert.True(data["retry_safe"]!.GetValue<bool>());
        Assert.Equal(CommandErrorCodes.RegexMatchTimeout, data["error_code"]!.GetValue<string>());
        Assert.Equal(500.0, data["timeout_ms"]!.GetValue<double>());
    }
}
