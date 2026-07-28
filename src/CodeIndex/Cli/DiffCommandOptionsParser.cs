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
        var includeContent = false;
        var limit = DefaultLimit;
        var offset = 0;
        var offsetExplicit = false;
        int? maxJsonBytes = null;
        string? cursor = null;
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
                case "--include-content":
                    includeContent = true;
                    break;
                case "--max-json-bytes" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedMaxJsonBytes)
                        || parsedMaxJsonBytes < DiffCommandRunner.MinDiffJsonBytes)
                    {
                        parseError = $"--max-json-bytes must be at least {DiffCommandRunner.MinDiffJsonBytes}";
                    }
                    else if (parsedMaxJsonBytes > DiffCommandRunner.MaxDiffJsonBytes)
                    {
                        parseError = $"--max-json-bytes must be less than or equal to {DiffCommandRunner.MaxDiffJsonBytes}";
                    }
                    else
                    {
                        maxJsonBytes = parsedMaxJsonBytes;
                    }
                    break;
                case "--max-json-bytes":
                    parseError = "--max-json-bytes requires a value";
                    break;
                case "--cursor" when i + 1 < args.Length:
                    cursor = args[++i];
                    if (cursor.Length > DiffCursorCodec.MaxCursorLength)
                        parseError = $"--cursor must not exceed {DiffCursorCodec.MaxCursorLength} characters";
                    break;
                case "--cursor":
                    parseError = "--cursor requires a value";
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
                case "--offset" when i + 1 < args.Length:
                    offsetExplicit = true;
                    if (!int.TryParse(args[++i], out offset) || offset < 0)
                        parseError = "--offset requires a non-negative integer";
                    break;
                case "--offset":
                    parseError = "--offset requires a value";
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
        if (parseError is null && includeContent && (!detailed || !json || summaryOnly))
            parseError = "--include-content requires --detailed --json and cannot be combined with --summary-only";
        if (parseError is null && maxJsonBytes.HasValue && !(json || summaryOnly))
            parseError = "--max-json-bytes is only supported with JSON diff output";
        if (parseError is null && cursor is not null && (!detailed || !json || summaryOnly))
            parseError = "--cursor requires --detailed --json and cannot be combined with --summary-only";
        if (parseError is null && cursor is not null && offsetExplicit)
            parseError = "--cursor cannot be combined with --offset";
        if (parseError is null && cursor is not null)
        {
            var selectionFingerprint = DiffCursorCodec.CreateSelectionFingerprint(
                dbs[0],
                dbs[1],
                includeContent);
            if (!DiffCursorCodec.TryDecode(cursor, selectionFingerprint, out offset, out var cursorError))
                parseError = cursorError;
        }
        if (parseError is null && offset > int.MaxValue - limit)
            parseError = "--offset is too large for the requested --limit";

        return new DiffCommandOptions
        {
            LeftDb = dbs.Count > 0 ? dbs[0] : null,
            RightDb = dbs.Count > 1 ? dbs[1] : null,
            Json = json,
            Detailed = detailed,
            SummaryOnly = summaryOnly,
            IncludeContent = includeContent,
            Limit = limit,
            Offset = offset,
            OffsetExplicit = offsetExplicit,
            MaxJsonBytes = maxJsonBytes,
            Cursor = cursor,
            ParseError = parseError,
        };
    }
}
