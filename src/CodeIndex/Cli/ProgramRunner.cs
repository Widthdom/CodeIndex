using System.Text.Json;
using System.Text.Json.Serialization;
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

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            ConsoleUi.PrintUsage(showBanner: args.Length > 0);
            GlobalToolLog.Info($"command_complete exit_code={(args.Length == 0 ? CommandExitCodes.UsageError : CommandExitCodes.Success)} help_or_usage=true");
            return args.Length == 0 ? CommandExitCodes.UsageError : CommandExitCodes.Success;
        }

        if (args[0] is "--version" or "-V")
        {
            Console.WriteLine($"cdidx v{appVersion}");
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} version_only=true");
            return CommandExitCodes.Success;
        }

        if (args[0] is "--license" or "license")
        {
            ConsoleUi.PrintLicenseSummary();
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} license_only=true");
            return CommandExitCodes.Success;
        }

        if (args[0] == "--completions")
        {
            var exitCode = RunCompletions(args[1..]);
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command=completions");
            return exitCode;
        }

        if (args.Length > 1 && ArgHelper.WantsHelp(args.AsSpan(1)))
        {
            ConsoleUi.PrintUsage(showBanner: true);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} subcommand_help=true");
            return CommandExitCodes.Success;
        }

        var easterEgg = args.FirstOrDefault(a => a is "--sushi" or "--coffee" or "--ramen" or "--wine" or "--beer" or "--matcha" or "--whisky");
        if (easterEgg != null && !args.Any(a => !a.StartsWith('-')))
        {
            ConsoleUi.PrintEasterEggMessage(easterEgg);
            GlobalToolLog.Info($"command_complete exit_code={CommandExitCodes.Success} easter_egg={easterEgg}");
            return CommandExitCodes.Success;
        }

        try
        {
            if (args[0] is "mcp" or "mcp-server")
            {
                var mcpExitCode = RunMcp(args[1..], appVersion);
                GlobalToolLog.Info($"command_complete exit_code={mcpExitCode} command=mcp");
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
                    _ when IsProjectPathArg(commandName)
                        => IndexCommandRunner.Run(args, jsonOptions),
                    _ => ShowError(args, $"Unknown command: {commandName}")
                };
            }
            GlobalToolLog.Info($"command_complete exit_code={exitCode} command={commandName}");
            return exitCode;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
            {
                GlobalToolLog.Error($"command_complete exit_code={exitCode} handled_exception={ex.GetType().Name}: {ex.Message}");
                return exitCode;
            }

            GlobalToolLog.Error($"unhandled_exception type={ex.GetType().FullName}: {ex}");
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
