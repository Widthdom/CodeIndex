using System.Text.Json;

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

        if (args[0] is "--help-all" or "--help-extended" or "help-all" or "help-extended")
        {
            ConsoleUi.PrintUsageFull(showBanner: true);
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} help_all=true");
            EmitCommandMetric("help-all", args, context.StartTimestamp, context.Stopwatch, exitCode);
            return true;
        }

        if (args[0] == "help")
        {
            exitCode = RunHelpCommand(args[1..], context.JsonOptions);
            GlobalToolLog.Info($"command_complete exit_code={exitCode} conventional_help=true");
            EmitCommandMetric("help", args, context.StartTimestamp, context.Stopwatch, exitCode);
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

    private static int RunHelpCommand(string[] helpArgs, System.Text.Json.JsonSerializerOptions jsonOptions)
    {
        const string usage = "cdidx help <command> [subcommand]";
        if (helpArgs.Length == 1 && helpArgs[0] is "--help" or "-h")
            helpArgs = ["help"];

        var wantsJson = ContainsJsonOutputFlag(helpArgs);
        if (helpArgs.Length == 0 || helpArgs[0].StartsWith("-", StringComparison.Ordinal))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                "help requires a command name.",
                CommandExitCodes.UsageError,
                "run `cdidx --help` to list commands, then rerun as `cdidx help <command>`.",
                usage,
                errorCode: "help_command_required");
        }

        var requestedCommand = helpArgs[0];
        if (!CliCommandCatalog.TryResolvePublicCommand(requestedCommand, out var command))
        {
            var suggestion = ConsoleUi.FindClosestCommand(requestedCommand);
            var hint = suggestion is null
                ? "run `cdidx --help` to list available commands."
                : $"Did you mean: `cdidx help {suggestion}`?";
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                $"unknown help command `{ConsoleUi.FormatBoundedValue(requestedCommand)}`.",
                CommandExitCodes.UsageError,
                hint,
                usage,
                errorCode: "help_command_unknown");
        }

        if (helpArgs.Length > 2)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                $"help accepts at most one nested subcommand; got {ConsoleUi.Counted(helpArgs.Length - 2, "extra argument")}.",
                CommandExitCodes.UsageError,
                "rerun with `cdidx help <command>` or `cdidx help <command> <subcommand>`.",
                usage,
                errorCode: "help_argument_count_invalid");
        }

        var helpTarget = command;
        if (helpArgs.Length == 2)
        {
            var requestedSubcommand = helpArgs[1];
            var subcommand = CliCommandCatalog.NormalizeSubcommandName(command, requestedSubcommand);
            var subcommands = CliCommandCatalog.GetSubcommands(command);
            if (!subcommands.Contains(subcommand, StringComparer.Ordinal))
            {
                var suggestion = ConsoleUi.FindClosestMatch(requestedSubcommand, subcommands);
                var hint = suggestion is null
                    ? $"run `cdidx help {command}` to list its accepted forms."
                    : $"Did you mean: `cdidx help {command} {suggestion}`?";
                return CommandErrorWriter.WriteJsonOrHuman(
                    wantsJson,
                    jsonOptions,
                    $"unknown subcommand `{ConsoleUi.FormatBoundedValue(requestedSubcommand)}` for help command `{ConsoleUi.FormatBoundedValue(command)}`.",
                    CommandExitCodes.UsageError,
                    hint,
                    usage,
                    errorCode: "help_subcommand_unknown");
            }

            if (command == "suggestions" && subcommand == "add")
            {
                SuggestionsCommandRunner.PrintAddHelp();
                return CommandExitCodes.Success;
            }

            helpTarget = ResolveSubcommandHelpName([command, subcommand, "--help"]);
        }

        if (ConsoleUi.PrintCommandUsage(helpTarget))
            return CommandExitCodes.Success;

        return CommandErrorWriter.WriteJsonOrHuman(
            wantsJson,
            jsonOptions,
            $"help metadata is unavailable for `{ConsoleUi.FormatBoundedValue(command)}`.",
            CommandExitCodes.UsageError,
            "run `cdidx --help` to list available command syntax.",
            usage,
            errorCode: "help_metadata_unavailable");
    }

    private static bool TryRunStandaloneUtilityCommand(string[] args, CommandRunContext context, out int exitCode)
    {
        if (args[0] is "--license" or "license")
        {
            var licenseArgs = args.Skip(1).ToArray();
            var wantsJson = licenseArgs.Contains("--json", StringComparer.Ordinal);
            if (licenseArgs.Any(static arg => arg.StartsWith("--json=", StringComparison.Ordinal)))
            {
                exitCode = CommandErrorWriter.WriteJsonOrHuman(
                    true,
                    context.JsonOptions,
                    "license supports --json only; --json=<format> is not supported.",
                    CommandExitCodes.UsageError,
                    "use `cdidx license --json` for the structured license summary.",
                    usage: "cdidx license [--json]");
                GlobalToolLog.Info($"command_complete exit_code={exitCode} license_json_format_unsupported=true");
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

            var unsupportedArg = licenseArgs.FirstOrDefault(static arg => arg != "--json");
            if (unsupportedArg is not null)
            {
                exitCode = CommandErrorWriter.WriteJsonOrHuman(
                    wantsJson,
                    context.JsonOptions,
                    $"Unknown license argument: {unsupportedArg}",
                    CommandExitCodes.InvalidArgument,
                    "use `cdidx license` for human-readable terms or `cdidx license --json` for structured output.",
                    usage: "cdidx license [--json]");
                GlobalToolLog.Info($"command_complete exit_code={exitCode} license_argument_unsupported=true");
                EmitCommandMetric("license", args, context.StartTimestamp, context.Stopwatch, exitCode);
                return true;
            }

            if (wantsJson)
            {
                var payload = ConsoleUi.BuildLicenseJsonResult();
                Console.WriteLine(JsonSerializer.Serialize(
                    payload,
                    CliJsonSerializerContextFactory.Create(context.JsonOptions).LicenseJsonResult));
            }
            else
            {
                ConsoleUi.PrintLicenseSummary();
            }
            exitCode = CommandExitCodes.Success;
            GlobalToolLog.Info($"command_complete exit_code={exitCode} license_only=true json={wantsJson.ToString().ToLowerInvariant()}");
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
        if (args.Length > 2 && args[0] == "suggestions" && args[1] == "add" && ArgHelper.WantsHelp(args.AsSpan(2)))
        {
            SuggestionsCommandRunner.PrintAddHelp();
            exitCode = CommandExitCodes.Success;
            return true;
        }

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
            && TryGetHelpSubcommand(args.AsSpan(1), out var requestedSubcommand))
        {
            var command = CliCommandCatalog.NormalizePublicCommandName(args[0]);
            var subcommand = CliCommandCatalog.NormalizeSubcommandName(command, requestedSubcommand);
            var nestedHelpName = $"{command}-{subcommand}";
            if (ConsoleUi.GetUsageLine(nestedHelpName) is not null)
                return nestedHelpName;
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
