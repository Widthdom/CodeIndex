namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    private static bool TryRunImmediateCommand(string[] args, CommandRunContext context, out int exitCode)
    {
        if (TryRunHelpVersionOrUpdateCommand(args, context, out exitCode))
            return true;
        if (TryRunStandaloneUtilityCommand(args, context, out exitCode))
            return true;
        if (TryRunSubcommandHelp(args, context, out exitCode))
            return true;
        if (TryRunDoctorCommand(args, context, out exitCode))
            return true;
        if (TryRunEasterEggCommand(args, context, out exitCode))
            return true;

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static bool TryRunHelpVersionOrUpdateCommand(string[] args, CommandRunContext context, out int exitCode)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            ConsoleUi.PrintUsageBrief(showBanner: args.Length > 0);
            exitCode = args.Length == 0 ? CommandExitCodes.UsageError : CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} help_or_usage=true");
            EmitCommandMetric("help", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        if (args[0] is "--help-all" or "--help-extended")
        {
            ConsoleUi.PrintUsageFull(showBanner: true);
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} help_all=true");
            EmitCommandMetric("help-all", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        if (args[0] == "--help-flags")
        {
            ConsoleUi.PrintFlagUsage(showBanner: true);
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} help_flags=true");
            EmitCommandMetric("help-flags", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        if (args[0] is "--version" or "-V")
        {
            exitCode = RunVersion(args[1..], context.JsonOptions, context.AppVersion, context.CancellationToken);
            GlobalToolLog.Info($"command_complete exit_code={exitCode} version_only=true");
            EmitCommandMetric("version", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        if (args[0] == "--check-updates")
        {
            exitCode = RunCheckUpdates(args[1..], context.JsonOptions, context.AppVersion, context.CancellationToken);
            GlobalToolLog.Info($"command_complete exit_code={exitCode} check_updates=true");
            EmitCommandMetric("check-updates", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static bool TryRunStandaloneUtilityCommand(string[] args, CommandRunContext context, out int exitCode)
    {
        if (args[0] is "--license" or "license")
        {
            if (args[0] == "license" && args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
            {
                ConsoleUi.PrintCommandUsage("license");
                exitCode = CommandExitCodes.Success;
                GlobalToolLog.Info($"command_complete exit_code={exitCode} subcommand_help=true");
                EmitCommandMetric("license", args, context.StartTimestamp, context.Stopwatch, exitCode);
                return true;
            }

            ConsoleUi.PrintLicenseSummary();
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} license_only=true");
            EmitCommandMetric("license", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        if (args[0] is "--completions" or "completions")
        {
            if (args[0] == "completions" && args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
            {
                ConsoleUi.PrintCommandUsage("completions");
                exitCode = CommandExitCodes.Success;
                GlobalToolLog.Info($"command_complete exit_code={exitCode} subcommand_help=true");
                EmitCommandMetric("completions", args, context.StartTimestamp, context.Stopwatch, exitCode);
                return true;
            }

            exitCode = RunCompletions(args[1..], args[0] == "completions" ? "completions" : "--completions");
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command=completions");
            EmitCommandMetric("completions", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static bool TryRunSubcommandHelp(string[] args, CommandRunContext context, out int exitCode)
    {
        if (args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
        {
            if (!ConsoleUi.PrintCommandUsage(args[0]))
                ConsoleUi.PrintUsage(showBanner: true);
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} subcommand_help=true");
            EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static bool TryRunDoctorCommand(string[] args, CommandRunContext context, out int exitCode)
    {
        if (args[0] == "doctor")
        {
            exitCode = RunDoctor(args[1..], context.AppVersion, context.JsonOptions);
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command=doctor");
            EmitCommandMetric("doctor", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static bool TryRunEasterEggCommand(string[] args, CommandRunContext context, out int exitCode)
    {
        var easterEgg = args.FirstOrDefault(a => a is "--sushi" or "--coffee" or "--ramen" or "--wine" or "--beer" or "--matcha" or "--whisky");
        if (easterEgg != null && !args.Any(a => !a.StartsWith('-')))
        {
            ConsoleUi.PrintEasterEggMessage(easterEgg);
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} easter_egg={easterEgg}");
            EmitCommandMetric("easter_egg", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static int RunDispatchedCommand(
        string[] args,
        CommandRunContext context,
        Action? beforeDispatchForTesting)
    {
        beforeDispatchForTesting?.Invoke();

        if (args[0] is "mcp" or "mcp-server")
        {
            var mcpExitCode = RunMcp(args[1..], context.AppVersion);
            GlobalToolLog.Info($"command_complete exit_code={mcpExitCode} command=mcp");
            EmitCommandMetric("mcp", args, context.StartTimestamp, context.Stopwatch, mcpExitCode);
            return mcpExitCode;
        }

        if (args[0] is "lsp" or "--lsp")
        {
            var lspExitCode = RunLsp(args[1..], context.AppVersion, context.JsonOptions, context.CancellationToken);
            GlobalToolLog.Info($"command_complete exit_code={lspExitCode} command=lsp");
            EmitCommandMetric("lsp", args, context.StartTimestamp, context.Stopwatch, lspExitCode);
            return lspExitCode;
        }

        var commandName = args[0];
        var subArgs = args[1..];
        var queryRunner = ResolveQueryRunner(commandName, context);

        int exitCode;
        if (queryRunner is not null)
        {
            subArgs = InsertQueryLiteralSentinelForNonLogGlobalOption(commandName, subArgs);

            if (!TryConsumeQueryTraceFlag(ref subArgs, out var traceMode, out var traceError))
            {
                CommandErrorWriter.Write(StripErrorPrefix(traceError), "use one of `none`, `stderr`, or `file`.");
                GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.InvalidArgument} command={commandName} trace_flag_invalid=true");
                EmitCommandMetric(commandName, args, context.StartTimestamp, context.Stopwatch, CommandExitCodes.InvalidArgument);
                return CommandExitCodes.InvalidArgument;
            }

            using var traceCapture = QueryTraceOutputCapture.TryStart(traceMode, subArgs);
            exitCode = JsonEnvelopeWrapper.ShouldWrap(commandName, subArgs)
                ? JsonEnvelopeWrapper.RunWrapped(commandName, subArgs, context.AppVersion, context.JsonOptions, queryRunner)
                : queryRunner(subArgs);
            EmitQueryTrace(traceMode, commandName, subArgs, context.StartTimestamp, context.Stopwatch, exitCode, traceCapture?.ResultCount);
        }
        else
        {
            exitCode = RunNonQueryCommand(commandName, subArgs, args, context);
        }

        GlobalToolLog.Info($"command_complete exit_code={exitCode} command={commandName}");
        EmitCommandMetric(commandName, args, context.StartTimestamp, context.Stopwatch, exitCode);
        return exitCode;
    }

    private static Func<string[], int>? ResolveQueryRunner(string commandName, CommandRunContext context) =>
        commandName switch
        {
            "search" => a => QueryCommandRunner.RunSearch(a, context.JsonOptions, context.CancellationToken),
            "definition" => a => QueryCommandRunner.RunDefinition(a, context.JsonOptions),
            "goto" => a => QueryCommandRunner.RunGoto(a, context.JsonOptions),
            "references" => a => QueryCommandRunner.RunReferences(a, context.JsonOptions),
            "callers" => a => QueryCommandRunner.RunCallers(a, context.JsonOptions),
            "callees" => a => QueryCommandRunner.RunCallees(a, context.JsonOptions),
            "symbols" => a => QueryCommandRunner.RunSymbols(a, context.JsonOptions),
            "files" => a => QueryCommandRunner.RunFiles(a, context.JsonOptions),
            "find" => a => QueryCommandRunner.RunFind(a, context.JsonOptions),
            "excerpt" => a => QueryCommandRunner.RunExcerpt(a, context.JsonOptions),
            "map" => a => QueryCommandRunner.RunMap(a, context.JsonOptions),
            "inspect" => a => QueryCommandRunner.RunInspect(a, context.JsonOptions),
            "outline" => a => QueryCommandRunner.RunOutline(a, context.JsonOptions),
            "status" => a => QueryCommandRunner.RunStatus(a, context.JsonOptions, context.AppVersion, context.CancellationToken),
            "validate" => a => QueryCommandRunner.RunValidate(a, context.JsonOptions),
            "languages" => a => QueryCommandRunner.RunLanguages(a, context.JsonOptions),
            "impact" => a => QueryCommandRunner.RunImpact(a, context.JsonOptions),
            "deps" => a => QueryCommandRunner.RunDeps(a, context.JsonOptions),
            "unused" => a => QueryCommandRunner.RunUnused(a, context.JsonOptions),
            "hotspots" => a => QueryCommandRunner.RunHotspots(a, context.JsonOptions),
            "batch" => a => QueryCommandRunner.RunBatch(a, context.JsonOptions),
            "suggestions" => a => SuggestionsCommandRunner.Run(a, context.JsonOptions, context.CancellationToken),
            _ => null,
        };

    private static int RunNonQueryCommand(
        string commandName,
        string[] subArgs,
        string[] originalArgs,
        CommandRunContext context) =>
        commandName switch
        {
            "upgrade" => RunUpgrade(subArgs, context.JsonOptions, context.AppVersion, context.CancellationToken),
            "index" => IndexCommandRunner.Run(subArgs, context.JsonOptions),
            "recipes" => RunRecipesAlias(subArgs, context),
            "audit" => RunAuditAlias(subArgs, context),
            "export" => ExportImportCommandRunner.RunExport(subArgs, context.JsonOptions, context.AppVersion, context.CancellationToken),
            "import" => ExportImportCommandRunner.RunImport(subArgs, context.JsonOptions, context.CancellationToken),
            "diff" => DiffCommandRunner.Run(subArgs, context.JsonOptions, context.CancellationToken),
            "hooks" => HookCommandRunner.Run(subArgs, context.JsonOptions),
            "backfill-fold" => IndexCommandRunner.RunBackfillFold(subArgs, context.JsonOptions),
            "optimize" => IndexCommandRunner.RunOptimizeFts(subArgs, context.JsonOptions),
            "vacuum" => QueryCommandRunner.RunVacuum(subArgs, context.JsonOptions, context.CancellationToken),
            "validate-config" => CdidxConfigFile.RunValidate(subArgs, context.JsonOptions),
            "config" => subArgs.Length > 0 && subArgs[0] == "show"
                ? CdidxConfigFile.RunShow(subArgs[1..], context.JsonOptions)
                : CommandErrorWriter.WriteJsonOrHuman(
                    ContainsJsonOutputFlag(subArgs),
                    context.JsonOptions,
                    "Unknown config command: use `cdidx config show`.",
                    CommandExitCodes.UsageError,
                    "use `cdidx config show`."),
            "workspace" => WorkspaceCommandRunner.Run(subArgs, context.JsonOptions),
            "db" => DbCommandRunner.Run(subArgs, context.JsonOptions, context.CancellationToken),
            "report" => ReportCommandRunner.Run(subArgs, context.JsonOptions, context.AppVersion),
            "test-extractor" => RunTestExtractor(subArgs, context.JsonOptions),
            _ when IsProjectPathArg(commandName)
                => IndexCommandRunner.Run(originalArgs, context.JsonOptions),
            _ => ShowError(originalArgs, $"Unknown command: {commandName}")
        };

    private static int RunRecipesAlias(string[] subArgs, CommandRunContext context)
    {
        var searchArgs = new string[subArgs.Length + 1];
        searchArgs[0] = "--list-recipes";
        Array.Copy(subArgs, 0, searchArgs, 1, subArgs.Length);
        return QueryCommandRunner.RunSearch(searchArgs, context.JsonOptions, context.CancellationToken);
    }

    private static int RunAuditAlias(string[] subArgs, CommandRunContext context)
    {
        if (subArgs.Length == 0 || subArgs[0].StartsWith("-", StringComparison.Ordinal))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                ContainsJsonOutputFlag(subArgs),
                context.JsonOptions,
                "audit requires a recipe name.",
                CommandExitCodes.UsageError,
                "pass a recipe name after `cdidx audit`, or run `cdidx recipes` to list built-in recipes.");
        }

        var searchArgs = new string[subArgs.Length + 1];
        searchArgs[0] = "--recipe";
        searchArgs[1] = subArgs[0];
        Array.Copy(subArgs, 1, searchArgs, 2, subArgs.Length - 1);
        return QueryCommandRunner.RunSearch(searchArgs, context.JsonOptions, context.CancellationToken);
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
}
