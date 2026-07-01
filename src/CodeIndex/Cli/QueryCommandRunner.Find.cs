using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const string FindUsage = "Usage: cdidx find <query> (--path <glob>|--all) [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--before <n>] [--after <n>] [--snippet-lines <n>] [--focus-line <line>] [--focus-column <n>] [--max-line-width <n>] [--exact] [--regex] [--count]\n       cdidx find --query <query> (--path <glob>|--all) [...]\n       cdidx find [options] -- <query>";

    public static int RunFind(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var preparedFindArgs = PrepareFindArgs(cmdArgs, out var preparationError);
        if (preparationError != null)
        {
            CommandErrorWriter.WriteStderr(preparationError);
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        var findValidationError = ValidateFindArgs(preparedFindArgs);
        if (findValidationError != null)
        {
            CommandErrorWriter.WriteStderr(findValidationError);
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        var options = ParseArgs(
            preparedFindArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false);
        if (options.ParseError != null)
        {
            CommandErrorWriter.WriteStderr(options.ParseError);
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.Query is not null && string.IsNullOrWhiteSpace(options.Query))
        {
            CommandErrorWriter.WriteStderr("Error: find query cannot be empty or whitespace-only");
            CommandErrorWriter.WriteStderr("Hint: Pass a non-empty value after `find`; empty or whitespace-only arguments (e.g. `\"\"` or `\"   \"`) are rejected.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            CommandErrorWriter.WriteStderr("Error: find requires a query argument");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
        {
            CommandErrorWriter.WriteStderr($"Error: {QueryLimits.FormatQueryTooLongError()}");
            CommandErrorWriter.WriteStderr("Hint: Shorten the find text or split generated input into smaller queries before running `cdidx find`.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        if (options.PathPatterns.Count == 0 && !options.All)
        {
            CommandErrorWriter.WriteStderr("Error: find requires at least one --path <glob> or explicit --all to scope the search");
            CommandErrorWriter.WriteStderr("Hint: use --path <glob> for a bounded file set, or --all to scan all indexed files with safety caps.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.PathPatterns.Count > 0 && options.All)
        {
            CommandErrorWriter.WriteStderr("Error: find accepts either --path <glob> or --all, not both");
            CommandErrorWriter.WriteStderr("Hint: remove --all when using explicit path filters, or remove --path to scan all indexed files with caps.");
            CommandErrorWriter.WriteStderr(FindUsage);
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var pathPatterns = options.All ? null : options.PathPatterns;
            var candidateFileLimit = options.All ? FindAllCandidateFileLimit : (int?)null;
            var lineLimit = options.All ? FindAllLineScanLimit : (int?)null;
            if (options.CountOnly)
            {
                FindCountResult counts;
                try
                {
                    counts = reader.CountFindInFiles(options.Query, options.Lang, pathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact, options.FocusLine, options.FocusColumn, options.Regex, candidateFileLimit, lineLimit);
                }
                catch (Exception ex) when (options.Regex && (ex is ArgumentException || ex is RegexMatchTimeoutException))
                {
                    return ex is RegexMatchTimeoutException timeout
                        ? WriteFindRegexTimeoutError(timeout, jsonOptions, options.Json)
                        : WriteFindInvalidRegexError(ex, jsonOptions, options.Json);
                }
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        var payload = BuildCountJsonPayload(
                            reader,
                            jsonOptions,
                            count: 0,
                            files: 0,
                            query: options.Query,
                            queryOptions: options,
                            extraFields: payload => AddFindScanJsonFields(payload, counts.Scan));
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        WriteFindScanSummary(counts.Scan);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(
                        reader,
                        jsonOptions,
                        counts.Count,
                        counts.FileCount,
                        query: options.Query,
                        queryOptions: options,
                        extraFields: payload => AddFindScanJsonFields(payload, counts.Scan));
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                    WriteFindScanSummary(counts.Scan);
                }
                return CommandExitCodes.Success;
            }

            var (contextBefore, contextAfter, snippetLines) = ResolveFindContext(options, preparedFindArgs);
            FindResults findResults;
            try
            {
                findResults = reader.FindInFiles(options.Query, options.Limit, options.Lang, pathPatterns, options.ExcludePaths, options.ExcludeTests, contextBefore, contextAfter, options.Exact, options.MaxLineWidth, options.FocusLine, options.FocusColumn, options.Regex, candidateFileLimit, lineLimit);
            }
            catch (ArgumentException ex) when (options.Regex)
            {
                return WriteFindInvalidRegexError(ex, jsonOptions, options.Json);
            }
            catch (RegexMatchTimeoutException ex) when (options.Regex)
            {
                return WriteFindRegexTimeoutError(ex, jsonOptions, options.Json);
            }
            var results = findResults.Results;
            if (results.Count == 0)
            {
                var candidateFileCount = findResults.Scan.CandidateFiles;
                if (options.Json)
                {
                    if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                    {
                        CommandOutputWriter.WriteJson(
                            new List<FileFindResult>(),
                            CliJsonSerializerContextFactory.Create(jsonOptions).ListFileFindResult);
                        return ZeroResultExitCode(options);
                    }
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return ZeroResultExitCode(options);
                    var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "results", queryOptions: options, extraFields: payload =>
                    {
                        payload["query"] = options.Query;
                        payload["path"] = JsonSerializer.SerializeToNode(options.PathPatterns, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
                        payload["exclude_tests"] = options.ExcludeTests;
                        payload["before"] = contextBefore;
                        payload["after"] = contextAfter;
                        if (snippetLines.HasValue)
                            payload["snippet_lines"] = snippetLines.Value;
                        payload["exact"] = options.Exact;
                        payload["regex"] = options.Regex;
                        payload["file_count"] = candidateFileCount;
                    });
                    AddFindScanJsonFields(payload, findResults.Scan);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No matches found", options));
                    if (candidateFileCount > 0)
                    {
                        var fileText = ConsoleUi.Counted(candidateFileCount, "file");
                        WriteZeroResultHints(options, reader, filterHint: $"--path matched {fileText}, but the query did not match their contents. Try a broader query or check the query syntax.");
                    }
                    else
                    {
                        WriteZeroResultHints(options, reader, filterHint: "try broadening --path or adding another --path value; --path is required for find.");
                    }
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.Line, r.Column, $"find match: {options.Query}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.Line, r.Column, $"find match: {options.Query}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.Line, r.Column, $"find match: {options.Query}", "find")), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatJson && options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    CommandOutputWriter.WriteJson(
                        results,
                        CliJsonSerializerContextFactory.Create(jsonOptions).ListFileFindResult);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).FileFindResult));
            }
            else
            {
                foreach (var r in results)
                {
                    Console.WriteLine($"{r.Path}:{r.Line}:{r.Column}");
                    WriteNumberedExcerpt(r.StartLine, r.Snippet);
                    Console.WriteLine();
                }
                var fileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} matches in {fileCount} files)");
                WriteFindScanSummary(findResults.Scan);
            }
            return CommandExitCodes.Success;
        });
    }

    private static int WriteFindInvalidRegexError(Exception ex, JsonSerializerOptions jsonOptions, bool json)
        => CommandErrorWriter.WriteJsonOrHuman(
            json,
            jsonOptions,
            $"invalid regular expression: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
            CommandExitCodes.UsageError,
            "fix the pattern passed with --regex, or omit --regex to run a literal text search.",
            errorCode: CommandErrorCodes.UsageError,
            category: "invalid_regex");

    internal static int WriteFindRegexTimeoutError(RegexMatchTimeoutException ex, JsonSerializerOptions jsonOptions, bool json)
    {
        return CommandErrorWriter.WriteJsonOrHuman(
            json,
            jsonOptions,
            RegexTimeoutPolicy.FormatFindTimeout(ex),
            CommandExitCodes.RuntimeError,
            hint: RegexTimeoutPolicy.FindTimeoutHint,
            errorCode: CommandErrorCodes.RegexMatchTimeout,
            category: RegexTimeoutPolicy.RegexTimeoutCategory);
    }

    internal static string FormatRegexMatchTimeout(TimeSpan timeout) =>
        RegexTimeoutPolicy.FormatDuration(timeout);

    private static string? ValidateFindArgs(string[] args)
    {
        var (allowedWithValues, allowedFlags) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("find");

        var queryCount = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var rawArg = args[i];
            // Accept both `--opt value` and `--opt=value` so ValidateFindArgs and ParseArgs
            // agree on inline-`=` shape; splitting the token in PrepareFindArgs would
            // destroy legitimate inline values that start with `--` (e.g. `--path=--literal.txt`).
            // ParseArgs と同じく `--opt value` と `--opt=value` の両形を受け入れる。
            // PrepareFindArgs でトークンを分解すると `--path=--literal.txt` のような `--` 始まりの合法な
            // inline 値が壊れるため、validation 側で inline 値を解決する。
            string arg;
            string? inlineValue;
            if (TrySplitInlineOptionValue(rawArg, out var inlineOptionName))
            {
                arg = inlineOptionName!;
                inlineValue = rawArg[(inlineOptionName!.Length + 1)..];
            }
            else
            {
                arg = rawArg;
                inlineValue = null;
            }

            if (allowedWithValues.Contains(arg))
            {
                string value;
                if (inlineValue != null)
                {
                    value = inlineValue;
                }
                else
                {
                    if (i + 1 >= args.Length)
                        return BuildMissingOptionValueError(arg);
                    value = args[i + 1];
                    i++;
                }
                if ((arg == "--limit" || arg == "--top") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0))
                    return BuildPositiveIntegerError("--limit", ConsoleUi.FormatBoundedValue(value), arg);
                if ((arg == "--limit" || arg == "--top")
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limitCeil)
                    && NumericFlagUpperBounds.TryGetValue("--limit", out var limitMax)
                    && limitCeil > limitMax)
                    return BuildPositiveIntegerUpperBoundError("--limit", ConsoleUi.FormatBoundedValue(value), limitMax);
                if (arg == "--max-line-width" && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var widthValue) || widthValue < 0))
                    return BuildNonNegativeIntegerError(arg, ConsoleUi.FormatBoundedValue(value));
                if (arg == "--max-line-width" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var widthCeil) && widthCeil > LineWidthFormatter.MaxAllowedLineWidth)
                    return BuildNonNegativeIntegerUpperBoundError("--max-line-width", ConsoleUi.FormatBoundedValue(value), LineWidthFormatter.MaxAllowedLineWidth);
                if ((arg == "--before" || arg == "--after") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var context) || context < 0))
                    return BuildNonNegativeIntegerError(arg, ConsoleUi.FormatBoundedValue(value));
                if ((arg == "--before" || arg == "--after")
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextCeil)
                    && NumericFlagUpperBounds.TryGetValue(arg, out var contextMax)
                    && contextCeil > contextMax)
                    return BuildNonNegativeIntegerUpperBoundError(arg, ConsoleUi.FormatBoundedValue(value), contextMax);
                if (arg == "--snippet-lines" && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snippetLines) || snippetLines <= 0))
                    return BuildPositiveIntegerError(arg, ConsoleUi.FormatBoundedValue(value), arg);
                if (arg == "--snippet-lines"
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snippetLinesCeil)
                    && NumericFlagUpperBounds.TryGetValue(arg, out var snippetLinesMax)
                    && snippetLinesCeil > snippetLinesMax)
                    return BuildPositiveIntegerUpperBoundError(arg, ConsoleUi.FormatBoundedValue(value), snippetLinesMax);
                if ((arg == "--focus-line" || arg == "--focus-column") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var focus) || focus <= 0))
                    return BuildPositiveIntegerError(arg, ConsoleUi.FormatBoundedValue(value), arg);
                if (arg == "--query")
                {
                    queryCount++;
                    if (queryCount > 1)
                        return "Error: find accepts exactly one query argument";
                }
                continue;
            }

            if (allowedFlags.Contains(arg))
                continue;

            if (rawArg.StartsWith('-'))
            {
                var error = $"Error: unsupported option for find: {ConsoleUi.FormatBoundedValue(rawArg)}";
                // Suggest the closest accepted find flag for typos like `--paht` → `--path`
                // (#1582). Strip any inline `=value` portion before matching, since the prefix
                // might not have been a recognized value-taking option (TrySplitInlineOptionValue
                // only splits on known options).
                // `--paht` → `--path` のようなタイプミスから回復させるため、find が受理する
                // フラグの中で最も近いものを提案する (#1582)。`--foo=bar` 形では prefix が未知
                // value-taking option の場合 TrySplitInlineOptionValue が分解しないので、
                // suggester 用に `=` 前の部分を独自に切り出して照合する。
                var nameForSuggestion = arg;
                var eq = nameForSuggestion.IndexOf('=');
                if (eq > 0)
                    nameForSuggestion = nameForSuggestion[..eq];
                var suggestion = ConsoleUi.FindClosestMatch(nameForSuggestion, allowedWithValues.Concat(allowedFlags).Where(o => o != "--"));
                if (suggestion != null)
                    error += $"\nDid you mean: {suggestion}?";
                return error;
            }

            queryCount++;
            if (queryCount > 1)
                return "Error: find accepts exactly one query argument";
        }

        return null;
    }

    private static (int Before, int After, int? SnippetLines) ResolveFindContext(QueryCommandOptions options, string[] preparedFindArgs)
    {
        if (!HasOption(preparedFindArgs, "--snippet-lines"))
            return (options.ContextBefore, options.ContextAfter, null);

        var explicitBefore = HasOption(preparedFindArgs, "--before");
        var explicitAfter = HasOption(preparedFindArgs, "--after");
        var surroundingLines = Math.Max(0, options.SnippetLines - 1);
        var before = explicitBefore ? options.ContextBefore : surroundingLines / 2;
        var after = explicitAfter ? options.ContextAfter : surroundingLines - before;
        return (before, after, options.SnippetLines);
    }

    private static string[] PrepareFindArgs(string[] args, out string? error)
    {
        var normalized = new List<string>(args.Length);
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: -- requires a following literal query for find";
                    return args;
                }

                if (i + 2 < args.Length)
                {
                    error = "Error: find accepts exactly one query argument after --";
                    return args;
                }

                normalized.Add("--query");
                normalized.Add(args[i + 1]);
                return [.. normalized];
            }

            normalized.Add(args[i]);
        }

        return [.. normalized];
    }

    private static void AddFindScanJsonFields(JsonObject payload, FindScanSummary scan)
    {
        payload["candidate_files"] = scan.CandidateFiles;
        payload["files_scanned"] = scan.FilesScanned;
        payload["lines_scanned"] = scan.LinesScanned;
        payload["scan_truncated"] = scan.Truncated;
        payload["scan_cap_reached"] = scan.CapReached;
        payload["scan_timed_out"] = scan.TimedOut;
        if (scan.TruncationReason != null)
            payload["scan_truncation_reason"] = scan.TruncationReason;
        if (scan.CandidateFileLimit.HasValue)
            payload["candidate_file_limit"] = scan.CandidateFileLimit.Value;
        if (scan.LineLimit.HasValue)
            payload["line_scan_limit"] = scan.LineLimit.Value;
    }

    private static void WriteFindScanSummary(FindScanSummary scan)
    {
        var summary = $"scanned {scan.FilesScanned}/{scan.CandidateFiles} candidate files, {ConsoleUi.Counted(scan.LinesScanned, "line")}";
        if (scan.Truncated)
            summary += scan.TruncationReason == null ? "; truncated" : $"; truncated by {scan.TruncationReason}";
        CommandErrorWriter.WriteStderr($"({summary})");
    }
}
