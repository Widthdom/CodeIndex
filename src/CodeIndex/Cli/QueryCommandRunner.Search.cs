using System.Text.Json;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunSearch(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default) =>
        RunSearchCore(
            cmdArgs,
            cmdArgs,
            QueryCommandInvocationContext.Search,
            jsonOptions,
            cancellationToken);

    internal static int RunRecipeList(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        var unexpectedPositionals = FindUnexpectedRecipePositionals(cmdArgs);
        if (unexpectedPositionals.Count > 0)
        {
            return CommandErrorWriter.Write(
                $"{ConsoleUi.Counted(unexpectedPositionals.Count, "unexpected extra positional argument")} for recipes: {string.Join(", ", unexpectedPositionals.Select(value => $"`{value}`"))}.",
                CommandExitCodes.UsageError,
                "remove the extra positional arguments, or pass a recipe-list filter with --query <text>.",
                GetUsageLineOrThrow("recipes"));
        }

        var searchArgs = new string[cmdArgs.Length + 1];
        searchArgs[0] = "--list-recipes";
        Array.Copy(cmdArgs, 0, searchArgs, 1, cmdArgs.Length);
        return RunSearchCore(
            searchArgs,
            cmdArgs,
            QueryCommandInvocationContext.Recipes,
            jsonOptions,
            cancellationToken);
    }

    private static int RunSearchCore(
        string[] cmdArgs,
        string[] validationArgs,
        QueryCommandInvocationContext invocationContext,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        return TryPrepareSearchRoute(
            cmdArgs,
            validationArgs,
            invocationContext,
            jsonOptions,
            cancellationToken,
            out var route)
                ? ExecuteSearchRoute(route)
                : CommandExitCodes.UsageError;
    }

    private static bool TryPrepareSearchRoute(
        string[] cmdArgs,
        string[] validationArgs,
        QueryCommandInvocationContext invocationContext,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        out SearchRoutePlan route)
    {
        route = default;
        var previewOptionError = ValidatePreviewOptions("search", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return false;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            allowIssueDraftsFormat: true,
            applySearchSourceDefaults: true);
        options.InvocationContext = invocationContext;
        options.InvocationJsonOptions = jsonOptions;
        options.InvocationMachineErrorOutputRequested = ProgramRunner.ContainsJsonOutputFlag(validationArgs);
        if (ReferenceEquals(invocationContext, QueryCommandInvocationContext.Search)
            && TryWriteSearchFindAlternativeError(validationArgs, options, jsonOptions))
            return false;
        var acceptedFlags = CliFlagSchema.GetAcceptedFlagNamesForCommand(invocationContext.ValidationCommandName);
        if (invocationContext == QueryCommandInvocationContext.Audit)
            acceptedFlags = new HashSet<string>(acceptedFlags, StringComparer.Ordinal) { "--progress" };
        if (TryWriteUnsupportedOptionError(
            invocationContext,
            validationArgs,
            acceptedFlags,
            options,
            options.Query,
            invocationContext.StructuredMachineUsageErrors ? jsonOptions : null))
            return false;
        if (TryWriteParseError(
            options,
            invocationContext,
            options.LanguageValidationError
                || invocationContext.StructuredMachineUsageErrors
                || options.Json
                && options.ParseError is not null
                && TryExtractNonPositiveMaxJsonBytes(options.ParseError, out _, out _, out _)
                ? jsonOptions
                : null))
            return false;
        if (!TryResolveSearchExactMode(options, out var exact, out var exactError, out var exactHint))
        {
            var message = StripErrorPrefix(exactError!);
            if (invocationContext.StructuredMachineUsageErrors)
            {
                WriteUsageError(message, options, exactHint!);
                return false;
            }

            CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                message,
                CommandExitCodes.UsageError,
                exactHint,
                usage: null,
                errorCode: CommandErrorCodes.UsageError,
                command: invocationContext.CommandName,
                omitNullUsage: true);
            return false;
        }
        if (!TryValidateSearchOptions(options, exact, invocationContext))
            return false;
        if (options.Progress && (!options.All || invocationContext != QueryCommandInvocationContext.Audit))
            return RejectSearchUsage(options, "--progress requires audit --all.", "Use `cdidx audit --all --progress`, or remove --progress.");
        if (!TryCreateSearchRoutePlan(cmdArgs, options, exact, cancellationToken, out route))
            return false;

        return true;
    }

    private static List<string> FindUnexpectedRecipePositionals(string[] args)
    {
        var unexpected = new List<string>();
        var (withValues, flagOnly) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("recipes");
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                unexpected.AddRange(args[(i + 1)..]);
                break;
            }

            var equalsIndex = arg.IndexOf('=');
            var optionName = equalsIndex > 0 ? arg[..equalsIndex] : arg;
            if (withValues.Contains(optionName))
            {
                if (equalsIndex < 0 && i + 1 < args.Length)
                    i++;
                continue;
            }
            if (flagOnly.Contains(optionName) || arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            unexpected.Add(arg);
        }

        return unexpected;
    }
}
