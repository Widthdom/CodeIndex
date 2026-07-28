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
    internal static void EnsureRedirectedStdoutUsesUtf8()
    {
        using var ownership = ConsoleStreamOwnership.Enter();
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

    private enum QueryCommandTokenRole
    {
        None,
        CommandOptionValue,
        FirstQueryLiteral,
    }

    private static string[] InsertQueryLiteralSentinelForNonLogGlobalOption(string commandName, string[] subArgs)
    {
        if (!CommandAcceptsQueryLiteral(commandName))
            return subArgs;

        for (var i = 0; i < subArgs.Length; i++)
        {
            if (subArgs[i] == "--")
                return subArgs;
            if (!IsNonLogGlobalOptionToken(subArgs[i]))
                continue;
            if (GetQueryCommandTokenRole(commandName, subArgs, i) != QueryCommandTokenRole.FirstQueryLiteral)
                continue;

            var rewritten = new List<string>(subArgs.Length + 1);
            for (var j = 0; j < i; j++)
                rewritten.Add(subArgs[j]);
            rewritten.Add("--");
            for (var j = i; j < subArgs.Length; j++)
                rewritten.Add(subArgs[j]);
            return rewritten.ToArray();
        }

        return subArgs;
    }

    private static bool ShouldPreserveQueryCommandToken(string[] args, int index)
    {
        var role = GetQueryCommandTokenRole(args, index);
        return ShouldPreserveQueryCommandToken(args, index, role);
    }

    private static bool ShouldPreserveQueryCommandToken(string commandName, string[] subArgs, int index)
    {
        var role = GetQueryCommandTokenRole(commandName, subArgs, index);
        return ShouldPreserveQueryCommandToken(subArgs, index, role);
    }

    private static bool ShouldPreserveQueryCommandToken(string[] args, int index, QueryCommandTokenRole role)
    {
        if (role == QueryCommandTokenRole.CommandOptionValue)
            return true;
        if (role != QueryCommandTokenRole.FirstQueryLiteral)
            return false;
        return !IsSeparatedNonLogGlobalValueOptionWithConsumableValue(args, index);
    }

    private static bool IsSeparatedNonLogGlobalValueOptionWithConsumableValue(string[] args, int index)
    {
        if (index + 1 >= args.Length)
            return false;

        var value = args[index + 1];
        return args[index] switch
        {
            "--color" => ConsoleUi.TryParseColorMode(value, out _),
            "--palette" => ConsoleUi.TryParseColorPalette(value, out _),
            "--metrics" => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal),
            "--trace" => value is "none" or "stderr" or "file",
            _ => false,
        };
    }

    private static QueryCommandTokenRole GetQueryCommandTokenRole(string[] args, int index)
    {
        if (!TryFindCommandBefore(args, index, out var commandIndex, out var commandName))
            return QueryCommandTokenRole.None;

        return GetQueryCommandTokenRole(commandName, args[(commandIndex + 1)..], index - commandIndex - 1);
    }

    private static bool TryFindCommandBefore(string[] args, int index, out int commandIndex, out string commandName)
    {
        commandIndex = -1;
        commandName = string.Empty;

        for (var i = 0; i < index; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return false;
            if (TryGetInlineOptionName(arg, out var inlineName) && TopLevelValueOptionNames.Contains(inlineName))
                continue;
            if (TopLevelValueOptionNames.Contains(arg))
            {
                i++;
                continue;
            }
            if (NonLogGlobalOptionNames.Contains(arg))
                continue;
            if (!CliFlagSchema.AllCommands.Contains(arg))
                return false;

            commandIndex = i;
            commandName = arg;
            return true;
        }

        return false;
    }

    private static QueryCommandTokenRole GetQueryCommandTokenRole(string commandName, string[] subArgs, int targetIndex)
    {
        var (withValues, flagOnly) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing(commandName);
        if (targetIndex > 0)
        {
            var previousArg = NormalizeCommandOptionToken(subArgs[targetIndex - 1], withValues, flagOnly, out var previousHasInlineValue);
            if (!previousHasInlineValue && withValues.Contains(previousArg))
                return QueryCommandTokenRole.CommandOptionValue;
        }

        if (!CommandAcceptsQueryLiteral(commandName))
            return QueryCommandTokenRole.None;

        if (IsInspectPathLineMode(commandName, subArgs))
        {
            var targetArg = NormalizeCommandOptionToken(subArgs[targetIndex], withValues, flagOnly, out _);
            if (withValues.Contains(targetArg) || flagOnly.Contains(targetArg))
                return QueryCommandTokenRole.None;
        }

        for (var i = 0; i < targetIndex; i++)
        {
            var arg = subArgs[i];
            if (arg == "--")
                return i + 1 == targetIndex ? QueryCommandTokenRole.FirstQueryLiteral : QueryCommandTokenRole.None;

            var normalizedArg = NormalizeCommandOptionToken(arg, withValues, flagOnly, out var hasInlineValue);
            if (withValues.Contains(normalizedArg))
            {
                if (hasInlineValue)
                {
                    if (normalizedArg == "--query")
                        return QueryCommandTokenRole.None;
                    continue;
                }
                if (i + 1 == targetIndex)
                    return QueryCommandTokenRole.CommandOptionValue;
                if (normalizedArg == "--query")
                    return QueryCommandTokenRole.None;
                if (i + 1 < targetIndex)
                {
                    i++;
                    continue;
                }

                return QueryCommandTokenRole.None;
            }

            if (flagOnly.Contains(normalizedArg))
                continue;

            return QueryCommandTokenRole.None;
        }

        return QueryCommandTokenRole.FirstQueryLiteral;
    }

    private static bool IsInspectPathLineMode(string commandName, string[] subArgs)
    {
        if (!string.Equals(commandName, "inspect", StringComparison.Ordinal))
            return false;

        var (withValues, flagOnly) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing(commandName);
        var pathSeen = false;
        var lineSeen = false;
        for (var i = 0; i < subArgs.Length; i++)
        {
            var arg = subArgs[i];
            if (arg == "--")
                break;

            var normalizedArg = NormalizeCommandOptionToken(arg, withValues, flagOnly, out var hasInlineValue);
            if (!withValues.Contains(normalizedArg))
                continue;

            pathSeen |= normalizedArg == "--path";
            lineSeen |= normalizedArg == "--line";
            if (!hasInlineValue && i + 1 < subArgs.Length)
                i++;
        }

        return pathSeen && lineSeen;
    }

    private static bool CommandAcceptsQueryLiteral(string commandName) =>
        CliFlagSchema.GetAcceptedFlagNamesForCommand(commandName).Contains("--query");

    private static bool IsNonLogGlobalOptionToken(string arg)
    {
        if (NonLogGlobalOptionNames.Contains(arg))
            return true;
        return TryGetInlineOptionName(arg, out var name) && NonLogGlobalOptionNames.Contains(name);
    }

    private static string NormalizeCommandOptionToken(
        string arg,
        IReadOnlySet<string> withValues,
        IReadOnlySet<string> flagOnly,
        out bool hasInlineValue)
    {
        hasInlineValue = false;
        if (!TryGetInlineOptionName(arg, out var name))
            return arg;

        if (withValues.Contains(name))
        {
            hasInlineValue = true;
            return name;
        }

        if (flagOnly.Contains(name) && string.Equals(name, "--json", StringComparison.Ordinal))
            return name;

        return arg;
    }

    private static bool TryGetInlineOptionName(string arg, out string name)
    {
        var equalsIndex = arg.IndexOf('=');
        if (equalsIndex <= 0)
        {
            name = string.Empty;
            return false;
        }

        name = arg[..equalsIndex];
        return name.StartsWith("-", StringComparison.Ordinal);
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
            if (arg is "--quiet" or "-q" or "--silent"
                && GetQueryCommandTokenRole(args, i) != QueryCommandTokenRole.CommandOptionValue)
            {
                quiet = true;
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
        return quiet;
    }

    internal static bool TryConsumePrettyJsonFlag(ref string[] args)
    {
        if (args.Length == 0)
            return false;

        var hasExplicitPrettyJsonOutput = HasExplicitPrettyJsonOutputSelection(args);
        var kept = new List<string>(args.Length);
        var pretty = false;
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
            if (arg == "--pretty"
                && GetQueryCommandTokenRole(args, i) != QueryCommandTokenRole.CommandOptionValue
                && hasExplicitPrettyJsonOutput)
            {
                pretty = true;
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--pretty")
            {
                pretty = true;
                continue;
            }

            kept.Add(arg);
        }

        args = kept.ToArray();
        return pretty;
    }

    internal static bool TryConsumeGlobalLogFlags(
        ref string[] args,
        out IReadOnlyDictionary<string, string> environment,
        out string error)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        environment = overrides;
        error = string.Empty;
        var kept = new List<string>(args.Length);
        var passthrough = false;
        var searchCommandSeen = false;
        var searchQuerySeen = false;
        var pendingSearchOptionValue = false;
        var pendingSearchOptionValueIsQuery = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }

            if (searchCommandSeen && pendingSearchOptionValue)
            {
                if (pendingSearchOptionValueIsQuery)
                    searchQuerySeen = true;
                pendingSearchOptionValue = false;
                pendingSearchOptionValueIsQuery = false;
                kept.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }

            if (searchCommandSeen && !searchQuerySeen && IsSearchGlobalLogFlagLiteral(args, i, arg))
            {
                searchQuerySeen = true;
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
                overrides[GlobalToolLog.LogFormatEnvironmentVariable] = format;
                continue;
            }

            if (TryConsumeValueFlag(args, ref i, arg, "--log-retain-count", out var retainCount))
            {
                if (!int.TryParse(retainCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                {
                    error = "--log-retain-count must be a positive integer.";
                    return false;
                }
                overrides[GlobalToolLog.LogRetainEnvironmentVariable] = parsed.ToString(CultureInfo.InvariantCulture);
                continue;
            }

            if (TryConsumeValueFlag(args, ref i, arg, "--log-max-size-mb", out var maxSizeMb))
            {
                if (!int.TryParse(maxSizeMb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    || parsed is < 1 or > GlobalToolLog.MaxLogSizeMb)
                {
                    error = $"--log-max-size-mb must be an integer between 1 and {GlobalToolLog.MaxLogSizeMb}.";
                    return false;
                }
                overrides[GlobalToolLog.LogMaxSizeMbEnvironmentVariable] = parsed.ToString(CultureInfo.InvariantCulture);
                continue;
            }

            if (arg == "search")
            {
                searchCommandSeen = true;
                kept.Add(arg);
                continue;
            }
            kept.Add(arg);
            if (searchCommandSeen && !searchQuerySeen)
                TrackSearchQueryState(args, i, arg, ref searchQuerySeen, ref pendingSearchOptionValue, ref pendingSearchOptionValueIsQuery);
        }

        args = kept.ToArray();
        return true;
    }

    private static bool IsSearchGlobalLogFlagLiteral(string[] args, int index, string arg)
    {
        static bool NextTokenLooksLikeSearchOption(string[] args, int index)
            => index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal);

        if (arg is "--log-format" or "--log-retain-count" or "--log-max-size-mb")
            return NextTokenLooksLikeSearchOption(args, index);

        return (arg.StartsWith("--log-format=", StringComparison.Ordinal) ||
                arg.StartsWith("--log-retain-count=", StringComparison.Ordinal) ||
                arg.StartsWith("--log-max-size-mb=", StringComparison.Ordinal)) &&
               NextTokenLooksLikeSearchOption(args, index);
    }

    private static void TrackSearchQueryState(
        string[] args,
        int index,
        string arg,
        ref bool searchQuerySeen,
        ref bool pendingSearchOptionValue,
        ref bool pendingSearchOptionValueIsQuery)
    {
        if (TryClassifySearchValueTakingOption(arg, out var hasInlineValue, out var valueIsQuery))
        {
            if (hasInlineValue)
            {
                if (valueIsQuery)
                    searchQuerySeen = true;
            }
            else if (index + 1 < args.Length)
            {
                pendingSearchOptionValue = true;
                pendingSearchOptionValueIsQuery = valueIsQuery;
            }
            return;
        }

        if (!arg.StartsWith("-", StringComparison.Ordinal))
            searchQuerySeen = true;
    }

    private static bool TryClassifySearchValueTakingOption(string arg, out bool hasInlineValue, out bool valueIsQuery)
    {
        hasInlineValue = false;
        valueIsQuery = false;

        var separator = arg.IndexOf('=');
        var optionName = separator > 0 ? arg[..separator] : arg;
        if (!SearchValueTakingOptions.Contains(optionName))
            return false;

        hasInlineValue = separator > 0;
        valueIsQuery = optionName == "--query";
        return true;
    }

    private static readonly HashSet<string> SearchValueTakingOptions =
        BuildSearchValueTakingOptions();

    private static HashSet<string> BuildSearchValueTakingOptions()
    {
        var (commandValues, _) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("search");
        commandValues.UnionWith(CliFlagSchema.GetTopLevelValueOptionNames());
        return commandValues;
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
}
