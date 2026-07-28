namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static readonly HashSet<string> FlagOnlyOptions = BuildFlagOnlyOptions();

    private static readonly HashSet<string> ValueTakingOptions =
        CliFlagSchema.GetAllValueTakingOptionNames();

    private static readonly HashSet<string> InlineValueOptions =
        new(ValueTakingOptions.Concat(["--json"]), StringComparer.Ordinal);

    private static HashSet<string> BuildFlagOnlyOptions()
    {
        var options = CliFlagSchema.GetAllFlagOnlyOptionNames();
        options.UnionWith(["--help", "-h", "--version", "-V"]);
        return options;
    }
}
