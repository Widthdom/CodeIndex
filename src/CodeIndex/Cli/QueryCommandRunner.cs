using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs query-style CLI commands.
/// クエリ系CLIコマンドを実行する。
/// </summary>
public static class QueryCommandRunner
{
    internal const int DefaultQueryLimit = 20;
    internal const int DefaultMapLimit = 10;
    internal const int DefaultImpactLimit = 50;
    internal const string DefaultLimitEnvironmentVariable = "CDIDX_DEFAULT_LIMIT";
    internal const string DefaultSnippetLinesEnvironmentVariable = "CDIDX_DEFAULT_SNIPPET_LINES";
    internal const string DefaultMaxLineWidthEnvironmentVariable = "CDIDX_DEFAULT_MAX_LINE_WIDTH";
    internal const string StaleAfterEnvironmentVariable = "CDIDX_STALE_AFTER";
    internal static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(24);
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    [ThreadStatic]
    private static DbReader? s_batchReader;

    private static DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;

    // Cap OR-joined `symbols` names well below SQLite's 1000 expression-tree depth so oversized
    // batches fail fast with a clear usage error instead of a confusing SQLite exception.
    // OR 結合の `symbols` 名は SQLite の式木深さ上限 1000 を十分下回る値で頭打ちにし、
    // 大量バッチを SQLite 例外ではなく明確な usage error で早期に弾く。
    internal const int MaxSymbolQueryNames = 256;
    internal const int MaxMapSectionsCsvLength = 256;
    internal const int MaxMapSectionsCsvEntries = 16;
    internal const int MaxStatusCheckScopesCsvLength = 256;
    internal const int MaxStatusCheckScopesCsvEntries = 16;
    internal const int MaxVisibilityFilterCsvLength = 256;
    internal const int MaxVisibilityFilterCsvEntries = 16;
    internal const int MaxQueryPathFilterCount = 128;
    internal const int MaxQueryPathFilterLength = 1024;
    internal const int ExactZeroHintProbeLimit = 1;
    internal const int ExactZeroHintSampleLimit = 5;
    private const string HotspotsGroupedByNameKind = "name_kind";
    private const string HotspotsGroupedBySymbol = "symbol";
    private const string HotspotsGroupedByFile = "file";
    private const string HotspotsGroupedByStatement = "statement";
    private const string JsonOutputFormatNdjson = "ndjson";
    private const string JsonOutputFormatArray = "array";
    private static readonly Dictionary<string, string[]> LanguageDisplayAliases = new(StringComparer.Ordinal)
    {
        ["javascript"] = ["js", "jsx", "cjs", "mjs"],
        ["csharp"] = ["c#", "cs", "cshtml", "razor", "blazor"],
        ["java"] = ["jav"],
        ["cpp"] = ["c++", "cplusplus"],
        ["fsharp"] = ["f#", "fs"],
        ["ruby"] = ["rb"],
        ["vb"] = ["vb.net", "vbnet", "visual basic", "visual-basic", "visual_basic", "vbs", "vbscript"],
        ["python"] = ["py", "py3", "python3"],
        ["yaml"] = ["yml"],
        ["typescript"] = ["ts", "tsx", "cts", "mts"],
        ["rust"] = ["rs"],
        ["sql"] = ["tsql", "t-sql", "transact-sql", "transactsql", "sqlserver", "mssql"],
        ["xml"] = ["xaml", "axaml"],
        ["assembly"] = ["asm", "assembler", "nasm", "gas", "gnuasm", "gnu assembler"],
    };
    private static readonly HashSet<string> ValueTakingOptions =
    [
        "--db",
        "--data-dir",
        "--limit",
        "--top",
        "--lang",
        "--kind",
        "--visibility",
        "--exclude-visibility",
        "--since",
        "--start",
        "--end",
        "--before",
        "--after",
        "--name",
        "--snippet-lines",
        "--snippet-focus",
        "--path",
        "--require-before",
        "--require-after",
        "--reject-before",
        "--reject-after",
        "--guard-window",
        "--project",
        "--solution",
        "--exclude-path",
        "--max-hops",
        "--depth",
        "--query",
        "--group-by",
        "--focus-line",
        "--focus-column",
        "--focus-length",
        "--max-line-width",
        "--stale-after",
        "--explain",
        "--rank-by",
        "--slow-query-ms",
        "--format",
        "--min-entrypoint-confidence",
        "--sections",
    ];
    private sealed record StatusReadinessField(
        string FieldName,
        string Label,
        string ReadyText,
        string DegradedText,
        string Remediation);

    private static readonly StatusReadinessField[] StatusReadinessFields =
    [
        new(
            "graph_table_available",
            "Reference graph table",
            "reference, caller, callee, impact, unused, and hotspot queries can read indexed reference edges.",
            "reference graph queries degrade to empty or incomplete results because the symbol_references table is missing.",
            "Run `cdidx index <projectPath>` to rebuild the graph-capable index."),
        new(
            "issues_table_available",
            "Validation issues table",
            "the file_issues table exists in this index.",
            "validate output degrades to empty because the file_issues table is missing.",
            "Run `cdidx index <projectPath>` to rebuild the issue table."),
        new(
            "file_issues_data_current",
            "Validation issues data",
            "file_issues rows are stamped current for this index generation.",
            "file_issues rows may be stale or partial for this index generation.",
            "Run `cdidx index <projectPath>` to refresh file issue rows."),
        new(
            "migration_in_progress",
            "Migration/write state",
            "no index write or migration is currently in progress.",
            "an index write or migration is in progress, so readiness may be temporarily degraded.",
            "Wait for the active `cdidx index` run to finish, then rerun `cdidx status --json`."),
        new(
            "sql_graph_contract_ready",
            "SQL graph contract",
            "SQL reference/dependency rows were written with the current call-column and qualified-name contract.",
            "SQL graph/dependency readers may return stale or incomplete results.",
            "Run `cdidx index <projectPath>` to rewrite SQL graph rows."),
        new(
            "hotspot_family_ready",
            "Hotspot family contract",
            "cross-file hotspot family grouping is stamped for all supported languages in this index.",
            "cross-file hotspot grouping may be degraded for one or more languages.",
            "Run `cdidx index <projectPath> --rebuild` to restamp authoritative hotspot families for every indexed row."),
        new(
            "csharp_symbol_name_ready",
            "C# symbol-name contract",
            "C# exact-name lookup uses authoritative persisted names for operators, conversions, and indexers.",
            "C# exact-name lookup for operators, conversions, and indexers may fall back to older canonical names.",
            "Run `cdidx index <projectPath>` to upgrade canonical C# symbol names."),
        new(
            "csharp_metadata_target_ready",
            "C# metadata target contract",
            "deps and impact use authoritative C# metadata-attribute targets.",
            "deps and impact metadata-attribute edges fall back to legacy signature/name heuristics.",
            "Run `cdidx index <projectPath>` to restamp authoritative C# metadata targets."),
        new(
            "fold_ready",
            "Unicode exact-name fold contract",
            "--exact-name can use Unicode NFKC + CaseFold equality.",
            "--exact-name falls back to ASCII COLLATE NOCASE, so non-ASCII casing pairs may not match.",
            "Run `cdidx backfill-fold` to restamp folded-name columns in place, or `cdidx index <projectPath> --rebuild` for a full rebuild."),
        new(
            "index_newer_than_reader",
            "Reader compatibility",
            "this cdidx binary understands all persisted index contract versions.",
            "this DB was written by a newer cdidx, so older readers may degrade instead of trusting newer contract stamps.",
            "Run status with a current cdidx binary, or rebuild the DB with the version you intend to use."),
    ];

    private static readonly HashSet<string> FlagOnlyOptions =
    [
        "--json",
        "--fts",
        "--body",
        "--count",
        "--strict-not-found",
        "--strict",
        "--no-dedup",
        "--no-visibility-rank",
        "--exact",
        "--exact-name",
        "--exact-substring",
        "--prefix",
        "--reverse",
        "--help",
        "-h",
        "--version",
        "-V",
        "--verbose",
        "--quiet",
        "-q",
        "--silent",
        "--by-bucket",
        "--all",
        "--cycles",
        "--group-by-name",
        "--with-paths",
        "--bytes",
        "--profile",
        "--check-updates",
        "--read-only",
        "--immutable",
    ];
    private const string OutputFormatText = "text";
    private const string OutputFormatJson = "json";
    private const string OutputFormatLsp = "lsp";
    private const string OutputFormatQf = "qf";
    private const string OutputFormatSarif = "sarif";
    private const string OutputFormatCount = "count";
    private const string OutputFormatCompact = "compact";
    private const string OutputFormatCsv = "csv";
    private const string OutputFormatTsv = "tsv";
    private const string OutputFormatDot = "dot";
    private const string OutputFormatGraphMl = "graphml";
    private const string OutputFormatJsonGraph = "json-graph";
    private const string OutputFormatEdgeList = "edgelist";
    private static readonly HashSet<string> InlineValueOptions =
        new(ValueTakingOptions.Concat(["--json"]), StringComparer.Ordinal);
    private const string FindUsage = "Usage: cdidx find <query> --path <glob> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--before <n>] [--after <n>] [--snippet-lines <n>] [--focus-line <line>] [--focus-column <n>] [--max-line-width <n>] [--exact] [--regex] [--count]\n       cdidx find --query <query> --path <glob> [...]\n       cdidx find [options] -- <query>";

    public static int RunBatch(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg == "--db")
            {
                if (i + 1 >= cmdArgs.Length || string.IsNullOrWhiteSpace(cmdArgs[i + 1]))
                {
                    Console.Error.WriteLine(BuildMissingOptionValueError("--db"));
                    return CommandExitCodes.UsageError;
                }
                dbPath = cmdArgs[++i];
                continue;
            }

            if (arg.StartsWith("--db=", StringComparison.Ordinal))
            {
                dbPath = arg["--db=".Length..];
                if (string.IsNullOrWhiteSpace(dbPath))
                {
                    Console.Error.WriteLine(BuildMissingOptionValueError("--db"));
                    return CommandExitCodes.UsageError;
                }
                continue;
            }

