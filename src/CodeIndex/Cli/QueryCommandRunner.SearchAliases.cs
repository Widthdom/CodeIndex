using System.Text.Json;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static int RunRecipes(
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        var recipeArgs = subArgs.Length > 0 && string.Equals(subArgs[0], "list", StringComparison.Ordinal)
            ? subArgs[1..]
            : subArgs;
        return RunRecipeList(recipeArgs, jsonOptions, cancellationToken);
    }

    internal static int RunAudit(
        string[] subArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        if (subArgs.Length > 0 && subArgs[0] is "baseline-export" or "baseline-compare" or "baseline-review")
            return RunAuditBaseline([subArgs[0]["baseline-".Length..], .. subArgs[1..]], jsonOptions, cancellationToken);
        if (HasAuditAllFlag(subArgs))
            return RunAuditAll(subArgs, jsonOptions, cancellationToken);
        if (subArgs.TakeWhile(arg => arg != "--").Any(arg => arg == "--continuation"
            || arg.StartsWith("--continuation=", StringComparison.Ordinal)))
            return WriteAuditContinuationError(subArgs, jsonOptions);

        if (subArgs.Length == 0 || subArgs[0].StartsWith("-", StringComparison.Ordinal))
        {
            var options = ParseArgs(
                subArgs,
                jsonDefault: false,
                allowNamedQuery: true,
                allowIssueDraftsFormat: true,
                applySearchSourceDefaults: true);
            options.InvocationContext = QueryCommandInvocationContext.Audit;
            options.InvocationJsonOptions = jsonOptions;
            options.InvocationMachineErrorOutputRequested = ProgramRunner.ContainsJsonOutputFlag(subArgs);
            WriteUsageError(
                "audit requires a recipe name.",
                options,
                "pass a recipe name after `cdidx audit`, or run `cdidx recipes` to list built-in recipes.");
            return CommandExitCodes.UsageError;
        }

        var hasSummaryOnly = false;
        var hasExplicitOutputFormat = false;
        for (var i = 1; i < subArgs.Length && subArgs[i] != "--"; i++)
        {
            var arg = subArgs[i];
            hasSummaryOnly |= arg == "--summary-only";
            hasExplicitOutputFormat |= arg is "--compact" or "--format"
                || arg.StartsWith("--format=", StringComparison.Ordinal);
        }

        var addCompactSummaryFormat = hasSummaryOnly && !hasExplicitOutputFormat;
        var searchArgs = new string[subArgs.Length + 1 + (addCompactSummaryFormat ? 2 : 0)];
        searchArgs[0] = "--recipe";
        searchArgs[1] = subArgs[0];
        if (!addCompactSummaryFormat)
        {
            Array.Copy(subArgs, 1, searchArgs, 2, subArgs.Length - 1);
        }
        else
        {
            var passthroughIndex = Array.IndexOf(subArgs, "--", 1);
            var insertIndex = passthroughIndex >= 0 ? passthroughIndex : subArgs.Length;
            Array.Copy(subArgs, 1, searchArgs, 2, insertIndex - 1);
            searchArgs[insertIndex + 1] = "--format";
            searchArgs[insertIndex + 2] = "compact";
            Array.Copy(
                subArgs,
                insertIndex,
                searchArgs,
                insertIndex + 3,
                subArgs.Length - insertIndex);
        }

        return RunSearchCore(
            searchArgs,
            subArgs,
            QueryCommandInvocationContext.Audit,
            jsonOptions,
            cancellationToken);
    }

    private static bool HasAuditAllFlag(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count && args[i] != "--"; i++)
        {
            if (args[i] == "--all")
                return true;
        }

        return false;
    }
}
