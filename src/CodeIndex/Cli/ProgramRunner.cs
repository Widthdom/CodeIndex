using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class ProgramRunner
{
    private const int RetainedQueryTraceFileCount = 30;
    internal const string QuietEnvironmentVariable = "CDIDX_QUIET";
    private const string InstallerScriptUrlTemplate = "https://raw.githubusercontent.com/Widthdom/CodeIndex/{0}/install.sh";
    private const long MaxInstallerScriptBytes = 1024 * 1024;
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

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
            return CommandErrorWriter.Write(
                StripErrorPrefix(configResult.Error ?? "configuration file validation failed."),
                CommandExitCodes.UsageError,
                $"fix or remove `{CdidxConfigFile.FileName}`, or set `{CdidxConfigFile.DisableEnvVar}=1` to bypass it.");
        }

        if (!TryConsumeGlobalLogFlags(ref args, out var globalLogError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(globalLogError), "use --log-format <text|json>, --log-retain-count <N>, or --log-max-size-mb <N>.");
            return CommandExitCodes.InvalidArgument;
        }

        using var globalToolLog = GlobalToolLog.TryStart(args, appVersion);
        if (configResult.Loaded)
            GlobalToolLog.Info($"config_file_loaded path={configResult.Path}");
        jsonOptions ??= CreateDefaultJsonOptions();
        EnsureRedirectedStdoutUsesUtf8();

        var quiet = TryConsumeQuietFlag(ref args) || IsTruthyEnvironmentVariable(QuietEnvironmentVariable);
        using var quietScope = quiet ? QuietStderrScope.Start() : null;

        if (!TryConsumeColorFlag(ref args, out var colorError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(colorError), "use one of `auto`, `always`, `never`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} color_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        if (!TryConsumePaletteFlag(ref args, out var paletteError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(paletteError), "use one of `basic`, `256`, `truecolor`.");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} palette_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        TryConsumeAsciiFlag(ref args);
        TryConsumeNoProgressFlag(ref args);

        if (!TryConsumeMetricsFlag(ref args, out var metricsPath, out var metricsError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(metricsError), "pass `--metrics <path>` (e.g. `--metrics out.jsonl`).");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} metrics_flag_invalid=true");
            return CommandExitCodes.InvalidArgument;
        }

        using var metricsSession = MetricsSink.TryStart(metricsPath);

        TryConsumeDebugUnsafeFlag(ref args);
        if (!TryConsumeStrictVersionFlag(ref args, out var strictVersion, out var strictVersionError))
        {
            CommandErrorWriter.Write(StripErrorPrefix(strictVersionError), "use `--strict-version` without a value.");
            return CommandExitCodes.InvalidArgument;
        }
        using var jsonAnsiScope = ConsoleUi.SuppressAnsiForJsonOutput(ContainsJsonOutputFlag(args));

        var commandStopwatch = Stopwatch.StartNew();
        var commandStartTimestamp = TimeProvider.GetUtcNow();
        var versionPinExit = CheckWorkspaceVersionPin(appVersion, configStartDirectory ?? Environment.CurrentDirectory, strictVersion);
        if (versionPinExit != CommandExitCodes.Success)
            return versionPinExit;

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            ConsoleUi.PrintUsageBrief(showBanner: args.Length > 0);
            var helpExit = args.Length == 0 ? CommandExitCodes.UsageError : CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={helpExit} help_or_usage=true");
            EmitCommandMetric("help", args, commandStartTimestamp, commandStopwatch, helpExit);
            return helpExit;
        }

        if (args[0] is "--help-all" or "--help-extended")
        {
            ConsoleUi.PrintUsageFull(showBanner: true);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} help_all=true");
            EmitCommandMetric("help-all", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        if (args[0] == "--help-flags")
        {
            ConsoleUi.PrintFlagUsage(showBanner: true);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} help_flags=true");
            EmitCommandMetric("help-flags", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        if (args[0] is "--version" or "-V")
        {
            var versionExitCode = RunVersion(args[1..], jsonOptions, appVersion);
            GlobalToolLog.Info($"command_complete exit_code={versionExitCode} version_only=true");
            EmitCommandMetric("version", args, commandStartTimestamp, commandStopwatch, versionExitCode);
            return versionExitCode;
        }

        if (args[0] == "--check-updates")
        {
            var updateExitCode = RunCheckUpdates(args[1..], jsonOptions, appVersion);
            GlobalToolLog.Info($"command_complete exit_code={updateExitCode} check_updates=true");
            EmitCommandMetric("check-updates", args, commandStartTimestamp, commandStopwatch, updateExitCode);
            return updateExitCode;
        }

        if (args[0] is "--license" or "license")
        {
            if (args[0] == "license" && args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
            {
                ConsoleUi.PrintCommandUsage("license");
                GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} subcommand_help=true");
                EmitCommandMetric("license", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
                return CommandExitCodes.Success;
            }

            ConsoleUi.PrintLicenseSummary();
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} license_only=true");
            EmitCommandMetric("license", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        if (args[0] is "--completions" or "completions")
        {
            if (args[0] == "completions" && args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
            {
                ConsoleUi.PrintCommandUsage("completions");
                GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} subcommand_help=true");
                EmitCommandMetric("completions", args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
                return CommandExitCodes.Success;
            }

            var exitCode = RunCompletions(args[1..], args[0] == "completions" ? "completions" : "--completions");
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command=completions");
            EmitCommandMetric("completions", args, commandStartTimestamp, commandStopwatch, exitCode);
            return exitCode;
        }

        if (args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
        {
            if (!ConsoleUi.PrintCommandUsage(args[0]))
                ConsoleUi.PrintUsage(showBanner: true);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} subcommand_help=true");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, CommandExitCodes.Success);
            return CommandExitCodes.Success;
        }

        if (args[0] == "doctor")
        {
            var doctorExitCode = RunDoctor(args[1..], appVersion);
            GlobalToolLog.Info($"command_complete exit_code={doctorExitCode} command=doctor");
            EmitCommandMetric("doctor", args, commandStartTimestamp, commandStopwatch, doctorExitCode);
            return doctorExitCode;
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

            if (args[0] is "lsp" or "--lsp")
            {
                var lspExitCode = RunLsp(args[1..], appVersion, jsonOptions);
                GlobalToolLog.Info($"command_complete exit_code={lspExitCode} command=lsp");
                EmitCommandMetric("lsp", args, commandStartTimestamp, commandStopwatch, lspExitCode);
                return lspExitCode;
            }

            var commandName = args[0];
            var subArgs = args[1..];
            Func<string[], int>? queryRunner = commandName switch
            {
                "search" => a => QueryCommandRunner.RunSearch(a, jsonOptions),
                "definition" => a => QueryCommandRunner.RunDefinition(a, jsonOptions),
                "goto" => a => QueryCommandRunner.RunGoto(a, jsonOptions),
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
                if (!TryConsumeQueryTraceFlag(ref subArgs, out var traceMode, out var traceError))
                {
                    CommandErrorWriter.Write(StripErrorPrefix(traceError), "use one of `none`, `stderr`, or `file`.");
                    GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} command={commandName} trace_flag_invalid=true");
                    EmitCommandMetric(commandName, args, commandStartTimestamp, commandStopwatch, CommandExitCodes.InvalidArgument);
                    return CommandExitCodes.InvalidArgument;
                }

                using var traceCapture = QueryTraceOutputCapture.TryStart(traceMode, subArgs);
                exitCode = JsonEnvelopeWrapper.ShouldWrap(commandName, subArgs)
                    ? JsonEnvelopeWrapper.RunWrapped(commandName, subArgs, appVersion, jsonOptions, queryRunner)
                    : queryRunner(subArgs);
                EmitQueryTrace(traceMode, commandName, subArgs, commandStartTimestamp, commandStopwatch, exitCode, traceCapture?.ResultCount);
            }
            else
            {
                exitCode = commandName switch
                {
                    "upgrade" => RunUpgrade(subArgs, jsonOptions, appVersion),
                    "index" => IndexCommandRunner.Run(subArgs, jsonOptions),
                    "export" => ExportImportCommandRunner.RunExport(subArgs, jsonOptions, appVersion),
                    "import" => ExportImportCommandRunner.RunImport(subArgs, jsonOptions),
                    "diff" => DiffCommandRunner.Run(subArgs, jsonOptions),
                    "hooks" => HookCommandRunner.Run(subArgs, jsonOptions),
                    "backfill-fold" => IndexCommandRunner.RunBackfillFold(subArgs, jsonOptions),
                    "optimize" => IndexCommandRunner.RunOptimizeFts(subArgs, jsonOptions),
                    "vacuum" => QueryCommandRunner.RunVacuum(subArgs, jsonOptions),
                    "validate-config" => CdidxConfigFile.RunValidate(subArgs, jsonOptions),
                    "config" => subArgs.Length > 0 && subArgs[0] == "show"
                        ? CdidxConfigFile.RunShow(subArgs[1..], jsonOptions)
                        : CommandErrorWriter.WriteJsonOrHuman(
                            ContainsJsonOutputFlag(subArgs),
                            jsonOptions,
                            "Unknown config command: use `cdidx config show`.",
                            CommandExitCodes.UsageError,
                            "use `cdidx config show`."),
                    "workspace" => WorkspaceCommandRunner.Run(subArgs, jsonOptions),
                    "db" => DbCommandRunner.Run(subArgs, jsonOptions),
                    "report" => ReportCommandRunner.Run(subArgs, jsonOptions, appVersion),
                    "test-extractor" => RunTestExtractor(subArgs, jsonOptions),
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
            GlobalToolLog.Error($"command_complete exit_code={exitCode} code_index_exception code={ex.Code} category={ex.Category} path={ex.Path}", ex, includeStacks: false);
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, exitCode, ex.Code);
            return exitCode;
        }
        catch (OperationCanceledException ex)
        {
            GlobalToolLog.Error($"command_complete exit_code={CommandExitCodes.CancelledBySignal} operation_cancelled", ex, includeStacks: false);
            Console.Error.WriteLine("Error: command cancelled before it could complete.");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, CommandExitCodes.CancelledBySignal, ex.GetType().Name);
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
            {
                GlobalToolLog.Error($"command_complete exit_code={exitCode} handled_exception", ex, includeStacks: false);
                EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, exitCode, ex.GetType().Name);
                return exitCode;
            }

            var unhandledExitCode = MapUnhandledExceptionExitCode(ex);
            GlobalToolLog.Error($"command_complete exit_code={unhandledExitCode} unhandled_exception", ex);
            Console.Error.WriteLine("Error: command failed before it could complete. Run `cdidx report` for details.");
            EmitCommandMetric(args[0], args, commandStartTimestamp, commandStopwatch, unhandledExitCode, ex.GetType().Name);
            return unhandledExitCode;
        }
    }

    internal static bool IsProjectPathArg(string arg)
    {
        if (arg.StartsWith('-'))
            return false;

        if (arg == "." || Directory.Exists(arg) || Path.IsPathRooted(arg) || Path.IsPathFullyQualified(arg))
            return true;

        if (arg.Contains(Path.DirectorySeparatorChar))
            return true;

        if (Path.AltDirectorySeparatorChar != '\0'
            && Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
            && arg.Contains(Path.AltDirectorySeparatorChar))
            return true;

        return OperatingSystem.IsWindows()
            && (IsWindowsDrivePath(arg) || arg.StartsWith(@"\\", StringComparison.Ordinal));
    }

    private static bool IsWindowsDrivePath(string arg) =>
        arg.Length >= 2
        && arg[1] == ':'
        && ((arg[0] >= 'A' && arg[0] <= 'Z') || (arg[0] >= 'a' && arg[0] <= 'z'));

    private static int RunTestExtractor(string[] args, JsonSerializerOptions jsonOptions)
    {
        string? language = null;
        string? file = null;
        string? expect = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryConsumeInlineOrNext(args, ref i, arg, "--language", out var value))
                language = value;
            else if (TryConsumeInlineOrNext(args, ref i, arg, "--file", out value))
                file = value;
            else if (TryConsumeInlineOrNext(args, ref i, arg, "--expect-symbols", out value) || TryConsumeInlineOrNext(args, ref i, arg, "--expect", out value))
                expect = value;
            else if (arg == "--json")
                json = true;
            else
                return CommandErrorWriter.Write($"Unknown test-extractor argument: {arg}", CommandExitCodes.InvalidArgument, "use --language <lang> --file <path> [--expect-symbols <json>] [--json].");
        }

        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(file))
            return CommandErrorWriter.Write("test-extractor requires --language and --file.", CommandExitCodes.InvalidArgument, "use --language <lang> --file <path> [--expect-symbols <json>] [--json].");
        if (!File.Exists(file))
            return CommandErrorWriter.Write($"File not found: {file}", CommandExitCodes.NotFound);

        var source = File.ReadAllText(file);
        var symbols = Indexer.SymbolExtractor.Extract(1, language, source, file);
        if (expect != null)
        {
            var expected = File.ReadAllText(expect);
            var actual = JsonSerializer.Serialize(symbols);
            if (!JsonEquivalent(expected, actual))
            {
                Console.Error.WriteLine("Expected symbols did not match extracted symbols.");
                Console.Error.WriteLine(actual);
                return CommandExitCodes.InvalidArgument;
            }
        }

        if (json || expect == null)
            Console.WriteLine(JsonSerializer.Serialize(symbols));
        return CommandExitCodes.Success;
    }

    private static bool TryConsumeInlineOrNext(string[] args, ref int index, string arg, string flag, out string value)
    {
        value = string.Empty;
        if (arg.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            value = arg[(flag.Length + 1)..];
            return true;
        }

        if (arg != flag || index + 1 >= args.Length)
            return false;

        value = args[++index];
        return true;
    }

    private static bool JsonEquivalent(string expected, string actual)
    {
        using var expectedDoc = JsonDocument.Parse(expected);
        using var actualDoc = JsonDocument.Parse(actual);
        return JsonSerializer.Serialize(expectedDoc.RootElement) == JsonSerializer.Serialize(actualDoc.RootElement);
    }

    private static int RunDoctor(string[] args, string appVersion)
    {
        if (args.Length > 0)
            return CommandErrorWriter.Write($"Unknown doctor argument: {args[0]}", CommandExitCodes.InvalidArgument, "use `cdidx doctor`.");

        var dbResolution = DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null);
        Console.WriteLine("cdidx doctor");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("version", appVersion));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("commit", ConsoleUi.LoadBuildMetadata().Commit));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("rid", RuntimeInformation.RuntimeIdentifier));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("os", RuntimeInformation.OSDescription));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("kernel", Environment.OSVersion.VersionString));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("dotnet", RuntimeInformation.FrameworkDescription));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("process", Environment.ProcessPath ?? "<unknown>"));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("base_dir", AppContext.BaseDirectory));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("cwd", Environment.CurrentDirectory));
        Console.WriteLine();
        Console.WriteLine("terminal:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("stdout_tty", !Console.IsOutputRedirected, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("stderr_tty", !Console.IsErrorRedirected, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("columns", Environment.GetEnvironmentVariable("COLUMNS") ?? "<unset>", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("no_color", Environment.GetEnvironmentVariable("NO_COLOR") ?? "<unset>", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("term", Environment.GetEnvironmentVariable("TERM") ?? "<unset>", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("locale", CultureInfo.CurrentCulture.Name, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("ui_locale", CultureInfo.CurrentUICulture.Name, indent: "  "));
        Console.WriteLine();
        Console.WriteLine("paths:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine("db", dbResolution.DbPath, indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("data_dir", dbResolution.DataDir ?? "<explicit-db>", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("data_source", dbResolution.DataDirSource ?? "explicit-db", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine("log_dir", GlobalToolLog.ResolveLogDirectoryForStatus(), indent: "  "));
        Console.WriteLine();
        Console.WriteLine("config:");
        Console.WriteLine(ConsoleUi.FormatSummaryLine(CdidxConfigFile.FileName, File.Exists(Path.Combine(Environment.CurrentDirectory, CdidxConfigFile.FileName)) ? "present" : "not found", indent: "  "));
        Console.WriteLine(ConsoleUi.FormatSummaryLine(CdidxConfigFile.DisableEnvVar, Environment.GetEnvironmentVariable(CdidxConfigFile.DisableEnvVar) ?? "<unset>", indent: "  "));
        Console.WriteLine();
        Console.WriteLine("cdidx_env:");
        foreach (var (key, value) in EnumerateCdidxEnvironment())
            Console.WriteLine(ConsoleUi.FormatSummaryLine(key, value, indent: "  "));
        return CommandExitCodes.Success;
    }

    private static IEnumerable<(string Key, string Value)> EnumerateCdidxEnvironment()
    {
        var rows = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Key: e.Key?.ToString() ?? string.Empty, Value: e.Value?.ToString() ?? string.Empty))
            .Where(e => e.Key.StartsWith("CDIDX_", StringComparison.Ordinal))
            .OrderBy(e => e.Key, StringComparer.Ordinal);
        var any = false;
        foreach (var row in rows)
        {
            any = true;
            yield return (row.Key, IsSensitiveEnvironmentName(row.Key) ? "<redacted>" : string.IsNullOrEmpty(row.Value) ? "<empty>" : row.Value);
        }

        if (!any)
            yield return ("<none>", "");
    }

    private static bool IsSensitiveEnvironmentName(string name) =>
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PWD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
        || name.Contains("APIKEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PRIVATE_KEY", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase);

    internal static void EnsureRedirectedStdoutUsesUtf8()
    {
        if (!Console.IsOutputRedirected || Console.Out is StringWriter || Console.Out.GetType().Assembly != typeof(Console).Assembly)
            return;

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (Console.Out.Encoding.CodePage == utf8NoBom.CodePage)
            return;

        var writer = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom)
        {
            AutoFlush = true
        };
        Console.SetOut(TextWriter.Synchronized(writer));
    }

    internal static bool ContainsJsonOutputFlag(IEnumerable<string> args)
    {
        var passthrough = false;
        foreach (var arg in args)
        {
            if (passthrough)
                continue;
            if (arg == "--")
            {
                passthrough = true;
                continue;
            }
            if (arg == "--json"
                || arg.StartsWith("--json=", StringComparison.Ordinal)
                || arg == JsonEnvelopeWrapper.EnvelopeFlag)
                return true;
        }

        return false;
    }

    internal static bool TryConsumeQuietFlag(ref string[] args)
    {
        if (args.Length == 0)
            return false;

        var kept = new List<string>(args.Length);
        var quiet = false;
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
            if (arg is "--quiet" or "-q" or "--silent")
            {
                quiet = true;
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
        return quiet;
    }

    internal static bool TryConsumeGlobalLogFlags(ref string[] args, out string error)
    {
        error = string.Empty;
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

            if (TryConsumeValueFlag(args, ref i, arg, "--log-format", out var format))
            {
                if (format is not ("text" or "json"))
                {
                    error = "--log-format must be `text` or `json`.";
                    return false;
                }
                Environment.SetEnvironmentVariable(GlobalToolLog.LogFormatEnvironmentVariable, format);
                continue;
            }

            if (TryConsumeValueFlag(args, ref i, arg, "--log-retain-count", out var retainCount))
            {
                if (!int.TryParse(retainCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                {
                    error = "--log-retain-count must be a positive integer.";
                    return false;
                }
                Environment.SetEnvironmentVariable(GlobalToolLog.LogRetainEnvironmentVariable, parsed.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (TryConsumeValueFlag(args, ref i, arg, "--log-max-size-mb", out var maxSizeMb))
            {
                if (!int.TryParse(maxSizeMb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                {
                    error = "--log-max-size-mb must be a positive integer.";
                    return false;
                }
                Environment.SetEnvironmentVariable(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, parsed.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
        return true;
    }

    private static bool TryConsumeValueFlag(string[] args, ref int index, string arg, string flag, out string value)
    {
        value = string.Empty;
        if (arg.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            value = arg[(flag.Length + 1)..].Trim();
            return true;
        }

        if (arg != flag)
            return false;

        if (index + 1 >= args.Length)
            return true;

        value = args[++index].Trim();
        return true;
    }

    private static bool IsTruthyEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value != null
               && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }

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

    internal static int MapUnhandledExceptionExitCode(Exception ex)
    {
        var sqliteException = FindSqliteException(ex);
        if (sqliteException is null)
            return CommandExitCodes.UnhandledException;

        return sqliteException.SqliteErrorCode switch
        {
            5 or 6 or 8 => CommandExitCodes.TransientDatabaseError,
            _ => CommandExitCodes.DatabaseError,
        };
    }

    private static SqliteException? FindSqliteException(Exception ex)
    {
        if (ex is SqliteException sqliteException)
            return sqliteException;
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var found = FindSqliteException(inner);
                if (found is not null)
                    return found;
            }
        }

        return ex.InnerException is null ? null : FindSqliteException(ex.InnerException);
    }

    private sealed class QuietStderrScope : IDisposable
    {
        private readonly TextWriter _originalError;

        private QuietStderrScope(TextWriter originalError)
        {
            _originalError = originalError;
        }

        public static QuietStderrScope Start()
        {
            var originalError = Console.Error;
            Console.SetError(new ErrorOnlyTextWriter(originalError));
            return new QuietStderrScope(originalError);
        }

        public void Dispose()
        {
            Console.Error.Flush();
            Console.SetError(_originalError);
        }
    }

    private sealed class ErrorOnlyTextWriter(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _lineBuffer = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (value == '\r')
                return;

            if (value == '\n')
            {
                FlushBufferedLine();
                return;
            }

            _lineBuffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;

            foreach (var ch in value)
                Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            FlushBufferedLine();
        }

        public override void Flush()
        {
            FlushBufferedLine();
            inner.Flush();
        }

        private void FlushBufferedLine()
        {
            if (_lineBuffer.Length == 0)
                return;

            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();
            if (IsErrorLine(line))
                inner.WriteLine(line);
        }

        private static bool IsErrorLine(string line)
            => line.StartsWith("Error", StringComparison.Ordinal);
    }

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

    internal static void TryConsumeNoProgressFlag(ref string[] args)
    {
        ConsoleUi.SetProgressAnimationEnabled(null);
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
            if (arg == "--no-progress")
            {
                ConsoleUi.SetProgressAnimationEnabled(false);
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

    internal static bool TryConsumeStrictVersionFlag(ref string[] args, out bool strictVersion, out string error)
    {
        strictVersion = IsTruthyEnvironmentVariable("CDIDX_STRICT_VERSION");
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        var passthrough = false;
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
            if (arg == "--strict-version")
            {
                strictVersion = true;
                continue;
            }
            if (arg.StartsWith("--strict-version=", StringComparison.Ordinal))
            {
                error = "Error: --strict-version does not accept a value.";
                return false;
            }
            kept.Add(arg);
        }

        args = kept.ToArray();
        return true;
    }

    private static int CheckWorkspaceVersionPin(string appVersion, string startDirectory, bool strictVersion)
    {
        var pinPath = FindWorkspaceVersionPin(startDirectory);
        if (pinPath == null)
            return CommandExitCodes.Success;

        string required;
        try
        {
            required = File.ReadLines(pinPath).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not read .cdidx-version at {pinPath}: {ex.Message}");
            return CommandExitCodes.Success;
        }

        if (string.IsNullOrWhiteSpace(required) || VersionsMatch(required, appVersion))
            return CommandExitCodes.Success;

        var message = $"workspace requires cdidx v{NormalizeVersion(required)}, but this binary is v{NormalizeVersion(appVersion)} ({pinPath}).";
        if (!strictVersion)
        {
            Console.Error.WriteLine($"Warning: {message}");
            return CommandExitCodes.Success;
        }

        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine("Hint: rerun without --strict-version to warn only, or install the pinned cdidx version for this workspace.");
        return CommandExitCodes.ExUsage;
    }

    internal static string? FindWorkspaceVersionPin(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        if (File.Exists(current))
            current = Path.GetDirectoryName(current) ?? current;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, ".cdidx-version");
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(current);
            if (parent == null)
                return null;
            current = parent.FullName;
        }

        return null;
    }

    private static bool VersionsMatch(string required, string actual)
        => string.Equals(NormalizeVersion(required), NormalizeVersion(actual), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
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

    internal static bool TryConsumeQueryTraceFlag(ref string[] args, out string traceMode, out string error)
    {
        traceMode = "none";
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

            string? rawValue = null;
            if (arg == "--trace")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --trace requires a value (use `--trace stderr`, `--trace file`, `--trace none`, or `--trace=<mode>`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--trace=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--trace=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                error = "Error: --trace requires a non-empty value.";
                return false;
            }
            if (rawValue is not ("none" or "stderr" or "file"))
            {
                error = $"Error: --trace must be one of `none`, `stderr`, or `file`, got `{rawValue}`.";
                return false;
            }
            traceMode = rawValue;
        }

        args = kept.ToArray();
        return true;
    }

    private static void EmitQueryTrace(string mode, string commandName, string[] subArgs, DateTimeOffset startTimestamp, Stopwatch stopwatch, int exitCode, int? resultCount)
    {
        if (mode == "none")
            return;

        try
        {
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var payload = BuildQueryTraceJson(commandName, subArgs, startTimestamp, elapsedMs, exitCode, resultCount);
            if (mode == "stderr")
            {
                Console.Error.WriteLine(payload);
                return;
            }

            var directory = GlobalToolLog.ResolveLogDirectoryForStatus();
            Directory.CreateDirectory(directory);
            PrivateLogFile.HardenExisting(directory, "query-trace-*.jsonl");
            var path = ResolveQueryTracePath(directory);
            var encoded = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
            using (var stream = PrivateLogFile.OpenAppend(path, FileShare.ReadWrite))
            {
                stream.Write(encoded, 0, encoded.Length);
                stream.Flush();
            }
            PrivateLogFile.TrySetPrivatePermissions(path);
            PrivateLogFile.PruneOldFiles(directory, "query-trace-*.jsonl", RetainedQueryTraceFileCount);
        }
        catch
        {
            // Best-effort only: trace output must never change query command behavior.
        }
    }

    private static string ResolveQueryTracePath(string directory)
    {
        var date = TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"query-trace-{date}.jsonl");
    }

    private static string BuildQueryTraceJson(string commandName, string[] subArgs, DateTimeOffset timestamp, double elapsedMs, int exitCode, int? resultCount)
    {
        var payload = new JsonObject
        {
            ["timestamp"] = timestamp.ToString("O", CultureInfo.InvariantCulture),
            ["tool"] = commandName,
            ["source"] = "cli_query",
            ["parameters"] = BuildQueryTraceParameters(subArgs),
            ["elapsed_ms"] = Math.Round(elapsedMs, 3),
            ["result_count"] = resultCount,
            ["exit_code"] = exitCode,
        };
        if (exitCode != CommandExitCodes.Success)
            payload["error"] = "command_failed";
        return payload.ToJsonString(CreateDefaultJsonOptions());
    }

    private static JsonObject BuildQueryTraceParameters(string[] args)
    {
        var parameters = new JsonObject
        {
            ["json"] = false,
            ["count"] = false,
        };
        var paths = new List<string>();
        var excludePaths = new List<string>();
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
                continue;
            if (arg == "--")
            {
                passthrough = true;
                continue;
            }

            string? inlineValue = null;
            var optionName = arg;
            var equals = arg.IndexOf('=');
            if (equals > 0)
            {
                optionName = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            string? value = inlineValue;
            if (value == null && optionName is "--lang" or "--limit" or "--top" or "--path" or "--exclude-path")
            {
                if (i + 1 < args.Length)
                    value = args[++i];
            }

            switch (optionName)
            {
                case "--json":
                    parameters["json"] = true;
                    if (!string.IsNullOrWhiteSpace(value))
                        parameters["json_format"] = value;
                    break;
                case "--count":
                    parameters["count"] = true;
                    break;
                case "--lang" when !string.IsNullOrWhiteSpace(value):
                    parameters["lang"] = value;
                    break;
                case "--limit" when !string.IsNullOrWhiteSpace(value):
                case "--top" when !string.IsNullOrWhiteSpace(value):
                    parameters["limit"] = value;
                    break;
                case "--path" when !string.IsNullOrWhiteSpace(value):
                    paths.Add(value);
                    break;
                case "--exclude-path" when !string.IsNullOrWhiteSpace(value):
                    excludePaths.Add(value);
                    break;
            }
        }
        if (paths.Count > 0)
            parameters["path"] = new JsonArray(paths.Select(path => JsonValue.Create(path)).ToArray());
        if (excludePaths.Count > 0)
            parameters["exclude_path"] = new JsonArray(excludePaths.Select(path => JsonValue.Create(path)).ToArray());
        return parameters;
    }

    private sealed class QueryTraceOutputCapture : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly bool _countNumericOutput;
        private readonly bool _countJsonLines;
        private bool _disposed;

        private QueryTraceOutputCapture(TextWriter inner, bool countNumericOutput, bool countJsonLines)
        {
            _inner = inner;
            _countNumericOutput = countNumericOutput;
            _countJsonLines = countJsonLines;
        }

        public override Encoding Encoding => _inner.Encoding;
        public int? ResultCount { get; private set; }

        public static QueryTraceOutputCapture? TryStart(string traceMode, string[] args)
        {
            if (traceMode == "none")
                return null;

            var capture = new QueryTraceOutputCapture(
                Console.Out,
                HasFlag(args, "--count"),
                HasFlag(args, "--json") && !HasInlineValue(args, "--json", "array"));
            Console.SetOut(capture);
            return capture;
        }

        public override void Write(char value) => _inner.Write(value);
        public override void Write(string? value) => _inner.Write(value);

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            ObserveLine(value);
        }

        public override void WriteLine()
        {
            _inner.WriteLine();
            ObserveLine(string.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                Console.SetOut(_inner);
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private void ObserveLine(string? value)
        {
            if (value == null)
                return;

            var trimmed = value.Trim();
            if (_countNumericOutput && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
            {
                ResultCount = count;
                return;
            }

            if (_countJsonLines && trimmed.StartsWith('{'))
                ResultCount = (ResultCount ?? 0) + 1;
        }

        private static bool HasFlag(string[] args, string name)
        {
            var passthrough = false;
            foreach (var arg in args)
            {
                if (passthrough)
                    continue;
                if (arg == "--")
                {
                    passthrough = true;
                    continue;
                }
                if (arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasInlineValue(string[] args, string name, string value)
        {
            var expected = name + "=" + value;
            var passthrough = false;
            foreach (var arg in args)
            {
                if (passthrough)
                    continue;
                if (arg == "--")
                {
                    passthrough = true;
                    continue;
                }
                if (arg == expected)
                    return true;
            }
            return false;
        }
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

    private static int RunLsp(string[] cmdArgs, string appVersion, JsonSerializerOptions jsonOptions)
    {
        var options = QueryCommandRunner.ParseArgs(cmdArgs, jsonDefault: true);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            PrintLspUsage();
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

            Console.Error.WriteLine($"Error: {cmdArgs[i]} is not supported for lsp.");
            Console.Error.WriteLine("Hint: use `--db <path>` to point at a specific index.");
            PrintLspUsage();
            return CommandExitCodes.UsageError;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(options.DbPath))
            {
                Console.Error.WriteLine("Error: database path could not be resolved.");
                PrintLspUsage();
                return CommandExitCodes.UsageError;
            }

            if (!options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath)))
            {
                var resolvedPath = Path.GetFullPath(options.DbPath);
                Console.Error.WriteLine($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {resolvedPath}");
                Console.Error.WriteLine("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun `cdidx lsp`.");
                return CommandExitCodes.DatabaseError;
            }

            using var db = new DbContext(options.DbPath);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
            {
                Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: invalid CodeIndex database: {validationReason}");
                return CommandExitCodes.DatabaseError;
            }

            db.TryMigrateForRead();
            var indexedProjectRoot = db.GetMetaString(DbContext.IndexedProjectRootMetaKey);
            if (!string.IsNullOrWhiteSpace(indexedProjectRoot)
                && bool.TryParse(db.GetMetaString(DbContext.WorkspacePathCaseSensitiveMetaKey), out var pathCaseSensitive))
            {
                PathCasing.SeedFromWorkspace(indexedProjectRoot, ignoreCase: !pathCaseSensitive);
            }

            using var server = new LspServer(new DbReader(db), appVersion, jsonOptions, indexedProjectRoot);
            return server.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
        }
        catch (OperationCanceledException)
        {
            Console.Out.Flush();
            Console.Error.Flush();
            return CommandExitCodes.CancelledBySignal;
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("lsp_server_failed " + GlobalToolLog.FormatExceptionChain(ex));
            Console.Error.WriteLine($"Error: LSP server failed ({ex.GetType().Name}: {ex.Message}).");
            Console.Out.Flush();
            Console.Error.Flush();
            return CommandExitCodes.DatabaseError;
        }
    }

    private static void PrintLspUsage()
    {
        Console.Error.WriteLine("Usage: cdidx lsp [--db <path>]");
        Console.Error.WriteLine("Runs a read-only Language Server Protocol server over stdio using an existing CodeIndex database.");
    }

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

        if (!TryConsumeSuggestionDedupThresholdFlag(ref cmdArgs, out var thresholdError))
        {
            Console.Error.WriteLine(thresholdError);
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

            if (residualArgs[i] == "--json")
                Console.Error.WriteLine("Error: --json is not supported for mcp; MCP already speaks JSON-RPC over the selected transport.");
            else
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
                Console.Error.WriteLine($"Error: MCP server failed ({ex.GetType().Name}: {ex.Message}).");
                Console.Out.Flush();
                Console.Error.Flush();
                return CommandExitCodes.DatabaseError;
            }
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
                    Console.Error.WriteLine($"Error: MCP HTTP server failed ({ex.GetType().Name}: {ex.Message}).");
                    Console.Out.Flush();
                    Console.Error.Flush();
                    return CommandExitCodes.DatabaseError;
                }
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
        Console.Error.WriteLine("Usage: cdidx mcp [--db <path>] [--transport stdio|http] [--http-listen <host:port>] [--audit-log <path>] [--audit-log-include-values] [--audit-log-max-bytes <n>] [--suggestion-dedup-threshold <0..1>]");
        Console.Error.WriteLine("Note: --json is not supported; MCP requests and responses are JSON-RPC over the selected transport.");
    }

    internal static bool TryConsumeSuggestionDedupThresholdFlag(ref string[] args, out string error)
    {
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

            Environment.SetEnvironmentVariable(SuggestionStore.DedupThresholdEnvironmentVariable, value);
        }

        args = kept.ToArray();
        return true;
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

    internal static int RunCheckUpdates(string[] cmdArgs, JsonSerializerOptions jsonOptions, string appVersion)
    {
        var wantsJson = false;
        foreach (var arg in cmdArgs)
        {
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }
            Console.Error.WriteLine($"Error: --check-updates does not accept '{arg}'.");
            Console.Error.WriteLine("Hint: use `cdidx --check-updates` or `cdidx --check-updates --json`.");
            return CommandExitCodes.UsageError;
        }

        var result = UpdateChecker.Check(appVersion);
        if (wantsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            return CommandExitCodes.Success;
        }

        if (result.UpdateAvailable && result.LatestVersion != null)
            Console.WriteLine($"A newer cdidx release is available: {result.LatestVersion} (current: {result.CurrentVersion}).");
        else if (result.Error != null)
            Console.WriteLine($"Could not check for updates; using cached release metadata if available (current: {result.CurrentVersion}).");
        else
            Console.WriteLine($"cdidx is up to date (current: {result.CurrentVersion}).");
        return CommandExitCodes.Success;
    }

    internal static int RunUpgrade(string[] cmdArgs, JsonSerializerOptions jsonOptions, string appVersion)
    {
        var checkOnly = false;
        var wantsJson = false;
        foreach (var arg in cmdArgs)
        {
            if (arg is "--check-only" or "--check-updates")
            {
                checkOnly = true;
                continue;
            }
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }
            if (arg is "--channel" or "--prerelease" || arg.StartsWith("--channel=", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: upgrade channels and prerelease upgrades are not supported yet.");
                Console.Error.WriteLine("Hint: rerun `install.sh` with an explicit release tag if you need a non-latest version.");
                return CommandExitCodes.UsageError;
            }
            Console.Error.WriteLine($"Error: upgrade does not accept '{arg}'.");
            Console.Error.WriteLine("Hint: use `cdidx upgrade` or `cdidx upgrade --check-only`.");
            return CommandExitCodes.UsageError;
        }

        var result = UpdateChecker.Check(appVersion);
        if (checkOnly || !result.UpdateAvailable || result.LatestVersion == null)
        {
            if (wantsJson)
                Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            else if (result.UpdateAvailable && result.LatestVersion != null)
                Console.WriteLine($"A newer cdidx release is available: {result.LatestVersion} (current: {result.CurrentVersion}).");
            else
                Console.WriteLine($"cdidx is up to date (current: {result.CurrentVersion}).");
            return CommandExitCodes.Success;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Error: cdidx upgrade currently requires a POSIX shell installer on Linux or macOS.");
            Console.Error.WriteLine("Hint: download the latest release asset manually, or rerun install.sh from a shell environment.");
            return CommandExitCodes.FeatureUnavailable;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!CanWriteDirectory(installDir))
        {
            Console.Error.WriteLine($"Error: install directory is not writable: {installDir}");
            Console.Error.WriteLine("Hint: rerun with permissions that can write this directory, or reinstall cdidx into a per-user directory.");
            return CommandExitCodes.UsageError;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"cdidx-install-{Guid.NewGuid():N}.sh");
        try
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                DownloadInstallerScriptAsync(
                        client,
                        result.LatestVersion,
                        scriptPath,
                        TimeSpan.FromSeconds(20),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            var startInfo = new ProcessStartInfo("bash", $"{QuoteShellArg(scriptPath)} {QuoteShellArg(result.LatestVersion)}")
            {
                UseShellExecute = false,
            };
            startInfo.Environment["CDIDX_INSTALL_DIR"] = installDir;
            var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Error: failed to start install.sh for upgrade.");
                return CommandExitCodes.DatabaseError;
            }
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: upgrade failed before install.sh completed ({ex.GetType().Name}: {ex.Message}).");
            Console.Error.WriteLine("Hint: rerun `install.sh` manually for the desired release.");
            return CommandExitCodes.DatabaseError;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    internal static string BuildInstallerScriptUrl(string releaseTag)
        => string.Format(
            CultureInfo.InvariantCulture,
            InstallerScriptUrlTemplate,
            Uri.EscapeDataString(releaseTag.Trim()));

    internal static async Task DownloadInstallerScriptAsync(
        HttpClient client,
        string releaseTag,
        string scriptPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildInstallerScriptUrl(releaseTag));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            downloadCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await BoundedHttpContentReader.WriteToPrivateFileAsync(
            response.Content,
            scriptPath,
            MaxInstallerScriptBytes,
            downloadCts.Token).ConfigureAwait(false);
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".cdidx-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteShellArg(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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

        var updateHint = UpdateChecker.GetNewerReleaseHint(metadata.Version);
        Console.WriteLine(FormatVersionLine(metadata, updateHint));
        return CommandExitCodes.Success;
    }

    internal static string FormatVersionLine(ConsoleUi.BuildMetadata metadata, string? updateHint = null)
    {
        var commit = string.IsNullOrWhiteSpace(metadata.Commit) ? "unknown" : metadata.Commit;
        var buildDate = string.IsNullOrWhiteSpace(metadata.BuildDate) ? "unknown" : metadata.BuildDate;
        var dirty = string.IsNullOrWhiteSpace(metadata.Dirty) ? "unknown" : metadata.Dirty;
        var suffix = string.IsNullOrWhiteSpace(updateHint) ? string.Empty : $" [{updateHint}]";

        // Suppress the metadata suffix only when every component is "unknown",
        // so legacy callers that depend on the exact `cdidx v<ver>` shape keep
        // working when no build stamp is present (e.g. mocked binaries).
        // 全項目が unknown のときだけ末尾メタデータを省略し、ビルド刻印が
        // 無い旧バイナリ／モックでも `cdidx v<ver>` 形式を保つ。
        if (commit == "unknown" && buildDate == "unknown" && dirty == "unknown")
            return $"cdidx v{metadata.Version}{suffix}";

        return $"cdidx v{metadata.Version} (commit {commit}, built {buildDate}, {dirty}){suffix}";
    }

    private static int RunCompletions(string[] cmdArgs, string commandName = "--completions")
    {
        var usage = $"cdidx {commandName} <shell>";
        if (cmdArgs.Length == 0)
            return CommandErrorWriter.Write(
                $"{commandName} requires a shell value.",
                CommandExitCodes.UsageError,
                "rerun with one of `bash`, `zsh`, `fish`, or `powershell`.",
                usage);

        if (cmdArgs[0] == "--json")
            return CommandErrorWriter.Write(
                "--json is not supported for completions.",
                CommandExitCodes.UsageError,
                "rerun with one of `bash`, `zsh`, `fish`, or `powershell`; completions output is already a shell script.",
                usage);

        if (cmdArgs[0].StartsWith("-", StringComparison.Ordinal))
            return CommandErrorWriter.Write(
                $"{commandName} requires a shell value, got option-like token '{cmdArgs[0]}'.",
                CommandExitCodes.UsageError,
                "rerun with one of `bash`, `zsh`, `fish`, or `powershell`.",
                usage);

        if (cmdArgs.Length > 1)
            return CommandErrorWriter.Write(
                $"{commandName} accepts exactly one shell value, got extra {ConsoleUi.Counted(cmdArgs.Length - 1, "argument")}: {string.Join(", ", cmdArgs.Skip(1).Select(arg => $"`{arg}`"))}.",
                CommandExitCodes.UsageError,
                "rerun with exactly one shell name: `bash`, `zsh`, `fish`, or `powershell`.",
                usage);

        if (ConsoleUi.PrintCompletions(cmdArgs[0]))
            return CommandExitCodes.Success;

        return CommandErrorWriter.Write(
            $"unsupported completion shell `{cmdArgs[0]}`.",
            CommandExitCodes.UsageError,
            "rerun with one of `bash`, `zsh`, `fish`, or `powershell`.",
            usage);
    }

    private static string StripErrorPrefix(string message)
    {
        const string prefix = "Error: ";
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
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
