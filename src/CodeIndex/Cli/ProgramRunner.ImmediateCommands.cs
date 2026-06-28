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
            var wantsJson = ContainsJsonOutputFlag(args.Skip(1));
            if (wantsJson)
            {
                exitCode = CommandErrorWriter.WriteJsonOrHuman(
                    true,
                    context.JsonOptions,
                    "license does not support --json or --json=<format>.",
                    CommandExitCodes.UsageError,
                    "rerun without --json; license output is human-readable text.",
                    usage: "cdidx license");
                GlobalToolLog.Info($"command_complete exit_code={exitCode} license_json_unsupported=true");
                EmitCommandMetric("license", args, context.StartTimestamp, context.Stopwatch, exitCode);
                return true;
            }

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

            exitCode = RunCompletions(args[1..], context.JsonOptions, args[0] == "completions" ? "completions" : "--completions");
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
            var helpCommand = ResolveSubcommandHelpName(args);
            if (!ConsoleUi.PrintCommandUsage(helpCommand))
            {
                if (IsProjectPathArg(args[0]))
                    ConsoleUi.PrintUsage(showBanner: true);
                else
                {
                    exitCode = ShowError(args, $"Unknown command: {args[0]}", context.JsonOptions);
                    GlobalToolLog.Info($"command_complete exit_code={exitCode} subcommand_help_unknown=true");
                    EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, exitCode);
                    return true;
                }
            }

            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} subcommand_help=true");
            EmitCommandMetric(args[0], args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        exitCode = CommandExitCodes.Success;
        return false;
    }

    private static string ResolveSubcommandHelpName(string[] args)
    {
        if (args.Length > 2
            && string.Equals(args[0], "db", StringComparison.Ordinal)
            && TryGetHelpSubcommand(args.AsSpan(1), out var dbSubcommand))
        {
            return dbSubcommand switch
            {
                "integrity" or "--integrity-check" => "db-integrity",
                "schema" => "db-schema",
                "prune" => "db-prune",
                "checkpoint" => "db-checkpoint",
                "checkpoints" => "db-checkpoints",
                "restore" => "db-restore",
                "restore-backups" => "db-restore-backups",
                _ => "db",
            };
        }

        if (args.Length > 2
            && string.Equals(args[0], "hooks", StringComparison.Ordinal)
            && TryGetHelpSubcommand(args.AsSpan(1), out var hooksSubcommand))
        {
            return hooksSubcommand switch
            {
                "install" => "hooks-install",
                "uninstall" => "hooks-uninstall",
                "status" => "hooks-status",
                _ => "hooks",
            };
        }

        return args[0];
    }

    private static bool TryGetHelpSubcommand(ReadOnlySpan<string> args, out string subcommand)
    {
        foreach (var arg in args)
        {
            if (arg is "--help" or "-h")
                break;
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg != "--integrity-check")
                continue;

            subcommand = arg;
            return true;
        }

        subcommand = string.Empty;
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
}
