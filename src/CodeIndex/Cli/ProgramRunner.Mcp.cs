using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    private sealed record McpRunOptions(
        QueryCommandOptions QueryOptions,
        string Transport,
        string? ListenSpec,
        bool AllowUnauthenticatedHttp,
        AuditLogOptions AuditOptions,
        IReadOnlyDictionary<string, string> EnvironmentOverrides);

    private static int RunMcp(string[] cmdArgs, string appVersion)
    {
        if (!TryPrepareMcpRun(cmdArgs, out var runOptions, out var exitCode))
            return exitCode;

        AuditLogSink? auditLog = null;
        using var mcpEnvironment = CdidxEnvironment.Push(runOptions.EnvironmentOverrides);
        if (!TryOpenMcpAuditLog(runOptions.AuditOptions, out auditLog, out exitCode))
            return exitCode;

        var auditFlushCompleted = true;
        try
        {
            // Pick the JSON-RPC authenticator for the selected transport. Stdio keeps the
            // historical `CDIDX_MCP_AUTH_TOKEN` / `params.auth.token` gate (#1559). HTTP uses
            // its bearer header gate instead, with `CDIDX_MCP_HTTP_TOKEN` taking precedence over
            // `CDIDX_MCP_AUTH_TOKEN` as a fallback (#3156), so clients never need both header and
            // body tokens for one HTTP request. The tool-enablement gate (#1561) is wired
            // automatically by the McpServer ctor via `McpToolFilter.FromEnvironment()`.
            // 選択済み transport に応じて JSON-RPC authenticator を選ぶ。stdio は従来通り
            // `CDIDX_MCP_AUTH_TOKEN` / `params.auth.token` ゲートを使う (#1559)。HTTP は bearer
            // header ゲートへ一本化し、`CDIDX_MCP_HTTP_TOKEN` を優先、未設定なら
            // `CDIDX_MCP_AUTH_TOKEN` を fallback として使う (#3156)。そのため HTTP では同一
            // リクエストに header token と body token の両方を要求しない。ツール有効化ゲート
            // (#1561) は McpServer のコンストラクタ内部で `McpToolFilter.FromEnvironment()`
            // から自動取得される。
            IMcpAuthenticator? authenticator = null;
            try
            {
                authenticator = CreateMcpAuthenticatorForTransport(runOptions.Transport);
            }
            catch (FormatException ex)
            {
                CommandErrorWriter.WriteStderr($"Error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
                PrintMcpUsage();
                exitCode = CommandExitCodes.UsageError;
            }

            if (authenticator is not null)
            {
                using var server = new McpServer(runOptions.QueryOptions.DbPath, appVersion, runOptions.QueryOptions.DbPathExplicit, authenticator, auditLog);
                exitCode = RunMcpServer(server, runOptions.Transport, runOptions.ListenSpec, runOptions.AllowUnauthenticatedHttp);
            }
        }
        finally
        {
            if (auditLog is not null)
            {
                var explicitShutdownCompleted = false;
                try
                {
                    auditFlushCompleted = auditLog.Shutdown().FlushCompleted;
                    explicitShutdownCompleted = true;
                }
                finally
                {
                    // Avoid a second bounded wait after a completed Shutdown call. Dispose
                    // remains the fallback only if explicit shutdown exits unexpectedly.
                    // 完了済み Shutdown の後に bounded wait を重ねない。明示 shutdown が
                    // 予期せず終了した場合だけ Dispose を fallback として使う。
                    if (!explicitShutdownCompleted)
                        auditLog.Dispose();
                }
            }
        }

        // RunDispatchedCommand emits the outer MCP command metric from this returned value,
        // so resolve strict shutdown only after the sink has reached its final state.
        // 外側 MCP command metric はこの戻り値を記録するため、sink の最終状態確定後に
        // strict shutdown の終了コードを解決する。
        return ResolveMcpAuditShutdownExitCode(exitCode, runOptions.AuditOptions.Strict, auditFlushCompleted);
    }

    internal static int ResolveMcpAuditShutdownExitCode(int serverExitCode, bool strict, bool flushCompleted)
        => strict && !flushCompleted && serverExitCode == CommandExitCodes.Success
            ? CommandExitCodes.RuntimeError
            : serverExitCode;

    private static bool TryPrepareMcpRun(string[] cmdArgs, out McpRunOptions runOptions, out int exitCode)
    {
        // Strip audit-log opt-in flags first so the strict mcp parser below does not see them
        // and raise an unknown-flag error. Keeps `--db` and `--` passthrough intact (#1562).
        // audit-log オプションフラグは厳格パーサに渡る前に除去し、未知フラグ扱いされるのを防ぐ (#1562)。
        runOptions = null!;
        exitCode = CommandExitCodes.Success;
        if (!TryConsumeAuditLogFlags(ref cmdArgs, out var auditOptions, out var auditError))
        {
            CommandErrorWriter.WriteStderr(auditError);
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (!TryConsumeSuggestionDedupThresholdFlag(ref cmdArgs, out var suggestionDedupThreshold, out var thresholdError))
        {
            CommandErrorWriter.WriteStderr(thresholdError);
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (!TryExtractMcpTransportFlags(
                cmdArgs,
                out var transportSpec,
                out var listenSpec,
                out var allowUnauthenticatedHttp,
                out var transportError))
        {
            CommandErrorWriter.WriteStderr(transportError);
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        // Strip the transport flags from the args before delegating to QueryCommandRunner.ParseArgs
        // and the unknown-flag guard below, both of which only understand the historic `--db` shape.
        // Transport フラグは ParseArgs / 未知フラグガードが知らないため、両者に渡す前に除去する。
        var residualArgs = RemoveMcpTransportFlags(cmdArgs);

        var options = QueryCommandRunner.ParseArgs(residualArgs, jsonDefault: true);
        if (options.ParseError != null)
        {
            CommandErrorWriter.WriteStderr(options.ParseError);
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (!TryValidateMcpResidualArgs(residualArgs, out exitCode))
            return false;

        if (!TryResolveMcpTransport(
                transportSpec,
                listenSpec,
                allowUnauthenticatedHttp,
                out var transport,
                out exitCode))
            return false;

        var environmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (suggestionDedupThreshold is not null)
            environmentOverrides[SuggestionStore.DedupThresholdEnvironmentVariable] = suggestionDedupThreshold;

        runOptions = new McpRunOptions(
            options,
            transport,
            listenSpec,
            allowUnauthenticatedHttp,
            auditOptions,
            environmentOverrides);
        return true;
    }

    private static bool TryValidateMcpResidualArgs(string[] residualArgs, out int exitCode)
    {
        for (var i = 0; i < residualArgs.Length; i++)
        {
            if (residualArgs[i].StartsWith("--db=", StringComparison.Ordinal))
                continue;

            if (residualArgs[i] == "--db")
            {
                i++;
                continue;
            }

            if (residualArgs[i] == "--json")
                CommandErrorWriter.WriteStderr("Error: --json is not supported for mcp; MCP already speaks JSON-RPC over the selected transport.");
            else
                CommandErrorWriter.WriteStderr($"Error: {residualArgs[i]} is not supported for mcp.");
            CommandErrorWriter.WriteStderr($"Hint: use `--db <path>` to point at a specific index, `--transport stdio|http` to pick a transport, `--http-listen host:port` for HTTP, `{AllowUnauthenticatedHttpFlag}` for explicit unsafe loopback operation, or `--audit-log <path>` to enable per-call auditing.");
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        exitCode = CommandExitCodes.Success;
        return true;
    }

    private static bool TryResolveMcpTransport(
        string? transportSpec,
        string? listenSpec,
        bool allowUnauthenticatedHttp,
        out string transport,
        out int exitCode)
    {
        transport = transportSpec ?? "stdio";
        if (!string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            CommandErrorWriter.WriteStderr($"Error: --transport '{transport}' is not supported. Use `stdio` (default) or `http`.");
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (listenSpec != null && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            CommandErrorWriter.WriteStderr("Error: --http-listen requires `--transport http`.");
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (allowUnauthenticatedHttp && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            CommandErrorWriter.WriteStderr($"Error: {AllowUnauthenticatedHttpFlag} requires `--transport http`.");
            PrintMcpUsage();
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        exitCode = CommandExitCodes.Success;
        return true;
    }

    private static bool TryOpenMcpAuditLog(AuditLogOptions auditOptions, out AuditLogSink? auditLog, out int exitCode)
    {
        auditLog = null;
        if (auditOptions.Path == null)
        {
            exitCode = CommandExitCodes.Success;
            return true;
        }

        try
        {
            auditLog = new AuditLogSink(auditOptions.Path, auditOptions.MaxBytes, auditOptions.IncludeValues);
            exitCode = CommandExitCodes.Success;
            return true;
        }
        catch (Exception ex) when (IsExpectedAuditLogOpenException(ex))
        {
            var displayPath = DiagnosticSanitizer.ForPath(auditOptions.Path);
            CommandErrorWriter.WriteStderr($"Error: failed to open audit log '{displayPath}' ({FormatSanitizedExceptionSummary(ex)}).");
            CommandErrorWriter.WriteStderr("Hint: pick a writable path or omit --audit-log to disable per-call auditing.");
            exitCode = CommandExitCodes.UsageError;
            return false;
        }
    }

    private static string FormatSanitizedExceptionSummary(Exception ex)
    {
        var exceptionType = CommandErrorWriter.FormatSanitizedException(ex);
        var message = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
        return string.IsNullOrEmpty(message) ? exceptionType : $"{exceptionType}: {message}";
    }

    private static bool IsExpectedAuditLogOpenException(Exception ex)
        => ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException;

    private static int RunMcpServer(
        McpServer server,
        string transport,
        string? listenSpec,
        bool allowUnauthenticatedHttp)
    {
        if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
            return RunMcpHttp(server, listenSpec ?? DefaultMcpHttpListen, allowUnauthenticatedHttp);

        try
        {
            server.RunAsync().GetAwaiter().GetResult();
            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            Console.Out.Flush();
            Console.Error.Flush();
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("mcp_server_failed " + GlobalToolLog.FormatExceptionChain(ex));
            CommandErrorWriter.WriteStderr($"Error: MCP server failed ({FormatSanitizedExceptionSummary(ex)}).");
            Console.Out.Flush();
            Console.Error.Flush();
            return CommandExitCodes.DatabaseError;
        }
    }

    internal static IMcpAuthenticator CreateMcpAuthenticatorForTransport(string transport)
        => string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
            ? LocalStdioAuthenticator.Instance
            : McpAuthenticatorFactory.FromEnvironment();

    internal static string? ResolveMcpHttpBearerTokenFromEnvironment()
    {
        var httpToken = McpEnvironment.GetOptionalToken(McpHttpTokenEnvVar);
        if (httpToken is not null)
            return httpToken;

        return McpEnvironment.GetOptionalToken(McpAuthenticatorFactory.AuthTokenEnvVar);
    }

    private static int RunMcpHttp(McpServer server, string listenSpec, bool allowUnauthenticatedHttp)
    {
        HttpMcpTransport.HttpListenSpec resolved;
        try
        {
            resolved = HttpMcpTransport.ResolveListenSpec(listenSpec);
        }
        catch (FormatException ex)
        {
            CommandErrorWriter.WriteStderr($"Error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        // Require a shared-secret bearer token for every HTTP listener by default. HTTP resolves that
        // bearer token from `CDIDX_MCP_HTTP_TOKEN` first, then falls back to the generic
        // `CDIDX_MCP_AUTH_TOKEN` so setting the generic auth token also protects HTTP without
        // forcing clients to send both `Authorization` and `params.auth.token` (#3156). Only the
        // explicit CLI opt-in permits an unauthenticated loopback listener; non-loopback binds
        // always require the token (#4549).
        // すべての HTTP listener で既定では共有秘密 bearer token を必須にする。HTTP はまず
        // `CDIDX_MCP_HTTP_TOKEN` を使い、未設定なら汎用の
        // `CDIDX_MCP_AUTH_TOKEN` を bearer token として使うため、汎用 token を設定しただけでも
        // HTTP は保護され、クライアントに `Authorization` と `params.auth.token` の両方を
        // 要求しない (#3156)。明示 CLI opt-in だけが unauthenticated loopback を許可し、
        // non-loopback bind は常に token を必須とする (#4549)。
        string? bearerToken;
        try
        {
            bearerToken = ResolveMcpHttpBearerTokenFromEnvironment();
        }
        catch (FormatException ex)
        {
            CommandErrorWriter.WriteStderr($"Error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        if (allowUnauthenticatedHttp && !resolved.IsLoopback)
        {
            CommandErrorWriter.WriteStderr($"Error: {AllowUnauthenticatedHttpFlag} is limited to loopback listeners; '{resolved.Host}' is not loopback.");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        if (bearerToken is null && !allowUnauthenticatedHttp)
        {
            CommandErrorWriter.WriteStderr($"Error: --transport http requires bearer authentication for '{resolved.Host}'. Set the `{McpHttpTokenEnvVar}` or `{McpAuthenticatorFactory.AuthTokenEnvVar}` environment variable. For explicitly unsafe loopback-only operation, pass {AllowUnauthenticatedHttpFlag}.");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        HttpMcpTransport transport;
        try
        {
            transport = new HttpMcpTransport(
                resolved.Prefix,
                resolved.Host,
                resolved.Port,
                bearerToken,
                requestLogger: LogHttpMcpRequest,
                allowUnauthenticatedLoopback: allowUnauthenticatedHttp);
        }
        catch (FormatException ex)
        {
            CommandErrorWriter.WriteStderr($"Error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            CommandErrorWriter.WriteStderr($"Error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }
        catch (HttpListenerException ex)
        {
            CommandErrorWriter.WriteStderr($"Error: {HttpMcpTransport.FormatBindFailureDiagnostic(resolved, ex)}");
            return CommandExitCodes.UsageError;
        }

        try
        {
            using var cts = new CancellationTokenSource();
            // Treat SIGINT (Ctrl+C) AND SIGTERM as graceful shutdown signals so orchestrators
            // (systemd, launchd, supervisord) can drain the listener and release the HTTP socket
            // instead of force-killing the process (#1573).
            // SIGINT (Ctrl+C) と SIGTERM を graceful shutdown として扱い、systemd / launchd /
            // supervisord が socket を解放して再起動できるようにする（#1573）。
            using (McpServer.RegisterShutdownHandlers(cts))
            {
                if (transport.AuthDisabledWarning is { } authWarning)
                {
                    CommandErrorWriter.WriteStderr($"[cdidx-mcp] Warning: {authWarning} Remove {AllowUnauthenticatedHttpFlag} and set `{McpHttpTokenEnvVar}` or `{McpAuthenticatorFactory.AuthTokenEnvVar}` to require bearer auth.");
                    CommandErrorWriter.WriteStderr($"[cdidx-mcp] HTTP transport listening on {resolved.Prefix} (loopback, explicit unsafe no-auth mode).");
                    GlobalToolLog.Info("mcp_http_auth_disabled_warning loopback=true");
                }
                else
                {
                    CommandErrorWriter.WriteStderr($"[cdidx-mcp] HTTP transport listening on {resolved.Prefix} (bearer auth required).");
                }
                CommandErrorWriter.WriteStderr(
                    $"[cdidx-mcp] HTTP request deadlines: body_idle_ms={transport.RequestBodyIdleTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}, total_ms={transport.RequestLifetimeTimeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}.");

                try
                {
                    server.RunAsync(transport, cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                    return CommandExitCodes.CancelledBySignal;
                }
                catch (Exception ex)
                {
                    GlobalToolLog.Error("mcp_http_server_failed " + GlobalToolLog.FormatExceptionChain(ex));
                    CommandErrorWriter.WriteStderr($"Error: MCP HTTP server failed ({FormatSanitizedExceptionSummary(ex)}).");
                    Console.Out.Flush();
                    Console.Error.Flush();
                    return CommandExitCodes.DatabaseError;
                }
            }
        }
        finally
        {
            DisposeMcpHttpTransport(transport);
        }

        return CommandExitCodes.Success;
    }

    private static void DisposeMcpHttpTransport(HttpMcpTransport transport)
    {
        try
        {
            var disposeTask = transport.DisposeAsync().AsTask();
            if (disposeTask.Wait(McpHttpDisposeTimeout))
                return;

            var message = $"MCP HTTP transport disposal did not finish within {FormatDuration(McpHttpDisposeTimeout)}.";
            GlobalToolLog.Error("mcp_http_transport_dispose_timeout " + message);
            CommandErrorWriter.WriteStderr("Warning: " + message);
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
            GlobalToolLog.Error("mcp_http_transport_dispose_failed " + GlobalToolLog.FormatExceptionChain(inner));
            CommandErrorWriter.WriteStderr($"Warning: MCP HTTP transport disposal failed ({FormatSanitizedExceptionSummary(inner)}).");
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("mcp_http_transport_dispose_failed " + GlobalToolLog.FormatExceptionChain(ex));
            CommandErrorWriter.WriteStderr($"Warning: MCP HTTP transport disposal failed ({FormatSanitizedExceptionSummary(ex)}).");
        }
    }

    private static void LogHttpMcpRequest(HttpMcpTransport.HttpRequestLogRecord record)
    {
        GlobalToolLog.Info(FormatHttpMcpRequestLogRecord(record));
    }

    internal static string FormatHttpMcpRequestLogRecord(HttpMcpTransport.HttpRequestLogRecord record)
        => "mcp_http_request"
            + $" correlation_id={record.CorrelationId}"
            + $" request_id={FormatLogValue(record.RequestId)}"
            + $" request_id_type={FormatLogValue(record.RequestIdType)}"
            + $" request_id_length={(record.RequestIdLength?.ToString(CultureInfo.InvariantCulture) ?? "-")}"
            + $" remote_peer={FormatLogValue(record.RemotePeer)}"
            + $" method={FormatLogValue(record.Method)}"
            + $" path={FormatLogValue(record.Path)}"
            + $" status={record.StatusCode.ToString(CultureInfo.InvariantCulture)}"
            + $" duration_ms={record.DurationMs.ToString("0.###", CultureInfo.InvariantCulture)}"
            + $" auth={FormatLogValue(record.AuthOutcome)}"
            + $" rejection={FormatLogValue(record.RejectionReason)}"
            + $" diagnostic={FormatLogValue(record.Diagnostic)}";

    private static string FormatLogValue(string? value)
    {
        var limited = HttpMcpTransport.LimitRequestLogField(value);
        if (string.IsNullOrEmpty(limited))
            return "-";

        return limited
            .Replace('\\', '/')
            .Replace('\r', '_')
            .Replace('\n', '_')
            .Replace('\t', '_')
            .Replace(' ', '_');
    }

    private static void PrintMcpUsage()
    {
        CommandErrorWriter.WriteStderr($"Usage: {ConsoleUi.GetUsageLine("mcp") ?? "cdidx mcp"}");
        CommandErrorWriter.WriteStderr("Note: --json is not supported; MCP requests and responses are JSON-RPC over the selected transport.");
        CommandErrorWriter.WriteStderr("stdio transport: one UTF-8 JSON-RPC object per LF-delimited line, not LSP Content-Length framing; lifecycle diagnostics are written to stderr.");
        CommandErrorWriter.WriteStderr($"HTTP security: bearer auth is required by default; {AllowUnauthenticatedHttpFlag} is an explicit unsafe loopback-only opt-in. Native clients omit Origin; POST requires UTF-8 application/json.");
        CommandErrorWriter.WriteStderr($"HTTP limits: {HttpMcpTransport.MaxRequestBodyBytesEnvVar}=<bytes> (1..{HttpMcpTransport.MaxConfiguredRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultMaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}), {HttpMcpTransport.MaxInFlightRequestBodyBytesEnvVar}=<bytes> (1..{HttpMcpTransport.MaxConfiguredInFlightRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultMaxInFlightRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}, must be >= {HttpMcpTransport.MaxRequestBodyBytesEnvVar}), {HttpMcpTransport.MaxResponseBodyBytesEnvVar}=<bytes> (1..{HttpMcpTransport.MaxConfiguredResponseBodyBytes.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultMaxResponseBodyBytes.ToString(CultureInfo.InvariantCulture)}), {HttpMcpTransport.MaxQueueDepthEnvVar}=<n> (1..{HttpMcpTransport.MaxConfiguredQueuedRequests.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultMaxQueuedRequests.ToString(CultureInfo.InvariantCulture)}), {HttpMcpTransport.MaxConcurrentHandlersEnvVar}=<n> (1..{HttpMcpTransport.MaxConfiguredConcurrentHandlers.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultMaxConcurrentHandlers.ToString(CultureInfo.InvariantCulture)}), {HttpMcpTransport.MaxEventStreamsEnvVar}=<n> (1..{HttpMcpTransport.MaxConfiguredEventStreams.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultMaxEventStreams.ToString(CultureInfo.InvariantCulture)}).");
        CommandErrorWriter.WriteStderr($"HTTP deadlines: {HttpMcpTransport.RequestBodyIdleTimeoutMillisecondsEnvVar}=<ms> (1..{HttpMcpTransport.MaxRequestBodyIdleTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultRequestBodyIdleTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}), {HttpMcpTransport.RequestLifetimeTimeoutMillisecondsEnvVar}=<ms> (1..{HttpMcpTransport.MaxRequestLifetimeTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}, default {HttpMcpTransport.DefaultRequestLifetimeTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}, must be >= {HttpMcpTransport.RequestBodyIdleTimeoutMillisecondsEnvVar}).");
        CommandErrorWriter.WriteStderr("Every present HTTP limit or deadline environment variable must be a positive integer in its displayed range; only an absent variable uses the default. POST handlers and SSE event streams use independent capacity gates.");
    }

    internal static bool TryConsumeSuggestionDedupThresholdFlag(ref string[] args, out string? thresholdValue, out string error)
    {
        thresholdValue = null;
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }

            string? value = null;
            if (arg == "--suggestion-dedup-threshold")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --suggestion-dedup-threshold requires a value between 0 and 1.";
                    return false;
                }
                value = args[++i];
            }
            else if (arg.StartsWith("--suggestion-dedup-threshold=", StringComparison.Ordinal))
            {
                value = arg.Substring("--suggestion-dedup-threshold=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold)
                || threshold < 0
                || threshold > 1)
            {
                error = "Error: --suggestion-dedup-threshold must be a value between 0 and 1.";
                return false;
            }

            thresholdValue = value;
        }

        args = kept.ToArray();
        return true;
    }

    internal static bool TryExtractMcpTransportFlags(
        string[] cmdArgs,
        out string? transport,
        out string? listen,
        out bool allowUnauthenticatedHttp,
        out string error)
    {
        transport = null;
        listen = null;
        allowUnauthenticatedHttp = false;
        error = string.Empty;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--transport")
            {
                if (i + 1 >= cmdArgs.Length)
                {
                    error = "Error: --transport requires a value (`stdio` or `http`).";
                    return false;
                }
                transport = cmdArgs[++i];
            }
            else if (arg.StartsWith("--transport=", StringComparison.Ordinal))
            {
                transport = arg.Substring("--transport=".Length);
            }
            else if (arg == "--http-listen")
            {
                if (i + 1 >= cmdArgs.Length)
                {
                    error = "Error: --http-listen requires a host:port value.";
                    return false;
                }
                listen = cmdArgs[++i];
            }
            else if (arg.StartsWith("--http-listen=", StringComparison.Ordinal))
            {
                listen = arg.Substring("--http-listen=".Length);
            }
            else if (arg == AllowUnauthenticatedHttpFlag)
            {
                allowUnauthenticatedHttp = true;
            }
        }
        return true;
    }

    private static string[] RemoveMcpTransportFlags(string[] cmdArgs)
    {
        var kept = new List<string>(cmdArgs.Length);
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--transport" || arg == "--http-listen")
            {
                if (i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }
            if (arg.StartsWith("--transport=", StringComparison.Ordinal)
                || arg.StartsWith("--http-listen=", StringComparison.Ordinal))
            {
                continue;
            }
            if (arg == AllowUnauthenticatedHttpFlag)
                continue;
            kept.Add(arg);
        }
        return kept.ToArray();
    }

    /// <summary>
    /// Strip the MCP audit-log opt-in flags (`--audit-log[=<path>]`,
    /// `--audit-log-include-values`, `--audit-log-max-bytes[=<n>]`, `--audit-log-strict`) from `cmdArgs` before
    /// the strict `cdidx mcp` parser runs. Keeps `--db` and everything after `--`
    /// untouched so existing escape semantics survive (#1562).
    /// `cdidx mcp` の厳格パーサが走る前に audit-log 用フラグを取り除く。`--db` と
    /// `--` 以降はそのまま残し既存意味論を保つ (#1562)。
    /// </summary>
    internal static bool TryConsumeAuditLogFlags(ref string[] args, out AuditLogOptions options, out string error)
    {
        options = new AuditLogOptions(null, AuditLogSink.DefaultMaxBytes, false, false);
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var state = new AuditLogFlagParseState(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (!TryConsumeAuditLogArgument(args, ref i, state, out error))
                return false;
        }

        if (state.IncludeValues && state.Path == null)
        {
            error = "Error: --audit-log-include-values requires --audit-log <path>.";
            return false;
        }

        if (state.Strict && state.Path == null)
        {
            error = "Error: --audit-log-strict requires --audit-log <path>.";
            return false;
        }

        options = state.ToOptions();
        args = state.Kept.ToArray();
        return true;
    }

    private sealed class AuditLogFlagParseState
    {
        internal AuditLogFlagParseState(int capacity)
        {
            Kept = new List<string>(capacity);
        }

        internal List<string> Kept { get; }
        internal string? Path { get; set; }
        internal long MaxBytes { get; set; } = AuditLogSink.DefaultMaxBytes;
        internal bool IncludeValues { get; set; }
        internal bool Strict { get; set; }
        internal bool Passthrough { get; set; }

        internal AuditLogOptions ToOptions() => new(Path, MaxBytes, IncludeValues, Strict);
    }

    private static bool TryConsumeAuditLogArgument(
        string[] args,
        ref int index,
        AuditLogFlagParseState state,
        out string error)
    {
        error = string.Empty;
        var arg = args[index];
        if (state.Passthrough)
        {
            state.Kept.Add(arg);
            return true;
        }

        if (arg == "--")
        {
            state.Passthrough = true;
            state.Kept.Add(arg);
            return true;
        }

        // Pass `--db` and its value through together so a dash-prefixed DB path
        // (e.g. `cdidx mcp --db --some-uri`) is not mis-consumed as the start of
        // an audit-log flag. The strict mcp parser downstream supports both
        // `--db <value>` and `--db=value`; here we only need to guard the spaced form.
        // `--db` とその値はまとめて通過させ、ダッシュ始まりの DB パス
        // (例: `cdidx mcp --db --some-uri`) を audit-log フラグの先頭と
        // 誤認しないようにする。`--db=value` 形式は値が同じトークンに含まれるため
        // 既存ループでそのまま `kept` に流れる。
        if (arg == "--db")
        {
            state.Kept.Add(arg);
            if (index + 1 < args.Length)
                state.Kept.Add(args[++index]);
            return true;
        }

        if (arg == "--audit-log")
            return TryConsumeAuditLogPathValue(args, ref index, state, out error);

        if (arg.StartsWith("--audit-log=", StringComparison.Ordinal))
            return TrySetAuditLogPath(arg.Substring("--audit-log=".Length), state, out error);

        if (arg == "--audit-log-include-values")
        {
            state.IncludeValues = true;
            return true;
        }

        if (arg == "--audit-log-strict")
        {
            state.Strict = true;
            return true;
        }

        if (arg == "--audit-log-max-bytes" || arg.StartsWith("--audit-log-max-bytes=", StringComparison.Ordinal))
            return TryConsumeAuditLogMaxBytes(args, ref index, state, out error);

        state.Kept.Add(arg);
        return true;
    }

    private static bool TryConsumeAuditLogPathValue(
        string[] args,
        ref int index,
        AuditLogFlagParseState state,
        out string error)
    {
        if (index + 1 >= args.Length)
        {
            error = "Error: --audit-log requires a path value (use `--audit-log <path>` or `--audit-log=<path>`).";
            return false;
        }

        return TrySetAuditLogPath(args[++index], state, out error);
    }

    private static bool TrySetAuditLogPath(string path, AuditLogFlagParseState state, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Error: --audit-log requires a non-empty path value.";
            return false;
        }

        state.Path = path;
        error = string.Empty;
        return true;
    }

    private static bool TryConsumeAuditLogMaxBytes(
        string[] args,
        ref int index,
        AuditLogFlagParseState state,
        out string error)
    {
        var arg = args[index];
        string raw;
        if (arg == "--audit-log-max-bytes")
        {
            if (index + 1 >= args.Length)
            {
                error = "Error: --audit-log-max-bytes requires a byte count.";
                return false;
            }
            raw = args[++index];
        }
        else
        {
            raw = arg.Substring("--audit-log-max-bytes=".Length);
        }

        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < AuditLogSink.MinMaxBytes
            || parsed > AuditLogSink.MaxMaxBytes)
        {
            error = $"Error: --audit-log-max-bytes must be an integer between {AuditLogSink.MinMaxBytes} and {AuditLogSink.MaxMaxBytes}.";
            return false;
        }

        state.MaxBytes = parsed;
        error = string.Empty;
        return true;
    }

    internal readonly record struct AuditLogOptions(string? Path, long MaxBytes, bool IncludeValues, bool Strict);
}