            Console.Error.WriteLine($"Error: {arg} is not supported for batch.");
            Console.Error.WriteLine($"Usage: {ConsoleUi.GetUsageLine("batch")}");
            return CommandExitCodes.UsageError;
        }

        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {Path.GetFullPath(dbPath)}");
            Console.Error.WriteLine("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.");
            return CommandExitCodes.DatabaseError;
        }

        try
        {
            using var db = new DbContext(dbPath);
            if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                return WriteInvalidCodeIndexDbError(dbPath, validationReason);

            db.TryMigrateForRead();
            s_batchReader = new DbReader(db);
            var firstFailure = CommandExitCodes.Success;
            string? line;
            var lineNumber = 0;
            while ((line = Console.In.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!TryParseBatchLine(line, lineNumber, out var commandName, out var subArgs, out var parseExitCode))
                {
                    if (firstFailure == CommandExitCodes.Success)
                        firstFailure = parseExitCode;
                    continue;
                }

                var exitCode = RunBatchQueryCommand(commandName, subArgs, jsonOptions);
                if (exitCode != CommandExitCodes.Success && firstFailure == CommandExitCodes.Success)
                    firstFailure = exitCode;
            }

            return firstFailure;
        }
        finally
        {
            s_batchReader = null;
        }
    }

    private static bool TryParseBatchLine(string line, int lineNumber, out string commandName, out string[] subArgs, out int exitCode)
    {
        commandName = string.Empty;
        subArgs = [];
        exitCode = CommandExitCodes.UsageError;

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                Console.Error.WriteLine($"Error: batch line {lineNumber} must be a non-empty JSON string array.");
                return false;
            }

            var values = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    Console.Error.WriteLine($"Error: batch line {lineNumber} must contain only strings.");
                    return false;
                }
                values.Add(element.GetString() ?? string.Empty);
            }

            commandName = values[0];
            subArgs = values.Skip(1).ToArray();
            return true;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: batch line {lineNumber} is not valid JSON: {ex.Message}");
            return false;
        }
    }

    private static int RunBatchQueryCommand(string commandName, string[] subArgs, JsonSerializerOptions jsonOptions)
        => commandName switch
        {
            "search" => RunSearch(subArgs, jsonOptions),
            "definition" => RunDefinition(subArgs, jsonOptions),
            "references" => RunReferences(subArgs, jsonOptions),
            "callers" => RunCallers(subArgs, jsonOptions),
            "callees" => RunCallees(subArgs, jsonOptions),
            "symbols" => RunSymbols(subArgs, jsonOptions),
            "files" => RunFiles(subArgs, jsonOptions),
            "find" => RunFind(subArgs, jsonOptions),
            "excerpt" => RunExcerpt(subArgs, jsonOptions),
            "map" => RunMap(subArgs, jsonOptions),
            "inspect" => RunInspect(subArgs, jsonOptions),
            "outline" => RunOutline(subArgs, jsonOptions),
            "status" => RunStatus(subArgs, jsonOptions),
            "validate" => RunValidate(subArgs, jsonOptions),
            "impact" => RunImpact(subArgs, jsonOptions),
            "deps" => RunDeps(subArgs, jsonOptions),
            "unused" => RunUnused(subArgs, jsonOptions),
            "hotspots" => RunHotspots(subArgs, jsonOptions),
            _ => WriteBatchUnsupportedCommand(commandName),
        };

    private static int WriteBatchUnsupportedCommand(string commandName)
    {
        Console.Error.WriteLine($"Error: batch only supports query commands; '{commandName}' is not supported.");
        Console.Error.WriteLine("Hint: use one of search, definition, references, callers, callees, symbols, files, find, excerpt, map, inspect, outline, status, validate, impact, deps, unused, or hotspots.");
        return CommandExitCodes.UsageError;
    }

    public static int RunSearch(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("search", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("search", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("search"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "search"))
            return CommandExitCodes.UsageError;
        if (!TryResolveSearchExactMode(options, out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Prefix)
        {
            WriteValidationError(
                "--prefix cannot be combined with --exact / --exact-substring (exact uses instr(), not FTS5 prefix phrases).",
                "Drop --prefix to keep the exact substring path, or drop --exact to opt into FTS5 prefix matching.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "search"))
            return CommandExitCodes.UsageError;
        if (options.Query == null)
        {
            WriteUsageError(
                "search requires a query argument",
                GetUsageLineOrThrow("search"),
                "Add the text you want to search for after the command, for example: `cdidx search authenticate`.");
            return CommandExitCodes.UsageError;
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
        {
            WriteUsageError(
                QueryLimits.FormatQueryTooLongError(),
                GetUsageLineOrThrow("search"),
                "Shorten the search text or split generated input into smaller queries before running `cdidx search`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("search", options))
            return CommandExitCodes.UsageError;

        var exactSubstringHint = SearchQueryAdvisor.BuildExactSubstringHint(options.Query, options.RawFts, exact, options.Prefix);
        int? jsonDoneCount = null;
        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountSearchResults(options.Query, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, options.GuardFilters, options.GuardWindow);
                var queryDiagnostics = DbReader.AnalyzeFtsQuery(options.Query, options.RawFts, options.Prefix, options.Lang);
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, query: options.Query, ftsQueryDiagnostics: queryDiagnostics, queryOptions: options, exactSubstringHint: exactSubstringHint).ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        WriteExactSubstringHintIfNeeded(exactSubstringHint);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new QueryCountFilesJsonResult(counts.Count, counts.FileCount, options.Query), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                    WriteExactSubstringHintIfNeeded(exactSubstringHint);
                }
                return CommandExitCodes.Success;
            }

            var results = reader.Search(options.Query, options.Limit, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow);
            var ftsQueryDiagnostics = DbReader.AnalyzeFtsQuery(options.Query, options.RawFts, options.Prefix, options.Lang);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                {
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return ZeroResultExitCode(options);
                    if (options.JsonOutputFormat == JsonOutputFormatArray)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            Array.Empty<CompactSearchResult>(),
                            CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResultArray));
                    }
                    else
                    {
                        Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "results", query: options.Query, ftsQueryDiagnostics: ftsQueryDiagnostics, queryOptions: options, exactSubstringHint: exactSubstringHint).ToJsonString(jsonOptions));
                        jsonDoneCount = 0;
                    }
                }
                else if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No results found", options));
                    WriteLangHint(options.Lang, reader);
                    WriteExactSubstringHintIfNeeded(exactSubstringHint);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.StartLine, null, $"search match: {options.Query}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.StartLine, 1, $"search match: {options.Query}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.StartLine, 1, $"search match: {options.Query}", "search")), jsonOptions);
                    return CommandExitCodes.Success;
                }
                var compactResults = results
                    .Select(r => SearchSnippetFormatter.ToCompactResult(r, options.Query, options.SnippetLines, exact, options.MaxLineWidth, r.Lang, options.SnippetFocus, exposeLiteralHighlights: exact))
                    .ToArray();
                if (exactSubstringHint != null)
                {
                    foreach (var result in compactResults)
                        result.ExactSubstringHint = exactSubstringHint;
                }
                if (options.JsonOutputFormat == JsonOutputFormatArray)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        compactResults,
                        CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResultArray));
                }
                else
                {
                    foreach (var result in compactResults)
                        Console.WriteLine(JsonSerializer.Serialize(
                            result,
                            CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResult));
                    jsonDoneCount = compactResults.Length;
                }
            }
            else
            {
                foreach (var r in results)
                {
                    Console.WriteLine($"{r.Path}:{r.StartLine}-{r.EndLine}{FormatSearchVisibilitySuffix(r.Visibility)}");
                    var snippetLines = SearchSnippetFormatter.Format(r.Content, options.Query, options.SnippetLines, exact, options.MaxLineWidth, r.Lang, options.SnippetFocus);
                    foreach (var line in snippetLines)
                        Console.WriteLine($"  {line}");
                    Console.WriteLine();
                }
                var fileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} results in {fileCount} files)");
                WriteExactSubstringHintIfNeeded(exactSubstringHint);
            }
            return CommandExitCodes.Success;
        }, exitCode =>
        {
            if (options.Json && options.JsonOutputFormat == JsonOutputFormatNdjson && jsonDoneCount.HasValue)
                WriteJsonStreamDone(jsonDoneCount.Value, jsonOptions);
        });
    }

    private static void WriteJsonStreamDone(int count, JsonSerializerOptions jsonOptions)
        => Console.WriteLine(JsonSerializer.Serialize(
            new JsonStreamDoneResult(Done: true, Count: count, Interrupted: false),
            CliJsonSerializerContextFactory.Create(jsonOptions).JsonStreamDoneResult));

    public static void AttachLspLocations(IEnumerable<DefinitionResult> results)
    {
        foreach (var result in results)
        {
            var location = BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);
            result.Uri = location.Uri;
            result.Range = location.Range;
        }
    }

    public static void AttachLspLocations(IEnumerable<ReferenceResult> results)
    {
        foreach (var result in results)
        {
            var location = BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + 1);
            result.Uri = location.Uri;
            result.Range = location.Range;
        }
    }

    public static LspLocation BuildLspLocation(string path, int startLine, int startColumn, int endLine, int endColumn)
    {
        var absolutePath = Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(path, Environment.CurrentDirectory);
        return new LspLocation
        {
            Uri = new Uri(absolutePath).AbsoluteUri,
            Range = new LspRange
            {
                Start = new LspPosition
                {
                    Line = Math.Max(0, startLine - 1),
                    Character = Math.Max(0, startColumn - 1),
                },
                End = new LspPosition
                {
                    Line = Math.Max(0, endLine - 1),
                    Character = Math.Max(0, endColumn - 1),
                },
            },
        };
    }

    private static LspLocation ToLspLocation(DefinitionResult result)
        => BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);

    private static LspLocation ToLspLocation(ReferenceResult result)
        => BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + Math.Max(1, result.SymbolName.Length));

    private static LspLocation ToLspLocation(SearchResult result)
        => BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);

    private static LspLocation ToLspLocation(FileFindResult result)
        => BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + 1);

    private static LspLocation ToLspLocation(FileIssue result)
        => BuildLspLocation(result.Path, result.Line, 1, result.Line, 1);

    private static LspLocation ToLspLocation(CallerResult result)
        => BuildLspLocation(result.Path, result.FirstLine, 1, result.FirstLine, 1);

    private static LspLocation ToLspLocation(CalleeResult result)
        => BuildLspLocation(result.Path, result.FirstLine, 1, result.FirstLine, 1);

    private static void WriteLspLocations(IEnumerable<LspLocation> locations, JsonSerializerOptions jsonOptions)
        => Console.WriteLine(JsonSerializer.Serialize(locations.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListLspLocation));

    private static bool TryWriteEmptyFormattedResult(QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.OutputFormat == OutputFormatCount)
        {
            WriteFormattedCount(0, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCompact)
        {
            WriteCompactLocations([], jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
        {
            WriteDelimitedLocations([], options.OutputFormat);
            return true;
        }
        if (options.OutputFormat == OutputFormatLsp)
        {
            WriteLspLocations([], jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatQf)
            return true;
        if (options.OutputFormat == OutputFormatSarif)
        {
            WriteSarif([], jsonOptions);
            return true;
        }
        return false;
    }

    private sealed record FormattedLocation(string File, int Line, int? Column = null, string? Label = null);

    private static bool TryWriteFormattedLocations(QueryCommandOptions options, IEnumerable<FormattedLocation> locations, JsonSerializerOptions jsonOptions)
    {
        if (options.OutputFormat == OutputFormatCount)
        {
            WriteFormattedCount(locations.Count(), jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCompact)
        {
            WriteCompactLocations(locations, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
        {
            WriteDelimitedLocations(locations, options.OutputFormat);
            return true;
        }
        return false;
    }

    private static void WriteFormattedCount(int count, JsonSerializerOptions jsonOptions)
        => Console.WriteLine(new JsonObject
        {
            ["count"] = count,
            ["total_estimated"] = count,
        }.ToJsonString(jsonOptions));

    private static void WriteCompactLocations(IEnumerable<FormattedLocation> locations, JsonSerializerOptions jsonOptions)
    {
        var rows = new JsonArray();
        foreach (var location in locations)
        {
            var row = new JsonObject
            {
                ["file"] = location.File,
                ["line"] = location.Line,
            };
            if (location.Column.HasValue)
                row["column"] = location.Column.Value;
            rows.Add(row);
        }
        Console.WriteLine(rows.ToJsonString(jsonOptions));
    }

    private static void WriteDelimitedLocations(IEnumerable<FormattedLocation> locations, string outputFormat)
    {
        var delimiter = outputFormat == OutputFormatTsv ? "\t" : ",";
        Console.WriteLine(string.Join(delimiter, ["file", "line", "column", "label"]));
        foreach (var location in locations)
        {
            var values = new[]
            {
                location.File,
                location.Line.ToString(CultureInfo.InvariantCulture),
                location.Column?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                location.Label ?? string.Empty,
            };
            Console.WriteLine(string.Join(delimiter, values.Select(value => EscapeDelimitedValue(value, outputFormat))));
        }
    }

    private static string EscapeDelimitedValue(string value, string outputFormat)
    {
        if (outputFormat == OutputFormatTsv)
            return value.Replace("\t", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (!value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void WriteQuickfix(IEnumerable<(string Path, int Line, int Column, string Message)> items)
    {
        foreach (var item in items)
            Console.WriteLine($"{item.Path}:{item.Line}:{item.Column}:{item.Message}");
    }

    private static void WriteSarif(IEnumerable<(string Path, int Line, int Column, string Message, string RuleId)> items, JsonSerializerOptions jsonOptions)
    {
        var results = new JsonArray();
        foreach (var item in items)
        {
            results.Add(new JsonObject
            {
                ["ruleId"] = item.RuleId,
                ["message"] = new JsonObject { ["text"] = item.Message },
                ["locations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject { ["uri"] = item.Path },
                            ["region"] = new JsonObject
                            {
                                ["startLine"] = Math.Max(1, item.Line),
                                ["startColumn"] = Math.Max(1, item.Column),
                            },
                        },
                    },
                },
            });
        }

        var payload = new JsonObject
        {
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = "cdidx",
                            ["informationUri"] = "https://github.com/Widthdom/CodeIndex",
                        },
                    },
                    ["results"] = results,
                },
            },
        };
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    public static int RunDefinition(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("definition", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("definition", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("definition"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "definition"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "definition", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "definition", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.CountOnly && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(options.Json
                ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, options.Query), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult)
                : "0");
            return CommandExitCodes.Success;
        }
        if (TryWriteBlankQueryError(options, "definition"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "definition requires a symbol query argument",
                GetUsageLineOrThrow("definition"),
                "Add the symbol name after the command, for example: `cdidx definition QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "definition requires a symbol query argument",
                GetUsageLineOrThrow("definition"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("definition", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var exactSignalForCount = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact,
                    () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name);
                WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, exactZeroHint: exactZeroHintForCount, exactSignal: exact ? exactSignalForCount : null, queryOptions: options).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = counts.Count,
                        ["files"] = counts.FileCount,
                    };
                    if (exact)
                        AddExactJsonFields(payload, exactSignalForCount);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var results = reader.GetDefinitions(options.Query, options.Limit, options.Kind, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            var exactSignal = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var exactZeroHint = BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                r => r.Name);
            WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No definitions found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader, "Try 'search' for full-text matches instead of symbol lookup.");
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.StartLine, null, $"{r.Kind} {r.Name}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.StartLine, 1, $"{r.Kind} {r.Name}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.StartLine, 1, $"{r.Kind} {r.Name}", "definition")), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteJsonResultWithExactSignal(r, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult, exactSignal, jsonOptions);
                    else
                        Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var container = r.ContainerName != null ? $" in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.Kind,-10} {r.Name,-40} {r.Path}:{r.StartLine}-{r.EndLine}{container}");
                    WriteNumberedExcerpt(r.StartLine, r.Content);
                    if (options.IncludeBody)
                    {
                        if (r.BodyContent != null && r.BodyStartLine != null)
                        {
                            Console.WriteLine();
                            Console.WriteLine("  Body:");
                            WriteNumberedExcerpt(r.BodyStartLine.Value, r.BodyContent);
                        }
                        else
                        {
                            Console.WriteLine("  Body: unavailable");
                        }
                    }
                    Console.WriteLine();
                }
                var defFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} definitions in {defFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunGoto(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var all = cmdArgs.Any(arg => arg == "--all");
        var filteredArgs = cmdArgs.Where(arg => arg != "--all").ToArray();
        var options = ParseArgs(filteredArgs, jsonDefault: true, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("goto", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("goto"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "goto"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "goto", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "goto", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "goto"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "goto requires a symbol query argument",
                GetUsageLineOrThrow("goto"),
                "Add the symbol name after the command, for example: `cdidx goto QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "goto requires a symbol query argument",
                GetUsageLineOrThrow("goto"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("goto", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var limit = all ? options.Limit : Math.Max(options.Limit, 2);
            var results = reader.GetDefinitions(options.Query, limit, options.Kind, options.Lang, includeBody: false, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            if (results.Count == 0)
            {
                if (!options.Json)
                    Console.Error.WriteLine(BuildZeroResultLine("No definitions found", options));
                return CommandExitCodes.NotFound;
            }

            if (all)
            {
                WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                return CommandExitCodes.Success;
            }

            if (results.Count > 1)
            {
                Console.Error.WriteLine($"Error: goto found {results.Count} matching definitions for '{options.Query}'.");
                Console.Error.WriteLine("Hint: narrow the query with --kind, --lang, --path, or pass --all to return all LSP locations.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(JsonSerializer.Serialize(ToLspLocation(results[0]), CliJsonSerializerContextFactory.Create(jsonOptions).LspLocation));
            return CommandExitCodes.Success;
        });
    }

    private static string FormatSearchVisibilitySuffix(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility)
            || string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $" [{visibility}]";
    }

    public static int RunReferences(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("references", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("references", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("references"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "references", AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "references"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "references", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "references"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "references requires a symbol query argument",
                GetUsageLineOrThrow("references"),
                "Add the symbol name you want to trace, for example: `cdidx references QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "references requires a symbol query argument",
                GetUsageLineOrThrow("references"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("references", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("references", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountSearchReferencesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountSearchReferences(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                    () => reader.CountSearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    () => reader.SearchReferences(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    r => r.SymbolName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.SearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.MaxLineWidth);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                () => reader.CountSearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                () => reader.SearchReferences(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                r => r.SymbolName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "references", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No references found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "references", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var owner = r.ContainerName != null ? $"  in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.ReferenceKind,-12} {r.SymbolName,-32} {r.Path}:{r.Line}:{r.Column}{owner}");
                    Console.WriteLine($"  {r.Context}");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var refFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} references in {refFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallers(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callers", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callers", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("callers"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callers", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callers", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "callers", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "callers requires a symbol query argument",
                GetUsageLineOrThrow("callers"),
                "Add the callee symbol name after the command, for example: `cdidx callers QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "callers requires a symbol query argument",
                GetUsageLineOrThrow("callers"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("callers", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("callers", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCallersTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                    () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                    () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                    r => r.CalleeName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.GetCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                r => r.CalleeName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callers", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No callers found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, null, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var callerFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} callers in {callerFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallees(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callees", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callees", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("callees"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callees", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callees", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "callees", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "callees requires a caller query argument",
                GetUsageLineOrThrow("callees"),
                "Add the caller symbol name after the command, for example: `cdidx callees RunIndex`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "callees requires a caller query argument",
                GetUsageLineOrThrow("callees"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("callees", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("callees", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCalleesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                    () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                    () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                    r => r.CallerName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.GetCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                r => r.CallerName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callees", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No callees found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callees", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, null, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CalleeName,-32} {r.Path}:{r.FirstLine}  <- {r.CallerName ?? "<top-level>"} ({r.ReferenceCount} refs)");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var calleeFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} callees in {calleeFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<ReferenceResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = result.ContainerName != null
                ? BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.ContainerName, snippetLines, maxLineWidth)
                : null;
            excerpt ??= BuildBodyExcerpt(reader, result.Path, result.Line, snippetLines, maxLineWidth, focusColumn: result.Column, focusLength: Math.Max(1, result.SymbolName.Length));
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<CallerResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = result.CallerName != null
                ? BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CallerName, snippetLines, maxLineWidth)
                : null;
            excerpt ??= BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<CalleeResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CalleeName, snippetLines, maxLineWidth)
                ?? BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<ImpactResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = result.CallerName != null
                ? BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CallerName, snippetLines, maxLineWidth)
                : null;
            excerpt ??= BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
        }
    }

    private static FileExcerptResult? BuildSymbolBodyExcerpt(DbReader reader, string path, string? lang, string symbolName, int snippetLines, int maxLineWidth)
    {
        var definitions = reader.GetDefinitions(
            symbolName,
            limit: 1,
            kind: null,
            lang: lang,
            includeBody: true,
            pathPatterns: [path],
            excludePathPatterns: null,
            excludeTests: false,
            since: null,
            exact: true);
        var definition = definitions.FirstOrDefault();
        if (definition == null)
            return null;

        var startLine = definition.StartLine;
        var naturalEndLine = definition.BodyEndLine ?? definition.EndLine;
        var cappedEndLine = (int)Math.Min(naturalEndLine, (long)startLine + SearchSnippetFormatter.ClampSnippetLines(snippetLines) - 1);
        return reader.GetExcerpt(path, startLine, cappedEndLine, maxLineWidth: maxLineWidth, focusLine: startLine);
    }

    private static FileExcerptResult? BuildBodyExcerpt(DbReader reader, string path, int line, int snippetLines, int maxLineWidth, int? focusColumn = null, int focusLength = 1)
    {
        var cappedLines = SearchSnippetFormatter.ClampSnippetLines(snippetLines);
        var endLine = (int)Math.Min(int.MaxValue, (long)line + cappedLines - 1);
        return reader.GetExcerpt(
            path,
            line,
            endLine,
            maxLineWidth: maxLineWidth,
            focusLine: line,
            focusColumn: focusColumn,
            focusLength: focusLength);
    }

    private static void ApplyBodyExcerpt(ReferenceResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
    }

    private static void ApplyBodyExcerpt(CallerResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
    }

    private static void ApplyBodyExcerpt(CalleeResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
    }

    private static void ApplyBodyExcerpt(ImpactResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
    }

    private static void WriteOptionalBodyExcerpt(int? startLine, string? content, string indent = "")
    {
        if (startLine == null || content == null)
            return;

        Console.WriteLine($"{indent}  Body:");
        WriteNumberedExcerpt(startLine.Value, content, indent + "  ");
    }

    /// <summary>
    /// Build the OR-joined name list for `symbols`: first positional + extra positionals + --name values.
    /// Pipe characters are treated as literal name characters so operator symbols like `operator |` remain searchable.
    /// Multi-name queries must use repeated positional args or `--name` flags.
    /// `symbols` コマンド用の名前リストを組み立て（最初の positional + 追加 positional + --name）。
    /// `|` は名前文字として扱うので `operator |` などの演算子シンボルも検索可能。複数名指定は繰り返し positional か `--name` で行う。
    /// </summary>
    internal static (List<string>? Queries, bool HadExplicitInput) BuildSymbolQueryList(QueryCommandOptions options)
    {
        var raw = new List<string>();
        if (options.Query != null)
            raw.Add(options.Query);
        raw.AddRange(options.ExtraNames);
        var hadExplicitInput = raw.Count > 0;
        if (!hadExplicitInput)
            return (null, false);
        var deduped = raw.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        if (deduped.Any(IsBareVerbatimQueryToken))
            return (null, hadExplicitInput);
        return (deduped.Count == 0 ? null : deduped, hadExplicitInput);
    }

    public static int RunSymbols(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("symbols", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("symbols", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("symbols"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "symbols", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "symbols"))
            return CommandExitCodes.UsageError;
        if (TryWriteBlankQueryError(options, "symbols"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "symbols", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        var exactBareVerbatimOnly = exact && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase) && (
            (options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.ExtraNames.Count == 0) ||
            (options.Query is null && options.ExtraNames.Count > 0 && options.ExtraNames.All(IsBareVerbatimQueryToken)));
        var (symbolQueries, hadExplicitInput) = BuildSymbolQueryList(options);
        if (hadExplicitInput && symbolQueries == null)
        {
            if (exactBareVerbatimOnly && options.CountOnly)
            {
                var countQuery = options.Query ?? string.Join(" ", options.ExtraNames);
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, countQuery), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult)
                    : "0");
                return CommandExitCodes.Success;
            }
            // Fail closed: an explicit name/query was provided but normalized to empty or a bare
            // verbatim prefix (e.g. `|`, `@`, `--name ""`). Returning null here would broaden into
            // an unfiltered symbol dump. /
            // 明示入力が正規化で空、または verbatim 接頭辞単独（`|`、`@`、`--name ""` など）になった場合は必ず拒否する。
            Console.Error.WriteLine("Error: symbol name list is empty after normalization. Check for empty --name values, bare verbatim prefixes like `@`, or bare `|` separators. / シンボル名リストが正規化の結果空です。--name の空値、`@` のような verbatim 接頭辞単独、単独の `|` を確認してください。");
            return CommandExitCodes.UsageError;
        }
        if (symbolQueries != null && symbolQueries.Count > MaxSymbolQueryNames)
        {
            Console.Error.WriteLine($"Error: too many symbol names ({symbolQueries.Count}); maximum is {MaxSymbolQueryNames}. Split the request into smaller batches. / シンボル名が多すぎます（{symbolQueries.Count}件、上限は {MaxSymbolQueryNames} 件）。分割してください。");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountSearchSymbolsTotal(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var hasExactPredicateForCount = exact && symbolQueries is { Count: > 0 };
                var exactSignalForCount = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var multiNameExactHintForCount = symbolQueries != null && symbolQueries.Count > 1;
                var exactZeroHintForCount = multiNameExactHintForCount
                    ? BuildExactZeroHint(
                        exact,
                        () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        r => r.Name)
                    : BuildExactZeroHint(
                        exact && symbolQueries != null && symbolQueries.Count > 0,
                        () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                        () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                        r => r.Name);
                WriteExactSymbolWarningIfNeeded(hasExactPredicateForCount, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, exactZeroHint: exactZeroHintForCount, exactSignal: hasExactPredicateForCount ? exactSignalForCount : null, queryOptions: options).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = counts.Count,
                        ["files"] = counts.FileCount,
                    };
                    if (hasExactPredicateForCount)
                        AddExactJsonFields(payload, exactSignalForCount);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var results = reader.SearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            var hasExactPredicate = exact && symbolQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var multiNameExactHint = symbolQueries != null && symbolQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name)
                : BuildExactZeroHint(
                    exact && symbolQueries != null && symbolQueries.Count > 0,
                    () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name);
            WriteExactSymbolWarningIfNeeded(hasExactPredicate, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No symbols found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                foreach (var r in results)
                {
                    if (hasExactPredicate)
                        WriteJsonResultWithExactSignal(r, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult, exactSignal, jsonOptions);
                    else
                        Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolResult));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var lineRange = r.EndLine > r.StartLine
                        ? $"{r.StartLine}-{r.EndLine}"
                        : r.StartLine.ToString();
                    Console.WriteLine($"{ConsoleUi.ColorizeKind(r.Kind, 10)} {r.Name,-40} {r.Path}:{lineRange}");
                }
                var symFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} symbols in {symFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunFiles(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("files", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("files", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("files"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "files"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedExtraPositionals("files", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountListFiles(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildJsonZeroResultPayload(reader, jsonOptions).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new QueryCountJsonResult(counts.Count), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountJsonResult)
                    : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var results = reader.ListFiles(options.Query, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            if (results.Count == 0)
            {
                if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "files", queryOptions: options).ToJsonString(jsonOptions));
                else if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No files found", options));
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, CliJsonSerializerContextFactory.Create(jsonOptions).FileResult));
            }
            else
            {
                foreach (var r in results)
                {
                    var size = options.RawBytes ? $"{r.Size.ToString(CultureInfo.InvariantCulture)} bytes" : ConsoleUi.FormatBytes(r.Size);
                    Console.WriteLine($"{r.Lang ?? "?",-12} {r.Lines,6} lines  {size,12}  {r.Path}");
                }
                Console.Error.WriteLine($"({results.Count} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunExcerpt(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("excerpt", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: true);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("excerpt", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("excerpt")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "excerpt"))
            return CommandExitCodes.UsageError;
        if (options.Query == null)
        {
            WriteUsageError(
                "excerpt requires a path argument",
                GetUsageLineOrThrow("excerpt"),
                "Pass the indexed file path after `excerpt`, for example: `cdidx excerpt src/CodeIndex/Program.cs --start 20`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("excerpt", options))
            return CommandExitCodes.UsageError;
        if (options.FocusColumn == null && (options.FocusLine.HasValue || cmdArgs.Any(arg => arg == "--focus-length" || arg.StartsWith("--focus-length=", StringComparison.Ordinal))))
        {
            WriteValidationError(
                "--focus-line and --focus-length require --focus-column.",
                "Add `--focus-column <n>` so excerpt knows which token to keep visible inside the clamped line.");
            return CommandExitCodes.UsageError;
        }

        if (options.StartLine == null)
        {
            WriteValidationError(
                "excerpt requires --start <line>",
                "Add a starting line number, for example: `cdidx excerpt src/CodeIndex/Program.cs --start 20`.");
            return CommandExitCodes.UsageError;
        }

        var endLine = options.EndLine ?? options.StartLine.Value;
        if (endLine < options.StartLine.Value)
        {
            WriteValidationError(
                $"--start ({options.StartLine.Value}) must be less than or equal to --end ({endLine}).",
                "Use `--start` less than or equal to `--end`, or omit `--end` to read a single line.");
            return CommandExitCodes.UsageError;
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, options.Query, options.DbPathExplicit);
        return WithDb(options, jsonOptions, reader =>
        {
            if (options.FocusLine.HasValue)
            {
                var file = reader.GetFileByPath(filePath);
                if (file != null)
                {
                    var requestedStart = Math.Max(1, options.StartLine.Value - options.ContextBefore);
                    var requestedEnd = Math.Min(file.Lines, endLine + options.ContextAfter);
                    if (options.FocusLine.Value < requestedStart || options.FocusLine.Value > requestedEnd)
                    {
                        Console.Error.WriteLine($"Error: --focus-line ({options.FocusLine.Value}) must be within the returned excerpt range ({requestedStart}-{requestedEnd}).");
                        return CommandExitCodes.UsageError;
                    }
                }
            }
            if (options.FocusColumn.HasValue)
            {
                var focusLineLength = reader.GetExcerptFocusLineLength(
                    filePath,
                    options.StartLine.Value,
                    endLine,
                    options.ContextBefore,
                    options.ContextAfter,
                    options.FocusLine ?? options.StartLine.Value);
                if (focusLineLength.HasValue && options.FocusColumn.Value > focusLineLength.Value)
                {
                    Console.Error.WriteLine($"Error: --focus-column ({options.FocusColumn.Value}) must be within the focused line length ({focusLineLength.Value}).");
                    return CommandExitCodes.UsageError;
                }
            }

            var excerpt = reader.GetExcerpt(
                filePath,
                options.StartLine.Value,
                endLine,
                options.ContextBefore,
                options.ContextAfter,
                options.MaxLineWidth,
                options.FocusLine ?? options.StartLine.Value,
                options.FocusColumn,
                options.FocusLength);
            if (excerpt == null)
            {
                if (!options.Json)
                    Console.Error.WriteLine("No excerpt found.");
                return ZeroResultExitCode(options);
            }
            if (options.Json)
                excerpt.SemanticTokens = BuildExcerptSemanticTokens(excerpt);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(excerpt, CliJsonSerializerContextFactory.Create(jsonOptions).FileExcerptResult));
            }
            else
            {
                Console.WriteLine($"{excerpt.Path}:{excerpt.StartLine}-{excerpt.EndLine}");
                WriteNumberedExcerpt(excerpt.StartLine, excerpt.Content);
            }
            return CommandExitCodes.Success;
        });
    }

    private static List<ExcerptSemanticToken> BuildExcerptSemanticTokens(FileExcerptResult excerpt)
    {
        var tokens = new List<ExcerptSemanticToken>();
        var lines = excerpt.Content.Replace("\r\n", "\n").Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = 0;
            while (column < line.Length)
            {
                if (!IsSemanticTokenStart(line[column]))
                {
                    column++;
                    continue;
                }

                var start = column;
                column++;
                while (column < line.Length && IsSemanticTokenPart(line[column]))
                    column++;

                var tokenText = line[start..column];
                tokens.Add(new ExcerptSemanticToken
                {
                    StartLine = excerpt.StartLine + lineIndex,
                    StartColumn = start + 1,
                    EndLine = excerpt.StartLine + lineIndex,
                    EndColumn = column + 1,
                    Type = ClassifySemanticToken(tokenText),
                });
            }
        }

        return tokens;
    }

    private static bool IsSemanticTokenStart(char value) =>
        char.IsLetter(value) || value == '_' || char.IsDigit(value);

    private static bool IsSemanticTokenPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static string ClassifySemanticToken(string token)
    {
        if (token.All(char.IsDigit))
            return "number";
        if (char.IsUpper(token[0]))
            return "type";
        return "variable";
    }

    public static int RunFind(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var preparedFindArgs = PrepareFindArgs(cmdArgs, out var preparationError);
        if (preparationError != null)
        {
            Console.Error.WriteLine(preparationError);
            Console.Error.WriteLine(FindUsage);
            return CommandExitCodes.UsageError;
        }

        var findValidationError = ValidateFindArgs(preparedFindArgs);
        if (findValidationError != null)
        {
            Console.Error.WriteLine(findValidationError);
            Console.Error.WriteLine(FindUsage);
            return CommandExitCodes.UsageError;
        }

        var options = ParseArgs(
            preparedFindArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            Console.Error.WriteLine(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (options.Query is not null && string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: find query cannot be empty or whitespace-only");
            Console.Error.WriteLine("Hint: Pass a non-empty value after `find`; empty or whitespace-only arguments (e.g. `\"\"` or `\"   \"`) are rejected.");
            Console.Error.WriteLine(FindUsage);
            return CommandExitCodes.UsageError;
        }
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: find requires a query argument");
            Console.Error.WriteLine(FindUsage);
            return CommandExitCodes.UsageError;
        }

        if (options.PathPatterns.Count == 0)
        {
            Console.Error.WriteLine("Error: find requires at least one --path <glob> to scope the search to known files");
            Console.Error.WriteLine(FindUsage);
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                QueryCountResult counts;
                try
                {
                    counts = reader.CountFindInFiles(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact, options.FocusLine, options.FocusColumn, options.Regex);
                }
                catch (Exception ex) when (options.Regex && (ex is ArgumentException || ex is RegexMatchTimeoutException))
                {
                    Console.Error.WriteLine($"Error: invalid regular expression: {ex.Message}");
                    return CommandExitCodes.UsageError;
                }
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        var payload = BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, queryOptions: options, extraFields: static payload =>
                        {
                            payload["file_count"] = 0;
                        });
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                    }
                    return CommandExitCodes.Success;
                }

                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new QueryFindCountJsonResult(counts.Count, counts.FileCount, counts.FileCount), CliJsonSerializerContextFactory.Create(jsonOptions).QueryFindCountJsonResult)
                    : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var (contextBefore, contextAfter, snippetLines) = ResolveFindContext(options, preparedFindArgs);
            List<FileFindResult> results;
            try
            {
                results = reader.FindInFiles(options.Query, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, contextBefore, contextAfter, options.Exact, options.MaxLineWidth, options.FocusLine, options.FocusColumn, options.Regex);
            }
            catch (ArgumentException ex) when (options.Regex)
            {
                Console.Error.WriteLine($"Error: invalid regular expression: {ex.Message}");
                return CommandExitCodes.UsageError;
            }
            catch (RegexMatchTimeoutException ex) when (options.Regex)
            {
                Console.Error.WriteLine($"Error: invalid regular expression: {ex.Message}");
                return CommandExitCodes.UsageError;
            }
            if (results.Count == 0)
            {
                var candidateFileCount = reader.CountFindCandidateFiles(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
                if (options.Json)
                {
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
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No matches found", options));
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
                Console.Error.WriteLine($"({results.Count} matches in {fileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

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
                    return BuildPositiveIntegerError("--limit", value, arg);
                if ((arg == "--limit" || arg == "--top")
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limitCeil)
                    && NumericFlagUpperBounds.TryGetValue("--limit", out var limitMax)
                    && limitCeil > limitMax)
                    return BuildPositiveIntegerUpperBoundError("--limit", value, limitMax);
                if (arg == "--max-line-width" && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var widthValue) || widthValue < 0))
                    return BuildNonNegativeIntegerError(arg, value);
                if (arg == "--max-line-width" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var widthCeil) && widthCeil > LineWidthFormatter.MaxAllowedLineWidth)
                    return BuildNonNegativeIntegerUpperBoundError("--max-line-width", value, LineWidthFormatter.MaxAllowedLineWidth);
                if ((arg == "--before" || arg == "--after") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var context) || context < 0))
                    return BuildNonNegativeIntegerError(arg, value);
                if ((arg == "--before" || arg == "--after")
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextCeil)
                    && NumericFlagUpperBounds.TryGetValue(arg, out var contextMax)
                    && contextCeil > contextMax)
                    return BuildNonNegativeIntegerUpperBoundError(arg, value, contextMax);
                if (arg == "--snippet-lines" && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snippetLines) || snippetLines <= 0))
                    return BuildPositiveIntegerError(arg, value, arg);
                if (arg == "--snippet-lines"
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var snippetLinesCeil)
                    && NumericFlagUpperBounds.TryGetValue(arg, out var snippetLinesMax)
                    && snippetLinesCeil > snippetLinesMax)
                    return BuildPositiveIntegerUpperBoundError(arg, value, snippetLinesMax);
                if ((arg == "--focus-line" || arg == "--focus-column") && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var focus) || focus <= 0))
                    return BuildPositiveIntegerError(arg, value, arg);
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
                var error = $"Error: unsupported option for find: {rawArg}";
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

    public static int RunMap(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("map", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        if (!TryExtractDepsFormat(cmdArgs, out var depsFormat, out var parseArgs, out var depsFormatError))
        {
            Console.Error.WriteLine(depsFormatError);
            return CommandExitCodes.UsageError;
        }

        var options = ParseArgs(
            parseArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("map", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("map")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "map"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("map", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var map = reader.GetRepoMap(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.MinEntrypointConfidence);
            WorkspaceMetadataEnricher.Enrich(map, options.DbPath, options.DbPathExplicit);
            if (options.ContextAfterExplicit)
                ApplyRepoMapDepth(map, options.ContextAfter);

            // Return not-found only when a narrowing filter is active and produces zero files.
            // Unfiltered empty indexes return success (valid state for health probes).
            // フィルタ指定時に該当0件なら未検出を返す。フィルタなしの空DBは正常（ヘルスチェック用途）。
            var hasFilter = options.PathPatterns.Count > 0 || options.ExcludePaths.Count > 0
                || options.ExcludeTests || options.Lang != null;
            if (map.FileCount == 0 && hasFilter)
            {
                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(map, CliJsonSerializerContextFactory.Create(jsonOptions).RepoMapResult));
                }
                else
                {
                    Console.Error.WriteLine("No files found matching the given filters.");
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var payload = BuildRepoMapJsonPayload(map, options, jsonOptions);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                Console.WriteLine($"Files      : {map.FileCount:N0}");
                Console.WriteLine($"Lines      : {map.TotalLines:N0}");
                Console.WriteLine($"Symbols    : {map.TotalSymbols:N0}");
                Console.WriteLine($"References : {map.TotalReferences:N0}");
                if (map.IndexedAt != null)
                    Console.WriteLine($"Scope Indexed At     : {map.IndexedAt:O}");
                if (map.LatestModified != null)
                    Console.WriteLine($"Scope Modified       : {map.LatestModified:O}");
                if (map.WorkspaceIndexedAt != null)
                    Console.WriteLine($"Workspace Indexed At : {map.WorkspaceIndexedAt:O}");
                if (map.WorkspaceLatestModified != null)
                    Console.WriteLine($"Workspace Modified   : {map.WorkspaceLatestModified:O}");
                if (map.GitHead != null)
                    Console.WriteLine($"Git HEAD   : {map.GitHead}");
                if (map.GitIsDirty != null)
                    Console.WriteLine($"Git Dirty  : {map.GitIsDirty}");
                if (!map.GraphTableAvailable)
                    Console.WriteLine("WARN       : symbol_references table missing — reference counts are synthesized 0. Do not use ReferenceRich / reference-derived ranking as authoritative.");
                if (MapSectionEnabled(options, "languages"))
                    WriteRepoMapSection("Languages", map.Languages.Select(item => $"{item.Lang,-12} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                if (MapSectionEnabled(options, "tree"))
                    WriteRepoMapSection("Modules", map.Modules.Select(item => $"{item.Module,-24} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                if (MapSectionEnabled(options, "hotspots"))
                {
                    WriteRepoMapSection("Top files", map.TopFiles.Select(item => $"{item.Path}  [score {item.Score}, {item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                    WriteRepoMapSection("Symbol-rich files", map.SymbolRichFiles.Select(item => $"{item.Path}  [{item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                    WriteRepoMapSection("Reference-rich files", map.ReferenceRichFiles.Select(item => $"{item.Path}  [{item.ReferenceCount} refs, {item.SymbolCount} syms]"));
                    WriteRepoMapSection("Entrypoints", map.Entrypoints.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.Line}  [score {item.Score}, confidence {item.Confidence:0.###}, {item.MatchType}, hint #{item.HintRank}]"));
                }
                if (MapSectionEnabled(options, "metrics"))
                    WriteRepoMapSection("Largest files", map.LargestFiles.Select(item =>
                {
                    var size = options.RawBytes ? $"{item.Size.ToString(CultureInfo.InvariantCulture)} bytes" : ConsoleUi.FormatBytes(item.Size);
                    return $"{item.Path}  [{item.Lines} lines, {size}]";
                }));
            }

            return CommandExitCodes.Success;
        });
    }

    private static bool MapSectionEnabled(QueryCommandOptions options, string section)
        => options.MapSections == null || options.MapSections.Contains(section, StringComparer.Ordinal);

    private static void ApplyRepoMapDepth(RepoMapResult map, int depth)
    {
        map.Modules = map.Modules
            .Where(module => GetPathDepth(module.Module) <= depth)
            .ToList();
    }

    private static int GetPathDepth(string path)
        => string.IsNullOrEmpty(path) ? 0 : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static JsonObject BuildRepoMapJsonPayload(RepoMapResult map, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(map, CliJsonSerializerContextFactory.Create(jsonOptions).RepoMapResult)!.AsObject();
        if (options.MapSections == null)
        {
            if (options.ContextAfterExplicit)
                payload["depth"] = options.ContextAfter;
            return payload;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "api_version",
            "fileCount",
            "totalLines",
            "totalSymbols",
            "totalReferences",
            "indexedAt",
            "latestModified",
            "workspaceIndexedAt",
            "workspaceLatestModified",
            "projectRoot",
            "gitHead",
            "gitIsDirty",
            "indexed_head_commit",
            "worktree_head_changed",
            "graphTableAvailable",
        };
        if (MapSectionEnabled(options, "languages"))
            keep.Add("languages");
        if (MapSectionEnabled(options, "tree"))
            keep.Add("modules");
        if (MapSectionEnabled(options, "hotspots"))
        {
            keep.Add("topFiles");
            keep.Add("symbolRichFiles");
            keep.Add("referenceRichFiles");
            keep.Add("entrypoints");
        }
        if (MapSectionEnabled(options, "metrics"))
            keep.Add("largestFiles");

        foreach (var propertyName in payload.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            payload.Remove(propertyName);
        payload["sections"] = new JsonArray(options.MapSections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        if (options.ContextAfterExplicit)
            payload["depth"] = options.ContextAfter;
        return payload;
    }

    public static int RunInspect(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("inspect", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("inspect", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("inspect"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "inspect", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "inspect requires a symbol query argument",
                GetUsageLineOrThrow("inspect"),
                "Add the symbol you want to inspect, for example: `cdidx inspect QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "inspect requires a symbol query argument",
                GetUsageLineOrThrow("inspect"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("inspect", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var analysis = reader.AnalyzeSymbol(options.Query, options.Limit, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.MaxLineWidth);
            var sqlGraphSignal = NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                DbReader.IsSqlLanguage(options.Lang)
                    || DbReader.IsSqlLanguage(analysis.GraphLanguage)
                    || DbReader.IsSqlLanguage(analysis.File?.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.References.Select(reference => reference.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callees.Select(callee => callee.Lang)));
            var exactSignal = exact && analysis.ExactIndexAvailable.HasValue
                ? new ExactQuerySignal(
                    analysis.ExactIndexAvailable.Value,
                    analysis.ExactHasMissingIndex ?? false,
                    analysis.ExactHasMissingTable ?? false,
                    analysis.DegradedReason)
                : (ExactQuerySignal?)null;
            analysis.SqlGraphContractReady = sqlGraphSignal.Relevant ? sqlGraphSignal.Ready : null;
            analysis.SqlGraphContractDegradedReason = sqlGraphSignal.Relevant ? sqlGraphSignal.DegradedReason : null;
            WorkspaceMetadataEnricher.Enrich(analysis, options.DbPath, options.DbPathExplicit);
            if (exactSignal.HasValue)
                WriteExactBundleWarningIfNeeded(exact, options.Json, exactSignal.Value, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (options.Json)
            {
                var payload = JsonSerializer.SerializeToNode(analysis, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolAnalysisResult)!.AsObject();
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                Console.WriteLine($"Query: {analysis.Query}");
                if (analysis.File != null)
                    Console.WriteLine($"File : {analysis.File.Path} ({analysis.File.Lang ?? "?"}, {analysis.File.Lines} lines)");
                if (analysis.WorkspaceIndexedAt != null)
                    Console.WriteLine($"Workspace Indexed At : {analysis.WorkspaceIndexedAt:O}");
                if (analysis.WorkspaceLatestModified != null)
                    Console.WriteLine($"Workspace Modified   : {analysis.WorkspaceLatestModified:O}");
                if (analysis.GitHead != null)
                    Console.WriteLine($"Git HEAD             : {analysis.GitHead}");
                if (analysis.GitIsDirty != null)
                    Console.WriteLine($"Git Dirty            : {analysis.GitIsDirty}");
                if (analysis.GraphLanguage != null)
                    Console.WriteLine($"Graph Language       : {analysis.GraphLanguage}");
                if (analysis.GraphSupported != null)
                    Console.WriteLine($"Graph Supported      : {analysis.GraphSupported}");
                if (analysis.GraphSupportReason != null)
                    Console.WriteLine($"Graph Note           : {analysis.GraphSupportReason}");
                if (analysis.UnsupportedSymbolKind != null)
                    Console.WriteLine($"Graph Limitation     : unsupported symbol kind '{analysis.UnsupportedSymbolKind}'");
                if (!analysis.GraphTableAvailable)
                    Console.WriteLine("Graph Table          : MISSING — empty References/Callers/Callees are degraded, NOT real zero-hit results.");
                if (exactSignal is ExactQuerySignal signal && !signal.ExactIndexAvailable && signal.DegradedReason != null)
                {
                    if (signal.HasMissingIndex)
                        Console.WriteLine($"Exact Index          : DEGRADED — {signal.DegradedReason}. Results are correct but may be slow.");
                    else if (IsCSharpCanonicalNameSignal(signal))
                    {
                        Console.WriteLine($"Exact Index          : DEGRADED — {signal.DegradedReason}. Exact-name C# operator / indexer matches may be incomplete.");
                        Console.WriteLine($"Hint                 : Run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}`.");
                    }
                }
                WriteExactZeroHint(analysis.ExactZeroHint);
                WriteRepoMapSection("Definitions", analysis.Definitions.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("Nearby symbols", analysis.NearbySymbols.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("References", analysis.References.Select(item => $"{item.Path}:{item.Line}:{item.Column}  {item.Context}"));
                WriteRepoMapSection("Callers", analysis.Callers.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                WriteRepoMapSection("Callees", analysis.Callees.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
            }

            return IsEmptySymbolAnalysis(analysis) ? ZeroResultExitCode(options) : CommandExitCodes.Success;
        });
    }

    public static int RunOutline(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        if (cmdArgs.Length == 0 || cmdArgs[0].StartsWith('-'))
        {
            WriteUsageError(
                "outline requires a file path.",
                GetUsageLineOrThrow("outline"),
                "Pass the indexed file path, for example: `cdidx outline src/CodeIndex/Program.cs`.");
            return CommandExitCodes.UsageError;
        }

        var previewOptionError = ValidatePreviewOptions("outline", cmdArgs[1..], allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs[1..],
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("outline", cmdArgs[1..], CliFlagSchema.GetAcceptedFlagNamesForCommand("outline")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "outline"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("outline", options))
            return CommandExitCodes.UsageError;

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, cmdArgs[0], options.DbPathExplicit);
        return WithDb(options, jsonOptions, reader =>
        {
            var outline = reader.GetOutline(filePath);
            if (outline == null)
            {
                if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new QueryPathErrorJsonResult(filePath, "file not found in index"), CliJsonSerializerContextFactory.Create(jsonOptions).QueryPathErrorJsonResult));
                else
                    Console.Error.WriteLine($"Error: '{filePath}' not found in index.");
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(outline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult));
            }
            else
            {
                var outlineContent = reader.GetExcerpt(filePath, 1, outline.TotalLines)?.Content;

                Console.WriteLine($"# {outline.Path}  ({outline.Lang ?? "unknown"}, {outline.TotalLines} lines, {outline.SymbolCount} symbols)");
                Console.WriteLine();
                var duplicateNames = outline.Symbols
                    .GroupBy(sym => sym.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var sym in outline.Symbols)
                {
                    // Indent nested symbols by computed tree depth / コンテナ連鎖の深さでインデント
                    var indent = sym.Depth > 0 ? new string(' ', 4 * sym.Depth) : "";
                    var useDisplayName = sym.Kind is "function" or "method" or "constructor"
                        && duplicateNames.Contains(sym.Name)
                        && !string.IsNullOrWhiteSpace(sym.DisplayName);
                    var ret = !useDisplayName && sym.ReturnType != null ? $": {sym.ReturnType} " : "";
                    var sig = useDisplayName ? sym.DisplayName : sym.Signature ?? $"{sym.Kind} {sym.Name}";
                    // Avoid duplicating visibility when signature already contains it
                    // シグネチャに既に visibility が含まれている場合は重複を避ける
                    var vis = !useDisplayName && sym.Visibility != null && !sig.TrimStart().StartsWith(sym.Visibility, StringComparison.Ordinal)
                        ? $"{sym.Visibility} "
                        : "";
                    Console.WriteLine($"  {sym.Line,5}  {indent}{vis}{sig} {ret}");
                }

                // AI-orientation hint for C# files that look like top-level-statements programs:
                // no class / struct / interface / enum / namespace / record / delegate at all
                // means the executable body lives between the imports and local functions and
                // will not appear in outline at all. Emitting a short note on stderr keeps the
                // main human-readable block clean while giving AI consumers a reason for the gap.
                // AI向けヒント: C# のトップレベルステートメント想定のファイル
                // （class / struct / interface / enum / namespace / record / delegate が一切無い）は、
                // 実行本体が import と local function の間に書かれるため outline に現れない。
                // 人間向け本体を汚さないよう、理由を短く stderr に出す。
                if (LooksLikeCsharpTopLevelStatements(outline, outlineContent))
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Note: no type/namespace declarations found; this file likely uses C# top-level statements.");
                    Console.Error.WriteLine("      Outline lists imports and local functions only; the executable body is not indexed as symbols.");
                }
            }
            return CommandExitCodes.Success;
        });
    }

    /// <summary>
    /// Heuristic: hint only when a non-trivial C# file has no type/namespace declarations and
    /// its reconstructed content still contains uncovered file-scope executable code after
    /// skipping symbol-covered lines, imports, metadata-only attribute lines, comments, and
    /// preprocessor directives. This keeps the note off common files such as GlobalUsings.cs,
    /// AssemblyInfo.cs, and local-function-only files while preserving statement-only Program.cs
    /// files.
    /// Tiny files (snippets, partials under ~20 lines) are excluded to avoid noise.
    /// ヒューリスティック: 20 行以上の C# ファイルで型/名前空間宣言が無く、かつ
    /// import 行、metadata-only 属性行、コメント、プリプロセッサ行を除いても
    /// file-scope の実行コードが残る場合だけヒントを出す。これにより GlobalUsings.cs や
    /// AssemblyInfo.cs の誤検出を避けつつ、
    /// statement-only の Program.cs は拾い続ける。小さい断片はノイズ回避のため除外。
    /// </summary>
    private static bool LooksLikeCsharpTopLevelStatements(OutlineResult outline, string? content)
    {
        if (outline.Lang != "csharp") return false;
        if (outline.TotalLines < 20) return false;
        foreach (var sym in outline.Symbols)
        {
            if (sym.Kind is "class" or "struct" or "interface" or "enum" or "namespace" or "delegate" or "record")
                return false;
        }

        if (string.IsNullOrWhiteSpace(content))
            return false;

        var coveredLines = new bool[Math.Max(outline.TotalLines, 0) + 1];
        foreach (var sym in outline.Symbols)
        {
            var startLine = sym.StartLine > 0 ? sym.StartLine : sym.Line;
            var endLine = sym.EndLine >= startLine ? sym.EndLine : startLine;
            startLine = Math.Max(1, startLine);
            endLine = Math.Min(outline.TotalLines, endLine);
            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
                coveredLines[lineNumber] = true;
        }

        var inBlockComment = false;
        var currentLineNumber = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            currentLineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (currentLineNumber < coveredLines.Length && coveredLines[currentLineNumber])
                continue;

            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = false;
                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = true;
                continue;
            }

            if (line.StartsWith("using ", StringComparison.Ordinal))
            {
                if (line.StartsWith("using var ", StringComparison.Ordinal))
                    return true;
                if (line.StartsWith("using (", StringComparison.Ordinal))
                    return true;
                continue;
            }
            if (line.StartsWith("global using ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("extern alias ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[assembly:", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[module:", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*/", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;
            return true;
        }

        return false;
    }

    public static int RunStatus(string[] cmdArgs, JsonSerializerOptions jsonOptions, string? appVersion = null)
    {
        var checkUpdates = cmdArgs.Contains("--check-updates", StringComparer.Ordinal);
        if (checkUpdates)
            cmdArgs = cmdArgs.Where(arg => !string.Equals(arg, "--check-updates", StringComparison.Ordinal)).ToArray();
        var previewOptionError = ValidatePreviewOptions("status", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowStatusCheck: true,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("status", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("status")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "status"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("status", options))
            return CommandExitCodes.UsageError;
        if (options.StatusConfig)
        {
            if (options.CheckWorkspace || options.StatusLogPath || options.StatusExplainField != null)
            {
                Console.Error.WriteLine("Error: status --config cannot be combined with --check, --log-path, or --explain.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(BuildEffectiveConfigJson(options, cmdArgs, appVersion).ToJsonString(jsonOptions));
            return CommandExitCodes.Success;
        }
        if (options.StatusLogPath)
        {
            if (options.CheckWorkspace)
            {
                Console.Error.WriteLine("Error: status --log-path cannot be combined with --check.");
                return CommandExitCodes.UsageError;
            }

            var logPath = GlobalToolLog.ResolveLogDirectoryForStatus();
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["log_path"] = logPath }, jsonOptions));
            else
                Console.WriteLine(logPath);
            return CommandExitCodes.Success;
        }
        if (options.StatusExplainField != null)
        {
            if (options.Json)
            {
                Console.Error.WriteLine("Error: status --explain is human-readable only and cannot be combined with --json.");
                Console.Error.WriteLine("Hint: omit --json, or use plain `status --json` to read machine-oriented readiness fields.");
                return CommandExitCodes.UsageError;
            }
            return WriteStatusReadinessExplanation(options.StatusExplainField);
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var staleAfter = (Value: DefaultStaleAfter, Error: (string?)null);
            if (options.CheckWorkspace || options.StaleAfter.HasValue)
            {
                staleAfter = ResolveStaleAfter(options, Environment.GetEnvironmentVariable(StaleAfterEnvironmentVariable));
                if (staleAfter.Error != null)
                {
                    Console.Error.WriteLine(staleAfter.Error);
                    return CommandExitCodes.UsageError;
                }
            }

            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
            status.DataDir = options.DataDir;
            status.DataDirSource = options.DataDirSource;
            status.DataDirMode = DataDirectorySecurity.GetUnixModeString(GetDataDirectoryPath(options.DbPath));
            status.DbFileMode = DbContext.GetUnixFileModeString(options.DbPath);
            status.MacProfile = MacProfileDetector.DetectCurrent();
            if (options.CheckWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(reader, status.ProjectRoot);
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = (long)Math.Round(staleAfter.Value.TotalSeconds, MidpointRounding.AwayFromZero);
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            // Attach runtime metadata / ランタイムメタデータを付加
            status.SymbolKinds = reader.GetSymbolKindCounts();
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot();
            using var postExtractionHookRunner = PostExtractionHookRunner.DiscoverDefault();
            var postExtractionHooks = postExtractionHookRunner.Hooks;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
                        Name = hook.Name,
                        AssemblyPath = hook.AssemblyPath,
                        TypeName = hook.TypeName,
                        CallbackBudgetMs = (long)Math.Round(postExtractionHookRunner.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero),
                    })
                    .ToList();
            }
            if (appVersion != null)
                status.Version = appVersion;
            var updateResult = checkUpdates && appVersion != null
                ? UpdateChecker.Check(appVersion)
                : null;
            status.UpdateCheck = updateResult;

            // Build one-line summary for AI orientation / AI向けの1行サマリーを構築
            var topLangs = status.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key);
            var freshness = BuildStatusFreshnessLabel(status);
            var dirty = status.GitIsDirty == true ? ", dirty" : "";
            ApplyStatusDegradationGuidance(status, options);

            var degraded = IsStatusDegraded(status)
                ? ", DEGRADED"
                : "";
            status.Summary = $"{status.Files} files, {status.Symbols} symbols, {status.References} refs across {status.Languages.Count} languages ({string.Join(", ", topLangs)}); index {freshness}{dirty}{degraded}";

            IReadOnlyList<StatusCheckFailure> checkFailures = options.CheckWorkspace
                ? BuildStatusCheckFailures(status, options.StatusCheckScopes)
                : Array.Empty<StatusCheckFailure>();
            if (options.CheckWorkspace)
                status.FailedChecks = checkFailures.Select(f => f.Name).ToList();

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    status,
                    CliJsonSerializerContextFactory.Create(jsonOptions).StatusResult));
            }
            else if (options.CheckWorkspace)
            {
                if (options.StaleAfter.HasValue)
                    WriteStatusAge(status, staleAfter.Value);
                if (checkFailures.Count > 0)
                    WriteStatusCheckDiagnostics(checkFailures);
            }
            else
            {
                if (status.Summary != null)
                    Console.WriteLine(status.Summary);
                Console.WriteLine();
                if (status.Version != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Version", $"cdidx v{status.Version}"));
                if (updateResult?.UpdateAvailable == true && updateResult.LatestVersion != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Update", $"cdidx v{updateResult.LatestVersion} is available."));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{status.Files:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", $"{status.Chunks:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", $"{status.Symbols:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", $"{status.References:N0}"));
                if (status.IndexedAt != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Indexed", $"{status.IndexedAt:O}"));
                if (status.LatestModified != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Source", $"{status.LatestModified:O}"));
                if (status.GitHead != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git HEAD", status.GitHead));
                if (status.GitIsDirty != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git Dirty", status.GitIsDirty));
                if (status.MacProfile != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("MAC", status.MacProfile));
                // #1509 surface: SHA / branch / timestamp / drift come from the per-success
                // stamp (indexed_head_sha / _branch / _timestamp) and reflect last-touched HEAD
                // regardless of update mode. #1508/#1512's IndexedHeadCommit (full-scan only)
                // is rendered separately below when it disagrees with the runtime GitHead.
                if (status.IndexedHeadSha != null)
                {
                    var branchSuffix = string.IsNullOrWhiteSpace(status.IndexedHeadBranch)
                        ? string.Empty
                        : $" (branch {status.IndexedHeadBranch})";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", $"{status.IndexedHeadSha}{branchSuffix}"));
                }
                else if (status.IndexedHeadCommit != null && !string.Equals(status.IndexedHeadCommit, status.GitHead, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", status.IndexedHeadCommit));
                }
                if (status.IndexedHeadTimestamp != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx Stamp", $"{status.IndexedHeadTimestamp:O}"));
                if (status.CommitsAheadOfIndexedHead is { } ahead && ahead > 0)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx Drift", $"workspace is {ConsoleUi.Counted(ahead, "commit")} ahead of indexed HEAD — rerun `cdidx index .` to refresh."));
                if (status.WorkspaceCheck != null)
                {
                    WriteStatusAge(status, staleAfter.Value);
                    WriteWorkspaceCheck(status.WorkspaceCheck);
                }
                if (status.Languages.Count > 0)
                {
                    Console.WriteLine("Languages:");
                    foreach (var (lang, count) in status.Languages)
                        Console.WriteLine($"  {lang,-12} {count,6}");
                }
                if (status.SymbolKinds is { Count: > 0 })
                {
                    Console.WriteLine("Kinds:");
                    foreach (var (kind, count) in status.SymbolKinds)
                        Console.WriteLine($"  {kind,-12} {count,6}");
                }
                if (status.GraphSupportedLanguages is { Count: > 0 })
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Graph", $"{status.GraphSupportedLanguages.Count} languages ({string.Join(", ", status.GraphSupportedLanguages)})"));
                // #1546: surface the persisted filesystem case-sensitivity so operators can
                // diagnose phantom path collapses on case-sensitive APFS / WSL / ReFS volumes.
                // #1546: case-sensitivity を診断用に明示する。
                if (status.PathCaseSensitive != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("FS Case", status.PathCaseSensitive == true ? "case-sensitive" : "case-insensitive"));
                WriteStatusReadinessSummary(status, options);
                if (status.WorktreeHeadChanged == true)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"worktree HEAD changed since the index was built ({ShortSha(status.IndexedHeadCommit)} -> {ShortSha(status.GitHead)}). Run `{BuildReindexRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to refresh the index for the current branch."));
                if (status.IndexNewerThanReader)
                {
                    var reason = status.IndexNewerThanReaderReason ?? "DB was written by a newer cdidx than this binary.";
                    var writerLabel = status.IndexWriterVersion is { Length: > 0 } writerVersion
                        ? $" (DB writer: cdidx v{writerVersion}; reader: cdidx v{status.Version ?? "unknown"})"
                        : status.Version is { Length: > 0 } readerVersion
                            ? $" (reader: cdidx v{readerVersion})"
                            : "";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"{reason}{writerLabel}"));
                }
                if (!status.GraphTableAvailable)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "symbol_references table missing — reference / caller / callee / unused counts are degraded to 0."));
                if (!status.IssuesTableAvailable)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "file_issues table missing — validate output is degraded to empty."));
                else if (!status.FileIssuesDataCurrent)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "file_issues table exists but its rows are not stamped current for this index generation."));
                if (!status.SqlGraphContractReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"SQL graph/dependency results may be stale. Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots."));
                if (!status.HotspotFamilyReady && status.HotspotFamilyDegradedReason != null)
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", status.HotspotFamilyDegradedReason));
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", "rerun `cdidx index <projectPath>` to restore authoritative cross-file hotspot families."));
                }
                if (!status.CSharpSymbolNameReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"C# exact-name for operators / conversion operators / indexers is degraded. Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical symbol names in place."));
                // #435: tell the user when deps / impact metadata-attribute edges fall back
                // to the legacy signature / name-suffix heuristic (impostor classes may be
                // silently promoted or demoted until the authoritative resolver is re-run).
                // #435: deps / impact の metadata-attribute edge が legacy heuristic に
                // 縮退しているときは明示する。
                if (!status.CSharpMetadataTargetReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "C# deps / impact metadata-attribute edges fall back to the signature / name-suffix heuristic. Run `cdidx index .` to re-stamp authoritative is_metadata_target values."));
                // #86: tell the user when `--exact` is running on the ASCII NOCASE fallback.
                // #86: --exact が ASCII NOCASE fallback で動いているときは明示する。
                if (!status.FoldReady)
                {
                    if (IsFoldOnlyReadinessDegraded(status) && status.DegradedReason != null && status.RecommendedAction != null && status.AlternativeAction != null)
                    {
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", status.DegradedReason));
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", $"run `{status.RecommendedAction}` to restamp folded-name columns in place."));
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", $"or run `{status.AlternativeAction}` for a full rebuild."));
                    }
                    else
                    {
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", BuildFoldNotReadyWarning(status.FoldReadyReason, BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit), BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit))));
                    }
                }
                var totalLangs = FileIndexer.GetLanguageExtensions().Values.Distinct().Count();
                var symbolLangs = SymbolExtractor.GetSupportedLanguages().Count;
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Support", $"{totalLangs} detected, {symbolLangs} with symbols, {status.GraphSupportedLanguages?.Count ?? 0} with graph"));
            }

            if (!options.CheckWorkspace)
                return CommandExitCodes.Success;
            return GetStatusCheckExitCode(checkFailures);
        });
    }

    private static JsonObject BuildEffectiveConfigJson(QueryCommandOptions options, string[] cmdArgs, string? appVersion)
    {
        JsonObject Entry<T>(T? value, string source) => new()
        {
            ["value"] = JsonSerializer.SerializeToNode(value),
            ["source"] = source,
        };
        var staleAfterEnvValue = Environment.GetEnvironmentVariable(StaleAfterEnvironmentVariable);

        var payload = new JsonObject
        {
            ["api_version"] = "1",
            ["effective_config"] = new JsonObject
            {
                ["db_path"] = Entry(options.DbPath, ResolveDbPathConfigSource(options)),
                ["data_dir"] = Entry(options.DataDir, options.DataDirSource ?? "flag"),
                ["limit"] = Entry(options.Limit, ResolveNumericConfigSource(cmdArgs, "--limit", "--top", DefaultLimitEnvironmentVariable)),
                ["snippet_lines"] = Entry(options.SnippetLines, ResolveNumericConfigSource(cmdArgs, "--snippet-lines", null, DefaultSnippetLinesEnvironmentVariable)),
                ["max_line_width"] = Entry(options.MaxLineWidth, ResolveNumericConfigSource(cmdArgs, "--max-line-width", null, DefaultMaxLineWidthEnvironmentVariable)),
                ["json"] = Entry(options.Json, HasOption(cmdArgs, "--json") ? "flag" : "default"),
                ["stale_after"] = Entry(options.StaleAfter?.ToString() ?? staleAfterEnvValue, options.StaleAfter.HasValue ? "flag" : ResolveEnvSource(StaleAfterEnvironmentVariable)),
                ["global_tool_log_dir"] = Entry(GlobalToolLog.ResolveLogDirectoryForStatus(), ResolveEnvSource("CDIDX_GLOBAL_TOOL_LOG_DIR")),
                ["version"] = Entry(appVersion ?? ConsoleUi.LoadVersion(), "build"),
            },
        };
        return payload;
    }

    private static string ResolveDbPathConfigSource(QueryCommandOptions options)
    {
        if (options.DbPathExplicit)
            return "flag";
        return options.DataDirSource switch
        {
            DbPathResolver.DataDirSourceFlag => "flag",
            DbPathResolver.DataDirSourceEnv => $"env:{DbPathResolver.DataDirEnvironmentVariable}",
            DbPathResolver.DataDirSourceXdg => "env:XDG_DATA_HOME",
            DbPathResolver.DataDirSourceWorkspace => "workspace",
            _ => "default",
        };
    }

    private static string ResolveNumericConfigSource(string[] args, string primaryFlag, string? aliasFlag, string envName)
    {
        if (HasOption(args, primaryFlag) || (aliasFlag != null && HasOption(args, aliasFlag)))
            return "flag";
        if (Environment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = Environment.GetEnvironmentVariable(CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }

    private static string ResolveEnvSource(string envName)
    {
        if (Environment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = Environment.GetEnvironmentVariable(CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }

    private static bool HasOption(string[] args, string optionName)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, optionName, StringComparison.Ordinal))
                return true;
            if (arg.StartsWith(optionName + "=", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static int RunVacuum(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("vacuum", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("vacuum")))
            return CommandExitCodes.UsageError;
        var explicitDbPathError = BuildExplicitDbPathParseError(options);
        if (explicitDbPathError != null && explicitDbPathError.Contains(CommandErrorCodes.DbNotFound, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(explicitDbPathError);
            Console.Error.WriteLine("Hint: point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.");
            return CommandExitCodes.NotFound;
        }
        if (TryWriteParseError(options, "vacuum"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("vacuum", options))
            return CommandExitCodes.UsageError;

        if (!DbContext.TryValidateExistingCodeIndexDb(options.DbPath, out var validationMessage, out var isNotFound))
        {
            Console.Error.WriteLine($"Error [{(isNotFound ? CommandErrorCodes.DbNotFound : CommandErrorCodes.DbError)}]: {validationMessage}");
            Console.Error.WriteLine(isNotFound
                ? "Hint: point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one."
                : "Hint: point `--db` at an existing CodeIndex database created by `cdidx index`, then retry `cdidx vacuum`.");
            return isNotFound ? CommandExitCodes.NotFound : CommandExitCodes.DatabaseError;
        }

        using var db = new DbContext(options.DbPath);
        var result = db.RunIncrementalVacuum();
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                result,
                CliJsonSerializerContextFactory.Create(jsonOptions).VacuumResult));
        }
        else
        {
            Console.WriteLine($"Vacuum complete: reclaimed {result.PagesReclaimed:N0} page(s) ({result.BytesReclaimed:N0} bytes).");
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Page size", $"{result.PageSize:N0} bytes"));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Pages", $"{result.PageCountBefore:N0} -> {result.PageCountAfter:N0}"));
            Console.WriteLine(ConsoleUi.FormatSummaryLine("Freelist", $"{result.FreelistCountBefore:N0} -> {result.FreelistCountAfter:N0}"));
        }

        return CommandExitCodes.Success;
    }

    public static int RunImpact(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("impact", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("impact", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("impact"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "impact"))
            return CommandExitCodes.UsageError;
        if (TryWriteBlankQueryError(options, "impact"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "impact requires a symbol query argument",
                GetUsageLineOrThrow("impact"),
                "Add the symbol whose callers you want to inspect, for example: `cdidx impact QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "impact requires a symbol query argument",
                GetUsageLineOrThrow("impact"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("impact", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var maxDepth = options.ContextAfterExplicit ? options.ContextAfter : 5; // --max-hops/--depth is parsed into ContextAfter; 0 means resolve-only
            if (!options.Json && options.ImpactDeprecatedDepthUsed)
                Console.Error.WriteLine("Warning: --depth is deprecated for impact; use --max-hops instead.");
            var analysis = reader.AnalyzeImpact(options.Query, maxDepth, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.WithPaths);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, analysis.Callers, options.SnippetLines, options.MaxLineWidth);
            var sqlGraphSignal = NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                DbReader.IsSqlLanguage(options.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || reader.AnyFilePathHasLanguage(analysis.FileImpacts.SelectMany(impact => new[] { impact.SourcePath, impact.TargetPath }), "sql"));
            var confirmedCount = analysis.Callers.Count;
            var confirmedFileCount = analysis.Callers.Select(r => r.Path).Distinct().Count();
            var hintCount = analysis.FileImpacts.Count;
            var hintFileCount = analysis.FileImpacts.Select(r => r.SourcePath).Distinct().Count();
            var hasHeuristicHints = analysis.ImpactMode == "file_dependency_hints";
            var visibleCount = hasHeuristicHints ? hintCount : confirmedCount;
            var visibleFileCount = hasHeuristicHints ? hintFileCount : confirmedFileCount;
            var depthZeroResolved = maxDepth == 0 && analysis.DefinitionCount > 0;

            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);

            if (confirmedCount == 0 && !hasHeuristicHints)
            {
                if (!options.CountOnly && depthZeroResolved)
                {
                    if (options.Json)
                    {
                        var payload = BuildJsonZeroResultPayload(
                            reader,
                            jsonOptions,
                            resultsKey: "callers",
                            graphTableAvailable: analysis.GraphTableAvailable,
                            degraded: false,
                            extraFields: zeroPayload =>
                            {
                                zeroPayload["query"] = options.Query;
                                zeroPayload["resolved_name"] = analysis.ResolvedName;
                                zeroPayload["file_count"] = 0;
                                zeroPayload["confirmed_count"] = 0;
                                zeroPayload["confirmed_file_count"] = 0;
                                zeroPayload["hint_count"] = 0;
                                zeroPayload["hint_file_count"] = 0;
                                zeroPayload["max_hops"] = maxDepth;
                                zeroPayload["max_depth"] = maxDepth;
                                zeroPayload["actual_depth"] = 0;
                                zeroPayload["truncated"] = analysis.Truncated;
                                if (analysis.TruncatedReason != null)
                                    zeroPayload["truncated_reason"] = analysis.TruncatedReason;
                                AddImpactTerminationJsonFields(zeroPayload, analysis, jsonOptions);
                                zeroPayload["impact_mode"] = analysis.ImpactMode;
                                zeroPayload["heuristic"] = analysis.Heuristic;
                                zeroPayload["file_impacts"] = new JsonArray();
                                zeroPayload["definition_count"] = analysis.DefinitionCount;
                                zeroPayload["definition_file_count"] = analysis.DefinitionFileCount;
                                zeroPayload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
                                zeroPayload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
                                zeroPayload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
                                zeroPayload["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult);
                                if (analysis.ZeroResultReason != null)
                                    zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                                AddImpactFailureJsonFields(zeroPayload, analysis, jsonOptions);
                                if (analysis.Suggestion != null)
                                    zeroPayload["suggestion"] = analysis.Suggestion;
                                AddSqlGraphContractJsonFields(zeroPayload, sqlGraphSignal);
                                AddImpactOptionWarnings(zeroPayload, options);
                            });
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.Error.WriteLine("Depth 0 requested: resolved the symbol only; callers were not traversed.");
                        WriteImpactResolutionHint(analysis);
                        WriteGraphSupportHint(options.Lang);
                    }
                    return StrictImpactExitCode(options, analysis, CommandExitCodes.Success);
                }

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new JsonObject
                        {
                            ["query"] = options.Query,
                            ["resolved_name"] = analysis.ResolvedName,
                            ["count"] = 0,
                            ["file_count"] = 0,
                            ["confirmed_count"] = 0,
                            ["confirmed_file_count"] = 0,
                            ["impact_mode"] = analysis.ImpactMode,
                            ["heuristic"] = analysis.Heuristic,
                            ["hint_count"] = analysis.HintCount,
                            ["hint_file_count"] = 0,
                            ["definition_count"] = analysis.DefinitionCount,
                            ["definition_file_count"] = analysis.DefinitionFileCount,
                            ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                            ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                            ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                            ["graph_table_available"] = analysis.GraphTableAvailable,
                            ["degraded"] = !analysis.GraphTableAvailable,
                        };
                        AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                        if (analysis.ZeroResultReason != null)
                            payload["zero_result_reason"] = analysis.ZeroResultReason;
                        AddImpactFailureJsonFields(payload, analysis, jsonOptions);
                        if (analysis.Suggestion != null)
                            payload["suggestion"] = analysis.Suggestion;
                        if (!analysis.GraphTableAvailable)
                            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        AddFreshnessHint(payload, reader);
                        AddImpactOptionWarnings(payload, options);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        if (!analysis.GraphTableAvailable)
                            Console.Error.WriteLine("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
                    }
                }
                else if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: "callers",
                        graphTableAvailable: analysis.GraphTableAvailable,
                        degraded: !analysis.GraphTableAvailable,
                        extraFields: zeroPayload =>
                        {
                            zeroPayload["query"] = options.Query;
                            zeroPayload["resolved_name"] = analysis.ResolvedName;
                            zeroPayload["file_count"] = 0;
                            zeroPayload["confirmed_count"] = 0;
                            zeroPayload["confirmed_file_count"] = 0;
                            zeroPayload["hint_count"] = 0;
                            zeroPayload["hint_file_count"] = 0;
                            zeroPayload["max_hops"] = maxDepth;
                            zeroPayload["max_depth"] = maxDepth;
                            zeroPayload["actual_depth"] = 0;
                            zeroPayload["truncated"] = analysis.Truncated;
                            if (analysis.TruncatedReason != null)
                                zeroPayload["truncated_reason"] = analysis.TruncatedReason;
                            AddImpactTerminationJsonFields(zeroPayload, analysis, jsonOptions);
                            zeroPayload["impact_mode"] = analysis.ImpactMode;
                            zeroPayload["heuristic"] = analysis.Heuristic;
                            zeroPayload["file_impacts"] = new JsonArray();
                            zeroPayload["definition_count"] = analysis.DefinitionCount;
                            zeroPayload["definition_file_count"] = analysis.DefinitionFileCount;
                            zeroPayload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
                            zeroPayload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
                            zeroPayload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
                            zeroPayload["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult);
                            if (analysis.ZeroResultReason != null)
                                zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                            AddImpactFailureJsonFields(zeroPayload, analysis, jsonOptions);
                            if (analysis.Suggestion != null)
                                zeroPayload["suggestion"] = analysis.Suggestion;
                            AddSqlGraphContractJsonFields(zeroPayload, sqlGraphSignal);
                            AddImpactOptionWarnings(zeroPayload, options);
                        });
                    if (!analysis.GraphTableAvailable)
                        payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else if (!options.Json)
                {
                    Console.Error.WriteLine($"No impact found for '{analysis.Query}'.");
                    WriteImpactResolutionHint(analysis);
                    WriteGraphSupportHint(options.Lang);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return StrictImpactExitCode(options, analysis, ZeroResultExitCode(options));
            }

            if (options.CountOnly)
            {
                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["query"] = options.Query,
                        ["resolved_name"] = analysis.ResolvedName,
                        ["count"] = visibleCount,
                        ["file_count"] = visibleFileCount,
                        ["confirmed_count"] = confirmedCount,
                        ["confirmed_file_count"] = confirmedFileCount,
                        ["impact_mode"] = analysis.ImpactMode,
                        ["heuristic"] = analysis.Heuristic,
                        ["hint_count"] = hintCount,
                        ["hint_file_count"] = hintFileCount,
                        ["truncated"] = analysis.Truncated,
                    };
                    AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                    if (analysis.TruncatedReason != null)
                        payload["truncated_reason"] = analysis.TruncatedReason;
                    AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    AddImpactOptionWarnings(payload, options);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{visibleCount}");
                }
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["query"] = options.Query,
                    ["resolved_name"] = analysis.ResolvedName,
                    ["count"] = visibleCount,
                    ["file_count"] = visibleFileCount,
                    ["confirmed_count"] = confirmedCount,
                    ["confirmed_file_count"] = confirmedFileCount,
                    ["hint_count"] = hintCount,
                    ["hint_file_count"] = hintFileCount,
                    ["max_hops"] = maxDepth,
                    ["max_depth"] = maxDepth,
                    ["actual_depth"] = analysis.Callers.Count > 0 ? analysis.Callers.Max(r => r.Depth) : 0,
                    ["truncated"] = analysis.Truncated,
                    ["impact_mode"] = analysis.ImpactMode,
                    ["heuristic"] = analysis.Heuristic,
                    ["callers"] = JsonSerializer.SerializeToNode(analysis.Callers, CliJsonSerializerContextFactory.Create(jsonOptions).ListImpactResult),
                    ["file_impacts"] = JsonSerializer.SerializeToNode(analysis.FileImpacts, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileDependencyResult),
                    ["definition_count"] = analysis.DefinitionCount,
                    ["definition_file_count"] = analysis.DefinitionFileCount,
                    ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                    ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                    ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                    ["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult),
                };
                AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                if (analysis.TruncatedReason != null)
                    payload["truncated_reason"] = analysis.TruncatedReason;
                if (analysis.Suggestion != null)
                    payload["suggestion"] = analysis.Suggestion;
                AddImpactFailureJsonFields(payload, analysis, jsonOptions);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                AddImpactOptionWarnings(payload, options);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                if (hasHeuristicHints)
                {
                    Console.Error.WriteLine($"No symbol-level callers found for '{analysis.ResolvedName}'. Possible file-level dependents follow.");
                    WriteImpactResolutionHint(analysis);
                    Console.Error.WriteLine("WARN: these file-level dependents are heuristic only; the current graph does not record resolved target file/type for each call.");
                    if (analysis.Truncated)
                        Console.Error.WriteLine("WARN: heuristic file-level dependents were truncated by the current limit.");
                    foreach (var edge in analysis.FileImpacts)
                        Console.WriteLine($"  {edge.SourcePath,-40} -> {edge.TargetPath} ({edge.ReferenceCount} refs: {edge.Symbols})");
                }
                else
                {
                    var grouped = analysis.Callers.GroupBy(r => r.Depth).OrderBy(g => g.Key);
                    foreach (var group in grouped)
                    {
                        Console.Error.WriteLine($"--- Depth {group.Key} ---");
                        foreach (var r in group)
                        {
                            var indent = new string(' ', (r.Depth - 1) * 2);
                            Console.WriteLine($"  {indent}{r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                            WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent, $"  {indent}");
                            if (options.WithPaths && r.Paths != null)
                            {
                                foreach (var p in r.Paths)
                                    Console.WriteLine($"  {indent}  via: {string.Join(" -> ", p)}");
                                if (r.PathsTruncated)
                                    Console.WriteLine($"  {indent}  via: ... (more paths exist, truncated by per-row cap)");
                            }
                        }
                    }
                }

                var truncNote = analysis.Truncated
                    ? analysis.TruncatedReason != null
                        ? $" [TRUNCATED: {analysis.TruncatedReason}]"
                        : " [TRUNCATED]"
                    : "";
                if (hasHeuristicHints)
                    Console.Error.WriteLine($"\n({hintCount} heuristic dependency hints across {hintFileCount} files{truncNote})");
                else
                    Console.Error.WriteLine($"\n({confirmedCount} callers across {confirmedFileCount} files, max depth {maxDepth}{truncNote})");
            }
            return StrictImpactExitCode(options, analysis, CommandExitCodes.Success);
        });
    }

    private static void AddImpactFailureJsonFields(JsonObject payload, ImpactAnalysisResult analysis, JsonSerializerOptions jsonOptions)
    {
        if (analysis.ImpactFailureChain is { Count: > 0 })
            payload["impact_failure_chain"] = JsonSerializer.SerializeToNode(analysis.ImpactFailureChain, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (analysis.SuggestionType != null)
            payload["suggestion_type"] = analysis.SuggestionType;
    }

    private static int StrictImpactExitCode(QueryCommandOptions options, ImpactAnalysisResult analysis, int defaultExitCode)
    {
        if (!options.Strict || analysis.ImpactFailureChain is not { Count: > 0 })
            return defaultExitCode;
        return analysis.ImpactFailureChain.Any(code => code != "no_callers")
            ? CommandExitCodes.FeatureUnavailable
            : defaultExitCode;
    }

    private static void AddImpactTerminationJsonFields(JsonObject payload, ImpactAnalysisResult analysis, JsonSerializerOptions jsonOptions)
    {
        payload["termination_reason"] = analysis.TerminationReason;
        payload["cycle_detected"] = analysis.CycleDetected;
        if (analysis.Cycles is { Count: > 0 })
            payload["cycles"] = JsonSerializer.SerializeToNode(analysis.Cycles, CliJsonSerializerContextFactory.Create(jsonOptions).ListImpactCycleResult);
    }

    public static int RunDeps(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("deps", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        if (!TryExtractDepsFormat(cmdArgs, out var depsFormat, out var parseArgs, out var depsFormatError))
        {
            Console.Error.WriteLine(depsFormatError);
            return CommandExitCodes.UsageError;
        }

        var options = ParseArgs(
            parseArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("deps", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("deps")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "deps"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("deps", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var reverse = cmdArgs.Any(a => a == "--reverse");
            var results = GetWorkspaceFileDependencies(reader, options, reverse);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var sqlGraphSignal = results.Count == 0
                ? baseSqlGraphSignal
                : NarrowSqlGraphContractSignalByPaths(
                    reader,
                    baseSqlGraphSignal,
                    results.SelectMany(result => new[] { result.SourcePath, result.TargetPath }),
                    options.Lang);
            if (results.Count == 0)
            {
                if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult(reader, "edges", json: true, graphAvailable: false, jsonOptions, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "edges", graphTableAvailable: true, degraded: !sqlGraphSignal.Ready, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal)).ToJsonString(jsonOptions));
                else
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No file dependencies found", options));
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "edges", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            List<List<string>> cycles = [];
            var outputEdges = options.DependencyCycles ? FilterCycleEdges(results, out cycles) : results;
            if (options.DependencyCycles && cycles.Count == 0)
            {
                if (options.Json)
                    Console.WriteLine(new JsonObject { ["count"] = 0, ["cycles"] = new JsonArray() }.ToJsonString(jsonOptions));
                else
                    Console.Error.WriteLine(BuildZeroResultLine("No dependency cycles found", options));
                return ZeroResultExitCode(options);
            }

            if (depsFormat is OutputFormatDot or OutputFormatGraphMl or OutputFormatJsonGraph)
            {
                WriteDependencyGraph(outputEdges, depsFormat, jsonOptions);
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["count"] = options.DependencyCycles ? cycles.Count : results.Count,
                };
                if (options.DependencyCycles)
                    payload["cycles"] = BuildDependencyCyclesJson(cycles);
                else
                    payload["edges"] = JsonSerializer.SerializeToNode(results, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileDependencyResult);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                if (options.DependencyCycles)
                {
                    foreach (var cycle in cycles)
                        Console.WriteLine(string.Join(" -> ", cycle.Concat([cycle[0]])));
                    Console.Error.WriteLine($"({cycles.Count} dependency cycles)");
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    return CommandExitCodes.Success;
                }

                foreach (var r in results)
                {
                    var syms = r.Symbols.Length > 60 ? r.Symbols[..57] + "..." : r.Symbols;
                    Console.WriteLine($"{r.SourcePath,-45} -> {r.TargetPath,-45} ({r.ReferenceCount} refs: {syms})");
                }
                Console.Error.WriteLine($"({results.Count} dependency edges)");
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    private static bool TryExtractDepsFormat(string[] args, out string format, out string[] parseArgs, out string? error)
    {
        format = OutputFormatEdgeList;
        error = null;
        var rewritten = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                var rawFormat = arg["--format=".Length..];
                if (!TryNormalizeDepsFormat(rawFormat, out format, out error))
                {
                    parseArgs = args;
                    return false;
                }
                rewritten.Add(format == OutputFormatJsonGraph ? "--format=json" : "--format=text");
                continue;
            }

            if (arg == "--format" && i + 1 < args.Length)
            {
                var rawFormat = args[++i];
                if (!TryNormalizeDepsFormat(rawFormat, out format, out error))
                {
                    parseArgs = args;
                    return false;
                }
                rewritten.Add("--format");
                rewritten.Add(format == OutputFormatJsonGraph ? "json" : "text");
                continue;
            }

            rewritten.Add(arg);
        }

        parseArgs = rewritten.ToArray();
        return true;
    }

    private static bool TryNormalizeDepsFormat(string rawFormat, out string format, out string? error)
    {
        format = rawFormat.ToLowerInvariant();
        error = null;
        switch (format)
        {
            case OutputFormatText:
            case OutputFormatJson:
            case OutputFormatEdgeList:
                format = OutputFormatEdgeList;
                return true;
            case OutputFormatDot:
            case OutputFormatGraphMl:
            case OutputFormatJsonGraph:
                return true;
            default:
                error = $"Error: deps --format must be one of edgelist, dot, graphml, or json-graph; got '{rawFormat}'.";
                return false;
        }
    }

    internal static List<FileDependencyResult> FilterCycleEdges(List<FileDependencyResult> results, out List<List<string>> cycles)
    {
        cycles = FindDependencyCycles(results);
        if (cycles.Count == 0)
            return [];
        var cycleNodes = cycles.SelectMany(cycle => cycle).ToHashSet(StringComparer.Ordinal);
        return results
            .Where(edge => cycleNodes.Contains(edge.SourcePath) && cycleNodes.Contains(edge.TargetPath))
            .ToList();
    }

    internal static List<List<string>> FindDependencyCycles(IReadOnlyList<FileDependencyResult> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.SourcePath, out var targets))
                adjacency[edge.SourcePath] = targets = [];
            targets.Add(edge.TargetPath);
            adjacency.TryAdd(edge.TargetPath, []);
        }

        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new List<List<string>>();

        void Visit(string node)
        {
            indexes[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var target in adjacency[node])
            {
                if (!indexes.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
                }
            }

            if (lowLinks[node] != indexes[node])
                return;

            var component = new List<string>();
            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component.Add(popped);
            } while (!string.Equals(popped, node, StringComparison.Ordinal));

            var selfCycle = component.Count == 1 && adjacency[component[0]].Contains(component[0], StringComparer.Ordinal);
            if (component.Count > 1 || selfCycle)
                cycles.Add(component.OrderBy(path => path, StringComparer.Ordinal).ToList());
        }

        foreach (var node in adjacency.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList())
            if (!indexes.ContainsKey(node))
                Visit(node);

        return cycles;
    }

    internal static JsonArray BuildDependencyCyclesJson(IReadOnlyList<List<string>> cycles)
    {
        var array = new JsonArray();
        foreach (var cycle in cycles)
        {
            array.Add(new JsonObject
            {
                ["length"] = cycle.Count,
                ["nodes"] = new JsonArray(cycle.Select(node => JsonValue.Create(node)).ToArray<JsonNode?>())
            });
        }
        return array;
    }

    private static void WriteDependencyGraph(IReadOnlyList<FileDependencyResult> edges, string format, JsonSerializerOptions jsonOptions)
    {
        switch (format)
        {
            case OutputFormatDot:
                Console.WriteLine("digraph deps {");
                foreach (var edge in edges)
                    Console.WriteLine($"  \"{EscapeDot(edge.SourcePath)}\" -> \"{EscapeDot(edge.TargetPath)}\" [label=\"{edge.ReferenceCount}\"];");
                Console.WriteLine("}");
                break;
            case OutputFormatGraphMl:
                Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                Console.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\"><graph edgedefault=\"directed\">");
                foreach (var node in edges.SelectMany(edge => new[] { edge.SourcePath, edge.TargetPath }).Distinct(StringComparer.Ordinal))
                    Console.WriteLine($"<node id=\"{System.Security.SecurityElement.Escape(node)}\" />");
                foreach (var edge in edges)
                    Console.WriteLine($"<edge source=\"{System.Security.SecurityElement.Escape(edge.SourcePath)}\" target=\"{System.Security.SecurityElement.Escape(edge.TargetPath)}\"><data key=\"references\">{edge.ReferenceCount}</data></edge>");
                Console.WriteLine("</graph></graphml>");
                break;
            case OutputFormatJsonGraph:
                var nodes = edges.SelectMany(edge => new[] { edge.SourcePath, edge.TargetPath }).Distinct(StringComparer.Ordinal).Select(path => new JsonObject { ["id"] = path }).ToArray<JsonNode?>();
                var graphEdges = edges.Select(edge => new JsonObject { ["source"] = edge.SourcePath, ["target"] = edge.TargetPath, ["reference_count"] = edge.ReferenceCount }).ToArray<JsonNode?>();
                Console.WriteLine(new JsonObject { ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(graphEdges) }.ToJsonString(jsonOptions));
                break;
        }
    }

    private static string EscapeDot(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static List<FileDependencyResult> GetWorkspaceFileDependencies(DbReader primaryReader, QueryCommandOptions options, bool reverse)
    {
        var results = primaryReader.GetFileDependencies(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse);
        if (options.WorkspaceDbPaths.Count == 0)
            return results;

        var primaryDb = Path.GetFullPath(DbPathResolver.NormalizeDbPath(options.DbPath));
        var memberDbs = options.WorkspaceDbPaths
            .Select(path => Path.GetFullPath(DbPathResolver.NormalizeDbPath(path)))
            .Prepend(primaryDb)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        TagFileDependencyResults(results, primaryDb);
        foreach (var normalizedDbPath in memberDbs.Skip(1))
        {
            using var db = new DbContext(normalizedDbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db) { IncludeGenerated = primaryReader.IncludeGenerated };
            var memberResults = reader.GetFileDependencies(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse);
            TagFileDependencyResults(memberResults, normalizedDbPath);
            results.AddRange(memberResults);
        }

        foreach (var sourceDb in memberDbs)
            foreach (var targetDb in memberDbs)
            {
                if (string.Equals(sourceDb, targetDb, StringComparison.Ordinal))
                    continue;
                results.AddRange(GetCrossDatabaseFileDependencies(sourceDb, targetDb, options, reverse));
            }

        return results
            .OrderByDescending(result => result.ReferenceCount)
            .ThenBy(result => result.SourceDb, StringComparer.Ordinal)
            .ThenBy(result => result.SourcePath, StringComparer.Ordinal)
            .ThenBy(result => result.TargetDb, StringComparer.Ordinal)
            .ThenBy(result => result.TargetPath, StringComparer.Ordinal)
            .Take(options.Limit)
            .ToList();
    }

    private static List<FileDependencyResult> GetCrossDatabaseFileDependencies(string sourceDbPath, string targetDbPath, QueryCommandOptions options, bool reverse)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE @targetDb AS targetdb";
        attach.Parameters.AddWithValue("@targetDb", targetDbPath);
        attach.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        var sourcePathExpr = reverse ? "dst.path" : "src.path";
        var targetPathExpr = reverse ? "src.path" : "dst.path";
        cmd.CommandText = $@"
            SELECT {sourcePathExpr} AS source_path,
                   {targetPathExpr} AS target_path,
                   COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.symbol_name) AS symbols
            FROM symbol_references r
            JOIN files src ON src.id = r.file_id
            JOIN targetdb.symbols s ON s.name = r.symbol_name
            JOIN targetdb.files dst ON dst.id = s.file_id
            WHERE 1 = 1";
        if (options.Lang != null)
        {
            cmd.CommandText += " AND src.lang = @lang AND dst.lang = @lang";
            cmd.Parameters.AddWithValue("@lang", options.Lang);
        }
        AddCrossDatabasePathFilters(cmd, "src", options.PathPatterns, include: !reverse);
        AddCrossDatabasePathFilters(cmd, "dst", options.PathPatterns, include: reverse);
        AddCrossDatabaseExcludeFilters(cmd, "src", options.ExcludePaths, include: !reverse);
        AddCrossDatabaseExcludeFilters(cmd, "dst", options.ExcludePaths, include: reverse);
        if (options.ExcludeTests)
            cmd.CommandText += reverse
                ? " AND dst.path NOT LIKE '%test%' COLLATE NOCASE"
                : " AND src.path NOT LIKE '%test%' COLLATE NOCASE";
        cmd.CommandText += @"
            GROUP BY source_path, target_path
            ORDER BY reference_count DESC, source_path, target_path
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", options.Limit);

        var results = new List<FileDependencyResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FileDependencyResult
            {
                SourcePath = reader.GetString(0),
                TargetPath = reader.GetString(1),
                SourceDb = reverse ? targetDbPath : sourceDbPath,
                TargetDb = reverse ? sourceDbPath : targetDbPath,
                ReferenceCount = reader.GetInt32(2),
                Symbols = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            });
        }
        return results;
    }

    private static void AddCrossDatabasePathFilters(SqliteCommand cmd, string alias, IReadOnlyList<string> patterns, bool include)
    {
        if (!include || patterns.Count == 0)
            return;
        var parts = new List<string>(patterns.Count);
        for (var i = 0; i < patterns.Count; i++)
        {
            var name = $"@crossPath{alias}{i}";
            parts.Add($"{alias}.path LIKE {name} ESCAPE '\\'");
            cmd.Parameters.AddWithValue(name, CrossDatabaseGlobToLikePattern(patterns[i]));
        }
        cmd.CommandText += " AND (" + string.Join(" OR ", parts) + ")";
    }

    private static void AddCrossDatabaseExcludeFilters(SqliteCommand cmd, string alias, IReadOnlyList<string> patterns, bool include)
    {
        if (!include || patterns.Count == 0)
            return;
        for (var i = 0; i < patterns.Count; i++)
        {
            var name = $"@crossExclude{alias}{i}";
            cmd.CommandText += $" AND {alias}.path NOT LIKE {name} ESCAPE '\\'";
            cmd.Parameters.AddWithValue(name, CrossDatabaseGlobToLikePattern(patterns[i]));
        }
    }

    private static string CrossDatabaseGlobToLikePattern(string pattern)
    {
        var builder = new System.Text.StringBuilder(pattern.Length);
        foreach (var ch in pattern)
        {
            builder.Append(ch switch
            {
                '*' => '%',
                '?' => '_',
                '%' => "\\%",
                '_' => "\\_",
                '\\' => "\\\\",
                _ => ch,
            });
        }
        return builder.ToString();
    }

    private static void TagFileDependencyResults(IEnumerable<FileDependencyResult> results, string dbPath)
    {
        foreach (var result in results)
        {
            result.SourceDb = dbPath;
            result.TargetDb = dbPath;
        }
    }

    public static int RunHotspots(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        bool groupByName = cmdArgs.Any(a => a == "--group-by-name");
        var previewOptionError = ValidatePreviewOptions("hotspots", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("hotspots", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("hotspots")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "hotspots"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "hotspots", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("hotspots", options))
            return CommandExitCodes.UsageError;
        if (!TryResolveHotspotsGroupBy(options.GroupBy, options.Lang, groupByName, out var groupBy, out var groupByError))
        {
            Console.Error.WriteLine(groupByError);
            Console.Error.WriteLine("Usage: cdidx hotspots [--db <path>] [--json] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
            if (groupBy == HotspotsGroupedByNameKind)
            {
                var groupedResults = reader.GetGroupedSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var effectiveSqlGraphSignal = groupedResults.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, groupedResults.Select(result => result.Symbol.Lang), options.Lang);
                if (groupedResults.Count == 0)
                {
                    if (options.CountOnly)
                    {
                        if (options.Json)
                        {
                            var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: true, graphAvailable: reader._hasReferencesTable, queryOptions: options);
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            Console.WriteLine(payload.ToJsonString(jsonOptions));
                        }
                        else
                            WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, new ExactQuerySignal(true, HasMissingIndex: false, HasMissingTable: false, null));
                    }
                    else if (options.Json)
                    {
                        var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: false, graphAvailable: reader._hasReferencesTable, queryOptions: options);
                        AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.Error.WriteLine(BuildZeroResultLine("No symbol hotspots found", options));
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return ZeroResultExitCode(options);
                }

                var definitionSiteTotal = groupedResults.Sum(g => g.DefinitionSites);
                var groupedFileCount = groupedResults
                    .SelectMany(g => g.Paths)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new JsonObject
                        {
                            ["count"] = groupedResults.Count,
                            ["files"] = groupedFileCount,
                            ["definition_site_total"] = definitionSiteTotal,
                            ["grouped_by"] = HotspotsGroupedByNameKind,
                        };
                        AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine($"{groupedResults.Count}");
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var items = groupedResults
                        .Select(g => new GroupedSymbolHotspotJsonResult(
                            g.Symbol.Name,
                            g.Symbol.Kind,
                            g.Symbol.Path,
                            g.Symbol.Line,
                            g.ReferenceCount,
                            g.ReferenceScore,
                            g.Symbol.Visibility,
                            g.Symbol.ContainerName,
                            g.DefinitionSites,
                            g.Paths))
                        .ToList();
                    var payload = new JsonObject
                    {
                        ["count"] = groupedResults.Count,
                        ["definition_site_total"] = definitionSiteTotal,
                        ["grouped_by"] = HotspotsGroupedByNameKind,
                        ["hotspots"] = JsonSerializer.SerializeToNode(items, CliJsonSerializerContextFactory.Create(jsonOptions).ListGroupedSymbolHotspotJsonResult)
                    };
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    foreach (var g in groupedResults)
                    {
                        var s = g.Symbol;
                        var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                        var multi = g.DefinitionSites > 1 ? $" (×{g.DefinitionSites} sites)" : "";
                        Console.WriteLine($"{FormatHotspotScore(g.ReferenceScore),5} score {g.ReferenceCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{multi}");
                    }
                    Console.Error.WriteLine($"({groupedResults.Count} unique name/kind groups, {definitionSiteTotal} definition sites)");
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            if (groupBy == HotspotsGroupedByFile)
            {
                var symbolRows = reader.GetSymbolHotspots(int.MaxValue, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var fileResults = symbolRows
                    .GroupBy(row => row.Symbol.Path, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var first = group.First();
                        return new
                        {
                            Path = first.Symbol.Path,
                            Lang = first.Symbol.Lang,
                            ReferenceCount = group.Sum(row => row.ReferenceCount),
                            SymbolCount = group.Count(),
                        };
                    })
                    .OrderByDescending(row => row.ReferenceCount)
                    .ThenBy(row => row.Path, StringComparer.Ordinal)
                    .Take(options.Limit)
                    .ToList();
                var effectiveSqlGraphSignal = fileResults.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, fileResults.Select(result => result.Lang), options.Lang);
                var fileHotspotSignal = reader.GetHotspotFamilySignal(options.Lang);

                if (fileResults.Count == 0)
                {
                    if (options.CountOnly)
                    {
                        if (options.Json)
                        {
                            var payload = new JsonObject
                            {
                                ["count"] = 0,
                                ["files"] = 0,
                                ["graph_table_available"] = reader._hasReferencesTable,
                                ["grouped_by"] = groupBy,
                            };
                            AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            AddFreshnessHint(payload, reader);
                            Console.WriteLine(payload.ToJsonString(jsonOptions));
                        }
                        else
                        {
                            Console.WriteLine("0");
                            WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                            WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        }
                    }
                    else if (options.Json)
                    {
                        Console.WriteLine(BuildJsonZeroResultPayload(
                            reader,
                            jsonOptions,
                            resultsKey: "hotspots",
                            graphTableAvailable: reader._hasReferencesTable,
                            degraded: !reader._hasReferencesTable || !fileHotspotSignal.Ready,
                            extraFields: payload =>
                            {
                                payload["grouped_by"] = groupBy;
                                AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                                AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            }).ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.Error.WriteLine("No symbol hotspots found.");
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return ZeroResultExitCode(options);
                }

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new JsonObject
                        {
                            ["count"] = fileResults.Count,
                            ["files"] = fileResults.Count,
                            ["graph_table_available"] = reader._hasReferencesTable,
                            ["grouped_by"] = groupBy,
                        };
                        AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                        AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine($"{fileResults.Count}");
                        WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var hotspots = new JsonArray();
                    foreach (var result in fileResults)
                    {
                        hotspots.Add(new JsonObject
                        {
                            ["path"] = result.Path,
                            ["lang"] = result.Lang,
                            ["reference_count"] = result.ReferenceCount,
                            ["symbol_count"] = result.SymbolCount,
                        });
                    }
                    var payload = new JsonObject
                    {
                        ["count"] = fileResults.Count,
                        ["files"] = fileResults.Count,
                        ["grouped_by"] = groupBy,
                        ["hotspots"] = hotspots,
                    };
                    AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    foreach (var result in fileResults)
                    {
                        Console.WriteLine($"{result.ReferenceCount,5} refs  {result.SymbolCount,5} symbols  {result.Path}");
                    }
                    Console.Error.WriteLine($"({fileResults.Count} file hotspots; grouped_by={groupBy})");
                    WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            var results = reader.GetSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Symbol.Lang), options.Lang);
            var hotspotSignal = reader.GetHotspotFamilySignal(options.Lang);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                {
                    if (!options.Json)
                    {
                        Console.WriteLine("0");
                        if (!reader._hasReferencesTable)
                            Console.Error.WriteLine("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
                        WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    }
                    else
                    {
                        var payload = new JsonObject
                        {
                            ["count"] = 0,
                            ["files"] = 0,
                            ["graph_table_available"] = reader._hasReferencesTable,
                            ["grouped_by"] = groupBy,
                        };
                        if (!reader._hasReferencesTable)
                            payload["degraded"] = true;
                        AddHotspotFamilyJsonFields(payload, hotspotSignal);
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        AddFreshnessHint(payload, reader);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                }
                else if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: true, graphAvailable: false, jsonOptions, queryOptions: options, extraFields: payload =>
                    {
                        payload["grouped_by"] = groupBy;
                        AddHotspotFamilyJsonFields(payload, hotspotSignal);
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    });
                else if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: "hotspots",
                        graphTableAvailable: true,
                        degraded: !hotspotSignal.Ready,
                        extraFields: payload =>
                        {
                            payload["grouped_by"] = groupBy;
                            AddHotspotFamilyJsonFields(payload, hotspotSignal);
                            AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        }).ToJsonString(jsonOptions));
                else if (!options.Json)
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No symbol hotspots found", options));
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Symbol.Path).Distinct().Count();
                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = results.Count,
                        ["files"] = fc,
                        ["graph_table_available"] = reader._hasReferencesTable,
                        ["grouped_by"] = groupBy,
                    };
                    AddHotspotFamilyJsonFields(payload, hotspotSignal);
                    AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{results.Count}");
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var items = results
                    .Select(r => new SymbolHotspotJsonResult(
                        r.Symbol.Name,
                        r.Symbol.Kind,
                        r.Symbol.Path,
                        r.Symbol.Line,
                        r.ReferenceCount,
                        r.ReferenceScore,
                        r.Symbol.Visibility,
                        r.Symbol.ContainerName))
                    .ToList();
                var payload = new JsonObject
                {
                    ["count"] = results.Count,
                    ["grouped_by"] = groupBy,
                    ["hotspots"] = JsonSerializer.SerializeToNode(items, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolHotspotJsonResult)
                };
                AddHotspotFamilyJsonFields(payload, hotspotSignal);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                foreach (var r in results)
                {
                    var s = r.Symbol;
                    var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                    Console.WriteLine($"{FormatHotspotScore(r.ReferenceScore),5} score {r.ReferenceCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}");
                }
                Console.Error.WriteLine($"({results.Count} symbol hotspots; grouped_by={groupBy})");
                WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunUnused(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var byBucket = cmdArgs.Any(arg => arg == "--by-bucket");
        var previewOptionError = ValidatePreviewOptions("unused", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("unused", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("unused")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "unused"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "unused", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("unused", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            // Warn if user specified an unsupported language / 未対応言語の場合は警告
            if (options.Lang != null && !ReferenceExtractor.SupportsLanguage(options.Lang) && !options.Json)
                Console.Error.WriteLine($"Warning: '{options.Lang}' does not support reference extraction. Unused results are unavailable for this language.");

            bool? graphSupported = options.Lang != null ? ReferenceExtractor.SupportsLanguage(options.Lang) : null;
            var graphSupportReason = ReferenceExtractor.BuildGraphSupportReason(options.Lang, graphSupported);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
            if (options.CountOnly)
            {
                var countSummary = reader.CountUnusedSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var effectiveSqlGraphSignal = countSummary.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignal(
                        baseSqlGraphSignal,
                        countSummary.IncludesSql || DbReader.IsSqlLanguage(options.Lang));
                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = countSummary.Count,
                        ["files"] = countSummary.FileCount,
                        ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(new Dictionary<string, int>(), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
                        ["summary"] = BuildUnusedSummaryJson(Array.Empty<UnusedSymbolResult>(), jsonOptions),
                        ["bucket_taxonomy"] = BuildUnusedBucketTaxonomyJson(),
                        ["graph_supported"] = graphSupported,
                        ["graph_support_reason"] = graphSupportReason,
                        ["graph_table_available"] = reader._hasReferencesTable,
                        ["degraded"] = !reader._hasReferencesTable
                    };
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{countSummary.Count}");
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "unused", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.Success;
            }

            var results = reader.GetUnusedSymbols(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : NarrowSqlGraphContractSignalByLanguages(
                    baseSqlGraphSignal,
                    results.Select(result => result.Lang),
                    options.Lang);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    Console.WriteLine(BuildUnusedJsonPayload(
                        Array.Empty<UnusedSymbolResult>(),
                        graphSupported,
                        graphSupportReason,
                        sqlGraphSignal,
                        reader._hasReferencesTable,
                        jsonOptions,
                        options));
                }
                else
                {
                    Console.Error.WriteLine(BuildZeroResultLine("No unused symbols found", options));
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "symbols", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                Console.WriteLine(BuildUnusedJsonPayload(results, graphSupported, graphSupportReason, sqlGraphSignal, reader._hasReferencesTable, jsonOptions, byBucket: byBucket));
            }
            else
            {
                var bucketCounts = BuildUnusedBucketCounts(results);
                foreach (var bucket in OrderedUnusedBuckets)
                {
                    var bucketResults = results.Where(s => s.UnusedBucket == bucket).ToList();
                    if (bucketResults.Count == 0)
                        continue;

                    Console.WriteLine($"{GetUnusedBucketHeading(bucket)} ({bucketResults.Count})");
                    foreach (var s in bucketResults)
                    {
                        var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                        var container = s.ContainerName != null ? $" in {s.ContainerName}" : "";
                        Console.WriteLine($"{ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{container}");
                        Console.WriteLine($"             confidence={s.UnusedConfidence} reason={s.UnusedReason}");
                    }
                    Console.WriteLine();
                }
                var summaryBuckets = OrderedUnusedBuckets
                    .Where(bucketCounts.ContainsKey)
                    .Select(bucket => $"{GetUnusedBucketHeading(bucket)}: {bucketCounts[bucket]}");
                Console.Error.WriteLine($"({results.Count} returned potentially unused symbols; returned buckets: {string.Join(", ", summaryBuckets)})");
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    internal static readonly string[] OrderedUnusedBuckets =
    [
        "likely_unused_private",
        "maybe_unused_nonpublic",
        "public_or_exported_no_refs",
        "reflection_or_config_suspect",
    ];

    internal static Dictionary<string, int> BuildUnusedBucketCounts(IEnumerable<UnusedSymbolResult> results)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var count))
                ordered[bucket] = count;
        }
        return ordered;
    }

    internal static Dictionary<string, int> BuildUnusedConfidenceCounts(IEnumerable<UnusedSymbolResult> results)
        => results
            .GroupBy(result => result.UnusedConfidence, StringComparer.Ordinal)
            .OrderBy(group => GetUnusedConfidenceOrder(group.Key))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static JsonObject BuildUnusedSummaryJson(IEnumerable<UnusedSymbolResult> results, JsonSerializerOptions jsonOptions)
    {
        var resultList = results as List<UnusedSymbolResult> ?? results.ToList();
        return new JsonObject
        {
            ["by_bucket"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
            ["by_confidence"] = JsonSerializer.SerializeToNode(BuildUnusedConfidenceCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
        };
    }

    internal static JsonObject BuildUnusedBucketTaxonomyJson()
    {
        var taxonomy = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
            taxonomy[bucket] = new JsonObject
            {
                ["confidence"] = GetUnusedBucketConfidence(bucket),
                ["description"] = GetUnusedBucketDescription(bucket),
            };
        return taxonomy;
    }

    private static int GetUnusedConfidenceOrder(string confidence) => confidence switch
    {
        "medium" => 0,
        "low" => 1,
        _ => 2,
    };

    private static string GetUnusedBucketConfidence(string bucket) => bucket switch
    {
        "likely_unused_private" => "medium",
        "maybe_unused_nonpublic" => "low",
        "public_or_exported_no_refs" => "low",
        "reflection_or_config_suspect" => "low",
        _ => "unknown",
    };

    private static string GetUnusedBucketDescription(string bucket) => bucket switch
    {
        "likely_unused_private" => "Private symbols with no indexed references; usually the highest-signal unused candidates.",
        "maybe_unused_nonpublic" => "Internal, protected, or otherwise non-public symbols with no indexed references; review call paths and framework entry points before removal.",
        "public_or_exported_no_refs" => "Public or exported symbols with no indexed references; may still be external API surface.",
        "reflection_or_config_suspect" => "Symbols with no indexed references that look reachable through reflection, attributes, config, or binding conventions.",
        _ => "Unknown unused-symbol bucket.",
    };

    private static string BuildUnusedJsonPayload(IEnumerable<UnusedSymbolResult> results, bool? graphSupported, string? graphSupportReason, SqlGraphContractSignal sqlGraphSignal, bool hasReferencesTable, JsonSerializerOptions jsonOptions, QueryCommandOptions? queryOptions = null, bool byBucket = false)
    {
        var resultList = results as List<UnusedSymbolResult> ?? results.ToList();
        var payload = new JsonObject
        {
            ["count"] = resultList.Count,
            ["graph_supported"] = graphSupported,
            ["graph_support_reason"] = graphSupportReason,
            ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), CliJsonSerializerContextFactory.Create(jsonOptions).DictionaryStringInt32),
            ["summary"] = BuildUnusedSummaryJson(resultList, jsonOptions),
            ["bucket_taxonomy"] = BuildUnusedBucketTaxonomyJson(),
            ["symbols"] = JsonSerializer.SerializeToNode(resultList, CliJsonSerializerContextFactory.Create(jsonOptions).ListUnusedSymbolResult)
        };
        if (byBucket)
            payload["by_bucket"] = BuildUnusedResultsByBucketJson(resultList, jsonOptions);

        if (!hasReferencesTable)
        {
            payload["graph_table_available"] = false;
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }

        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
        if (queryOptions != null)
            payload["query_context"] = BuildQueryContextJson(queryOptions, jsonOptions);
        return payload.ToJsonString(jsonOptions);
    }

    private static JsonObject BuildUnusedResultsByBucketJson(IEnumerable<UnusedSymbolResult> results, JsonSerializerOptions jsonOptions)
    {
        var grouped = results
            .GroupBy(result => result.UnusedBucket, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var byBucket = new JsonObject();
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (grouped.TryGetValue(bucket, out var bucketResults))
                byBucket[bucket] = JsonSerializer.SerializeToNode(bucketResults, CliJsonSerializerContextFactory.Create(jsonOptions).ListUnusedSymbolResult);
            else
                byBucket[bucket] = new JsonArray();
        }
        return byBucket;
    }

    private static string GetUnusedBucketHeading(string bucket) => bucket switch
    {
        "likely_unused_private" => "Likely unused private",
        "maybe_unused_nonpublic" => "Maybe unused non-public",
        "public_or_exported_no_refs" => "Public/exported with no refs",
        "reflection_or_config_suspect" => "Reflection/config suspects",
        _ => bucket,
    };

    // Issue kinds emitted by FileIndexer.ValidateFileContent for `validate --kind` filtering.
    // Keep in sync with `Kind = "..."` assignments in FileIndexer.cs so typos like
    // `--kind replacement_chra` produce a did-you-mean hint instead of silently filtering
    // to zero results (#1582).
    // FileIndexer.ValidateFileContent が出力する file_issues 行の Kind 一覧。
    // `--kind replacement_chra` のようなタイプミスを did-you-mean で救うため、
    // FileIndexer.cs 内の `Kind = "..."` 代入と同期させる (#1582)。
    private static readonly string[] AllValidValidateKinds =
        ["bom", "cr_only_line_endings", "file_too_large", "fts_token_too_long", "line_too_long", "mixed_line_endings", "mixed_line_endings_three_way", "non_utf8_likely", "null_byte", "replacement_char", "utf16_bom"];

    public static int RunValidate(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("validate", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("validate", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("validate")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "validate"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("validate", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var issues = reader.GetIssues(options.Kind, options.PathPatterns);
            var issuesAvailable = reader._hasIssuesTable;
            if (issues.Count == 0)
            {
                if (options.Json)
                {
                    if (TryWriteEmptyFormattedResult(options, jsonOptions))
                        return CommandExitCodes.Success;
                    Console.WriteLine(new JsonObject
                    {
                        ["count"] = 0,
                        ["issues"] = new JsonArray(),
                        ["issues_table_available"] = issuesAvailable,
                        ["degraded"] = !issuesAvailable,
                    }.ToJsonString(jsonOptions));
                }
                else if (!issuesAvailable)
                    Console.Error.WriteLine("WARN: file_issues table missing in this index (legacy or read-only DB) — validate output is degraded, not a real clean signal.");
                else
                {
                    Console.Error.WriteLine("No encoding issues found.");
                    WriteValidateKindHint(options.Kind);
                }
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    issues.Select(i => new FormattedLocation(i.Path, i.Line, null, $"{i.Kind}: {i.Message}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(issues.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(issues.Select(i => (i.Path, i.Line, 1, $"{i.Kind}: {i.Message}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(issues.Select(i => (i.Path, i.Line, 1, i.Message, i.Kind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                Console.WriteLine(new JsonObject
                {
                    ["count"] = issues.Count,
                    ["issues"] = JsonSerializer.SerializeToNode(issues, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileIssue),
                }.ToJsonString(jsonOptions));
            }
            else
            {
                foreach (var issue in issues)
                {
                    var location = issue.Line > 0 ? $":{issue.Line}" : "";
                    Console.WriteLine($"  {issue.Kind,-20} {issue.Path}{location}  {issue.Message}");
                }
                var kindCounts = issues.GroupBy(i => i.Kind).Select(g => $"{g.Key}: {g.Count()}");
                Console.Error.WriteLine($"\n({issues.Count} issues: {string.Join(", ", kindCounts)})");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunLanguages(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("languages", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("languages")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "languages"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("languages", options))
            return CommandExitCodes.UsageError;
        var json = options.Json;

        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();

        // Build a consolidated view: language -> (extensions, hasSymbols, hasGraph)
        // 統合ビュー: 言語 -> (拡張子, シンボル対応, グラフ対応)
        var allLangs = new Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool Graph)>(StringComparer.Ordinal);

        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                info = (new List<string>(), GetLanguageAliases(lang).ToList(), symbolLangs.Contains(lang), graphLangs.Contains(lang));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        // Sort by language name / 言語名でソート
        var sorted = allLangs.OrderBy(kv => kv.Key).ToList();

        if (json)
        {
            var entries = sorted.Select(kv => new LanguageEntryJsonResult(
                kv.Key,
                kv.Value.Extensions.OrderBy(e => e).ToList(),
                kv.Value.Aliases.OrderBy(a => a).ToList(),
                kv.Value.Symbols,
                kv.Value.Graph)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(new LanguagesJsonResult(entries), CliJsonSerializerContextFactory.Create(jsonOptions).LanguagesJsonResult));
        }
        else
        {
            // Fixed-width Extensions column for short lists; spill long lists onto a continuation
            // line so the Symbols / Graph columns are never swallowed by a wide extension string.
            // 拡張子が短い場合は固定幅テーブル、長い場合は継続行に退避させることで、
            // Symbols / Graph 列が拡張子文字列に埋もれないようにする。
            const int ExtensionColumnWidth = 36;
            const int AliasColumnWidth = 12;
            Console.WriteLine($"{"Language",-14} {"Extensions",-36} {"Aliases",-12} {"Symbols",-9} {"Graph",-7}");
            Console.WriteLine(new string('-', 79));
            foreach (var (lang, info) in sorted)
            {
                var exts = string.Join(" ", info.Extensions.OrderBy(e => e));
                var aliases = string.Join(" ", info.Aliases.OrderBy(a => a));
                var aliasCell = string.IsNullOrWhiteSpace(aliases) ? "-" : aliases;
                var sym = info.Symbols ? "yes" : "-";
                var graph = info.Graph ? "yes" : "-";
                if (exts.Length <= ExtensionColumnWidth && aliases.Length <= AliasColumnWidth)
                {
                    Console.WriteLine($"{lang,-14} {exts,-36} {aliasCell,-12} {sym,-9} {graph,-7}");
                }
                else
                {
                    Console.WriteLine($"{lang,-14} {"",-36} {"",-12} {sym,-9} {graph,-7}");
                    Console.WriteLine($"  Extensions: {exts}");
                    if (!string.IsNullOrWhiteSpace(aliases))
                        Console.WriteLine($"  Aliases: {aliases}");
                }
            }
            Console.Error.WriteLine($"\n({sorted.Count} languages)");
        }
        return CommandExitCodes.Success;
    }

    public static QueryCommandOptions ParseArgs(
        string[] args,
        bool jsonDefault,
        bool allowNamedQuery = false,
        bool allowStatusCheck = false,
        bool validateDefaultLimit = true,
        bool validateDefaultSnippetLines = true,
        bool validateDefaultMaxLineWidth = true)
    {
        string? dbPath = null;
        string? dataDir = null;
        bool? json = null;
        string jsonOutputFormat = JsonOutputFormatNdjson;
        int limit = ResolveDefaultPositiveInt(DefaultLimitEnvironmentVariable, DefaultQueryLimit, "--limit", out var defaultLimitError);
        string? lang = null;
        string? kind = null;
        string? query = null;
        bool rawFts = false;
        bool includeBody = false;
        bool countOnly = false;
        bool strictNotFound = false;
        int? startLine = null;
        int? endLine = null;
        int contextBefore = 0;
        int contextAfter = 0;
        int? focusLine = null;
        int? focusColumn = null;
        int focusLength = 1;
        int snippetLines = ResolveDefaultPositiveInt(DefaultSnippetLinesEnvironmentVariable, SearchSnippetFormatter.DefaultSnippetLines, "--snippet-lines", out var defaultSnippetLinesError);
        var snippetFocus = SearchSnippetFocusMode.Quality;
        int maxLineWidth = ResolveDefaultNonNegativeInt(DefaultMaxLineWidthEnvironmentVariable, LineWidthFormatter.DefaultMaxLineWidth, "--max-line-width", out var defaultMaxLineWidthError);
        bool contextAfterExplicit = false;
        var pathPatterns = new List<string>();
        var userPathPatterns = new List<string>();
        var workspaceDbPaths = new List<string>();
        var projectFilters = new List<string>();
        string? solutionFilter = null;
        var excludePaths = new List<string>();
        var visibilityFilters = new List<string>();
        var excludeVisibilityFilters = new List<string>();
        bool excludeTests = false;
        bool includeGenerated = false;
        DateTime? since = null;
        bool noDedup = false;
        bool noVisibilityRank = false;
        bool exact = false;
        bool regex = false;
        bool prefix = false;
        var guardFilters = new List<SearchGuardFilter>();
        var guardWindow = DbReader.DefaultSearchGuardWindow;
        List<string>? parseErrors = null;
        bool exactName = false;
        bool exactSubstring = false;
        bool dbPathExplicit = false;
        bool readOnly = false;
        bool checkWorkspace = false;
        TimeSpan? staleAfter = null;
        HashSet<string>? statusCheckScopes = null;
        bool withPaths = false;
        string? groupBy = null;
        bool rawBytes = false;
        bool rawKinds = false;
        bool verbose = false;
        bool profile = false;
        int? slowQueryMs = null;
        double minEntrypointConfidence = 0;
        string? statusExplainField = null;
        bool statusLogPath = false;
        string outputFormat = OutputFormatText;
        bool statusConfig = false;
        bool limitExplicit = false;
        bool snippetLinesExplicit = false;
        bool maxLineWidthExplicit = false;
        bool strict = false;
        var rankMode = ReferenceRankMode.Weighted;
        var extraNames = new List<string>();
        bool impactDeprecatedDepthUsed = false;
        List<string>? mapSections = null;
        bool dependencyCycles = false;

        void AddParseError(string error)
        {
            parseErrors ??= [];
            parseErrors.Add(error);
        }

        void AddSearchGuardFilter(string optionName, SearchGuardRole role, SearchGuardDirection direction, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddParseError(BuildMissingOptionValueError(optionName));
                return;
            }
            if (value.Length > QueryLimits.MaxQueryLength)
            {
                AddParseError($"Error: {optionName} query too long (max {QueryLimits.MaxQueryLength} characters).");
                return;
            }

            guardFilters.Add(new SearchGuardFilter(role, direction, value));
        }

        void AddStatusCheckScopes(string rawScopes)
        {
            if (string.IsNullOrWhiteSpace(rawScopes))
            {
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
                return;
            }
            if (!ValidateCsvBounds("--check", rawScopes, MaxStatusCheckScopesCsvLength, MaxStatusCheckScopesCsvEntries, AddParseError))
                return;

            statusCheckScopes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawScope in rawScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var scope = rawScope.ToLowerInvariant();
                switch (scope)
                {
                    case "workspace":
                    case "fold":
                    case "graph":
                    case "issues":
                    case "hotspot":
                    case "csharp":
                    case "sql":
                    case "newer":
                        statusCheckScopes.Add(scope);
                        break;
                    default:
                        AddParseError($"Error: unsupported --check scope '{rawScope}'. Use one or more of workspace, fold, graph, issues, hotspot, csharp, sql, newer.");
                        break;
                }
            }

            if (statusCheckScopes.Count == 0)
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
        }

        // Track non-repeatable value-taking options that have already been observed and warn on
        // subsequent occurrences. Previously `--db /A --db /B` silently used `/B`; this makes the
        // override explicit so users (and AI callers) can spot a copy/paste or scripted mistake.
        // 非 repeatable な value-taking オプションの初出を記録し、2 回目以降で警告する。以前は
        // `--db /A --db /B` が silent に `/B` を採用していたため、スクリプトやコピペのミスに
        // ユーザーや AI 呼び出し側が気付けるよう、上書きを明示化する。
        var seenSingleValueOptions = new HashSet<string>(StringComparer.Ordinal);
        void WarnIfDuplicateSingleValueOption(string canonicalName, string newValue)
        {
            if (seenSingleValueOptions.Add(canonicalName))
                return;
            Console.Error.WriteLine($"Warning: {canonicalName} specified more than once; the rightmost CLI value '{newValue}' takes precedence over earlier CLI values and any environment/config default.");
        }

        for (int i = 0; i < args.Length; i++)
        {
            var currentArg = args[i];
            if (allowStatusCheck && currentArg.StartsWith("--check=", StringComparison.Ordinal))
            {
                checkWorkspace = true;
                AddStatusCheckScopes(currentArg["--check=".Length..]);
                continue;
            }

            var inlineValue = TrySplitInlineOptionValue(currentArg, out var inlineOptionName)
                ? currentArg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? currentArg;

            switch (normalizedArg)
            {
                case "--":
                    if (i + 1 >= args.Length)
                    {
                        AddParseError("Error: -- requires a following literal query.");
                    }
                    else if (query == null)
                    {
                        query = args[++i];
                    }
                    else
                    {
                        extraNames.Add(args[++i]);
                    }
                    break;
                case "--db":
                    if (TryReadStringOptionValue(args, ref i, "--db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dbPathValue, out var dbPathError))
                    {
                        WarnIfDuplicateSingleValueOption("--db", dbPathValue!);
                        dbPath = dbPathValue!;
                        dbPathExplicit = true;
                    }
                    else
                        AddParseError(dbPathError!);
                    break;
                case "--read-only":
                case "--immutable":
                    readOnly = true;
                    break;
                case "--workspace-db":
                    if (TryReadStringOptionValue(args, ref i, "--workspace-db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var workspaceDbPath, out var workspaceDbError))
                        workspaceDbPaths.Add(workspaceDbPath!);
                    else
                        AddParseError(workspaceDbError!);
                    break;
                case "--data-dir":
                    if (TryReadStringOptionValue(args, ref i, "--data-dir", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dataDirValue, out var dataDirError))
                    {
                        WarnIfDuplicateSingleValueOption("--data-dir", dataDirValue!);
                        dataDir = dataDirValue!;
                    }
                    else
                        AddParseError(dataDirError!);
                    break;
                case "--json":
                    if (inlineValue == null)
                    {
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else if (TryParseJsonOutputFormat(inlineValue, out var parsedJsonOutputFormat))
                    {
                        json = true;
                        jsonOutputFormat = parsedJsonOutputFormat;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError($"Error: --json format must be one of ndjson or array, got '{inlineValue}'. Hint: use `--json` or `--json=ndjson` for newline-delimited JSON, or `--json=array` for a single JSON array.");
                    }
                    break;
                case "--format":
                    if (TryReadStringOptionValue(args, ref i, "--format", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var formatValue, out var formatError))
                    {
                        WarnIfDuplicateSingleValueOption("--format", formatValue!);
                        if (TryParseOutputFormat(formatValue!, out var parsedOutputFormat))
                        {
                            outputFormat = parsedOutputFormat;
                            if (parsedOutputFormat != OutputFormatText &&
                                parsedOutputFormat != OutputFormatDot &&
                                parsedOutputFormat != OutputFormatGraphMl)
                                json = true;
                        }
                        else
                        {
                            AddParseError($"Error: --format must be one of text, json, count, compact, csv, tsv, lsp, qf, or sarif; got '{formatValue}'.");
                        }
                    }
                    else
                    {
                        AddParseError(formatError!);
                    }
                    break;
                case "--limit":
                case "--top":
                    if (!TryReadRawOptionValue(args, ref i, "--limit", inlineValue, out var limitValue, out var missingLimitError))
                        AddParseError(missingLimitError!);
                    else if (TryParsePositiveInt(limitValue!, "--limit", out var parsedLimit, out var limitError))
                    {
                        WarnIfDuplicateSingleValueOption("--limit", limitValue!);
                        limit = parsedLimit;
                        limitExplicit = true;
                    }
                    else
                        AddParseError(limitError!);
                    break;
                case "--lang":
                    if (TryReadStringOptionValue(args, ref i, "--lang", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var langValue, out var langError))
                    {
                        WarnIfDuplicateSingleValueOption("--lang", langValue!);
                        // Normalize to lowercase so '--lang Python' == '--lang python' — every LangMap key and
                        // every DB 'files.lang' row is lowercase, so the SQL filter and WriteLangHint match.
                        // Also fold common short aliases (e.g. `py`) to canonical language names so Python-heavy
                        // workflows can use familiar shorthand without silently returning zero rows.
                        // '--lang Python' と '--lang python' を同一視するため lowercase 正規化する。LangMap の key と
                        // DB の `files.lang` はすべて lowercase なので、SQL filter と WriteLangHint が一致する。
                        // さらに `py` のような短縮エイリアスを正規名へ畳み込み、Python 利用時の慣用入力で
                        // 意図せず 0 件になる事故を避ける。
                        lang = NormalizeLangFilterValue(langValue);
                    }
                    else
                        AddParseError(langError!);
                    break;
                case "--query":
                    if (!allowNamedQuery)
                    {
                        AddParseError("Error: --query is not supported by this command.");
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            i++;
                    }
                    else if (TryReadStringOptionValue(args, ref i, "--query", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var queryValue, out var queryError))
                    {
                        WarnIfDuplicateSingleValueOption("--query", queryValue!);
                        query = queryValue;
                    }
                    else
                        AddParseError(queryError!);
                    break;
                case "--require-before":
                    if (TryReadStringOptionValue(args, ref i, "--require-before", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var requireBeforeValue, out var requireBeforeError))
                        AddSearchGuardFilter("--require-before", SearchGuardRole.Require, SearchGuardDirection.Before, requireBeforeValue!);
                    else
                        AddParseError(requireBeforeError!);
                    break;
                case "--require-after":
                    if (TryReadStringOptionValue(args, ref i, "--require-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var requireAfterValue, out var requireAfterError))
                        AddSearchGuardFilter("--require-after", SearchGuardRole.Require, SearchGuardDirection.After, requireAfterValue!);
                    else
                        AddParseError(requireAfterError!);
                    break;
                case "--reject-before":
                    if (TryReadStringOptionValue(args, ref i, "--reject-before", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var rejectBeforeValue, out var rejectBeforeError))
                        AddSearchGuardFilter("--reject-before", SearchGuardRole.Reject, SearchGuardDirection.Before, rejectBeforeValue!);
                    else
                        AddParseError(rejectBeforeError!);
                    break;
                case "--reject-after":
                    if (TryReadStringOptionValue(args, ref i, "--reject-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var rejectAfterValue, out var rejectAfterError))
                        AddSearchGuardFilter("--reject-after", SearchGuardRole.Reject, SearchGuardDirection.After, rejectAfterValue!);
                    else
                        AddParseError(rejectAfterError!);
                    break;
                case "--guard-window":
                    if (!TryReadRawOptionValue(args, ref i, "--guard-window", inlineValue, out var guardWindowValue, out var missingGuardWindowError))
                    {
                        AddParseError(missingGuardWindowError!);
                    }
                    else if (TryParseNonNegativeInt(guardWindowValue!, "--guard-window", out var parsedGuardWindow, out var guardWindowError))
                    {
                        WarnIfDuplicateSingleValueOption("--guard-window", guardWindowValue!);
                        if (parsedGuardWindow > DbReader.MaxSearchGuardWindow)
                            AddParseError($"Error: --guard-window must be between 0 and {DbReader.MaxSearchGuardWindow}; got {parsedGuardWindow}.");
                        else
                            guardWindow = parsedGuardWindow;
                    }
                    else
                    {
                        AddParseError(guardWindowError!);
                    }
                    break;
                case "--kind":
                    if (TryReadStringOptionValue(args, ref i, "--kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var kindValue, out var kindError))
                    {
                        WarnIfDuplicateSingleValueOption("--kind", kindValue!);
                        // Normalize to lowercase so '--kind FUNCTION' == '--kind function'. AllValidKinds entries
                        // and every DB 'symbols.kind' row are lowercase.
                        // '--kind FUNCTION' と '--kind function' を同一視するため lowercase 正規化する。AllValidKinds
                        // と DB の `symbols.kind` はすべて lowercase。
                        kind = kindValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(kindError!);
                    break;
                case "--visibility":
                    if (TryReadStringOptionValue(args, ref i, "--visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var visibilityValue, out var visibilityError))
                        AddVisibilityFilterValues("--visibility", visibilityValue!, visibilityFilters, AddParseError);
                    else
                        AddParseError(visibilityError!);
                    break;
                case "--exclude-visibility":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludeVisibilityValue, out var excludeVisibilityError))
                        AddVisibilityFilterValues("--exclude-visibility", excludeVisibilityValue!, excludeVisibilityFilters, AddParseError);
                    else
                        AddParseError(excludeVisibilityError!);
                    break;
                case "--rank-by":
                    if (TryReadStringOptionValue(args, ref i, "--rank-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var rankByValue, out var rankByError))
                    {
                        WarnIfDuplicateSingleValueOption("--rank-by", rankByValue!);
                        if (TryParseReferenceRankMode(rankByValue!, out var parsedRankMode))
                            rankMode = parsedRankMode;
                        else
                            AddParseError($"Error: --rank-by must be one of weighted, count, kind; got '{rankByValue}'.");
                    }
                    else
                        AddParseError(rankByError!);
                    break;
                case "--sections":
                    if (TryReadStringOptionValue(args, ref i, "--sections", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sectionsValue, out var sectionsError))
                    {
                        WarnIfDuplicateSingleValueOption("--sections", sectionsValue!);
                        mapSections = ParseMapSections(sectionsValue!, AddParseError);
                    }
                    else
                        AddParseError(sectionsError!);
                    break;
                case "--fts":
                    rawFts = true;
                    break;
                case "--body":
                    includeBody = true;
                    break;
                case "--count":
                    countOnly = true;
                    break;
                case "--cycles":
                    dependencyCycles = true;
                    break;
                case "--strict-not-found":
                    strictNotFound = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--by-bucket":
                    break;
                case "--all":
                    break;
                case "--no-dedup":
                    noDedup = true;
                    break;
                case "--no-visibility-rank":
                    noVisibilityRank = true;
                    break;
                case "--exact":
                    exact = true;
                    break;
                case "--regex":
                    regex = true;
                    break;
                case "--exact-name":
                    exactName = true;
                    break;
                case "--exact-substring":
                    exactSubstring = true;
                    break;
                case "--prefix":
                    prefix = true;
                    break;
                case "--max-hops":
                case "--depth":
                    var depthOptionName = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, depthOptionName, inlineValue, out var depthValue, out var missingDepthError))
                        AddParseError(missingDepthError!);
                    else if (TryParseNonNegativeInt(depthValue!, depthOptionName, out var parsedDepth, out var depthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-hops", depthValue!);
                        contextAfter = parsedDepth; // reused as depth for impact / impact用に再利用
                        contextAfterExplicit = true;
                        if (depthOptionName == "--depth")
                            impactDeprecatedDepthUsed = true;
                    }
                    else
                        AddParseError(depthError!);
                    break;
                case "--reverse":
                    break; // handled by specific commands / 特定コマンドで処理
                case "--group-by-name":
                    break;
                case "--group-by":
                    if (TryReadStringOptionValue(args, ref i, "--group-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var groupByValue, out var groupByError))
                    {
                        WarnIfDuplicateSingleValueOption("--group-by", groupByValue!);
                        groupBy = groupByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(groupByError!);
                    break;
                case "--with-paths":
                    withPaths = true;
                    break;
                case "--bytes":
                    rawBytes = true;
                    break;
                case "--raw-kinds":
                    rawKinds = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--profile":
                    profile = true;
                    break;
                case "--slow-query-ms":
                    if (!TryReadRawOptionValue(args, ref i, "--slow-query-ms", inlineValue, out var slowQueryValue, out var missingSlowQueryError))
                        AddParseError(missingSlowQueryError!);
                    else if (TryParseNonNegativeInt(slowQueryValue!, "--slow-query-ms", out var parsedSlowQueryMs, out var slowQueryError))
                    {
                        WarnIfDuplicateSingleValueOption("--slow-query-ms", slowQueryValue!);
                        slowQueryMs = parsedSlowQueryMs;
                    }
                    else
                        AddParseError(slowQueryError!);
                    break;
                case "--min-entrypoint-confidence":
                    if (!TryReadRawOptionValue(args, ref i, "--min-entrypoint-confidence", inlineValue, out var minEntrypointConfidenceValue, out var missingMinEntrypointConfidenceError))
                        AddParseError(missingMinEntrypointConfidenceError!);
                    else if (TryParseConfidence(minEntrypointConfidenceValue!, out var parsedMinEntrypointConfidence))
                    {
                        WarnIfDuplicateSingleValueOption("--min-entrypoint-confidence", minEntrypointConfidenceValue!);
                        minEntrypointConfidence = parsedMinEntrypointConfidence;
                    }
                    else
                        AddParseError($"Error: --min-entrypoint-confidence must be a number from 0.0 through 1.0; got '{minEntrypointConfidenceValue}'.");
                    break;
                case "--check":
                    if (allowStatusCheck)
                    {
                        checkWorkspace = true;
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --check is not supported by this command.");
                    }
                    break;
                case "--stale-after":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--stale-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var staleAfterValue, out var staleAfterError))
                        {
                            WarnIfDuplicateSingleValueOption("--stale-after", staleAfterValue!);
                            if (TryParseStaleAfter(staleAfterValue!, out var parsedStaleAfter, out var parseStaleAfterError))
                                staleAfter = parsedStaleAfter;
                            else
                                AddParseError(parseStaleAfterError!);
                        }
                        else
                        {
                            AddParseError(staleAfterError!);
                        }
                    }
                    else
                    {
                        AddParseError("Error: --stale-after is not supported by this command.");
                    }
                    break;
                case "--explain":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--explain", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var explainValue, out var explainError))
                        {
                            WarnIfDuplicateSingleValueOption("--explain", explainValue!);
                            statusExplainField = explainValue;
                        }
                        else
                            AddParseError(explainError!);
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --explain is not supported by this command.");
                    }
                    break;
                case "--log-path":
                    if (allowStatusCheck)
                    {
                        statusLogPath = true;
                    }
                    else
                    {
                        AddParseError("Error: --log-path is not supported by this command.");
                    }
                    break;
                case "--config":
                    if (allowStatusCheck)
                    {
                        statusConfig = true;
                    }
                    else
                    {
                        AddParseError("Error: --config is only supported by status.");
                    }
                    break;
                case "--path":
                    if (TryReadStringOptionValue(args, ref i, "--path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var pathPattern, out var pathError))
                    {
                        pathPatterns.Add(pathPattern!); // Repeatable; multiple values OR together / 繰り返し可、複数値は OR で結合
                        userPathPatterns.Add(pathPattern!);
                    }
                    else
                        AddParseError(pathError!);
                    break;
                case "--project":
                    if (TryReadStringOptionValue(args, ref i, "--project", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var projectName, out var projectError))
                        projectFilters.Add(projectName!);
                    else
                        AddParseError(projectError!);
                    break;
                case "--solution":
                    if (TryReadStringOptionValue(args, ref i, "--solution", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var solutionValue, out var solutionError))
                    {
                        WarnIfDuplicateSingleValueOption("--solution", solutionValue!);
                        solutionFilter = solutionValue;
                    }
                    else
                        AddParseError(solutionError!);
                    break;
                case "--exclude-path":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var excludePath, out var excludePathError))
                        excludePaths.Add(excludePath!);
                    else
                        AddParseError(excludePathError!);
                    break;
                case "--exclude-tests":
                    excludeTests = true;
                    break;
                case "--include-generated":
                    includeGenerated = true;
                    break;
                case "--since":
                    if (!TryReadStringOptionValue(args, ref i, "--since", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sinceValue, out var sinceError))
                        AddParseError(sinceError!);
                    else if (TryParseIso8601Since(sinceValue!, out var parsedSince))
                    {
                        WarnIfDuplicateSingleValueOption("--since", sinceValue!);
                        since = parsedSince;
                    }
                    else
                        AddParseError($"Error: could not parse --since value '{sinceValue}' as a date/time. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).");
                    break;
                case "--start":
                case "--start-line":
                    var startFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, startFlag, inlineValue, out var startValue, out var missingStartError))
                        AddParseError(missingStartError!);
                    else if (TryParsePositiveInt(startValue!, startFlag, out var parsedStart, out var startError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", startValue!);
                        startLine = parsedStart;
                    }
                    else
                        AddParseError(startError!);
                    break;
                case "--end":
                case "--end-line":
                    var endFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, endFlag, inlineValue, out var endValue, out var missingEndError))
                        AddParseError(missingEndError!);
                    else if (TryParsePositiveInt(endValue!, endFlag, out var parsedEnd, out var endError))
                    {
                        WarnIfDuplicateSingleValueOption("--end", endValue!);
                        endLine = parsedEnd;
                    }
                    else
                        AddParseError(endError!);
                    break;
                case "--before":
                    if (!TryReadRawOptionValue(args, ref i, "--before", inlineValue, out var beforeValue, out var missingBeforeError))
                        AddParseError(missingBeforeError!);
                    else if (TryParseNonNegativeInt(beforeValue!, "--before", out var parsedBefore, out var beforeError))
                    {
                        WarnIfDuplicateSingleValueOption("--before", beforeValue!);
                        contextBefore = parsedBefore;
                    }
                    else
                        AddParseError(beforeError!);
                    break;
                case "--after":
                    if (!TryReadRawOptionValue(args, ref i, "--after", inlineValue, out var afterValue, out var missingAfterError))
                        AddParseError(missingAfterError!);
                    else if (TryParseNonNegativeInt(afterValue!, "--after", out var parsedAfter, out var afterError))
                    {
                        WarnIfDuplicateSingleValueOption("--after", afterValue!);
                        contextAfter = parsedAfter;
                    }
                    else
                        AddParseError(afterError!);
                    break;
                case "--focus-line":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-line", inlineValue, out var focusLineValue, out var missingFocusLineError))
                        AddParseError(missingFocusLineError!);
                    else if (TryParsePositiveInt(focusLineValue!, "--focus-line", out var parsedFocusLine, out var focusLineError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-line", focusLineValue!);
                        focusLine = parsedFocusLine;
                    }
                    else
                        AddParseError(focusLineError!);
                    break;
                case "--focus-column":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-column", inlineValue, out var focusColumnValue, out var missingFocusColumnError))
                        AddParseError(missingFocusColumnError!);
                    else if (TryParsePositiveInt(focusColumnValue!, "--focus-column", out var parsedFocusColumn, out var focusColumnError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-column", focusColumnValue!);
                        focusColumn = parsedFocusColumn;
                    }
                    else
                        AddParseError(focusColumnError!);
                    break;
                case "--focus-length":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-length", inlineValue, out var focusLengthValue, out var missingFocusLengthError))
                        AddParseError(missingFocusLengthError!);
                    else if (TryParsePositiveInt(focusLengthValue!, "--focus-length", out var parsedFocusLength, out var focusLengthError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-length", focusLengthValue!);
                        focusLength = parsedFocusLength;
                    }
                    else
                        AddParseError(focusLengthError!);
                    break;
                case "--name":
                    if (TryReadStringOptionValue(args, ref i, "--name", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var extraName, out var nameError))
                        extraNames.Add(extraName!); // Repeatable; OR-joined with other --name values and extra positional names / 繰り返し可、他の --name や追加の positional 引数と OR 結合
                    else
                        AddParseError($"{nameError} / --name には値（シンボル名パターン）が必要です。");
                    break;
                case "--snippet-lines":
                    if (!TryReadRawOptionValue(args, ref i, "--snippet-lines", inlineValue, out var snippetLinesValue, out var missingSnippetLinesError))
                        AddParseError(missingSnippetLinesError!);
                    else if (TryParsePositiveInt(snippetLinesValue!, "--snippet-lines", out var parsedSnippetLines, out var snippetLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-lines", snippetLinesValue!);
                        snippetLines = parsedSnippetLines;
                        snippetLinesExplicit = true;
                    }
                    else
                        AddParseError(snippetLinesError!);
                    break;
                case "--snippet-focus":
                    if (!TryReadStringOptionValue(args, ref i, "--snippet-focus", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var snippetFocusValue, out var snippetFocusError))
                    {
                        AddParseError(snippetFocusError!);
                    }
                    else if (TryParseSnippetFocusMode(snippetFocusValue!, out var parsedSnippetFocus))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-focus", snippetFocusValue!);
                        snippetFocus = parsedSnippetFocus;
                    }
                    else
                    {
                        AddParseError($"Error: invalid --snippet-focus value '{snippetFocusValue}'. Use leftmost, quality, or proximity.");
                    }
                    break;
                case "--max-line-width":
                    if (!TryReadRawOptionValue(args, ref i, "--max-line-width", inlineValue, out var maxLineWidthValue, out var missingMaxLineWidthError))
                        AddParseError(missingMaxLineWidthError!);
                    else if (TryParseNonNegativeInt(maxLineWidthValue!, "--max-line-width", out var parsedMaxLineWidth, out var maxLineWidthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-line-width", maxLineWidthValue!);
                        maxLineWidth = parsedMaxLineWidth;
                        maxLineWidthExplicit = true;
                    }
                    else
                        AddParseError(maxLineWidthError!);
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        AddParseError($"Error: unsupported option: {args[i]}. Use `--` before a query literal that starts with `-`.");
                        break;
                    }
                    else if (query == null)
                    {
                        query = args[i];
                    }
                    else
                    {
                        // Extra positional args become additional symbol names / 追加の positional 引数を追加の symbol name として扱う
                        extraNames.Add(args[i]);
                    }
                    break;
            }
        }

        if (parseErrors == null && projectFilters.Count > 0)
        {
            try
            {
                foreach (var glob in SolutionProjectResolver.ResolveProjectDirectoryGlobs(Environment.CurrentDirectory, projectFilters, solutionFilter))
                    pathPatterns.Add(glob);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddParseError($"Error: {ex.Message}");
            }
        }

        ValidateQueryPathOptionValues(userPathPatterns, excludePaths, AddParseError);
        if (guardFilters.Count > DbReader.MaxSearchGuardFilters)
            AddParseError($"Error: search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {guardFilters.Count}.");

        if (validateDefaultLimit && !limitExplicit && defaultLimitError != null)
            AddParseError(defaultLimitError);
        if (validateDefaultSnippetLines && !snippetLinesExplicit && defaultSnippetLinesError != null)
            AddParseError(defaultSnippetLinesError);
        if (validateDefaultMaxLineWidth && !maxLineWidthExplicit && defaultMaxLineWidthError != null)
            AddParseError(defaultMaxLineWidthError);

        var dbResolution = DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, dbPath, dataDir);
        var resolvedDbPath = readOnly ? DbContext.ToReadOnlyUri(dbResolution.DbPath) : dbResolution.DbPath;

        return new QueryCommandOptions
        {
            DbPath = resolvedDbPath,
            DbPathExplicit = dbPathExplicit,
            ReadOnly = readOnly,
            DataDir = dbResolution.DataDir,
            DataDirSource = dbResolution.DataDirSource,
            Json = json ?? jsonDefault,
            JsonOutputFormat = jsonOutputFormat,
            OutputFormat = outputFormat,
            Limit = limit,
            Lang = lang,
            Kind = kind,
            Query = query,
            RawFts = rawFts,
            IncludeBody = includeBody,
            StartLine = startLine,
            EndLine = endLine,
            ContextBefore = contextBefore,
            ContextAfter = contextAfter,
            ContextAfterExplicit = contextAfterExplicit,
            ImpactDeprecatedDepthUsed = impactDeprecatedDepthUsed,
            FocusLine = focusLine,
            FocusColumn = focusColumn,
            FocusLength = focusLength,
            SnippetLines = snippetLines,
            SnippetFocus = snippetFocus,
            MaxLineWidth = maxLineWidth,
            PathPatterns = pathPatterns,
            WorkspaceDbPaths = workspaceDbPaths,
            ProjectFilters = projectFilters,
            SolutionFilter = solutionFilter,
            ExcludePaths = excludePaths,
            VisibilityFilters = visibilityFilters,
            ExcludeVisibilityFilters = excludeVisibilityFilters,
            ExcludeTests = excludeTests,
            IncludeGenerated = includeGenerated,
            CountOnly = countOnly,
            StrictNotFound = strictNotFound,
            Strict = strict,
            Since = since,
            NoDedup = noDedup,
            NoVisibilityRank = noVisibilityRank,
            Exact = exact,
            Regex = regex,
            Prefix = prefix,
            GuardFilters = guardFilters,
            GuardWindow = guardWindow,
            ExactName = exactName,
            ExactSubstring = exactSubstring,
            CheckWorkspace = checkWorkspace,
            StaleAfter = staleAfter,
            StatusCheckScopes = statusCheckScopes,
            WithPaths = withPaths,
            GroupBy = groupBy,
            RawBytes = rawBytes,
            RawKinds = rawKinds,
            Verbose = verbose,
            Profile = profile,
            SlowQueryMs = slowQueryMs,
            MinEntrypointConfidence = minEntrypointConfidence,
            StatusExplainField = statusExplainField,
            StatusLogPath = statusLogPath,
            StatusConfig = statusConfig,
            RankMode = rankMode,
            ExtraNames = extraNames,
            MapSections = mapSections,
            DependencyCycles = dependencyCycles,
            ParseError = parseErrors == null ? null : string.Join(Environment.NewLine, parseErrors),
        };
    }

    private static List<string> ParseMapSections(string rawValue, Action<string> addParseError)
    {
        var sections = new List<string>();
        if (!ValidateCsvBounds("--sections", rawValue, MaxMapSectionsCsvLength, MaxMapSectionsCsvEntries, addParseError))
            return sections;

        foreach (var rawSection in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var section = rawSection.ToLowerInvariant();
            switch (section)
            {
                case "tree":
                case "modules":
                    sections.Add("tree");
                    break;
                case "languages":
                case "hotspots":
                case "metrics":
                    sections.Add(section);
                    break;
                default:
                    addParseError($"Error: --sections contains unsupported section '{rawSection}'. Use one or more of tree, languages, hotspots, metrics.");
                    break;
            }
        }

        if (sections.Count == 0)
            addParseError("Error: --sections cannot be empty. Use one or more of tree, languages, hotspots, metrics.");
        return sections.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool ValidateCsvBounds(
        string optionName,
        string rawValue,
        int maxLength,
        int maxEntries,
        Action<string> addParseError)
    {
        if (rawValue.Length > maxLength)
        {
            addParseError($"Error: {optionName} value is too long ({rawValue.Length} characters; max {maxLength}).");
            return false;
        }

        var entries = CountCsvEntries(rawValue);
        if (entries > maxEntries)
        {
            addParseError($"Error: {optionName} accepts at most {maxEntries} comma-separated entries.");
            return false;
        }

        return true;
    }

    private static int CountCsvEntries(string rawValue)
    {
        if (rawValue.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in rawValue)
        {
            if (ch == ',')
                count++;
        }

        return count;
    }

    private static void ValidateQueryPathOptionValues(
        IReadOnlyList<string> pathPatterns,
        IReadOnlyList<string> excludePaths,
        Action<string> addParseError)
    {
        ValidatePathOptionValues("--path", pathPatterns, addParseError);
        ValidatePathOptionValues("--exclude-path", excludePaths, addParseError);
    }

    private static void ValidatePathOptionValues(
        string optionName,
        IReadOnlyList<string> patterns,
        Action<string> addParseError)
    {
        if (patterns.Count > MaxQueryPathFilterCount)
            addParseError($"Error: {optionName} accepts at most {MaxQueryPathFilterCount} values.");

        foreach (var pattern in patterns)
        {
            if (pattern.Length > MaxQueryPathFilterLength)
            {
                addParseError($"Error: {optionName} value is too long ({pattern.Length} characters; max {MaxQueryPathFilterLength}).");
                continue;
            }

            ValidatePathGlobPattern(optionName, pattern, addParseError);
        }
    }

    private static bool TryParseJsonOutputFormat(string rawValue, out string format)
    {
        if (string.Equals(rawValue, JsonOutputFormatArray, StringComparison.OrdinalIgnoreCase))
        {
            format = JsonOutputFormatArray;
            return true;
        }
        if (string.Equals(rawValue, JsonOutputFormatNdjson, StringComparison.OrdinalIgnoreCase))
        {
            format = JsonOutputFormatNdjson;
            return true;
        }

        format = JsonOutputFormatNdjson;
        return false;
    }

    private static bool TryParseOutputFormat(string rawValue, out string format)
    {
        switch (rawValue.ToLowerInvariant())
        {
            case OutputFormatText:
            case OutputFormatJson:
            case OutputFormatCount:
            case OutputFormatCompact:
            case OutputFormatCsv:
            case OutputFormatTsv:
            case OutputFormatLsp:
            case OutputFormatQf:
            case OutputFormatSarif:
                format = rawValue.ToLowerInvariant();
                return true;
            default:
                format = OutputFormatText;
                return false;
        }
    }

    private static void ValidatePathGlobPattern(string optionName, string pattern, Action<string> addParseError)
    {
        if (TryFindUnsupportedBracketGlob(pattern, out var reason))
        {
            addParseError($"Error: {optionName} '{pattern}' is not a valid glob: {reason}. Hint: escape '[' or ']' with a backslash when matching literal path characters, or use only '*' and '?' wildcards.");
        }
    }

    private static bool TryFindUnsupportedBracketGlob(string pattern, out string reason)
    {
        var escaped = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '[')
            {
                reason = "character classes are not supported";
                return true;
            }

            if (ch == ']')
            {
                reason = "unmatched ']'";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static bool TryParseReferenceRankMode(string value, out ReferenceRankMode rankMode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "weighted":
                rankMode = ReferenceRankMode.Weighted;
                return true;
            case "count":
                rankMode = ReferenceRankMode.Count;
                return true;
            case "kind":
                rankMode = ReferenceRankMode.Kind;
                return true;
            default:
                rankMode = ReferenceRankMode.Weighted;
                return false;
        }
    }

    private static bool TryParseConfidence(string value, out double confidence)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out confidence) &&
            !double.IsNaN(confidence) &&
            !double.IsInfinity(confidence) &&
            confidence >= 0 &&
            confidence <= 1)
        {
            return true;
        }

        confidence = 0;
        return false;
    }

    private static bool TryResolveHotspotsGroupBy(string? requestedGroupBy, string? lang, bool groupByName, out string groupBy, out string error)
    {
        groupBy = string.Empty;
        error = string.Empty;

        if (groupByName && requestedGroupBy != null)
        {
            error = "Error: --group-by-name cannot be combined with --group-by.";
            return false;
        }

        if (groupByName)
        {
            groupBy = HotspotsGroupedByNameKind;
            return true;
        }

        if (requestedGroupBy == null)
        {
            groupBy = IsSqlLanguageFilter(lang) ? HotspotsGroupedByStatement : HotspotsGroupedBySymbol;
            return true;
        }

        switch (requestedGroupBy)
        {
            case HotspotsGroupedBySymbol:
            case HotspotsGroupedByFile:
            case HotspotsGroupedByStatement:
                groupBy = requestedGroupBy;
                return true;
            case "name":
            case HotspotsGroupedByNameKind:
                groupBy = HotspotsGroupedByNameKind;
                return true;
            default:
                error = $"Error: unsupported hotspots --group-by value '{requestedGroupBy}'. Use symbol, file, or statement.";
                return false;
        }
    }

    private static bool IsSqlLanguageFilter(string? lang) =>
        string.Equals(lang, "sql", StringComparison.Ordinal);

    internal static string? NormalizeLangFilterValue(string? langValue)
    {
        return DbReader.NormalizeQueryLanguage(langValue);
    }

    internal static IReadOnlyList<string> GetLanguageAliases(string lang)
        => LanguageDisplayAliases.TryGetValue(lang, out var aliases) ? aliases : [];

    internal static bool TryParseSnippetFocusMode(string value, out SearchSnippetFocusMode mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "leftmost" => SearchSnippetFocusMode.Leftmost,
            "quality" => SearchSnippetFocusMode.Quality,
            "proximity" => SearchSnippetFocusMode.Proximity,
            _ => default,
        };
        return value.Trim().Equals("leftmost", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("quality", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("proximity", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyCollection<string> GetCompletionLanguageAliases()
        => LanguageDisplayAliases.Values.SelectMany(aliases => aliases).ToArray();

    internal static bool TryParseStaleAfter(string value, out TimeSpan staleAfter, out string? error)
    {
        staleAfter = default;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Error: --stale-after requires a duration like 30m, 2h, or 7d.";
            return false;
        }

        var trimmed = value.Trim();
        var suffix = trimmed[^1];
        var numberText = trimmed[..^1];
        TimeSpan unit;
        switch (suffix)
        {
            case 'm':
            case 'M':
                unit = TimeSpan.FromMinutes(1);
                break;
            case 'h':
            case 'H':
                unit = TimeSpan.FromHours(1);
                break;
            case 'd':
            case 'D':
                unit = TimeSpan.FromDays(1);
                break;
            default:
                error = $"Error: could not parse stale-after value '{value}'. Use a positive duration with m, h, or d suffix (e.g. 30m, 2h, 7d).";
                return false;
        }

        if (!double.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) ||
            number <= 0)
        {
            error = $"Error: could not parse stale-after value '{value}'. Use a positive duration with m, h, or d suffix (e.g. 30m, 2h, 7d).";
            return false;
        }

        var ticks = number * unit.Ticks;
        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            error = $"Error: stale-after value '{value}' is too large.";
            return false;
        }

        staleAfter = TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
        return true;
    }

    private static (TimeSpan Value, string? Error) ResolveStaleAfter(QueryCommandOptions options, string? envValue)
    {
        if (options.StaleAfter.HasValue)
            return (options.StaleAfter.Value, null);

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (TryParseStaleAfter(envValue, out var parsed, out var error))
                return (parsed, null);
            return (DefaultStaleAfter, error!.Replace("--stale-after", StaleAfterEnvironmentVariable, StringComparison.Ordinal));
        }

        return (DefaultStaleAfter, null);
    }

    private static bool TryResolveSearchExactMode(QueryCommandOptions options, out bool exact, out string? error)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            return false;
        }
        if (options.ExactName)
        {
            exact = false;
            error = "Error: --exact-name applies to name-based commands (symbols/definition/references/callers/callees/inspect), not search. Use --exact-substring for search, or keep --exact for backward compatibility.";
            return false;
        }

        exact = options.Exact || options.ExactSubstring;
        error = null;
        return true;
    }

    private static bool TryResolveNameExactMode(QueryCommandOptions options, string commandName, out bool exact, out string? error)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            return false;
        }
        if (options.ExactSubstring)
        {
            exact = false;
            error = $"Error: --exact-substring only applies to search. Use --exact-name for {commandName}, or keep --exact for backward compatibility.";
            return false;
        }

        exact = options.Exact || options.ExactName;
        error = null;
        return true;
    }

    private static bool TryRejectMultipleExactFlags(QueryCommandOptions options, out string? error)
    {
        var count = (options.Exact ? 1 : 0) + (options.ExactSubstring ? 1 : 0) + (options.ExactName ? 1 : 0);
        if (count > 1)
        {
            error = "Error: pass only one of --exact, --exact-substring, --exact-name.";
            return false;
        }

        error = null;
        return true;
    }

    // Preview option validation now lives in the command-specific unsupported-option allowlists.
    // Keep this shim so the existing call sites stay simple while the actual fail-closed logic
    // runs through ParseArgs() + TryWriteUnsupportedOptionError().
    // preview 系オプションの検証はコマンド別 allowlist に寄せたため、この shim は常に null を返す。
    private static string? ValidatePreviewOptions(string commandName, string[] args, bool allowMaxLineWidth, bool allowFocusOptions) => null;

    private static int ZeroResultExitCode(QueryCommandOptions options)
        => options.StrictNotFound ? CommandExitCodes.NotFound : CommandExitCodes.Success;

    private static bool IsEmptySymbolAnalysis(SymbolAnalysisResult analysis)
        => analysis.File == null
           && analysis.Definitions.Count == 0
           && analysis.NearbySymbols.Count == 0
           && analysis.References.Count == 0
           && analysis.Callers.Count == 0
           && analysis.Callees.Count == 0;

    private static int WithDb(QueryCommandOptions options, JsonSerializerOptions jsonOptions, Func<DbReader, int> action, Action<int>? afterProfile = null)
    {
        var dbPath = options.DbPath;
        if (s_batchReader == null)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                Console.Error.WriteLine(BuildMissingOptionValueError("--db"));
                return CommandExitCodes.UsageError;
            }

            // Allow SQLite URI forms (file:///abs/path?immutable=1 etc.) so users and AI agents
            // on read-only mounts / sandboxes can opt into the immutable read-only escape hatch
            // explicitly when the automatic DbContext fallback cannot recover. File.Exists is
            // skipped for URI-shaped inputs because they may carry query params and schemes that
            // are meaningless to the filesystem API but are understood by SQLite.
            // URI 形式の --db を受け入れるため、file: で始まる値は File.Exists チェックをスキップ。
            var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            var fileExistsPath = dbPath;
            if (isUri)
            {
                if (!DbPathResolver.TryNormalizeDbPath(dbPath, out fileExistsPath, out var parseError))
                {
                    Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: invalid --db file URI: {parseError?.Message ?? dbPath}");
                    Console.Error.WriteLine($"Hint: pass a valid SQLite file URI such as `file:///absolute/path/to/codeindex.db?immutable=1`; the --db value resolved to: {dbPath}");
                    GlobalToolLog.Error($"invalid_db_file_uri db={FormatLogValue(dbPath)} exception={FormatLogValue(parseError?.ToString() ?? "<unknown>")}");
                    return CommandExitCodes.DatabaseError;
                }
            }

            if (!fileExistsPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(LongPath.EnsureWindowsPrefix(fileExistsPath)))
            {
                var resolvedPath = Path.GetFullPath(fileExistsPath);
                Console.Error.WriteLine($"Error [{CommandErrorCodes.DbNotFound}]: database not found at {resolvedPath}");
                if (isUri)
                    Console.Error.WriteLine($"Hint: the --db path resolved to: {resolvedPath}");
                Console.Error.WriteLine("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.");
                return CommandExitCodes.DatabaseError;
            }
        }

        Database.DbDebug.ResetContext();
        var profiling = options.Profile || options.Verbose || options.SlowQueryMs.HasValue;
        if (profiling)
            Database.DbDebug.BeginProfile(options.SlowQueryMs);
        DbContext? db = null;
        try
        {
            DbReader reader;
            if (s_batchReader != null)
            {
                reader = s_batchReader;
            }
            else
            {
                db = new DbContext(dbPath);
                if (!db.TryValidateIsCodeIndexDb(out var validationReason))
                    return WriteInvalidCodeIndexDbError(dbPath, validationReason);
                db.TryMigrateForRead();
                reader = new DbReader(db);
            }

            reader.IncludeGenerated = options.IncludeGenerated;
            var exitCode = reader.RunWithGeneratedScope(() => action(reader));
            var profileEntries = profiling ? Database.DbDebug.EndProfile() : [];
            if (options.Profile)
                WriteProfilePayload(profileEntries, jsonOptions);
            if (options.Verbose)
                WriteVerboseQueryDebug(options, profileEntries, jsonOptions);
            afterProfile?.Invoke(exitCode);
            return exitCode;
        }
        catch (FtsQuerySyntaxException ex)
        {
            Console.Error.WriteLine($"Error [{CommandErrorCodes.FtsQuerySyntax}]: FTS5 query syntax: {ex.Message}");
            if (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Hint: `--fts` passes raw FTS5 syntax, so `:` is treated as a column qualifier. Drop `--fts` if you want literal-safe search.");
            }
            else
            {
                Console.Error.WriteLine("Hint: `--fts` passes raw FTS5 syntax. Fix the query or drop `--fts` to use literal-safe search.");
            }
            return CommandExitCodes.UsageError;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            if (ex is SqliteException sqliteEx)
            {
                if (sqliteEx.SqliteErrorCode == 13)
                {
                    Console.Error.WriteLine($"Error [{CommandErrorCodes.TempStoreExhausted}]: SQLite temp-store exhausted while evaluating this query.");
                    Console.Error.WriteLine("Hint: narrow the query with `--lang`, `--path`, or `--kind`, then retry with a freshly updated cdidx build if the problem persists.");
                    Database.DbDebug.DumpToStderr(ex);
                    return CommandExitCodes.DatabaseError;
                }

                // SQLITE_BUSY (5) and SQLITE_LOCKED (6) both mean a concurrent writer is
                // holding the database; surface E002_DB_LOCKED so scripts can implement
                // retry-with-backoff without substring-matching the prose message.
                // SQLITE_BUSY/LOCKED は別 writer によるロック競合なので、リトライ判断用に
                // E002_DB_LOCKED で機械可読に区別する。
                if (sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6)
                {
                    Console.Error.WriteLine($"Error [{CommandErrorCodes.DbLocked}]: SQLite reported the database is locked or busy: {ex.Message}");
                    Console.Error.WriteLine("Hint: another process may be holding the database. Wait for it to finish, or retry with backoff.");
                    Database.DbDebug.DumpToStderr(ex);
                    return CommandExitCodes.DatabaseError;
                }
            }

            WriteDatabaseOpenFailure(ex, dbPath);
            Database.DbDebug.DumpToStderr(ex);
            return CommandExitCodes.DatabaseError;
        }
        finally
        {
            db?.Dispose();
            if (profiling)
                Database.DbDebug.EndProfile();
            Database.DbDebug.ResetContext();
        }
    }

    private static int WriteInvalidCodeIndexDbError(string dbPath, string? validationReason)
    {
        Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: {dbPath} does not appear to be a valid CodeIndex database ({validationReason}).");
        Console.Error.WriteLine("Hint: rebuild with `cdidx index <projectPath> --db <path>` to create a fresh database.");
        return CommandExitCodes.DatabaseError;
    }

    private static string? GetDataDirectoryPath(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) ||
            dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetDirectoryName(Path.GetFullPath(dbPath));
    }

    private static void WriteDatabaseOpenFailure(Exception ex, string dbPath)
    {
        GlobalToolLog.Error($"database_open_failed db={FormatLogValue(dbPath)} exception={FormatLogValue(ex.ToString())}");

        var unauthorized = FindException<UnauthorizedAccessException>(ex);
        if (unauthorized != null)
        {
            Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: database access denied: {unauthorized.Message}");
            Console.Error.WriteLine(MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()));
            return;
        }

        var io = FindException<IOException>(ex);
        if (io != null)
        {
            Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: database I/O error: {io.Message}");
            Console.Error.WriteLine(MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()));
            return;
        }

        var sqlite = FindException<SqliteException>(ex);
        if (sqlite != null)
        {
            if (sqlite.SqliteErrorCode == 14)
            {
                Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: database access/open denied: {sqlite.Message}");
                Console.Error.WriteLine(MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent()));
                return;
            }

            if (sqlite.SqliteErrorCode == 11)
            {
                Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: SQLite reported database corruption: {sqlite.Message}");
                Console.Error.WriteLine("Hint: rebuild the index with `cdidx index <projectPath> --rebuild`, or delete the broken `.cdidx/codeindex.db*` files and run `cdidx index <projectPath>` again.");
                return;
            }

            Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: SQLite database error ({sqlite.SqliteErrorCode}): {sqlite.Message}");
            Console.Error.WriteLine(MacProfileDetector.IsPermissionStyleSqliteError(sqlite)
                ? MacProfileDetector.BuildDatabaseHint(MacProfileDetector.DetectCurrent())
                : "Hint: check `--db`, verify the index was written by a compatible cdidx version, or rebuild it with `cdidx index <projectPath> --rebuild`.");
            return;
        }

        Console.Error.WriteLine($"Error [{CommandErrorCodes.DbError}]: database error: {ex.Message}");
        Console.Error.WriteLine("Hint: check `--db`, or rebuild the index with `cdidx index <projectPath>` if the DB may be stale or corrupted.");
    }

    private static T? FindException<T>(Exception ex)
        where T : Exception
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is T typed)
                return typed;
        }

        return null;
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return value
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private static void WriteProfilePayload(IReadOnlyList<QueryProfileEntry> entries, JsonSerializerOptions jsonOptions)
    {
        var phases = new JsonArray();
        var queryPlan = new JsonArray();
        var queries = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var name = "sql_" + (i + 1).ToString(CultureInfo.InvariantCulture);
            var entry = entries[i];
            phases.Add(new JsonObject
            {
                ["name"] = name,
                ["elapsed_ms"] = Math.Round(entry.ElapsedMs, 3),
                ["rows_scanned"] = entry.RowsScanned,
            });
            queries.Add(new JsonObject
            {
                ["name"] = name,
                ["sql"] = entry.Sql,
            });
            foreach (var row in entry.QueryPlan)
            {
                queryPlan.Add(new JsonObject
                {
                    ["phase"] = name,
                    ["id"] = row.Id,
                    ["parent"] = row.Parent,
                    ["not_used"] = row.NotUsed,
                    ["detail"] = row.Detail,
                });
            }
        }

        Console.WriteLine(new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["phases"] = phases,
                ["query_plan"] = queryPlan,
                ["queries"] = queries,
            },
        }.ToJsonString(jsonOptions));
    }

    private static void WriteVerboseQueryDebug(QueryCommandOptions options, IReadOnlyList<QueryProfileEntry> entries, JsonSerializerOptions jsonOptions)
    {
        var elapsedMs = Math.Round(entries.Sum(entry => entry.ElapsedMs), 3);
        var rowsScanned = entries.Sum(entry => entry.RowsScanned);
        if (!options.Json)
        {
            Console.Error.WriteLine($"DEBUG query: sql_statements={entries.Count} elapsed_ms={elapsedMs.ToString(CultureInfo.InvariantCulture)} rows_scanned={rowsScanned}");
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                Console.Error.WriteLine(
                    $"DEBUG query sql_{i + 1}: elapsed_ms={Math.Round(entry.ElapsedMs, 3).ToString(CultureInfo.InvariantCulture)} rows_scanned={entry.RowsScanned}");
            }
            return;
        }

        var phases = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            phases.Add(new JsonObject
            {
                ["name"] = "sql_" + (i + 1).ToString(CultureInfo.InvariantCulture),
                ["elapsed_ms"] = Math.Round(entry.ElapsedMs, 3),
                ["rows_scanned"] = entry.RowsScanned,
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["_debug"] = new JsonObject
            {
                ["sql_statement_count"] = entries.Count,
                ["elapsed_ms"] = elapsedMs,
                ["rows_scanned"] = rowsScanned,
                ["phases"] = phases,
                ["redaction"] = "SQL text and parameter values are omitted from --verbose debug output; use --profile for opt-in SQL diagnostics.",
            },
        }.ToJsonString(jsonOptions));
    }

    private static void WriteNumberedExcerpt(int startLine, string content, string indent = "")
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine($"{indent}  {startLine + i,4}: {lines[i]}");
    }

    private static bool TryWriteParseError(QueryCommandOptions options, string commandName)
    {
        var dbPathError = BuildExplicitDbPathParseError(options);
        if (options.ParseError == null && dbPathError == null)
            return false;

        var primaryError = options.ParseError ?? dbPathError!;
        CommandErrorWriter.Write(
            StripErrorPrefix(primaryError),
            primaryError == dbPathError && options.ParseError == null
                ? "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command."
                : "fix the invalid or missing option value, then rerun with the command shape below.",
            GetUsageLineOrThrow(commandName),
            ExtractErrorCode(primaryError));
        if (options.ParseError != null && dbPathError != null)
            CommandErrorWriter.Write(
                StripErrorPrefix(dbPathError),
                "create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.",
                GetUsageLineOrThrow(commandName),
                ExtractErrorCode(dbPathError));
        return true;
    }

    private static string? BuildExplicitDbPathParseError(QueryCommandOptions options)
    {
        if (options.StatusConfig)
            return null;
        if (!options.DbPathExplicit)
            return null;
        if (string.IsNullOrWhiteSpace(options.DbPath))
            return BuildMissingOptionValueError("--db");
        if (options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath)))
            return null;

        return $"Error [{CommandErrorCodes.DbNotFound}]: --db '{options.DbPath}' does not point to an existing database file.";
    }

    private static readonly HashSet<string> KnownSymbolKindFilters = new(StringComparer.Ordinal)
    {
        "accessor",
        "associatedtype",
        "attribute",
        "class",
        "class_hook",
        "constant",
        "constructor",
        "delegate",
        "enum",
        "event",
        "field",
        "function",
        "heading",
        "hook",
        "impl",
        "implements",
        "import",
        "interface",
        "label",
        "lambda",
        "layout",
        "method",
        "module",
        "namespace",
        "object",
        "operator",
        "procedure",
        "property",
        "protocol",
        "record",
        "reference",
        "route",
        "specialization",
        "struct",
        "test.method",
        "trait",
        "type",
        "typealias",
        "union",
        "variable",
    };

    private static readonly HashSet<string> KnownVisibilityFilters = new(StringComparer.Ordinal)
    {
        "public",
        "protected",
        "internal",
        "private",
    };

    private static void AddVisibilityFilterValues(string optionName, string rawValue, List<string> target, Action<string> addParseError)
    {
        if (!ValidateCsvBounds(optionName, rawValue, MaxVisibilityFilterCsvLength, MaxVisibilityFilterCsvEntries, addParseError))
            return;

        var values = rawValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (values.Count == 0)
        {
            addParseError($"Error: {optionName} requires one or more of public, protected, internal, private.");
            return;
        }

        foreach (var value in values)
        {
            if (!KnownVisibilityFilters.Contains(value))
            {
                addParseError($"Error: unsupported {optionName} value '{value}'. Use one or more of public, protected, internal, private.");
                continue;
            }

            if (!target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    private static bool TryWriteInvalidKindFilterError(QueryCommandOptions options, string commandName, IReadOnlyCollection<string> acceptedKinds, params IReadOnlyCollection<string>[] alternateAcceptedKinds)
    {
        if (options.Kind != null
            && !acceptedKinds.Contains(options.Kind)
            && !alternateAcceptedKinds.Any(kinds => kinds.Contains(options.Kind)))
        {
            CommandErrorWriter.Write(
                $"invalid --kind value `{options.Kind}`.",
                $"use one of: {string.Join(", ", acceptedKinds)}.",
                GetUsageLineOrThrow(commandName));
            return true;
        }

        return false;
    }

    private static bool TryWriteUnsupportedOptionError(string commandName, string[] cmdArgs, IEnumerable<string> supportedOptions, string? queryLiteral = null)
    {
        var supported = supportedOptions.ToHashSet(StringComparer.Ordinal);
        var skippedQueryLiteral = false;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            if (queryLiteral != null && !skippedQueryLiteral && arg == queryLiteral)
            {
                skippedQueryLiteral = true;
                continue;
            }

            var normalizedArg = TrySplitInlineOptionValue(arg, out var inlineOptionName)
                ? inlineOptionName!
                : arg;
            if (arg.StartsWith("--check=", StringComparison.Ordinal) && supported.Contains("--check"))
                normalizedArg = "--check";
            if (normalizedArg == "--json" && !string.Equals(arg, "--json", StringComparison.Ordinal) && commandName != "search")
            {
                CommandErrorWriter.Write(
                    "--json=<format> is only supported by 'search'.",
                    "use plain `--json` here, or rerun search with `--json=array`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (supported.Contains(normalizedArg))
            {
                if (normalizedArg == "--" && normalizedArg == arg && i + 1 < cmdArgs.Length)
                    i++;
                if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            // `--query` is parsed specially so commands without query literals can emit the
            // dedicated parser message instead of the generic unsupported-option error.
            // `--query` は専用エラー文言を出したいので generic unsupported 判定からは外す。
            if (normalizedArg == "--query")
            {
                if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                    i++;
                continue;
            }

            if (normalizedArg == "--group-by-name")
            {
                CommandErrorWriter.Write(
                    "--group-by-name is only supported by 'hotspots'.",
                    "remove `--group-by-name` here, or rerun with `cdidx hotspots --group-by-name ...`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (normalizedArg == "--group-by")
            {
                CommandErrorWriter.Write(
                    "--group-by is only supported by 'hotspots'.",
                    "remove `--group-by` here, or rerun with `cdidx hotspots --group-by <symbol|file|statement> ...`.",
                    GetUsageLineOrThrow(commandName));
                return true;
            }

            if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                i++;

            // Suggest the closest accepted flag for this command when the user mistypes
            // a flag name (e.g. `--paht` → `--path`). Built on the same suggester used for
            // subcommand typos so the recovery experience is consistent (#1582).
            // TrySplitInlineOptionValue only splits inline `=value` when the prefix is a
            // known value-taking option, so for an unrecognized `--paht=foo` the normalized
            // arg keeps the `=value`. Strip any trailing `=value` here so the matcher can
            // still find `--path` from `--paht=foo`.
            // ユーザーがフラグ名をミスタイプしたとき (例: `--paht` → `--path`) に
            // そのコマンドで受理される最も近いフラグを提案する。サブコマンドの did-you-mean と
            // 同じ suggester を共用し、回復体験を統一する (#1582)。
            // TrySplitInlineOptionValue は prefix が既知の value-taking option のときだけ
            // inline `=value` を分解するため、`--paht=foo` のように未知のオプションでは
            // `=value` が残る。matcher のために `=` 以降を除去してから候補を探す。
            var nameForSuggestion = normalizedArg;
            var eq = nameForSuggestion.IndexOf('=');
            if (eq > 0)
                nameForSuggestion = nameForSuggestion[..eq];
            var suggestion = ConsoleUi.FindClosestMatch(nameForSuggestion, supported.Where(o => o != "--"));
            var hint = suggestion == null
                ? $"remove `{arg}` and rerun, or use only the options shown in `{commandName} --help`."
                : $"Did you mean: {suggestion}? Remove `{arg}` and rerun, or use `{suggestion}` if that is what you meant.";
            CommandErrorWriter.Write(
                $"{arg} is not supported for {commandName}.",
                hint,
                GetUsageLineOrThrow(commandName));
            return true;
        }

        return false;
    }

    private static bool TryWriteUnexpectedExtraPositionals(string commandName, QueryCommandOptions options)
    {
        if (options.ExtraNames.Count == 0)
            return false;

        CommandErrorWriter.Write(
            $"unexpected extra positional {ConsoleUi.Counted(options.ExtraNames.Count, "argument")} for {commandName}: {string.Join(", ", options.ExtraNames.Select(name => $"`{name}`"))}.",
            "quote multi-word queries as a single argument, or remove the extra positional values.",
            GetUsageLineOrThrow(commandName));
        return true;
    }

    private static bool TryWriteUnexpectedPositionals(string commandName, QueryCommandOptions options)
    {
        var unexpected = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Query))
            unexpected.Add($"`{options.Query}`");
        unexpected.AddRange(options.ExtraNames.Select(name => $"`{name}`"));
        if (unexpected.Count == 0)
            return false;

        CommandErrorWriter.Write(
            $"{commandName} does not accept positional arguments: {string.Join(", ", unexpected)}.",
            "remove the extra positional arguments and use the documented flags only.",
            GetUsageLineOrThrow(commandName));
        return true;
    }

    private static string GetUsageLineOrThrow(string commandName) =>
        ConsoleUi.GetUsageLine(commandName)
        ?? throw new InvalidOperationException($"Missing usage line for command '{commandName}'.");

    // Human-readable reference_kind label for a grouped caller/callee row. Counts
    // keep high-volume relationships visible without requiring JSON re-querying.
    // grouped caller/callee 行の人間向け reference_kind ラベル。count を併記して、
    // JSON で再取得しなくても高頻度の関係が見えるようにする。
    private static string FormatReferenceKindLabel(string primary, IReadOnlyList<string> kinds, bool hasMixed, IReadOnlyDictionary<string, int>? counts)
    {
        if (counts == null || counts.Count == 0)
        {
            if (!hasMixed || kinds == null || kinds.Count <= 1)
                return primary ?? string.Empty;
            return string.Join("+", kinds);
        }

        var orderedKinds = kinds is { Count: > 0 } && kinds.Any(kind => counts.TryGetValue(kind, out var count) && count > 0)
            ? kinds
            : counts.Keys.Where(kind => counts[kind] > 0).OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        return string.Join(", ", orderedKinds
            .Where(kind => counts.TryGetValue(kind, out var count) && count > 0)
            .Select(kind => counts[kind] == 1 ? kind : $"{kind} x{counts[kind]}"));
    }

    // Pick a column width that fits every label in the current batch so mixed-kind
    // labels like `call+subscribe` do not overrun the neighbouring column. The
    // minimum matches the historic single-kind width (`instantiate` = 11) with a
    // small buffer so short-label batches still align consistently (issue #501).
    // 現在のバッチ内の全ラベルが収まる列幅を選び、`call+subscribe` のような
    // mixed ラベルが隣接列を押し出さないようにする。最小幅は従来の単一 kind
    // （`instantiate` = 11）と整合するよう余裕付きで設定する（issue #501）。
    private const int ReferenceKindColumnMinWidth = 12;

    private static int ComputeReferenceKindColumnWidth<T>(IEnumerable<T> rows, Func<T, string> labelSelector)
    {
        var max = ReferenceKindColumnMinWidth;
        foreach (var row in rows)
        {
            var label = labelSelector(row);
            if (label != null && label.Length > max)
                max = label.Length;
        }
        return max;
    }

    private static void WriteUsageError(string message, string usage, string hint)
        => CommandErrorWriter.Write(message, hint, usage);

    // Reject queries that were supplied but resolve to empty / whitespace-only text so the user gets
    // a distinct error instead of the generic "<cmd> requires a query argument" message that fires
    // when the positional was actually missing. The null case is left to the existing missing-query
    // checks in each runner (issue #1505).
    // 入力されたクエリが空白のみ・空文字に正規化されたケースを「引数未指定」とは区別して
    // 専用エラーで弾く。null（未指定）は各 runner の既存チェックに委ねる (issue #1505)。
    private static bool TryWriteBlankQueryError(QueryCommandOptions options, string commandName)
    {
        if (options.Query is null)
            return false;
        if (!string.IsNullOrWhiteSpace(options.Query))
            return false;
        WriteUsageError(
            $"{commandName} query cannot be empty or whitespace-only",
            GetUsageLineOrThrow(commandName),
            $"Pass a non-empty value after `{commandName}`; empty or whitespace-only arguments (e.g. `\"\"` or `\"   \"`) are rejected.");
        return true;
    }

    private static void WriteValidationError(string message, string hint)
        => CommandErrorWriter.Write(message, hint);

    private static string StripErrorPrefix(string message)
    {
        const string prefix = "Error: ";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
            return message[prefix.Length..];

        var codedPrefixEnd = message.IndexOf("]: ", StringComparison.Ordinal);
        if (message.StartsWith("Error [", StringComparison.Ordinal) && codedPrefixEnd >= 0)
            return message[(codedPrefixEnd + 3)..];

        return message;
    }

    private static string? ExtractErrorCode(string message)
    {
        const string prefix = "Error [";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var end = message.IndexOf("]: ", StringComparison.Ordinal);
        return end > prefix.Length ? message[prefix.Length..end] : null;
    }

    private static void WriteRepoMapSection(string title, IEnumerable<string> rows)
    {
        var materialized = rows.ToList();
        if (materialized.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (var row in materialized)
            Console.WriteLine($"  {row}");
    }

    /// <summary>
    /// Write actionable hints when a query returns zero results.
    /// 0件時に実行可能なヒントを出力する。
    /// </summary>
    private static void WriteZeroResultHints(QueryCommandOptions options, DbReader reader, string? alternativeHint = null, string? filterHint = null)
    {
        var freshness = reader.GetFreshnessHint();
        if (freshness.FileCount == 0)
        {
            Console.Error.WriteLine("Hint: the index is empty. Run 'cdidx index <projectPath>' first.");
            return;
        }

        if (options.Lang != null || options.PathPatterns.Count > 0 || options.ExcludeTests || options.ExcludePaths.Count > 0)
            Console.Error.WriteLine($"Hint: {filterHint ?? "try removing --lang, --path, --exclude-path, or --exclude-tests to broaden the search."}");

        if (alternativeHint != null)
            Console.Error.WriteLine($"Hint: {alternativeHint}");

        var staleAfter = ResolveStaleAfter(options, Environment.GetEnvironmentVariable(StaleAfterEnvironmentVariable));
        if (staleAfter.Error != null)
        {
            Console.Error.WriteLine(staleAfter.Error);
            return;
        }

        if (freshness.IndexedAt.HasValue)
        {
            var age = GetUtcNow() - freshness.IndexedAt.Value;
            if (age > staleAfter.Value)
                Console.Error.WriteLine($"Hint: the index is {FormatDuration(age)} old (threshold: {FormatDuration(staleAfter.Value)}). Run 'cdidx index <projectPath>' to refresh.");
        }
    }

    private static void WriteExactSubstringHintIfNeeded(SearchQueryHint? hint)
    {
        if (hint == null)
            return;

        Console.Error.WriteLine($"Hint: {hint.SuggestedAction}");
    }

    private static string BuildZeroResultLine(string message, QueryCommandOptions options)
    {
        var context = BuildQueryContextParts(options, includeDefaultLimit: true).ToList();
        if (context.Count == 0)
            return message + ".";

        return $"{message}. ({string.Join(", ", context)})";
    }

    private static IEnumerable<string> BuildQueryContextParts(QueryCommandOptions options, bool includeDefaultLimit)
    {
        if (!string.IsNullOrWhiteSpace(options.Query))
            yield return $"query: \"{options.Query}\"";
        if (options.PathPatterns.Count > 0)
            yield return $"path: {string.Join(", ", options.PathPatterns)}";
        if (options.ExcludePaths.Count > 0)
            yield return $"exclude-path: {string.Join(", ", options.ExcludePaths)}";
        if (options.Lang != null)
            yield return $"lang: {options.Lang}";
        if (options.Kind != null)
            yield return $"kind: {options.Kind}";
        if (options.RankMode != ReferenceRankMode.Weighted)
            yield return $"rank-by: {FormatReferenceRankMode(options.RankMode)}";
        if (options.ExcludeTests)
            yield return "exclude-tests: true";
        if (options.Since.HasValue)
            yield return $"since: {options.Since.Value:O}";
        if (options.CountOnly)
            yield return "count: true";
        if (options.RawFts)
            yield return "fts: true";
        if (options.Exact)
            yield return "exact: true";
        if (options.Prefix)
            yield return "prefix: true";
        if (options.NoDedup)
            yield return "dedup: false";
        if (options.ContextBefore > 0)
            yield return $"before: {options.ContextBefore}";
        if (options.ContextAfter > 0)
            yield return options.ContextAfterExplicit ? $"depth: {options.ContextAfter}" : $"after: {options.ContextAfter}";
        if (includeDefaultLimit || options.Limit != 20)
            yield return $"limit: {options.Limit}";
    }

    private static JsonObject BuildQueryContextJson(QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var query = new JsonObject
        {
            ["limit"] = options.Limit,
        };
        if (!string.IsNullOrWhiteSpace(options.Query))
            query["text"] = options.Query;
        if (options.PathPatterns.Count > 0)
            query["path"] = JsonSerializer.SerializeToNode(options.PathPatterns, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ExcludePaths.Count > 0)
            query["exclude_path"] = JsonSerializer.SerializeToNode(options.ExcludePaths, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.Lang != null)
            query["lang"] = options.Lang;
        if (options.Kind != null)
            query["kind"] = options.Kind;
        if (options.RankMode != ReferenceRankMode.Weighted)
            query["rank_by"] = FormatReferenceRankMode(options.RankMode);
        if (options.ExcludeTests)
            query["exclude_tests"] = true;
        if (options.IncludeGenerated)
            query["include_generated"] = true;
        if (options.Since.HasValue)
            query["since"] = options.Since.Value;
        if (options.CountOnly)
            query["count"] = true;
        if (options.RawFts)
            query["fts"] = true;
        if (options.Exact)
            query["exact"] = true;
        if (options.Prefix)
            query["prefix"] = true;
        if (options.NoDedup)
            query["dedup"] = false;
        if (options.ContextBefore > 0)
            query["before"] = options.ContextBefore;
        if (options.ContextAfter > 0)
            query[options.ContextAfterExplicit ? "depth" : "after"] = options.ContextAfter;
        return query;
    }

    internal static ExactZeroHintResult? BuildExactZeroHint<T>(bool shouldProbe, Func<bool> anyRelaxedMatch, Func<List<T>> relaxedSampleQuery, Func<T, string?> nameSelector)
    {
        return BuildExactZeroHint(shouldProbe, anyRelaxedMatch, relaxedCountQuery: null, relaxedSampleQuery, nameSelector);
    }

    internal static ExactZeroHintResult? BuildExactZeroHint<T>(bool shouldProbe, Func<bool> anyRelaxedMatch, Func<int>? relaxedCountQuery, Func<List<T>> relaxedSampleQuery, Func<T, string?> nameSelector)
    {
        if (!shouldProbe)
            return null;

        if (!anyRelaxedMatch())
            return null;

        int? relaxedCount = null;
        if (relaxedCountQuery != null)
        {
            relaxedCount = relaxedCountQuery();
            if (relaxedCount == 0)
                return null;
        }

        var relaxedResults = relaxedSampleQuery();
        if (relaxedResults.Count == 0)
            return null;

        var sampleNames = relaxedResults
            .Select(nameSelector)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .Select(name => name!)
            .ToList();

        return new ExactZeroHintResult
        {
            RelaxedCount = relaxedCount,
            SampleNames = sampleNames,
            Suggestion = ExactZeroHintResult.DefaultSuggestion,
        };
    }

    private static void AddFreshnessHint(JsonObject payload, DbReader reader)
    {
        var freshness = reader.GetFreshnessHint();
        payload["indexed_file_count"] = freshness.FileCount;
        payload["indexed_at"] = freshness.IndexedAt.HasValue
            ? JsonValue.Create(freshness.IndexedAt.Value)
            : null;
        payload["freshness_available"] = freshness.FreshnessAvailable;
        if (!freshness.FreshnessAvailable && freshness.FreshnessDegradedReason != null)
            payload["freshness_degraded_reason"] = freshness.FreshnessDegradedReason;
    }

    private static JsonObject BuildJsonZeroResultPayload(
        DbReader reader,
        JsonSerializerOptions jsonOptions,
        string? resultsKey = null,
        string? query = null,
        ExactZeroHintResult? exactZeroHint = null,
        FtsQueryDiagnostics? ftsQueryDiagnostics = null,
        bool includeFiles = false,
        bool? graphTableAvailable = null,
        bool? degraded = null,
        ExactQuerySignal? exactSignal = null,
        QueryCommandOptions? queryOptions = null,
        SearchQueryHint? exactSubstringHint = null,
        Action<JsonObject>? extraFields = null)
    {
        var payload = new JsonObject
        {
            ["count"] = 0,
        };

        if (query != null)
            payload["query"] = query;
        if (resultsKey != null)
            payload[resultsKey] = new JsonArray();
        if (includeFiles)
            payload["files"] = 0;
        if (graphTableAvailable.HasValue)
            payload["graph_table_available"] = graphTableAvailable.Value;
        if (degraded.HasValue)
            payload["degraded"] = degraded.Value;
        if (exactSignal.HasValue)
        {
            payload["exact_index_available"] = exactSignal.Value.ExactIndexAvailable;
            if (exactSignal.Value.DegradedReason != null)
                payload["degraded_reason"] = exactSignal.Value.DegradedReason;
        }
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        if (ftsQueryDiagnostics is { HasDegradation: true })
        {
            payload["query_degraded_reason"] = ftsQueryDiagnostics.QueryDegradedReason;
            payload["tokens_dropped"] = JsonSerializer.SerializeToNode(ftsQueryDiagnostics.TokensDropped.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        }
        if (exactSubstringHint != null)
            payload["exact_substring_hint"] = BuildSearchQueryHintJson(exactSubstringHint);
        if (queryOptions != null)
            payload["query_context"] = BuildQueryContextJson(queryOptions, jsonOptions);
        extraFields?.Invoke(payload);
        AddFreshnessHint(payload, reader);

        return payload;
    }

    private static JsonObject BuildSearchQueryHintJson(SearchQueryHint hint) => new()
    {
        ["reason"] = hint.Reason,
        ["suggested_action"] = hint.SuggestedAction,
        ["flag"] = hint.Flag,
        ["mcp_argument"] = hint.McpArgument,
    };

    private static JsonObject BuildGroupedHotspotsZeroJsonPayload(DbReader reader, JsonSerializerOptions jsonOptions, bool countOnly, bool graphAvailable, QueryCommandOptions? queryOptions = null)
    {
        var payload = BuildJsonZeroResultPayload(
            reader,
            jsonOptions,
            resultsKey: countOnly ? null : "hotspots",
            includeFiles: countOnly,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
            queryOptions: queryOptions,
            extraFields: static zeroPayload =>
            {
                zeroPayload["definition_site_total"] = 0;
                zeroPayload["grouped_by"] = HotspotsGroupedByNameKind;
            });
        if (!graphAvailable)
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        return payload;
    }

    private static void WriteExactZeroHint(ExactZeroHintResult? exactZeroHint)
    {
        if (exactZeroHint == null)
            return;

        var examples = exactZeroHint.SampleNames.Count == 0
            ? string.Empty
            : $" (e.g. {string.Join(", ", exactZeroHint.SampleNames.Select(name => $"`{name}`"))})";
        if (exactZeroHint.RelaxedCount.HasValue)
            Console.Error.WriteLine($"Hint: --exact found 0 matches, but substring matching would return {exactZeroHint.RelaxedCount}{examples}. Drop --exact or use the exact indexed name.");
        else
            Console.Error.WriteLine($"Hint: --exact found 0 matches, but substring matching would return results{examples}. Drop --exact or use the exact indexed name.");
    }

    private static bool IsSqlGraphContractSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains(DegradationReasonCodes.SqlGraphContractNotReady, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCSharpCanonicalNameSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains(DegradationReasonCodes.CSharpSymbolNameNotReady, StringComparison.OrdinalIgnoreCase) == true;

    private static int WriteStatusReadinessExplanation(string fieldName)
    {
        var field = FindStatusReadinessField(fieldName);
        if (field == null)
        {
            Console.Error.WriteLine($"Error: unknown status readiness field `{fieldName}`.");
            Console.Error.WriteLine($"Hint: use one of: {string.Join(", ", StatusReadinessFields.Select(f => f.FieldName))}.");
            return CommandExitCodes.UsageError;
        }

        Console.WriteLine($"{field.Label} ({field.FieldName})");
        Console.WriteLine();
        Console.WriteLine($"Ready: {field.ReadyText}");
        Console.WriteLine($"Degraded: {field.DegradedText}");
        Console.WriteLine($"Remediation: {field.Remediation}");
        return CommandExitCodes.Success;
    }

    private static StatusReadinessField? FindStatusReadinessField(string fieldName)
        => StatusReadinessFields.FirstOrDefault(
            field => string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(field.Label, fieldName, StringComparison.OrdinalIgnoreCase));

    private static void WriteStatusReadinessSummary(StatusResult status, QueryCommandOptions options)
    {
        Console.WriteLine("Readiness:");
        foreach (var field in StatusReadinessFields)
        {
            var degraded = IsStatusReadinessFieldDegraded(status, field.FieldName);
            var state = degraded ? "degraded" : "ready";
            Console.WriteLine($"  {field.Label,-32} {state}");

            if (degraded)
            {
                Console.WriteLine($"    {BuildStatusReadinessDegradedDetail(status, options, field.FieldName, field.DegradedText)}");
                Console.WriteLine($"    {BuildStatusReadinessRemediation(status, options, field.FieldName, field.Remediation)}");
            }
        }
    }

    private static bool IsStatusReadinessFieldDegraded(StatusResult status, string fieldName)
        => fieldName switch
        {
            "graph_table_available" => !status.GraphTableAvailable,
            "issues_table_available" => !status.IssuesTableAvailable,
            "file_issues_data_current" => !status.FileIssuesDataCurrent,
            "migration_in_progress" => status.MigrationInProgress,
            "sql_graph_contract_ready" => !status.SqlGraphContractReady,
            "hotspot_family_ready" => !status.HotspotFamilyReady,
            "csharp_symbol_name_ready" => !status.CSharpSymbolNameReady,
            "csharp_metadata_target_ready" => !status.CSharpMetadataTargetReady,
            "fold_ready" => !status.FoldReady,
            "index_newer_than_reader" => status.IndexNewerThanReader,
            _ => false,
        };

    private static string BuildStatusReadinessDegradedDetail(StatusResult status, QueryCommandOptions options, string fieldName, string fallback)
        => fieldName switch
        {
            "sql_graph_contract_ready" => status.SqlGraphContractDegradedReason ?? fallback,
            "hotspot_family_ready" => status.HotspotFamilyDegradedReason ?? fallback,
            "fold_ready" => BuildFoldNotReadyExplanation(status.FoldReadyReason),
            "index_newer_than_reader" => status.IndexNewerThanReaderReason ?? fallback,
            "graph_table_available" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.GraphTableMissing).HumanText,
            "issues_table_available" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.IssuesTableMissing).HumanText,
            "file_issues_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.FileIssuesDataStale).HumanText,
            "migration_in_progress" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.MigrationInProgress).HumanText,
            "csharp_symbol_name_ready" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.CSharpSymbolNameNotReady).HumanText,
            "csharp_metadata_target_ready" => DegradationReasonCodes.GetMetadata(status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady).HumanText,
            _ => fallback,
        };

    private static string BuildStatusReadinessRemediation(StatusResult status, QueryCommandOptions options, string fieldName, string fallback)
        => fieldName switch
        {
            "sql_graph_contract_ready" => $"Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots.",
            "hotspot_family_ready" => $"Run `{BuildHotspotFamilyRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to restamp authoritative hotspot families for every indexed row.",
            "csharp_symbol_name_ready" => $"Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical C# symbol names in place.",
            "fold_ready" => $"Run `{BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit)}` to restamp folded-name columns in place, or `{BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` for a full rebuild.",
            "csharp_metadata_target_ready" => DegradationReasonCodes.GetMetadata(status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady).RecommendedAction,
            "file_issues_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.FileIssuesDataStale).RecommendedAction,
            "migration_in_progress" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.MigrationInProgress).RecommendedAction,
            "index_newer_than_reader" => "Run status with a current cdidx binary, or rebuild the DB with the version you intend to use.",
            _ => fallback,
        };

    private static void ApplyStatusDegradationGuidance(StatusResult status, QueryCommandOptions options)
    {
        var degradations = BuildStatusReadinessDegradations(status, options);
        if (degradations.Count == 0)
            return;

        status.ReadinessDegradations = degradations;
        var primary = degradations[0];
        status.DegradedRootCause = primary.RootCause;
        status.DegradedReason = primary.DegradedReason;
        status.RecommendedAction = primary.RecommendedAction;
        status.AlternativeAction = primary.AlternativeAction;
    }

    private static List<StatusReadinessDegradation> BuildStatusReadinessDegradations(StatusResult status, QueryCommandOptions options)
    {
        var result = new List<StatusReadinessDegradation>();
        if (status.MigrationInProgress)
            result.Add(BuildStatusReadinessDegradation("migration_in_progress", DegradationReasonCodes.MigrationInProgress, options, status));
        if (!status.GraphTableAvailable)
            result.Add(BuildStatusReadinessDegradation("graph_table_available", DegradationReasonCodes.GraphTableMissing, options, status));
        if (!status.IssuesTableAvailable)
            result.Add(BuildStatusReadinessDegradation("issues_table_available", DegradationReasonCodes.IssuesTableMissing, options, status));
        else if (!status.FileIssuesDataCurrent)
            result.Add(BuildStatusReadinessDegradation("file_issues_data_current", DegradationReasonCodes.FileIssuesDataStale, options, status));
        if (!status.SqlGraphContractReady)
            result.Add(BuildStatusReadinessDegradation("sql_graph_contract_ready", DegradationReasonCodes.SqlGraphContractNotReady, options, status));
        if (!status.HotspotFamilyReady)
            result.Add(BuildStatusReadinessDegradation("hotspot_family_ready", DegradationReasonCodes.HotspotFamilyNotReady, options, status));
        if (!status.CSharpSymbolNameReady)
            result.Add(BuildStatusReadinessDegradation("csharp_symbol_name_ready", DegradationReasonCodes.CSharpSymbolNameNotReady, options, status));
        if (!status.CSharpMetadataTargetReady)
            result.Add(BuildStatusReadinessDegradation("csharp_metadata_target_ready", status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady, options, status));
        if (!status.FoldReady)
            result.Add(BuildStatusReadinessDegradation("fold_ready", DegradationReasonCodes.NormalizeFoldReason(status.FoldReadyReason), options, status));
        if (status.IndexNewerThanReader)
            result.Add(BuildStatusReadinessDegradation("index_newer_than_reader", DegradationReasonCodes.IndexNewerThanReader, options, status));
        return result;
    }

    private static StatusReadinessDegradation BuildStatusReadinessDegradation(string field, string rootCause, QueryCommandOptions options, StatusResult status)
    {
        var metadata = DegradationReasonCodes.GetMetadata(rootCause);
        return new StatusReadinessDegradation
        {
            Field = field,
            RootCause = metadata.Code,
            DegradedReason = metadata.HumanText,
            RecommendedAction = field switch
            {
                "fold_ready" => BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit),
                "sql_graph_contract_ready" => BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                "hotspot_family_ready" => BuildHotspotFamilyRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                "csharp_symbol_name_ready" => BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                _ => metadata.RecommendedAction,
            },
            AlternativeAction = field == "fold_ready"
                ? BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)
                : metadata.AlternativeAction,
        };
    }

    private static bool IsStatusDegraded(StatusResult status)
        => !status.GraphTableAvailable
           || !status.IssuesTableAvailable
           || !status.FileIssuesDataCurrent
           || !status.SqlGraphContractReady
           || !status.HotspotFamilyReady
           || !status.CSharpSymbolNameReady
           || !status.CSharpMetadataTargetReady
           || !status.FoldReady
           || status.IndexNewerThanReader
           || status.MigrationInProgress;

    private sealed record StatusCheckFailure(string Name, bool IsStale, string Diagnostic);

    private static IReadOnlyList<StatusCheckFailure> BuildStatusCheckFailures(StatusResult status, IReadOnlySet<string>? scopedChecks)
    {
        var failures = new List<StatusCheckFailure>();
        var checkAll = scopedChecks is not { Count: > 0 };
        bool Includes(string scope) => checkAll || scopedChecks!.Contains(scope);

        if (Includes("workspace"))
        {
            if (status.WorkspaceCheck?.Checked != true)
            {
                failures.Add(new StatusCheckFailure("workspace_unavailable", true, "[stale] workspace_check unavailable"));
            }
            else if (!status.WorkspaceCheck.MatchesWorkspace)
            {
                var check = status.WorkspaceCheck;
                failures.Add(new StatusCheckFailure(
                    "workspace_stale",
                    true,
                    $"[stale] workspace_check reason={check.Reason} changed={check.ChangedFileCount} missing={check.MissingFileCount} unindexed={check.UnindexedFileCount}"));
            }
        }

        if (Includes("graph") && !status.GraphTableAvailable)
            failures.Add(new StatusCheckFailure("graph_table_available", false, "[degraded] graph_table_available=false"));
        if (Includes("issues") && !status.IssuesTableAvailable)
            failures.Add(new StatusCheckFailure("issues_table_available", false, "[degraded] issues_table_available=false"));
        if (Includes("issues") && status.IssuesTableAvailable && !status.FileIssuesDataCurrent)
            failures.Add(new StatusCheckFailure("file_issues_data_current", false, "[degraded] file_issues_data_current=false"));
        if (Includes("workspace") && status.MigrationInProgress)
            failures.Add(new StatusCheckFailure("migration_in_progress", false, "[degraded] migration_in_progress=true"));
        if (Includes("sql") && !status.SqlGraphContractReady)
            failures.Add(new StatusCheckFailure("sql_graph_contract_ready", false, $"[degraded] sql_graph_contract_ready=false reason={status.SqlGraphContractDegradedReason ?? "unknown"}"));
        if (Includes("hotspot") && !status.HotspotFamilyReady)
            failures.Add(new StatusCheckFailure("hotspot_family_ready", false, $"[degraded] hotspot_family_ready=false reason={status.HotspotFamilyDegradedReason ?? "unknown"}"));
        if (Includes("csharp") && !status.CSharpSymbolNameReady)
            failures.Add(new StatusCheckFailure("csharp_symbol_name_ready", false, "[degraded] csharp_symbol_name_ready=false"));
        if (Includes("csharp") && !status.CSharpMetadataTargetReady)
            failures.Add(new StatusCheckFailure("csharp_metadata_target_ready", false, $"[degraded] csharp_metadata_target_ready=false reason={status.CSharpMetadataTargetDegradedReason ?? "unknown"}"));
        if (Includes("fold") && !status.FoldReady)
            failures.Add(new StatusCheckFailure("fold_ready", false, $"[degraded] fold_ready=false reason={status.FoldReadyReason ?? "unknown"}"));
        if (Includes("newer") && status.IndexNewerThanReader)
            failures.Add(new StatusCheckFailure("index_newer_than_reader", false, $"[degraded] index_newer_than_reader=true reason={status.IndexNewerThanReaderReason ?? "unknown"}"));

        return failures;
    }

    private static void WriteStatusCheckDiagnostics(IReadOnlyList<StatusCheckFailure> failures)
    {
        foreach (var failure in failures)
            Console.Error.WriteLine(failure.Diagnostic);
    }

    private static int GetStatusCheckExitCode(IReadOnlyList<StatusCheckFailure> failures)
    {
        var stale = failures.Any(f => f.IsStale);
        var degraded = failures.Any(f => !f.IsStale);
        return (stale, degraded) switch
        {
            (false, false) => CommandExitCodes.Success,
            (true, false) => 1,
            (false, true) => 2,
            _ => 3,
        };
    }

    private static bool IsFoldOnlyReadinessDegraded(StatusResult status)
        => !status.FoldReady
           && status.GraphTableAvailable
           && status.IssuesTableAvailable
           && status.SqlGraphContractReady
           && status.HotspotFamilyReady
           && status.CSharpSymbolNameReady
           && status.CSharpMetadataTargetReady;

    private static bool IsCSharpMetadataTargetOnlyReadinessDegraded(StatusResult status)
        => !status.CSharpMetadataTargetReady
           && status.GraphTableAvailable
           && status.IssuesTableAvailable
           && status.SqlGraphContractReady
           && status.HotspotFamilyReady
           && status.CSharpSymbolNameReady
           && status.FoldReady
           && !status.IndexNewerThanReader;

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => DegradationReasonCodes.BuildFoldNotReadyExplanation(foldReadyReason);

    private static string BuildFoldNotReadyWarning(string? foldReadyReason, string backfillCommand, string rebuildCommand)
        => $"{BuildFoldNotReadyExplanation(foldReadyReason)} Run `{backfillCommand}` to restamp folded-name columns in place, or `{rebuildCommand}` for a full rebuild.";

    private static string BuildStatusFreshnessLabel(StatusResult status)
    {
        if (status.WorkspaceCheck != null)
            return status.WorkspaceCheck.Checked
                ? (status.WorkspaceCheck.MatchesWorkspace ? "fresh" : "stale")
                : "unknown";

        if (!status.IndexedAt.HasValue || !status.LatestModified.HasValue)
            return "unknown";

        if (status.GitIsDirty == true)
            return "stale";

        return status.IndexedAt.Value >= status.LatestModified.Value ? "fresh" : "stale";
    }

    private static void WriteWorkspaceCheck(IndexFreshnessCheckResult check)
    {
        if (!check.Checked)
        {
            Console.WriteLine($"Check   : unavailable ({check.Reason})");
        }
        else if (check.MatchesWorkspace)
        {
            Console.WriteLine($"Check   : matches workspace ({check.MatchedFileCount:N0} files)");
        }
        else
        {
            Console.WriteLine($"Check   : stale ({check.Reason})");
        }

        if (check.ChangedFileCount > 0)
            Console.WriteLine($"  Changed indexed files : {check.ChangedFileCount:N0}{FormatSamples(check.ChangedFiles)}");
        if (check.MissingFileCount > 0)
            Console.WriteLine($"  Missing indexed files : {check.MissingFileCount:N0}{FormatSamples(check.MissingFiles)}");
        if (check.OutsideSparseConeFileCount > 0)
            Console.WriteLine($"  Outside sparse cone : {check.OutsideSparseConeFileCount:N0}{FormatSamples(check.OutsideSparseConeFiles)}");
        if (check.UnindexedFileCount > 0)
            Console.WriteLine($"  Unindexed workspace files : {check.UnindexedFileCount:N0}{FormatSamples(check.UnindexedFiles)}");
        if (check.UnverifiableFileCount > 0)
            Console.WriteLine($"  Unverifiable DB rows : {check.UnverifiableFileCount:N0}{FormatSamples(check.UnverifiableFiles)}");
        if (check.ScanErrorCount > 0)
            Console.WriteLine($"  Scan errors : {check.ScanErrorCount:N0}{FormatSamples(check.ScanErrors)}");
    }

    private static void WriteStatusAge(StatusResult status, TimeSpan staleAfter)
    {
        if (!status.IndexedAt.HasValue)
            return;

        var age = GetUtcNow() - status.IndexedAt.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        Console.WriteLine($"Age     : index is {FormatDuration(age)} old (threshold: {FormatDuration(staleAfter)})");
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalDays = (int)duration.TotalDays;
        var hours = duration.Hours;
        var minutes = duration.Minutes;
        var seconds = duration.Seconds;

        if (totalDays > 0)
            return hours > 0 ? $"{totalDays}d{hours}h" : $"{totalDays}d";
        if (duration.TotalHours >= 1)
            return minutes > 0 ? $"{(int)duration.TotalHours}h{minutes}m" : $"{(int)duration.TotalHours}h";
        if (duration.TotalMinutes >= 1)
            return seconds > 0 ? $"{(int)duration.TotalMinutes}m{seconds}s" : $"{(int)duration.TotalMinutes}m";
        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero))}s";
    }

    private static string FormatHotspotScore(double score) => score.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatSamples(IReadOnlyList<string> samples)
        => samples.Count == 0 ? string.Empty : $" ({string.Join(", ", samples)})";

    private static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "<unknown>";
        return sha.Length <= 12 ? sha : sha[..12];
    }

    private static string BuildFoldBackfillCommand(string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx backfill-fold";

        return $"cdidx backfill-fold --db {QuoteCommandArgument(ResolveWritableDbPathOrPlaceholder(dbPath))}";
    }

    private static string BuildCSharpCanonicalNameRepairCommand(DbReader reader, QueryCommandOptions options)
    {
        var status = reader.GetStatus();
        WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
        return BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
    }

    private static string BuildCSharpCanonicalNameRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit);

    private static string BuildSqlGraphContractRepairCommand(DbReader reader, QueryCommandOptions options)
    {
        var status = reader.GetStatus();
        WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
        return BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
    }

    private static string BuildSqlGraphContractRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit);

    private static string BuildHotspotFamilyRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit, rebuild: true);

    private static string BuildFoldRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
        => BuildReindexRepairCommand(projectRoot, dbPath, dbPathExplicit, rebuild: true);

    private static string BuildReindexRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit, bool rebuild = false)
    {
        var rebuildSuffix = rebuild ? " --rebuild" : string.Empty;
        if (!dbPathExplicit)
            return $"cdidx index .{rebuildSuffix}";

        var resolvedDbPath = ResolveWritableDbPathOrPlaceholder(dbPath);
        var targetProject = string.IsNullOrWhiteSpace(projectRoot)
            ? "<projectPath>"
            : QuoteCommandArgument(projectRoot);
        return $"cdidx index {targetProject} --db {QuoteCommandArgument(resolvedDbPath)}{rebuildSuffix}";
    }

    private static string ResolveWritableDbPathOrPlaceholder(string dbPath)
        => DbPathResolver.TryResolveWritableMutationDbPath(dbPath, out var writableDbPath)
            ? writableDbPath
            : "<writable-db-path>";

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }

    private static void WriteExactSymbolWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            Console.Error.WriteLine($"WARN: --exact symbol query ran without the supporting index ({signal.DegradedReason}). Results are correct but may be slow.");
            Console.Error.WriteLine("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            Console.Error.WriteLine($"WARN: --exact symbol query may return false negatives ({signal.DegradedReason}).");
            Console.Error.WriteLine($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            Console.Error.WriteLine($"WARN: --exact symbol query may return false negatives ({signal.DegradedReason}).");
            Console.Error.WriteLine($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    /// <summary>
    /// Show available symbol kinds when --kind produces zero results.
    /// --kind で 0 件のとき、有効なシンボル種別を表示する。
    /// </summary>
    /// <summary>
    /// Show available languages when --lang produces zero results.
    /// --lang で 0 件のとき、有効な言語を表示する。
    /// </summary>
    private static void WriteLangHint(string? lang, DbReader reader)
    {
        if (lang == null) return;
        var status = reader.GetStatus();
        if (status.Languages.Count > 0 && status.Languages.ContainsKey(lang))
            return;

        if (status.Languages.Count > 0)
            Console.Error.WriteLine($"Hint: '{lang}' not found in index. Available: {string.Join(", ", status.Languages.Keys.OrderBy(l => l))}");

        // Recover from `--lang pythno` / `--lang csarp` typos by suggesting the
        // closest indexed language first; if the typo does not match anything currently
        // in the DB (or the DB has no languages yet) fall back to the full supported set
        // exposed by `ReferenceExtractor.GetSupportedLanguages()` so the suggester is still
        // useful against an empty/fresh index (#1582).
        // `--lang pythno` / `--lang csarp` のようなタイプミスから回復させるため、
        // インデックスに存在する言語の中から最も近いものを優先的に提案する。
        // インデックスに無い、もしくは languages が空の場合は
        // `ReferenceExtractor.GetSupportedLanguages()` 全体から候補を探し、
        // 空のインデックスでも did-you-mean が機能するようにする (#1582)。
        // Skip the suggestion entirely if the closest candidate is the exact value the user
        // already supplied (case-insensitive). FindClosestMatch returns the input verbatim when
        // it is a member of the candidate set — e.g. `--lang java` against a Java-supported but
        // unindexed repo would otherwise self-suggest "Did you mean: --lang java?".
        // 提案候補がユーザー指定値そのものと一致する場合は提案を出さない。
        // FindClosestMatch は候補集合に同名がいれば入力をそのまま返すため、例えば Java は
        // サポート対象だが index 済みでない場合の `--lang java` で自己提案を出してしまう。
        var suggestion = ConsoleUi.FindClosestMatch(lang, status.Languages.Keys)
                         ?? ConsoleUi.FindClosestMatch(lang, ReferenceExtractor.GetSupportedLanguages());
        if (suggestion != null && !string.Equals(suggestion, lang, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"Did you mean: --lang {suggestion}?");
    }

    // All valid symbol kinds emitted by SymbolExtractor / SymbolExtractor が出力する全有効シンボル種別
    private static readonly string[] AllValidKinds =
        KnownSymbolKindFilters.OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
    // Reference kinds valid on `references --kind`. Includes the compile-time type-position
    // `type_reference` edge emitted by ReferenceExtractor for C#/Java base lists, declaration
    // types, generic constraints, `throws`, `is`/`as`/`instanceof`, and XML-doc `cref` targets.
    // C++ `friend` declarations are also accepted because they are extractor-owned dependency
    // edges and participate in graph queries.
    // `references --kind` で有効な reference kind。ReferenceExtractor が C#/Java の継承リスト、
    // 宣言型、generic 制約、`throws`、`is`/`as`/`instanceof`、XML-doc `cref` 対象向けに出力する
    // compile-time な `type_reference` エッジを含む。C++ の `friend` 宣言も extractor が出す
    // dependency edge として受け付け、graph query にも参加させる。
    private static readonly string[] AllValidReferenceKinds =
        ["annotation", "attribute", "augmentation", "call", "consumes_hook", "friend", "import", "instantiate", "razor_event_binding", "subscribe", "type_reference", "unsubscribe"];
    // Reference kinds that `callers` / `callees` can legitimately return. Metadata kinds
    // (`attribute` / `annotation`) and type-position edges (`type_reference`) are structurally
    // not call-graph edges, so those queries are rejected at the CLI / MCP boundary. C++ `friend`
    // is a graph-visible coupling edge.
    // `callers` / `callees` が正しく返せる reference kind。metadata 種別 (`attribute` / `annotation`)
    // や型位置エッジ (`type_reference`) は構造的に call-graph エッジではないため、CLI / MCP 境界で弾く。
    // C++ の `friend` は graph に出す coupling edge。
    private static readonly string[] CallGraphOnlyReferenceKinds =
        ["augmentation", "call", "consumes_hook", "friend", "instantiate", "razor_event_binding", "subscribe", "unsubscribe"];

    private static void WriteKindHint(string? kind, DbReader reader)
    {
        if (kind == null) return;
        if (!AllValidKinds.Contains(kind))
        {
            Console.Error.WriteLine($"Hint: '{kind}' is not a known kind. Available: {string.Join(", ", AllValidKinds)}");
            var suggestion = ConsoleUi.FindClosestMatch(kind, AllValidKinds);
            if (suggestion != null)
                Console.Error.WriteLine($"Did you mean: --kind {suggestion}?");
            return;
        }
        // Kind is valid but not found in this index — hint that no symbols of this kind exist
        // 種別は有効だがインデックスに存在しない場合のヒント
        var existingKinds = reader.GetDistinctKinds();
        if (!existingKinds.Contains(kind))
            Console.Error.WriteLine($"Hint: no '{kind}' symbols in the index. Indexed kinds: {string.Join(", ", existingKinds)}");
    }

    private static void WriteValidateKindHint(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return;
        if (AllValidValidateKinds.Contains(kind, StringComparer.Ordinal))
            return;

        // `validate --kind` accepts only the file-issue kinds emitted by FileIndexer. A typo
        // like `--kind replacement_chra` filters to zero rows, which previously printed the
        // same "No encoding issues found." message as a genuinely clean repo and silently
        // hid the typo. Surface a hint + suggester for the closest known kind (#1582).
        // `validate --kind` は FileIndexer が出す file_issues kind のみ受理する。
        // `--kind replacement_chra` のようなタイプミスは 0 行となり、クリーンな状態と区別が
        // つかないまま暗黙に握り潰されていた。ヒントと did-you-mean を出すよう改修 (#1582)。
        Console.Error.WriteLine($"Hint: '{kind}' is not a known validate kind. Available: {string.Join(", ", AllValidValidateKinds)}");
        var suggestion = ConsoleUi.FindClosestMatch(kind, AllValidValidateKinds);
        if (suggestion != null)
            Console.Error.WriteLine($"Did you mean: --kind {suggestion}?");
    }

    private static void WriteGraphReferenceKindHint(string command, string? kind, bool json)
    {
        if (json || string.IsNullOrWhiteSpace(kind))
            return;

        // `references` accepts all reference kinds emitted by the extractor; `callers` / `callees`
        // are restricted to call-graph kinds. Pick the right acceptance set per command.
        // `references` は extractor が出す全 reference kind を受け付ける。`callers` / `callees` は
        // call-graph 種別のみ。コマンドごとに許容集合を使い分ける。
        var acceptedKinds = command == "references" ? AllValidReferenceKinds : CallGraphOnlyReferenceKinds;
        if (acceptedKinds.Contains(kind))
            return;

        if (AllValidKinds.Contains(kind))
        {
            Console.Error.WriteLine($"WARN: '{kind}' is a symbol kind, but --kind on '{command}' filters by reference kind ({string.Join(", ", acceptedKinds)}). Use symbols/definition/hotspots/unused to filter by symbol kind.");
            return;
        }

        Console.Error.WriteLine($"Hint: '{kind}' is not a known reference kind for '{command}'. Available reference kinds: {string.Join(", ", acceptedKinds)}");
        var suggestion = ConsoleUi.FindClosestMatch(kind, acceptedKinds);
        if (suggestion != null)
            Console.Error.WriteLine($"Did you mean: --kind {suggestion}?");
    }

    // Reference kinds that are valid `references --kind` values but NOT valid
    // `callers --kind` / `callees --kind` values.
    // - `attribute` / `annotation`: metadata rows are attributed to the enclosing body-range
    //   symbol rather than the annotated target itself, so `callers Obsolete --kind attribute`
    //   and equivalent `callees` queries return structurally wrong answers (method-level
    //   metadata reported under the enclosing class; file-level targets such as
    //   `[assembly: ...]` drop entirely because `container_name` is null).
    // - `type_reference`: type-position edges are compile-time references, not runtime calls,
    //   so `callers Foo --kind type_reference` misreports type mentions as caller edges
    //   (declaration types, generic constraints, `is`/`as`, XML-doc `cref`, etc.).
    // Reject these kinds at the CLI boundary and redirect users to
    // `references --kind <kind>` (which IS correct).
    // `references --kind` では有効だが、`callers --kind` / `callees --kind` では
    // 使ってはいけない reference kind。
    // - `attribute` / `annotation`: metadata 行は注釈対象そのものではなく body-range 上の
    //   外側シンボルに帰属するため、`callers` / `callees` でこの kind を受け付けると
    //   構造的に誤答する（メソッドレベルは外側クラスに寄り、`[assembly: ...]` のような
    //   ファイルレベルは `container_name = null` で丸ごと消える）。
    // - `type_reference`: 型位置エッジは compile-time な参照であり実行時呼び出しではない。
    //   `callers Foo --kind type_reference` は宣言型や generic 制約、`is`/`as`、XML-doc `cref`
    //   などの型言及を caller edge として誤って返す。
    // - `import`: import/include dependency edges are structural, not call-graph edges.
    // CLI 境界で弾き、正しい列挙パスである `references --kind <kind>` に誘導する。
    private static readonly HashSet<string> NonCallGraphReferenceKinds = new(StringComparer.Ordinal)
    {
        "attribute", "annotation", "type_reference", "import",
    };

    /// <summary>
    /// Reject non-call-graph reference kinds (`attribute` / `annotation` / `type_reference` / `import`) on
    /// commands (`callers` / `callees`) whose data model cannot answer those queries correctly.
    /// Returns true if the kind was rejected; the caller should then return
    /// `CommandExitCodes.UsageError`.
    /// `callers` / `callees` のようにデータモデル的に metadata / 型位置参照に答えられない
    /// コマンドで `--kind attribute` / `--kind annotation` / `--kind type_reference` / `--kind import` を弾く。
    /// 弾いた場合 true を返すので、呼び出し側は `CommandExitCodes.UsageError` を返すこと。
    /// </summary>
    private static bool TryRejectNonCallGraphKindForGraphCommand(string command, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || !NonCallGraphReferenceKinds.Contains(kind))
            return false;

        if (kind == "type_reference")
            Console.Error.WriteLine($"Error: '--kind type_reference' is not supported on '{command}'. Type-position references are compile-time edges (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so `{command} --kind type_reference` cannot return accurate call-graph rows.");
        else if (kind == "import")
            Console.Error.WriteLine($"Error: '--kind import' is not supported on '{command}'. Import references are structural dependency edges, not runtime calls, so `{command} --kind import` cannot return accurate call-graph rows.");
        else
            Console.Error.WriteLine($"Error: '--kind {kind}' is not supported on '{command}'. Metadata references are attributed to the enclosing body-range symbol rather than the annotated target, so `{command} --kind {kind}` cannot return accurate rows (file-level targets such as `[assembly: ...]` drop entirely).");
        Console.Error.WriteLine($"Hint: use `cdidx references <name> --kind {kind}` instead.");
        return true;
    }

    private static void WriteGraphSupportHint(string? lang)
    {
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            Console.Error.WriteLine($"Note: call-graph queries are not indexed for '{lang}'. Use search, definition, excerpt, or files instead.");
    }

    private static void WriteImpactResolutionHint(ImpactAnalysisResult analysis)
    {
        if (analysis.DefinitionCount > 0)
        {
            var kinds = string.Join(", ", analysis.Definitions.Select(d => d.Kind).Distinct().OrderBy(k => k));
            var pathPreview = analysis.Definitions
                .Select(d => d.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            var extra = analysis.DefinitionFileCount > pathPreview.Count
                ? $" (+{analysis.DefinitionFileCount - pathPreview.Count} more)"
                : string.Empty;
            Console.Error.WriteLine($"Note: '{analysis.Query}' resolved to '{analysis.ResolvedName}' ({kinds}) as {ConsoleUi.Counted(analysis.DefinitionCount, "definition")} across {ConsoleUi.Counted(analysis.DefinitionFileCount, "file")}: {string.Join(", ", pathPreview)}{extra}");
        }
        else if (analysis.ZeroResultReason == "no_matching_definition")
        {
            Console.Error.WriteLine($"Note: no indexed definition matched '{analysis.Query}'.");
        }

        if (!string.IsNullOrWhiteSpace(analysis.Suggestion))
            Console.Error.WriteLine($"Hint: {analysis.Suggestion}");
    }

    // Emit a zero-result payload that distinguishes "real 0 hits" from "graph table missing
    // (degraded)". Without this, AI agents and humans cannot tell the index from a legacy /
    // read-only DB apart from a DB that genuinely has no callers for the query.
    // graph テーブル欠損による 0 と本物の 0 を JSON で区別できるようにする。
    private static void WriteDegradedGraphZeroResult(DbReader reader, string resultsKey, bool json, bool graphAvailable, JsonSerializerOptions jsonOptions,
        ExactQuerySignal? exactSignal = null, QueryCommandOptions? queryOptions = null, Action<JsonObject>? extraFields = null)
    {
        if (graphAvailable) return;
        if (json)
        {
            var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: false, degraded: true, exactSignal: exactSignal, queryOptions: queryOptions, extraFields: extraFields);
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
            Console.WriteLine(payload.ToJsonString(jsonOptions));
        }
        else
        {
            Console.Error.WriteLine("WARN: symbol_references table missing — this 0-result is degraded, not authoritative.");
        }
    }

    private static void WriteExactGraphWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            Console.Error.WriteLine($"WARN: --exact graph query ran without the supporting index ({signal.DegradedReason}). Results are correct but may be slow.");
            Console.Error.WriteLine("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            Console.Error.WriteLine($"WARN: --exact graph query may return false negatives ({signal.DegradedReason}).");
            Console.Error.WriteLine($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            Console.Error.WriteLine($"WARN: --exact graph query may return false negatives ({signal.DegradedReason}).");
            Console.Error.WriteLine($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    private static void WriteExactBundleWarningIfNeeded(bool exact, bool json, ExactQuerySignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (!exact || json || signal.ExactIndexAvailable || signal.DegradedReason == null)
            return;

        if (signal.HasMissingIndex)
        {
            Console.Error.WriteLine($"WARN: --exact inspect bundle ran without all supporting indexes ({signal.DegradedReason}). Results are correct but may be slow.");
            Console.Error.WriteLine("Hint: re-index with `cdidx index <projectPath>` to upgrade the DB layout.");
            return;
        }

        if (IsCSharpCanonicalNameSignal(signal))
        {
            Console.Error.WriteLine($"WARN: --exact inspect bundle may return false negatives ({signal.DegradedReason}).");
            Console.Error.WriteLine($"Hint: run `{BuildCSharpCanonicalNameRepairCommand(reader, options)}` to refresh canonical C# symbol names.");
            return;
        }

        if (IsSqlGraphContractSignal(signal))
        {
            Console.Error.WriteLine($"WARN: --exact inspect bundle may return false negatives ({signal.DegradedReason}).");
            Console.Error.WriteLine($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows.");
        }
    }

    private static void WriteGraphCountResult(DbReader reader, int count, int files, QueryCommandOptions options, JsonSerializerOptions jsonOptions,
        bool graphAvailable, ExactQuerySignal exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        if (!options.Json)
        {
            Console.WriteLine($"{count}");
            WriteGraphSupportOverrideHint(graphSupportOverride);
            if (!graphAvailable)
                Console.Error.WriteLine("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
            return;
        }

        var payload = new JsonObject
        {
            ["count"] = count,
            ["files"] = files,
            ["graph_table_available"] = graphAvailable,
        };
        if (!graphAvailable)
            payload["degraded"] = true;
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (options.Exact || options.ExactName)
            AddExactGraphJsonFields(payload, exactSignal);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        extraFields?.Invoke(payload);
        if (count == 0)
            AddFreshnessHint(payload, reader);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphZeroJsonResult(DbReader reader, string resultsKey, JsonSerializerOptions jsonOptions, bool graphAvailable,
        ExactQuerySignal? exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, QueryCommandOptions? queryOptions = null, Action<JsonObject>? extraFields = null)
    {
        var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: graphAvailable, queryOptions: queryOptions);
        if (!graphAvailable)
        {
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (exactSignal != null)
            AddExactGraphJsonFields(payload, exactSignal.Value);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphJsonResult<T>(T result, JsonTypeInfo<T> jsonTypeInfo, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        AddExactGraphJsonFields(payload, exactSignal);
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResult<T>(T result, JsonTypeInfo<T> jsonTypeInfo, JsonSerializerOptions jsonOptions, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResultWithExactSignal<T>(T result, JsonTypeInfo<T> jsonTypeInfo, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        AddExactJsonFields(payload, exactSignal);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void AddExactGraphJsonFields(JsonObject payload, ExactQuerySignal exactSignal)
    {
        AddExactJsonFields(payload, exactSignal);
    }

    private static void AddExactJsonFields(JsonObject payload, ExactQuerySignal exactSignal)
    {
        payload["exact_index_available"] = exactSignal.ExactIndexAvailable;
        if (exactSignal.DegradedReason != null)
            payload["degraded_reason"] = exactSignal.DegradedReason;
    }

    private static void AddGraphSupportOverrideFields(JsonObject payload, GraphSupportOverride? graphSupportOverride)
    {
        if (graphSupportOverride == null)
            return;

        if (graphSupportOverride.GraphLanguage != null)
            payload["graph_language"] = graphSupportOverride.GraphLanguage;
        if (graphSupportOverride.GraphSupported.HasValue)
            payload["graph_supported"] = graphSupportOverride.GraphSupported.Value;
        if (graphSupportOverride.GraphSupportReason != null)
            payload["graph_support_reason"] = graphSupportOverride.GraphSupportReason;
        if (graphSupportOverride.GraphDegraded)
            payload["graph_degraded"] = true;
        if (graphSupportOverride.UnsupportedSymbolKind != null)
            payload["unsupported_symbol_kind"] = graphSupportOverride.UnsupportedSymbolKind;
    }

    private static void AddImpactOptionWarnings(JsonObject payload, QueryCommandOptions options)
    {
        if (!options.ImpactDeprecatedDepthUsed)
            return;

        JsonArray warnings;
        if (payload["warnings"] is JsonArray existingWarnings)
        {
            warnings = existingWarnings;
        }
        else
        {
            warnings = [];
            payload["warnings"] = warnings;
        }

        warnings.Add("--depth is deprecated for impact; use --max-hops instead.");
    }

    private static void WriteGraphSupportOverrideHint(GraphSupportOverride? graphSupportOverride)
    {
        if (graphSupportOverride == null)
            return;

        Console.Error.WriteLine($"Note: {graphSupportOverride.GraphSupportReason}");
    }

    private sealed record GraphSupportOverride(
        string? GraphLanguage,
        bool? GraphSupported,
        string GraphSupportReason,
        string? UnsupportedSymbolKind,
        bool GraphDegraded);

    private static void AddHotspotFamilyJsonFields(JsonObject payload, HotspotFamilySignal signal)
    {
        payload["hotspot_family_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
                payload["hotspot_family_degraded_reason"] = signal.DegradedReason;
        }
    }

    private static void WriteHotspotFamilyWarningIfNeeded(bool json, HotspotFamilySignal signal)
    {
        if (json || signal.Ready || signal.DegradedReason == null)
            return;

        Console.Error.WriteLine($"WARN: {signal.DegradedReason}");
        Console.Error.WriteLine("Hint: rerun `cdidx index <projectPath>` to restore authoritative cross-file hotspot families.");
    }

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignal(SqlGraphContractSignal signal, bool relevant)
    {
        if (!signal.Relevant || relevant)
            return signal;

        return new SqlGraphContractSignal(Ready: true, Relevant: false, DegradedReason: null);
    }

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignalByLanguages(
        SqlGraphContractSignal signal,
        IEnumerable<string?> langs,
        params string?[] additionalLangs)
        => NarrowSqlGraphContractSignal(
            signal,
            additionalLangs.Any(DbReader.IsSqlLanguage) || DbReader.ContainsSqlLanguage(langs));

    internal static SqlGraphContractSignal NarrowSqlGraphContractSignalByPaths(
        DbReader reader,
        SqlGraphContractSignal signal,
        IEnumerable<string> paths,
        params string?[] additionalLangs)
        => NarrowSqlGraphContractSignal(
            signal,
            additionalLangs.Any(DbReader.IsSqlLanguage) || reader.AnyFilePathHasLanguage(paths, "sql"));

    private static void AddSqlGraphContractJsonFields(JsonObject payload, SqlGraphContractSignal signal)
    {
        if (!signal.Relevant)
            return;

        payload["sql_graph_contract_ready"] = signal.Ready;
        if (!signal.Ready)
        {
            payload["degraded"] = true;
            if (signal.DegradedReason != null)
                payload["sql_graph_contract_degraded_reason"] = signal.DegradedReason;
        }
    }

    private static void WriteSqlGraphContractWarningIfNeeded(bool json, SqlGraphContractSignal signal, DbReader reader, QueryCommandOptions options)
    {
        if (json || !signal.Relevant || signal.Ready || signal.DegradedReason == null)
            return;

        Console.Error.WriteLine($"WARN: {signal.DegradedReason}");
        Console.Error.WriteLine($"Hint: run `{BuildSqlGraphContractRepairCommand(reader, options)}` to refresh SQL graph rows before trusting SQL graph/dependency results.");
    }

    // Per-flag upper bounds for numeric CLI options. Without a cap, `--limit 2147483647` or
    // `--snippet-lines 999999` previously parsed silently and either ran with the absurd value
    // (huge allocations / output) or got quietly clamped (e.g. snippet-lines down to 20 with no
    // signal), hiding typos from users. Each cap below is the documented user-facing maximum.
    // 数値 CLI フラグごとの上限値。上限が無いと `--limit 2147483647` や
    // `--snippet-lines 999999` が黙って通り、巨大確保/出力をそのまま走らせるか silent に clamp
    // されてユーザーのタイポを隠していた。下の値は各フラグのドキュメント上の最大値。
    internal static readonly IReadOnlyDictionary<string, int> NumericFlagUpperBounds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["--limit"] = 10_000,
            ["--snippet-lines"] = SearchSnippetFormatter.MaxSnippetLines,
            ["--max-line-width"] = LineWidthFormatter.MaxAllowedLineWidth,
            ["--slow-query-ms"] = 3_600_000,
            ["--max-hops"] = 64,
            ["--depth"] = 64,
            ["--before"] = 1_000,
            ["--after"] = 1_000,
            ["--start"] = 10_000_000,
            ["--end"] = 10_000_000,
            ["--focus-line"] = 10_000_000,
            ["--focus-column"] = 100_000,
            ["--focus-length"] = 100_000,
        };

    // Per-flag hints appended to "Error: <flag> requires a value." so users learn the expected
    // value type or range without consulting `--help`. Routed through BuildMissingOptionValueError
    // so every missing-value site reuses the same table and the messages stay consistent.
    // 「<flag> requires a value.」 missing-value error に追記するフラグ別ヒント。
    // すべての missing-value 経路を BuildMissingOptionValueError 経由にして、コマンド間で
    // メッセージを揃え、ヒントの単一情報源を維持する。
    private static readonly Dictionary<string, string> MissingOptionValueHints = new(StringComparer.Ordinal)
    {
        ["--db"] = "pass a path to a CodeIndex SQLite database, e.g. `--db .cdidx/codeindex.db` or `--db file:///absolute/path/to/codeindex.db?immutable=1`, or omit `--db` to use `.cdidx/codeindex.db`.",
        ["--workspace-db"] = "pass a path to another workspace member CodeIndex SQLite database. Repeat the flag to aggregate multiple member DBs.",
        ["--data-dir"] = "pass a directory where cdidx should store `codeindex.db`, e.g. `--data-dir /var/cache/cdidx`.",
        ["--limit"] = "pass a positive integer, e.g. `--limit 20` (default 20).",
        ["--top"] = "pass a positive integer, e.g. `--top 20` (alias for `--limit`, default 20).",
        ["--lang"] = "pass a language identifier, e.g. `--lang csharp`. Run `cdidx languages` for the supported set.",
        ["--query"] = "pass a search literal, e.g. `--query \"authenticate\"`. Use the `--query` form when the literal starts with `-`.",
        ["--kind"] = "pass a kind identifier, e.g. `--kind function`. definition/symbols/hotspots/unused take a symbol kind; references/callers/callees take a reference kind such as `call`, `instantiate`, or `subscribe`. Run the command's `--help` for the kind list.",
        ["--visibility"] = "pass one or more of public, protected, internal, private, e.g. `--visibility public,internal`.",
        ["--exclude-visibility"] = "pass one or more of public, protected, internal, private to exclude, e.g. `--exclude-visibility private`.",
        ["--rank-by"] = "pass `weighted`, `count`, or `kind` (callers/callees only).",
        ["--max-hops"] = "pass a non-negative integer, e.g. `--max-hops 5` (default 5).",
        ["--depth"] = "deprecated alias for `--max-hops`; pass a non-negative integer, e.g. `--max-hops 5` (default 5).",
        ["--path"] = "pass a glob-style path pattern, e.g. `--path src/**`. Repeat `--path` to add more patterns.",
        ["--exclude-path"] = "pass a glob-style path pattern to exclude, e.g. `--exclude-path tests/**`. Repeat `--exclude-path` to add more.",
        ["--since"] = "pass an ISO 8601 datetime, e.g. `--since 2024-01-01` or `--since 2024-01-01T00:00:00Z`.",
        ["--start"] = "pass a 1-based line number, e.g. `--start 10`.",
        ["--end"] = "pass a 1-based line number greater than or equal to `--start`, e.g. `--end 20`.",
        ["--before"] = "pass a non-negative integer of context lines before each match, e.g. `--before 2`.",
        ["--after"] = "pass a non-negative integer of context lines after each match, e.g. `--after 2`.",
        ["--focus-line"] = "pass a 1-based line number to focus on, e.g. `--focus-line 12`.",
        ["--focus-column"] = "pass a 1-based column number to keep visible, e.g. `--focus-column 80`.",
        ["--focus-length"] = "pass a positive integer for the focused span width, e.g. `--focus-length 1` (default 1).",
        ["--name"] = "pass a literal symbol name, e.g. `--name UserService`. Repeat `--name` to add more names.",
        ["--snippet-lines"] = "pass an integer between 1 and 20, e.g. `--snippet-lines 8` (default 8).",
        ["--snippet-focus"] = "pass one of `leftmost`, `quality`, or `proximity`, e.g. `--snippet-focus quality` (default quality).",
        ["--max-line-width"] = "pass a non-negative integer (`0` disables clamping), e.g. `--max-line-width 512` (default 512).",
        ["--stale-after"] = "pass a compact positive duration, e.g. `--stale-after 30m`, `--stale-after 2h`, or `--stale-after 7d`.",
        ["--slow-query-ms"] = "pass a non-negative millisecond threshold, e.g. `--slow-query-ms 500`; use 0 to log every profiled SQL statement.",
        ["--min-entrypoint-confidence"] = "pass a decimal from 0.0 through 1.0, e.g. `--min-entrypoint-confidence 0.6`.",
        ["--sections"] = "pass a comma-separated map section list, e.g. `--sections tree,languages`. Supported sections: tree, languages, hotspots, metrics.",
    };

    // Build a missing-value error string with optional caller-supplied hint lines first, then the
    // per-flag hint from MissingOptionValueHints. Newline-separated so each Hint stays on its own
    // line when written via Console.Error.WriteLine. Returns just the base error if no hint exists.
    // 呼び出し元固有のヒント (例: inline-form) を先に、テーブル由来のフラグ別ヒントを後ろに追記する。
    // Console.Error.WriteLine 経由で出力されたとき各 Hint が別行になるよう改行で連結する。
    private static string BuildMissingOptionValueError(string optionName, params string?[] extraHintLines)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Error: ").Append(optionName).Append(" requires a value.");
        foreach (var hint in extraHintLines)
        {
            if (string.IsNullOrEmpty(hint))
                continue;
            sb.Append('\n').Append(hint);
        }
        if (MissingOptionValueHints.TryGetValue(optionName, out var perFlagHint))
            sb.Append('\n').Append("Hint: ").Append(perFlagHint);
        return sb.ToString();
    }

    private static int ResolveDefaultPositiveInt(string environmentVariable, int fallback, string optionName, out string? error)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return fallback;
        }

        if (TryParsePositiveInt(raw, optionName, out var value, out var parseError))
        {
            error = null;
            return value;
        }

        error = parseError!.Replace(optionName, environmentVariable, StringComparison.Ordinal);
        return fallback;
    }

    private static int ResolveDefaultNonNegativeInt(string environmentVariable, int fallback, string optionName, out string? error)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return fallback;
        }

        if (TryParseNonNegativeInt(raw, optionName, out var value, out var parseError))
        {
            error = null;
            return value;
        }

        error = parseError!.Replace(optionName, environmentVariable, StringComparison.Ordinal);
        return fallback;
    }

    private static bool TryParsePositiveInt(string rawValue, string optionName, out int value, out string? error)
    {
        if (string.Equals(optionName, "--max-line-width", StringComparison.Ordinal))
            return TryParseNonNegativeInt(rawValue, optionName, out value, out error);

        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            value = 0;
            error = BuildPositiveIntegerError(optionName, rawValue);
            return false;
        }

        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && value > maxAllowed)
        {
            error = BuildPositiveIntegerUpperBoundError(optionName, rawValue, maxAllowed);
            value = 0;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseNonNegativeInt(string rawValue, string optionName, out int value, out string? error)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            value = 0;
            error = BuildNonNegativeIntegerError(optionName, rawValue);
            return false;
        }

        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && value > maxAllowed)
        {
            error = BuildNonNegativeIntegerUpperBoundError(optionName, rawValue, maxAllowed);
            value = 0;
            return false;
        }

        error = null;
        return true;
    }

    private static string BuildPositiveIntegerError(string optionName, string rawValue, string? displayOptionName = null)
    {
        displayOptionName ??= optionName;
        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed))
            return $"Error: {displayOptionName} requires an integer between 1 and {maxAllowed}, got '{rawValue}'. Hint: retry with `{displayOptionName} 1` or another value up to {maxAllowed}.";
        return $"Error: {displayOptionName} requires a positive integer, got '{rawValue}'. Hint: retry with `{displayOptionName} 1` or another positive integer.";
    }

    private static string BuildPositiveIntegerUpperBoundError(string optionName, string rawValue, int maxAllowed)
    {
        return $"Error: {optionName} must be less than or equal to {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} {maxAllowed}` or a smaller positive integer.";
    }

    private static string BuildNonNegativeIntegerError(string optionName, string rawValue)
    {
        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed))
            return $"Error: {optionName} requires an integer between 0 and {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} 0` or another value up to {maxAllowed}.";
        return $"Error: {optionName} requires a non-negative integer, got '{rawValue}'. Hint: retry with `{optionName} 0` or another non-negative integer.";
    }

    private static string BuildNonNegativeIntegerUpperBoundError(string optionName, string rawValue, int maxAllowed)
    {
        return $"Error: {optionName} must be less than or equal to {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} {maxAllowed}` or a smaller non-negative integer.";
    }

    private static bool TryReadRawOptionValue(string[] args, ref int index, string optionName, string? inlineValue, out string? value, out string? error)
    {
        if (inlineValue != null)
        {
            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        var candidate = args[index + 1];
        // If the next token is itself a recognized CLI option, treat this as a missing-value
        // case rather than consuming the option as if it were a value. Without this guard
        // `--limit --lang rust` was parsed as `--limit=--lang` (numeric-parse failure) and then
        // the trailing `rust` was silently dropped, leaving the user with a confusing message
        // about `--lang` being an invalid integer.
        // 次トークンが別の既知オプションなら「値欠如」として扱い、index を進めない。これを
        // 入れないと `--limit --lang rust` が `--limit=--lang` と解釈され、後続の `rust` が
        // 黙って捨てられ、`--lang` が integer じゃないという混乱したメッセージが出てしまう。
        if (IsRecognizedOptionToken(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    private static bool TryReadStringOptionValue(string[] args, ref int index, string optionName, string? inlineValue, bool allowSeparatedDashPrefixedLiteralValue, out string? value, out string? error)
    {
        if (inlineValue != null)
        {
            if (string.IsNullOrWhiteSpace(inlineValue))
            {
                value = null;
                error = BuildMissingOptionValueError(optionName);
                return false;
            }

            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        var candidate = args[index + 1];
        // Apply the recognized-option guard only when the option does NOT legitimately accept
        // separated dash-prefixed literal values. For flags like `--lang` / `--kind` / `--since`
        // / `--name` (allowSeparatedDashPrefixedLiteralValue=false), `--lang --limit 5` must stop
        // at `--limit` instead of consuming a known CLI flag as the `--lang` value. For flags like
        // `--db` / `--path` / `--exclude-path` / `--query` (allowSeparatedDashPrefixedLiteralValue=true),
        // skip this guard so the downstream `IsRejectedSeparatedStringValue` can emit the
        // inline-form hint for double-dash literals, preserving the pre-existing contract.
        // dash-prefix ヒューリスティックより前に既知オプション判定を置くが、この guard は
        // `allowSeparatedDashPrefixedLiteralValue=false` の時だけ適用する。`--lang` / `--kind` /
        // `--since` / `--name` は `--lang --limit 5` のとき `--limit` を値として飲み込まず値欠如
        // として扱う。`--db` / `--path` / `--exclude-path` / `--query` は dashed literal を受け入れる
        // 設計なので対象外とし、後段の `IsRejectedSeparatedStringValue` 側で double-dash に対する
        // inline-form ヒントを返して既存契約を維持する。
        if (!allowSeparatedDashPrefixedLiteralValue && IsRecognizedOptionToken(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }
        if (optionName != "--query" && IsRejectedSeparatedStringValue(candidate, allowSeparatedDashPrefixedLiteralValue))
        {
            value = null;
            var inlineFormHint = allowSeparatedDashPrefixedLiteralValue && candidate.StartsWith("--", StringComparison.Ordinal)
                ? $"Hint: if the literal value starts with `--`, pass it as `{optionName}=<value>`."
                : null;
            error = BuildMissingOptionValueError(optionName, inlineFormHint);
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            value = null;
            error = BuildMissingOptionValueError(optionName);
            return false;
        }

        index++;
        value = candidate;
        error = null;
        return true;
    }

    private static bool IsRejectedSeparatedStringValue(string candidate, bool allowSeparatedDashPrefixedLiteralValue)
    {
        if (!candidate.StartsWith("-", StringComparison.Ordinal))
            return false;

        if (!allowSeparatedDashPrefixedLiteralValue)
            return true;

        return candidate.StartsWith("--", StringComparison.Ordinal);
    }

    private static bool IsRecognizedOptionToken(string value) =>
        ValueTakingOptions.Contains(value) || FlagOnlyOptions.Contains(value);

    private static bool IsBareVerbatimQueryToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '@');
    }

    private static bool TrySplitInlineOptionValue(string token, out string? optionName)
    {
        optionName = null;
        var separator = token.IndexOf('=');
        if (separator <= 0)
            return false;

        var candidate = token[..separator];
        if (!InlineValueOptions.Contains(candidate))
            return false;

        optionName = candidate;
        return true;
    }

    // Accepted ISO 8601 formats for --since / --sinceフィルタで受け付けるISO 8601書式
    private static readonly string[] Iso8601Formats =
    [
        // date only / 日付のみ
        "yyyy-MM-dd",
        // minute precision / 分精度
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mmZ",
        "yyyy-MM-ddTHH:mmzzz",
        // second precision / 秒精度
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:sszzz",
        // fractional seconds (1-7 digits via 'F') / 小数秒（1-7桁、'F'で可変長）
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        // round-trip format / ラウンドトリップ書式
        "o",
    ];

    /// <summary>
    /// Parse a --since value using invariant ISO 8601 formats only.
    /// Rejects ambiguous locale-dependent formats like MM/dd/yyyy.
    /// Offsetless inputs are treated as UTC so the same `--since 2024-01-01T00:00:00`
    /// resolves to the same logical UTC moment regardless of the caller's timezone
    /// (Issue #1545). Append `Z` or an explicit offset (`+09:00`) to opt out.
    /// ISO 8601形式のみで--since値をパースする。MM/dd/yyyyなどロケール依存の曖昧な形式は拒否する。
    /// オフセットなしの入力はUTCとして扱い、呼び出し側のタイムゾーンに依らず同じUTC時点になる
    /// （Issue #1545）。明示的にオフセットを付けたい場合は `Z` または `+09:00` 等を付与する。
    /// </summary>
    internal static bool TryParseIso8601Since(string value, out DateTime result)
    {
        if (DateTimeOffset.TryParseExact(value, Iso8601Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            result = dto.UtcDateTime;
            return true;
        }
        result = default;
        return false;
    }

    public static string FormatReferenceRankMode(ReferenceRankMode mode) => mode switch
    {
        ReferenceRankMode.Count => "count",
        ReferenceRankMode.Kind => "kind",
        _ => "weighted",
    };
}

public sealed class QueryCommandOptions
{
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool DbPathExplicit { get; init; }
    public bool ReadOnly { get; init; }
    public string? DataDir { get; init; }
    public string? DataDirSource { get; init; }
    public bool Json { get; init; }
    public string JsonOutputFormat { get; init; } = "ndjson";
    public string OutputFormat { get; init; } = "text";
    public int Limit { get; init; } = 20;
    public string? Lang { get; init; }
    public string? Kind { get; init; }
    public List<string> VisibilityFilters { get; init; } = [];
    public List<string> ExcludeVisibilityFilters { get; init; } = [];
    public string? Query { get; init; }
    public bool RawFts { get; init; }
    public bool IncludeBody { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public bool ContextAfterExplicit { get; init; }
    public bool ImpactDeprecatedDepthUsed { get; init; }
    public int? FocusLine { get; init; }
    public int? FocusColumn { get; init; }
    public int FocusLength { get; init; } = 1;
    public int SnippetLines { get; init; } = SearchSnippetFormatter.DefaultSnippetLines;
    public SearchSnippetFocusMode SnippetFocus { get; init; } = SearchSnippetFocusMode.Quality;
    public int MaxLineWidth { get; init; } = LineWidthFormatter.DefaultMaxLineWidth;
    public List<string> PathPatterns { get; init; } = [];
    public List<string> WorkspaceDbPaths { get; init; } = [];
    public List<string> ProjectFilters { get; init; } = [];
    public string? SolutionFilter { get; init; }
    public List<string> ExcludePaths { get; init; } = [];
    public bool ExcludeTests { get; init; }
    public bool IncludeGenerated { get; init; }
    public bool CountOnly { get; init; }
    public bool StrictNotFound { get; init; }
    public bool Strict { get; init; }
    public DateTime? Since { get; init; }
    public bool NoDedup { get; init; }
    public bool NoVisibilityRank { get; init; }
    public bool Exact { get; init; }
    public bool Regex { get; init; }
    public bool Prefix { get; init; }
    public List<SearchGuardFilter> GuardFilters { get; init; } = [];
    public int GuardWindow { get; init; } = DbReader.DefaultSearchGuardWindow;
    public bool ExactName { get; init; }
    public bool ExactSubstring { get; init; }
    public bool CheckWorkspace { get; init; }
    public TimeSpan? StaleAfter { get; init; }
    public IReadOnlySet<string>? StatusCheckScopes { get; init; }
    public bool WithPaths { get; init; }
    public string? GroupBy { get; init; }
    public bool RawBytes { get; init; }
    public bool RawKinds { get; init; }
    public bool Verbose { get; init; }
    public bool Profile { get; init; }
    public int? SlowQueryMs { get; init; }
    public double MinEntrypointConfidence { get; init; }
    public string? StatusExplainField { get; init; }
    public bool StatusLogPath { get; init; }
    public bool StatusConfig { get; init; }
    public ReferenceRankMode RankMode { get; init; } = ReferenceRankMode.Weighted;
    public List<string> ExtraNames { get; init; } = [];
    public List<string>? MapSections { get; init; }
    public bool DependencyCycles { get; init; }
    public string? ParseError { get; init; }
}
