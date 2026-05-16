using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Mcp;

namespace CodeIndex.Cli;

internal static class ProgramRunner
{
    internal static int Run(string[] args, JsonSerializerOptions? jsonOptions = null, string? appVersion = null)
    {
        appVersion ??= ConsoleUi.LoadVersion();
        using var globalToolLog = GlobalToolLog.TryStart(args, appVersion);
        jsonOptions ??= CreateDefaultJsonOptions();

        if (!TryConsumeColorFlag(ref args, out var colorError))
        {
            Console.Error.WriteLine(colorError);
            Console.Error.WriteLine("Hint: use one of `auto`, `always`, `never`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.UsageError} color_flag_invalid=true");
            return CommandExitCodes.UsageError;
        }

        if (!TryConsumeMetricsFlag(ref args, out var metricsPath, out var metricsError))
        {
            Console.Error.WriteLine(metricsError);
            Console.Error.WriteLine("Hint: pass `--metrics <path>` (e.g. `--metrics out.jsonl`).");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.UsageError} metrics_flag_invalid=true");
            return CommandExitCodes.UsageError;
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
            Console.WriteLine($"cdidx v{appVersion}");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} version_only=true");
            EmitCommandMetric("version", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
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
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
            {
                GlobalToolLog.Error($"command_complete exit_code={exitCode} handled_exception={ex.GetType().Name}: {ex.Message}");
                EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, exitCode, ex.GetType().Name);
                return exitCode;
            }

            GlobalToolLog.Error($"unhandled_exception type={ex.GetType().FullName}: {ex}");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, CommandExitCodes.DatabaseError, ex.GetType().Name);
            throw;
        }
    }

    internal static bool IsProjectPathArg(string arg) =>
        !arg.StartsWith('-') && (Directory.Exists(arg) || arg.Contains('/') || arg.Contains('\\') || arg == ".");

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

    private static int RunMcp(string[] cmdArgs, string appVersion)
    {
        var options = QueryCommandRunner.ParseArgs(cmdArgs, jsonDefault: true);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            Console.Error.WriteLine("Usage: cdidx mcp [--db <path>]");
            return CommandExitCodes.UsageError;
        }

        for (var i = 0; i < cmdArgs.Length; i++)
        {
            if (cmdArgs[i].StartsWith("--db=", StringComparison.Ordinal))
                continue;

            if (cmdArgs[i] == "--db")
            {
                i++;
                continue;
            }

            Console.Error.WriteLine($"Error: {cmdArgs[i]} is not supported for mcp.");
            Console.Error.WriteLine("Hint: use `--db <path>` to point at a specific index, or run `cdidx mcp` to use the default DB.");
            Console.Error.WriteLine("Usage: cdidx mcp [--db <path>]");
            return CommandExitCodes.UsageError;
        }

        using var server = new McpServer(options.DbPath, appVersion, options.DbPathExplicit);
        server.RunAsync().GetAwaiter().GetResult();
        return CommandExitCodes.Success;
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
            Console.Error.WriteLine($"Error: --completions accepts exactly one shell value, got extra argument(s): {string.Join(", ", cmdArgs.Skip(1).Select(arg => $"`{arg}`"))}.");
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
