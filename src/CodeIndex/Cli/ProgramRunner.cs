using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Mcp;

namespace CodeIndex.Cli;

internal static class ProgramRunner
{
    internal static int Run(
        string[] args,
        JsonSerializerOptions? jsonOptions = null,
        string? appVersion = null,
        string? configStartDirectory = null,
        Action? beforeDispatchForTesting = null)
    {
        appVersion ??= ConsoleUi.LoadVersion();

        // Load project-local `.cdidxrc.json` before anything else reads env vars so log
        // location, debug mode, and MCP gates honor the file (#1571). Hard-fail on
        // validation errors so silent typos cannot quietly change behavior.
        // 環境変数を読む処理より先に `.cdidxrc.json` を読み込み、ログ位置 / debug / MCP ゲート
        // などが config を反映できるようにする (#1571)。スキーマ違反は黙って無視せず exit する。
        var configResult = CdidxConfigFile.LoadAndApply(configStartDirectory ?? Environment.CurrentDirectory);
        if (configResult.Failed)
        {
            Console.Error.WriteLine(configResult.Error);
            Console.Error.WriteLine($"Hint: fix or remove `{CdidxConfigFile.FileName}`, or set `{CdidxConfigFile.DisableEnvVar}=1` to bypass it.");
            return CommandExitCodes.UsageError;
        }

        using var globalToolLog = GlobalToolLog.TryStart(args, appVersion);
        if (configResult.Loaded)
            GlobalToolLog.Info($"config_file_loaded path={configResult.Path}");
        jsonOptions ??= CreateDefaultJsonOptions();

        if (!TryConsumeColorFlag(ref args, out var colorError))
        {
            Console.Error.WriteLine(colorError);
            Console.Error.WriteLine("Hint: use one of `auto`, `always`, `never`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} color_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        if (!TryConsumePaletteFlag(ref args, out var paletteError))
        {
            Console.Error.WriteLine(paletteError);
            Console.Error.WriteLine("Hint: use one of `basic`, `256`, `truecolor`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} palette_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        TryConsumeAsciiFlag(ref args);

        if (!TryConsumeMetricsFlag(ref args, out var metricsPath, out var metricsError))
        {
            Console.Error.WriteLine(metricsError);
            Console.Error.WriteLine("Hint: pass `--metrics <path>` (e.g. `--metrics out.jsonl`).");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} metrics_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        using var metricsSession = MetricsSink.TryStart(metricsPath);

        TryConsumeDebugUnsafeFlag(ref args);

        var commandStopwatch = Stopwatch.StartNew();
        var commandStartTimestamp = DateTimeOffset.UtcNow;

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            ConsoleUi.PrintUsage(showBanner: args.Length > 0);
            var helpExit = args.Length == 0 ? CommandExitCodes.UsageError : CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={helpExit} help_or_usage=true");
            EmitCommandMetric("help", args, commandStartTimestamp, commandStopwatch, helpExit);
            return helpExit;
        }

        if (args[0] is "--version" or "-V")
        {
            var versionExitCode = RunVersion(args[1..], jsonOptions, appVersion);
            GlobalToolLog.Info($"command_complete exit_code={versionExitCode} version_only=true");
            EmitCommandMetric("version", args, commandStartTimestamp, commandStopwatch, versionExitCode);
            return versionExitCode;
        }

        if (args[0] is "--license" or "license")
        {
            ConsoleUi.PrintLicenseSummary();
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} license_only=true");
            EmitCommandMetric("license", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        if (args[0] == "--completions")
        {
            var exitCode = RunCompletions(args[1..]);
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command=completions");
            EmitCommandMetric("completions", args, commandStartTimestamp, commandStopwatch, exitCode);
            return exitCode;
        }

        if (args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
        {
            ConsoleUi.PrintUsage(showBanner: true);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} subcommand_help=true");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        var easterEgg = args.FirstOrDefault(a => a is "--sushi" or "--coffee" or "--ramen" or "--wine" or "--beer" or "--matcha" or "--whisky");
        if (easterEgg != null && !args.Any(a => !a.StartsWith('-')))
        {
            ConsoleUi.PrintEasterEggMessage(easterEgg);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} easter_egg={easterEgg}");
            EmitCommandMetric("easter_egg", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        try
        {
            beforeDispatchForTesting?.Invoke();

            if (args[0] is "mcp" or "mcp-server")
            {
                var mcpExitCode = RunMcp(args[1..], appVersion);
                GlobalToolLog.Info($"command_complete exit_code={mcpExitCode} command=mcp");
                EmitCommandMetric("mcp", args, commandStartTimestamp, commandStopwatch, mcpExitCode);
                return mcpExitCode;
            }

            var commandName = args[0];
            var subArgs = args[1..];
            Func<string[], int>? queryRunner = commandName switch
            {
                "search" => a => QueryCommandRunner.RunSearch(a, jsonOptions),
                "definition" => a => QueryCommandRunner.RunDefinition(a, jsonOptions),
                "references" => a => QueryCommandRunner.RunReferences(a, jsonOptions),
                "callers" => a => QueryCommandRunner.RunCallers(a, jsonOptions),
                "callees" => a => QueryCommandRunner.RunCallees(a, jsonOptions),
                "symbols" => a => QueryCommandRunner.RunSymbols(a, jsonOptions),
                "files" => a => QueryCommandRunner.RunFiles(a, jsonOptions),
                "find" => a => QueryCommandRunner.RunFind(a, jsonOptions),
                "excerpt" => a => QueryCommandRunner.RunExcerpt(a, jsonOptions),
                "map" => a => QueryCommandRunner.RunMap(a, jsonOptions),
                "inspect" => a => QueryCommandRunner.RunInspect(a, jsonOptions),
                "outline" => a => QueryCommandRunner.RunOutline(a, jsonOptions),
                "status" => a => QueryCommandRunner.RunStatus(a, jsonOptions, appVersion),
                "validate" => a => QueryCommandRunner.RunValidate(a, jsonOptions),
                "languages" => a => QueryCommandRunner.RunLanguages(a, jsonOptions),
                "impact" => a => QueryCommandRunner.RunImpact(a, jsonOptions),
                "deps" => a => QueryCommandRunner.RunDeps(a, jsonOptions),
                "unused" => a => QueryCommandRunner.RunUnused(a, jsonOptions),
                "hotspots" => a => QueryCommandRunner.RunHotspots(a, jsonOptions),
                "batch" => a => QueryCommandRunner.RunBatch(a, jsonOptions),
                "suggestions" => a => SuggestionsCommandRunner.Run(a, jsonOptions),
                _ => null,
            };

            int exitCode;
            if (queryRunner is not null)
            {
                exitCode = JsonEnvelopeWrapper.ShouldWrap(commandName, subArgs)
                    ? JsonEnvelopeWrapper.RunWrapped(commandName, subArgs, appVersion, jsonOptions, queryRunner)
                    : queryRunner(subArgs);
            }
            else
            {
                exitCode = commandName switch
                {
                    "index" => IndexCommandRunner.Run(subArgs, jsonOptions),
                    "hooks" => HookCommandRunner.Run(subArgs, jsonOptions),
                    "backfill-fold" => IndexCommandRunner.RunBackfillFold(subArgs, jsonOptions),
                    "db" => DbCommandRunner.RunIntegrityCheck(subArgs, jsonOptions),
                    "report" => ReportCommandRunner.Run(subArgs, jsonOptions, appVersion),
                    _ when IsProjectPathArg(commandName)
                        => IndexCommandRunner.Run(args, jsonOptions),
                    _ => ShowError(args, $"Unknown command: {commandName}")
                };
            }
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command={commandName}");
            EmitCommandMetric(commandName, args, commandStartTimestamp, commandStopwatch, exitCode);
            return exitCode;
        }
        catch (CodeIndexException ex)
        {
            // Issue #1580: surface Code, Path, Category, and Hint uniformly so
            // users can tell which file failed and automation has a stable
            // signal to branch on instead of parsing free-form messages.
            // #1580: 失敗ファイル / 構造化フィールドを CLI で一律に表示する。
            var exitCode = MapCodeIndexExceptionExitCode(ex.Code);
            CodeIndexExceptionFormatter.Write(ex, args, jsonOptions);
            GlobalToolLog.Error($"command_complete exit_code={exitCode} code_index_exception code={ex.Code} category={ex.Category} path={ex.Path}");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, exitCode, ex.Code);
            return exitCode;
        }
        catch (OperationCanceledException ex)
        {
            GlobalToolLog.Error($"command_complete exit_code={CommandExitCodes.CancelledBySignal} operation_cancelled type={ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("Error: command cancelled before it could complete.");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, CommandExitCodes.CancelledBySignal, ex.GetType().Name);
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
            {
                GlobalToolLog.Error($"command_complete exit_code={exitCode} handled_exception={ex.GetType().Name}: {ex.Message}");
                EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, exitCode, ex.GetType().Name);
                return exitCode;
            }

            GlobalToolLog.Error($"unhandled_exception type={ex.GetType().FullName}: {ex}");
            Console.Error.WriteLine("Error: command failed before it could complete. Run `cdidx report` for details.");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, CommandExitCodes.DatabaseError, ex.GetType().Name);
            return CommandExitCodes.DatabaseError;
        }
    }

    internal static bool IsProjectPathArg(string arg) =>
        !arg.StartsWith('-') && (Directory.Exists(arg) || arg.Contains('/') || arg.Contains('\\') || arg == ".");

    internal static int MapCodeIndexExceptionExitCode(string code) => code switch
    {
        CommandErrorCodes.DbNotFound => CommandExitCodes.NotFound,
        CommandErrorCodes.DbLocked => CommandExitCodes.TransientDatabaseError,
        CommandErrorCodes.DbNotWritable => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DbIntegrityFailed => CommandExitCodes.DatabaseError,
        CommandErrorCodes.SchemaTooNew => CommandExitCodes.DatabaseError,
        CommandErrorCodes.TempStoreExhausted => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DbError => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DirectoryNotFound => CommandExitCodes.NotFound,
        CommandErrorCodes.FeatureUnavailable => CommandExitCodes.FeatureUnavailable,
        CommandErrorCodes.UsageError => CommandExitCodes.InvalidArgument,
        CommandErrorCodes.Interrupted => CommandExitCodes.CancelledBySignal,
        _ => CommandExitCodes.DatabaseError,
    };

    internal static bool TryConsumeColorFlag(ref string[] args, out string error)
    {
        error = string.Empty;
        ConsoleUi.SetColorMode(ColorMode.Auto);
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        ColorMode? requested = null;
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // After a `--` token, leave everything alone so subcommands keep
            // their query-escape semantics (e.g. `cdidx search -- --color=auto`).
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

            string? rawValue = null;
            if (arg == "--color")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --color requires a value (one of `auto`, `always`, `never`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--color=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--color=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (!ConsoleUi.TryParseColorMode(rawValue, out var mode))
            {
                error = $"Error: invalid --color value `{rawValue}`.";
                return false;
            }
            requested = mode;
        }

        if (requested.HasValue)
            ConsoleUi.SetColorMode(requested.Value);
        args = kept.ToArray();
        return true;
    }

    internal static void TryConsumeAsciiFlag(ref string[] args)
    {
        ConsoleUi.SetAsciiOutput(false);
        if (args.Length == 0)
            return;

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
            if (arg == "--ascii")
            {
                ConsoleUi.SetAsciiOutput(true);
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
    }

    // Strip `--palette <name>` / `--palette=<name>` from `args` before
    // subcommand parsing. Mirrors `TryConsumeColorFlag` so any subcommand
    // (CLI or MCP) inherits the chosen ANSI palette without re-parsing.
    // Anything after `--` is passed through verbatim so subcommand
    // query-escape semantics are preserved (#1569).
    internal static bool TryConsumePaletteFlag(ref string[] args, out string error)
    {
        error = string.Empty;
        ConsoleUi.SetColorPalette(null);
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        ColorPalette? requested = null;
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

            string? rawValue = null;
            if (arg == "--palette")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --palette requires a value (one of `basic`, `256`, `truecolor`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--palette=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--palette=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (!ConsoleUi.TryParseColorPalette(rawValue, out var palette))
            {
                error = $"Error: invalid --palette value `{rawValue}`.";
                return false;
            }
            requested = palette;
        }

        if (requested.HasValue)
            ConsoleUi.SetColorPalette(requested.Value);
        args = kept.ToArray();
        return true;
    }

    // Strip the `--debug-unsafe` opt-in from `args` before subcommand parsing.
    // The flag must be passed every command invocation (not via env var) so a stale
    // CDIDX_DEBUG=unsafe in a shell profile or CI env cannot quietly leak indexed
    // source content (#1530). Anything after `--` is left untouched so subcommand
    // query strings keep their literal semantics.
    // サブコマンド処理前に `--debug-unsafe` を取り除く。環境変数 CDIDX_DEBUG=unsafe が
    // シェルプロファイル / CI に残った状態で索引済みソースが漏れないよう、明示的にフラグを
    // 毎回渡す運用にする（#1530）。`--` 以降はサブコマンドのクエリ文字列を保つため触らない。
    internal static bool TryConsumeDebugUnsafeFlag(ref string[] args)
    {
        if (args.Length == 0)
            return false;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        var seen = false;
        foreach (var arg in args)
        {
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
            if (arg == "--debug-unsafe")
            {
                seen = true;
                continue;
            }
            kept.Add(arg);
        }

        if (seen)
        {
            DbDebug.EnableUnsafeForProcess();
            args = kept.ToArray();
        }
        return seen;
    }

    // Strip `--metrics <path>` / `--metrics=<path>` from the global args before subcommand
    // parsing so any command (CLI or MCP) inherits the same JSONL metrics sink without
    // each subcommand re-declaring the flag. Falls back to the CDIDX_METRICS env var when
    // the explicit flag is absent. Anything after `--` is left untouched to preserve
    // subcommand query-escape semantics (#1549).
    // サブコマンド解析前に `--metrics <path>` / `--metrics=<path>` を取り除き、CLI/MCPいずれの
    // コマンドでも同じJSONLシンクを継承させる。明示フラグが無い場合は CDIDX_METRICS 環境変数に
    // フォールバック。`--` 以降はサブコマンドのクエリエスケープ意味論を保つため触らない (#1549)。
    internal static bool TryConsumeMetricsFlag(ref string[] args, out string? path, out string error)
    {
        path = null;
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        string? requested = null;
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

            string? rawValue = null;
            if (arg == "--metrics")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --metrics requires a path value (use `--metrics <path>` or `--metrics=<path>`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--metrics=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--metrics=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                error = "Error: --metrics requires a non-empty path value.";
                return false;
            }
            requested = rawValue;
        }

        path = requested;
        args = kept.ToArray();
        return true;
    }

    internal static void EmitCommandMetric(string tool, string[] args, DateTimeOffset startTimestamp, Stopwatch stopwatch, int exitCode, string? error = null)
    {
        if (!MetricsSink.IsActive)
            return;

        stopwatch.Stop();
        MetricsSink.Record(new MetricsEvent(
            Timestamp: startTimestamp,
            Tool: tool,
            Source: "cli",
            ElapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            ExitCode: exitCode,
            Language: TryParseLanguageFromArgs(args),
            Error: error));
    }

    internal static string? TryParseLanguageFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return null;
            if (arg == "--lang" && i + 1 < args.Length)
                return args[i + 1];
            if (arg.StartsWith("--lang=", StringComparison.Ordinal))
                return arg.Substring("--lang=".Length);
        }
        return null;
    }

    internal static JsonSerializerOptions CreateDefaultJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = CliJsonSerializerContext.Default,
    };

    private const string DefaultMcpHttpListen = "127.0.0.1:38080";
    private const string McpHttpTokenEnvVar = "CDIDX_MCP_HTTP_TOKEN";

    private static int RunMcp(string[] cmdArgs, string appVersion)
    {
        // Strip audit-log opt-in flags first so the strict mcp parser below does not see them
        // and raise an unknown-flag error. Keeps `--db` and `--` passthrough intact (#1562).
        // audit-log オプションフラグは厳格パーサに渡る前に除去し、未知フラグ扱いされるのを防ぐ (#1562)。
        if (!TryConsumeAuditLogFlags(ref cmdArgs, out var auditOptions, out var auditError))
        {
            Console.Error.WriteLine(auditError);
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        if (!TryExtractMcpTransportFlags(cmdArgs, out var transportSpec, out var listenSpec, out var transportError))
        {
            Console.Error.WriteLine(transportError);
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        // Strip the transport flags from the args before delegating to QueryCommandRunner.ParseArgs
        // and the unknown-flag guard below, both of which only understand the historic `--db` shape.
        // Transport フラグは ParseArgs / 未知フラグガードが知らないため、両者に渡す前に除去する。
        var residualArgs = RemoveMcpTransportFlags(cmdArgs);

        var options = QueryCommandRunner.ParseArgs(residualArgs, jsonDefault: true);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        for (var i = 0; i < residualArgs.Length; i++)
        {
            if (residualArgs[i].StartsWith("--db=", StringComparison.Ordinal))
                continue;

            if (residualArgs[i] == "--db")
            {
                i++;
                continue;
            }

            Console.Error.WriteLine($"Error: {residualArgs[i]} is not supported for mcp.");
            Console.Error.WriteLine("Hint: use `--db <path>` to point at a specific index, `--transport stdio|http` to pick a transport, `--http-listen host:port` for HTTP, or `--audit-log <path>` to enable per-call auditing.");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        var transport = transportSpec ?? "stdio";
        if (!string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: --transport '{transport}' is not supported. Use `stdio` (default) or `http`.");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        if (listenSpec != null && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Error: --http-listen requires `--transport http`.");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        AuditLogSink? auditLog = null;
        if (auditOptions.Path != null)
        {
            try
            {
                auditLog = new AuditLogSink(auditOptions.Path, auditOptions.MaxBytes, auditOptions.IncludeValues);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: failed to open audit log '{auditOptions.Path}' ({ex.GetType().Name}: {ex.Message}).");
                Console.Error.WriteLine("Hint: pick a writable path or omit --audit-log to disable per-call auditing.");
                return CommandExitCodes.UsageError;
            }
        }

        // Pick the authenticator based on `CDIDX_MCP_AUTH_TOKEN` (#1559). When unset the
        // permissive local-stdio default keeps the historical behaviour; when set every
        // JSON-RPC request must include a matching `params.auth.token`. The tool-enablement
        // gate (#1561) is wired automatically by the McpServer ctor via
        // `McpToolFilter.FromEnvironment()`.
        // `CDIDX_MCP_AUTH_TOKEN` の有無で authenticator を切り替える (#1559)。未設定なら
        // permissive な stdio 既定で従来動作を維持し、設定済みなら全 JSON-RPC リクエストに
        // `params.auth.token` の一致を要求する。ツール有効化ゲート (#1561) は McpServer の
        // コンストラクタ内部で `McpToolFilter.FromEnvironment()` から自動取得される。
        var authenticator = Mcp.McpAuthenticatorFactory.FromEnvironment();

        try
        {
            using var server = new McpServer(options.DbPath, appVersion, options.DbPathExplicit, authenticator, auditLog);

            if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
                return RunMcpHttp(server, listenSpec ?? DefaultMcpHttpListen);

            server.RunAsync().GetAwaiter().GetResult();
            return CommandExitCodes.Success;
        }
        finally
        {
            auditLog?.Dispose();
        }
    }

    private static int RunMcpHttp(McpServer server, string listenSpec)
    {
        HttpMcpTransport.HttpListenSpec resolved;
        try
        {
            resolved = HttpMcpTransport.ResolveListenSpec(listenSpec);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        // Require a shared-secret bearer token when the user opts into a non-loopback bind so the
        // MCP catalog is not exposed to the local network unauthenticated. Loopback binds skip the
        // requirement because they're indistinguishable from the existing stdio threat model.
        // 非 loopback への bind 時は共有秘密トークンを必須にし、認証なしの LAN 露出を防ぐ。
        // loopback bind は stdio と同等の脅威モデルとみなしてトークン要件を緩める。
        var bearerToken = Environment.GetEnvironmentVariable(McpHttpTokenEnvVar);
        if (string.IsNullOrEmpty(bearerToken))
            bearerToken = null;

        if (!resolved.IsLoopback && bearerToken is null)
        {
            Console.Error.WriteLine($"Error: --transport http refuses to bind to '{resolved.Host}' without a shared secret. Set the `{McpHttpTokenEnvVar}` environment variable or bind to a loopback address.");
            PrintMcpUsage();
            return CommandExitCodes.UsageError;
        }

        HttpMcpTransport transport;
        try
        {
            transport = new HttpMcpTransport(resolved.Prefix, resolved.Host, resolved.Port, bearerToken, LogHttpMcpRequest);
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Error: failed to bind HTTP listener on {resolved.Prefix}: {ex.Message}");
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
                if (resolved.IsLoopback && bearerToken is null)
                    Console.Error.WriteLine($"[cdidx-mcp] HTTP transport listening on {resolved.Prefix} (loopback, no auth).");
                else
                    Console.Error.WriteLine($"[cdidx-mcp] HTTP transport listening on {resolved.Prefix} (bearer auth required).");

                server.RunAsync(transport, cts.Token).GetAwaiter().GetResult();
            }
        }
        finally
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return CommandExitCodes.Success;
    }

    private static void LogHttpMcpRequest(HttpMcpTransport.HttpRequestLogRecord record)
    {
        GlobalToolLog.Info(
            "mcp_http_request"
            + $" correlation_id={record.CorrelationId}"
            + $" request_id={FormatLogValue(record.RequestId)}"
            + $" remote_peer={FormatLogValue(record.RemotePeer)}"
            + $" method={FormatLogValue(record.Method)}"
            + $" path={FormatLogValue(record.Path)}"
            + $" status={record.StatusCode.ToString(CultureInfo.InvariantCulture)}"
            + $" duration_ms={record.DurationMs.ToString("0.###", CultureInfo.InvariantCulture)}"
            + $" auth={FormatLogValue(record.AuthOutcome)}");
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "-";

        return value
            .Replace('\\', '/')
            .Replace('\r', '_')
            .Replace('\n', '_')
            .Replace('\t', '_')
            .Replace(' ', '_');
    }

    private static void PrintMcpUsage()
    {
        Console.Error.WriteLine("Usage: cdidx mcp [--db <path>] [--transport stdio|http] [--http-listen <host:port>] [--audit-log <path>] [--audit-log-include-values] [--audit-log-max-bytes <n>]");
    }

    internal static bool TryExtractMcpTransportFlags(string[] cmdArgs, out string? transport, out string? listen, out string error)
    {
        transport = null;
        listen = null;
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
            kept.Add(arg);
        }
        return kept.ToArray();
    }

    /// <summary>
    /// Strip the MCP audit-log opt-in flags (`--audit-log[=<path>]`,
    /// `--audit-log-include-values`, `--audit-log-max-bytes[=<n>]`) from `cmdArgs` before
    /// the strict `cdidx mcp` parser runs. Keeps `--db` and everything after `--`
    /// untouched so existing escape semantics survive (#1562).
    /// `cdidx mcp` の厳格パーサが走る前に audit-log 用フラグを取り除く。`--db` と
    /// `--` 以降はそのまま残し既存意味論を保つ (#1562)。
    /// </summary>
    internal static bool TryConsumeAuditLogFlags(ref string[] args, out AuditLogOptions options, out string error)
    {
        options = new AuditLogOptions(null, AuditLogSink.DefaultMaxBytes, false);
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        string? path = null;
        long maxBytes = AuditLogSink.DefaultMaxBytes;
        var includeValues = false;
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
                kept.Add(arg);
                if (i + 1 < args.Length)
                    kept.Add(args[++i]);
                continue;
            }

            if (arg == "--audit-log")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --audit-log requires a path value (use `--audit-log <path>` or `--audit-log=<path>`).";
                    return false;
                }
                path = args[++i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "Error: --audit-log requires a non-empty path value.";
                    return false;
                }
                continue;
            }
            if (arg.StartsWith("--audit-log=", StringComparison.Ordinal))
            {
                path = arg.Substring("--audit-log=".Length);
                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "Error: --audit-log requires a non-empty path value.";
                    return false;
                }
                continue;
            }
            if (arg == "--audit-log-include-values")
            {
                includeValues = true;
                continue;
            }
            if (arg == "--audit-log-max-bytes" || arg.StartsWith("--audit-log-max-bytes=", StringComparison.Ordinal))
            {
                string raw;
                if (arg == "--audit-log-max-bytes")
                {
                    if (i + 1 >= args.Length)
                    {
                        error = "Error: --audit-log-max-bytes requires a byte count.";
                        return false;
                    }
                    raw = args[++i];
                }
                else
                {
                    raw = arg.Substring("--audit-log-max-bytes=".Length);
                }
                if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    || parsed < AuditLogSink.MinMaxBytes)
                {
                    error = $"Error: --audit-log-max-bytes must be an integer >= {AuditLogSink.MinMaxBytes}.";
                    return false;
                }
                maxBytes = parsed;
                continue;
            }
            kept.Add(arg);
        }

        if (includeValues && path == null)
        {
            error = "Error: --audit-log-include-values requires --audit-log <path>.";
            return false;
        }

        options = new AuditLogOptions(path, maxBytes, includeValues);
        args = kept.ToArray();
        return true;
    }

    internal readonly record struct AuditLogOptions(string? Path, long MaxBytes, bool IncludeValues);


    // `--version` is now build-aware so dev builds from main are not
    // indistinguishable from tagged releases in bug reports (#1550). Human
    // output stays on a single line — `cdidx v<ver>` optionally followed by
    // ` (commit <sha>, built <date>, <clean|dirty>)` — so the install.sh
    // reinstall validator can stay anchored against trailing diagnostic spam.
    // バグ報告で dev ビルドとリリースタグを区別できるよう `--version` を
    // ビルド情報付きにする (#1550)。人間出力は 1 行に保ち、install.sh の
    // reinstall validator が末尾診断文を誤って許容しないよう、括弧で囲った
    // メタデータ以外を許さない形に揃える。
    internal static int RunVersion(string[] cmdArgs, JsonSerializerOptions jsonOptions, string? appVersion = null)
    {
        var wantsJson = false;
        foreach (var arg in cmdArgs)
        {
            if (arg is "--json")
            {
                wantsJson = true;
                continue;
            }
            Console.Error.WriteLine($"Error: --version does not accept '{arg}'.");
            Console.Error.WriteLine("Hint: use `cdidx --version` or `cdidx --version --json`.");
            return CommandExitCodes.UsageError;
        }

        var baseMetadata = ConsoleUi.LoadBuildMetadata();
        // Honour the caller-provided appVersion (overrides version.json so
        // tests and embedded hosts can pin a specific semver) while keeping
        // the assembly-stamped commit/build-date/dirty fields.
        // 呼び出し元が appVersion を渡した場合はそれを優先する（テストや
        // 組み込みホストが semver を固定できるよう）一方、commit / build
        // date / dirty は刻印された値をそのまま使う。
        var metadata = string.IsNullOrWhiteSpace(appVersion)
            ? baseMetadata
            : baseMetadata with { Version = appVersion! };
        if (wantsJson)
        {
            var payload = new VersionInfoJsonResult(
                Name: "cdidx",
                Version: metadata.Version,
                Commit: metadata.Commit,
                BuildDate: metadata.BuildDate,
                Dirty: metadata.Dirty);
            var json = JsonSerializer.Serialize(payload, CliJsonSerializerContextFactory.Create(jsonOptions).VersionInfoJsonResult);
            Console.WriteLine(json);
            return CommandExitCodes.Success;
        }

        Console.WriteLine(FormatVersionLine(metadata));
        return CommandExitCodes.Success;
    }

    internal static string FormatVersionLine(ConsoleUi.BuildMetadata metadata)
    {
        var commit = string.IsNullOrWhiteSpace(metadata.Commit) ? "unknown" : metadata.Commit;
        var buildDate = string.IsNullOrWhiteSpace(metadata.BuildDate) ? "unknown" : metadata.BuildDate;
        var dirty = string.IsNullOrWhiteSpace(metadata.Dirty) ? "unknown" : metadata.Dirty;

        // Suppress the metadata suffix only when every component is "unknown",
        // so legacy callers that depend on the exact `cdidx v<ver>` shape keep
        // working when no build stamp is present (e.g. mocked binaries).
        // 全項目が unknown のときだけ末尾メタデータを省略し、ビルド刻印が
        // 無い旧バイナリ／モックでも `cdidx v<ver>` 形式を保つ。
        if (commit == "unknown" && buildDate == "unknown" && dirty == "unknown")
            return $"cdidx v{metadata.Version}";

        return $"cdidx v{metadata.Version} (commit {commit}, built {buildDate}, {dirty})";
    }

    private static int RunCompletions(string[] cmdArgs)
    {
        if (cmdArgs.Length == 0)
        {
            Console.Error.WriteLine("Error: --completions requires a shell value.");
            Console.Error.WriteLine("Hint: rerun with one of `bash`, `zsh`, or `fish`.");
            Console.Error.WriteLine("Usage: cdidx --completions <shell>");
            return CommandExitCodes.UsageError;
        }

        if (cmdArgs[0].StartsWith("-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Error: --completions requires a shell value, got option-like token '{cmdArgs[0]}'.");
            Console.Error.WriteLine("Hint: rerun with one of `bash`, `zsh`, or `fish`.");
            Console.Error.WriteLine("Usage: cdidx --completions <shell>");
            return CommandExitCodes.UsageError;
        }

        if (cmdArgs.Length > 1)
        {
            Console.Error.WriteLine($"Error: --completions accepts exactly one shell value, got extra {ConsoleUi.Counted(cmdArgs.Length - 1, "argument")}: {string.Join(", ", cmdArgs.Skip(1).Select(arg => $"`{arg}`"))}.");
            Console.Error.WriteLine("Hint: rerun with exactly one shell name: `bash`, `zsh`, or `fish`.");
            Console.Error.WriteLine("Usage: cdidx --completions <shell>");
            return CommandExitCodes.UsageError;
        }

        if (ConsoleUi.PrintCompletions(cmdArgs[0]))
            return CommandExitCodes.Success;

        Console.Error.WriteLine("Usage: cdidx --completions <shell>");
        return CommandExitCodes.UsageError;
    }

    private static int ShowError(string[] args, string message)
    {
        Console.Error.WriteLine($"Error: {message}");

        var input = args[0];
        if (!input.StartsWith('-'))
        {
            var best = ConsoleUi.FindClosestCommand(input);
            if (best != null)
                Console.Error.WriteLine($"Did you mean: cdidx {best}?");
        }

        Console.Error.WriteLine("Run 'cdidx --help' for usage information.");
        return CommandExitCodes.UsageError;
    }
}
