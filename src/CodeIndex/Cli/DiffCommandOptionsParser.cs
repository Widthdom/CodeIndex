namespace CodeIndex.Cli;

internal static class DiffCommandOptionsParser
{
    internal const int DefaultLimit = 20;

    internal static DiffCommandOptions Parse(string[] args, int maxLimit)
    {
        var dbs = new List<string>(2);
        var json = false;
        var detailed = false;
        var summaryOnly = false;
        var limit = DefaultLimit;
        string? parseError = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    return new DiffCommandOptions { ShowHelp = true };
                case "--json":
                    json = true;
                    break;
                case "--detailed":
                    detailed = true;
                    break;
                case "--summary-only":
                    summaryOnly = true;
                    break;
                case "--limit" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out limit) || limit < 0)
                        parseError = "--limit requires a non-negative integer";
                    else if (limit > maxLimit)
                    {
                        parseError = $"--limit must be less than or equal to {maxLimit}";
                        limit = DefaultLimit;
                    }
                    break;
                case "--limit":
                    parseError = "--limit requires a value";
                    break;
                default:
                    if (arg.StartsWith('-'))
                        parseError = $"diff does not support option: '{arg}'";
                    else if (dbs.Count >= 2)
                        parseError = $"diff accepts exactly two database paths; unexpected argument: '{arg}'";
                    else
                        dbs.Add(arg);
                    break;
            }

            if (parseError is not null)
                break;
        }

        if (parseError is null && dbs.Count != 2)
            parseError = "diff requires exactly two database paths";

        return new DiffCommandOptions
        {
            LeftDb = dbs.Count > 0 ? dbs[0] : null,
            RightDb = dbs.Count > 1 ? dbs[1] : null,
            Json = json,
            Detailed = detailed,
            SummaryOnly = summaryOnly,
            Limit = limit,
            ParseError = parseError,
        };
    }
}
