using System.Text.Json.Nodes;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const string LargeFileDecompositionPlanPath = "docs/large-file-decomposition-plan.md";

    private static void AddLargeFileDecompositionPlanJsonField(JsonObject payload, QueryCommandOptions options)
    {
        if (!TryResolveLargeFileDecompositionPlanPath(options, out var planPath))
            return;

        payload["decomposition_plan"] = new JsonObject
        {
            ["path"] = planPath,
            ["description"] = "Staged plan for oversized source files and partial-class surfaces.",
        };
    }

    private static void WriteLargeFileDecompositionPlanHintIfAvailable(QueryCommandOptions options)
    {
        if (TryResolveLargeFileDecompositionPlanPath(options, out var planPath))
            CommandErrorWriter.WriteStderr($"Hint: decomposition plan: {planPath}");
    }

    private static bool TryResolveLargeFileDecompositionPlanPath(QueryCommandOptions options, out string planPath)
    {
        planPath = string.Empty;
        string? projectRoot;
        try
        {
            projectRoot = DbPathResolver.ResolveProjectRootForQuery(options.DbPath, options.DbPathExplicit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(projectRoot))
            return false;

        try
        {
            var candidate = Path.Combine(projectRoot, LargeFileDecompositionPlanPath);
            if (!File.Exists(candidate))
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        planPath = LargeFileDecompositionPlanPath;
        return true;
    }
}
