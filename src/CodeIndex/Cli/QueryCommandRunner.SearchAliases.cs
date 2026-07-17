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
        if (subArgs.Length == 0 || subArgs[0].StartsWith("-", StringComparison.Ordinal))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                ProgramRunner.ContainsJsonOutputFlag(subArgs),
                jsonOptions,
                "audit requires a recipe name.",
                CommandExitCodes.UsageError,
                "pass a recipe name after `cdidx audit`, or run `cdidx recipes` to list built-in recipes.");
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

        return RunSearch(searchArgs, jsonOptions, cancellationToken);
    }
}
