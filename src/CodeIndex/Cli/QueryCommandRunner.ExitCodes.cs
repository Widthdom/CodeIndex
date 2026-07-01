using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int ZeroResultExitCode(QueryCommandOptions options)
        => options.StrictNotFound ? CommandExitCodes.NotFound : CommandExitCodes.Success;

    private static int UnusedZeroResultExitCode(QueryCommandOptions options, UnusedDefaultSuppressionResult suppression)
        => !options.StrictNotFound && suppression.Applied && GetUnusedSuppressedCount(suppression) > 0
            ? CommandExitCodes.Success
            : ZeroResultExitCode(options);

    private static bool IsEmptySymbolAnalysis(SymbolAnalysisResult analysis)
        => analysis.File == null
           && analysis.Definitions.Count == 0
           && analysis.NearbySymbols.Count == 0
           && analysis.References.Count == 0
           && analysis.Callers.Count == 0
           && analysis.Callees.Count == 0;
}
