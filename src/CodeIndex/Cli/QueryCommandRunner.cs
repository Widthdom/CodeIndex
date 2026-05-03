using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs query-style CLI commands.
/// クエリ系CLIコマンドを実行する。
/// </summary>
public static class QueryCommandRunner
{
    // Cap OR-joined `symbols` names well below SQLite's 1000 expression-tree depth so oversized
    // batches fail fast with a clear usage error instead of a confusing SQLite exception.
    // OR 結合の `symbols` 名は SQLite の式木深さ上限 1000 を十分下回る値で頭打ちにし、
    // 大量バッチを SQLite 例外ではなく明確な usage error で早期に弾く。
    internal const int MaxSymbolQueryNames = 256;
    internal const int ExactZeroHintProbeLimit = 1;
    internal const int ExactZeroHintSampleLimit = 5;
    private const string HotspotsGroupedByNameKind = "name_kind";
    private static readonly Dictionary<string, string> LangFilterAliases = new(StringComparer.Ordinal)
    {
        ["c#"] = "csharp",
        ["cs"] = "csharp",
        ["cshtml"] = "csharp",
        ["bat"] = "batch",
        ["cmd"] = "batch",
        ["py"] = "python",
        ["py3"] = "python",
        ["pyi"] = "python",
        ["pyw"] = "python",
        ["yml"] = "yaml",
        ["razor"] = "csharp",
        ["kt"] = "kotlin",
        ["kts"] = "kotlin",
        ["ts"] = "typescript",
        ["tsx"] = "typescript",
        ["cts"] = "typescript",
        ["mts"] = "typescript",
        ["tsql"] = "sql",
        ["transactsql"] = "sql",
    };
    private static readonly Dictionary<string, string[]> LanguageDisplayAliases = new(StringComparer.Ordinal)
    {
        ["csharp"] = ["cshtml", "razor"],
        ["yaml"] = ["yml"],
        ["typescript"] = ["ts", "tsx", "cts", "mts"],
        ["sql"] = ["tsql", "t-sql", "transact-sql", "transactsql", "sqlserver", "mssql"],
    };
    private static readonly HashSet<string> ValueTakingOptions =
    [
        "--db",
        "--limit",
        "--top",
        "--lang",
        "--kind",
        "--since",
        "--start",
        "--end",
        "--before",
        "--after",
        "--name",
        "--snippet-lines",
        "--path",
        "--exclude-path",
        "--depth",
        "--query",
        "--focus-line",
        "--focus-column",
        "--focus-length",
        "--max-line-width",
    ];
    private static readonly HashSet<string> FlagOnlyOptions =
    [
        "--json",
        "--fts",
        "--body",
        "--count",
        "--no-dedup",
        "--exact",
        "--exact-name",
        "--exact-substring",
        "--reverse",
        "--help",
        "-h",
        "--version",
        "-V",
        "--group-by-name",
    ];
    private const string FindUsage = "Usage: cdidx find <query> --path <glob> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--before <n>] [--after <n>] [--max-line-width <n>] [--exact] [--count]\n       cdidx find --query <query> --path <glob> [...]\n       cdidx find [options] -- <query>";
    public static int RunSearch(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("search", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("search", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--path", "--exclude-path", "--exclude-tests", "--snippet-lines", "--max-line-width", "--fts", "--count", "--since", "--no-dedup", "--exact", "--exact-substring", "--exact-name"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "search"))
            return CommandExitCodes.UsageError;
        if (!TryResolveSearchExactMode(options, out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (options.Query == null)
        {
            WriteUsageError(
                "search requires a query argument",
                GetUsageLineOrThrow("search"),
                "Add the text you want to search for after the command, for example: `cdidx search authenticate`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("search", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountSearchResults(options.Query, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, query: options.Query).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(counts.Count, counts.FileCount, options.Query), CliJsonSerializerContext.Default.QueryCountFilesJsonResult)
                    : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var results = reader.Search(options.Query, options.Limit, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact);
            if (results.Count == 0)
            {
                if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "results", query: options.Query).ToJsonString(jsonOptions));
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No results found.");
                    WriteZeroResultHints(options, reader);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(SearchSnippetFormatter.ToCompactResult(r, options.Query, options.SnippetLines, exact, options.MaxLineWidth, r.Lang), jsonOptions));
            }
            else
            {
                foreach (var r in results)
                {
                    Console.WriteLine($"{r.Path}:{r.StartLine}-{r.EndLine}");
                    var snippetLines = SearchSnippetFormatter.Format(r.Content, options.Query, options.SnippetLines, exact, options.MaxLineWidth, r.Lang);
                    foreach (var line in snippetLines)
                        Console.WriteLine($"  {line}");
                    Console.WriteLine();
                }
                var fileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} results in {fileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunDefinition(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("definition", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("definition", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--kind", "--body", "--count", "--path", "--exclude-path", "--exclude-tests", "--since", "--exact", "--exact-name", "--exact-substring"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "definition"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "definition", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.CountOnly && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(options.Json
                ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, options.Query), CliJsonSerializerContext.Default.QueryCountFilesJsonResult)
                : "0");
            return CommandExitCodes.Success;
        }
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

        return WithDb(options.DbPath, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact);
                var exactSignalForCount = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact,
                    () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false) > 0,
                    () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                    () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                    r => r.Name);
                WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, exactZeroHint: exactZeroHintForCount, exactSignal: exact ? exactSignalForCount : null).ToJsonString(jsonOptions)
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

            var results = reader.GetDefinitions(options.Query, options.Limit, options.Kind, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact);
            var exactSignal = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var exactZeroHint = BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false) > 0,
                () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                r => r.Name);
            WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (!options.Json)
                {
                    Console.Error.WriteLine("No definitions found.");
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader, "Try 'search' for full-text matches instead of symbol lookup.");
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                {
                    if (exact)
                        WriteJsonResultWithExactSignal(r, exactSignal, jsonOptions);
                    else
                        Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
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

    public static int RunReferences(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("references", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("references", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--kind", "--count", "--path", "--exclude-path", "--exclude-tests", "--max-line-width", "--exact", "--exact-name", "--exact-substring"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "references"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "references", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
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

        return WithDb(options.DbPath, reader =>
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
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "references", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No references found.");
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "references", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var owner = r.ContainerName != null ? $"  in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.ReferenceKind,-12} {r.SymbolName,-32} {r.Path}:{r.Line}:{r.Column}{owner}");
                    Console.WriteLine($"  {r.Context}");
                }
                var refFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} references in {refFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallers(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callers", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callers", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--kind", "--count", "--path", "--exclude-path", "--exclude-tests", "--exact", "--exact-name", "--exact-substring"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callers", options.Kind))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "callers", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
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

        return WithDb(options.DbPath, reader =>
        {
            WriteGraphReferenceKindHint("callers", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCallersTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                    () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
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

            var results = reader.GetCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                r => r.CalleeName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callers", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No callers found.");
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                }
                var callerFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} callers in {callerFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallees(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callees", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callees", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--kind", "--count", "--path", "--exclude-path", "--exclude-tests", "--exact", "--exact-name", "--exact-substring"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callees", options.Kind))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "callees", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
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

        return WithDb(options.DbPath, reader =>
        {
            WriteGraphReferenceKindHint("callees", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCalleesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                    () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
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

            var results = reader.GetCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                r => r.CallerName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callees", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No callees found.");
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callees", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CalleeName,-32} {r.Path}:{r.FirstLine}  <- {r.CallerName ?? "<top-level>"} ({r.ReferenceCount} refs)");
                }
                var calleeFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} callees in {calleeFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
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
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("symbols", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--kind", "--count", "--path", "--exclude-path", "--exclude-tests", "--since", "--exact", "--exact-name", "--exact-substring", "--name"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "symbols"))
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
                    ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, countQuery), CliJsonSerializerContext.Default.QueryCountFilesJsonResult)
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

        return WithDb(options.DbPath, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountSearchSymbolsTotal(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact);
                var hasExactPredicateForCount = exact && symbolQueries is { Count: > 0 };
                var exactSignalForCount = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var multiNameExactHintForCount = symbolQueries != null && symbolQueries.Count > 1;
                var exactZeroHintForCount = multiNameExactHintForCount
                    ? BuildExactZeroHint(
                        exact,
                        () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                        r => r.Name)
                    : BuildExactZeroHint(
                        exact && symbolQueries != null && symbolQueries.Count > 0,
                        () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false) > 0,
                        () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                        () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                        r => r.Name);
                WriteExactSymbolWarningIfNeeded(hasExactPredicateForCount, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    Console.WriteLine(options.Json
                        ? BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, exactZeroHint: exactZeroHintForCount, exactSignal: hasExactPredicateForCount ? exactSignalForCount : null).ToJsonString(jsonOptions)
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

            var results = reader.SearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact);
            var hasExactPredicate = exact && symbolQueries is { Count: > 0 };
            var exactSignal = reader.GetSymbolsExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var multiNameExactHint = symbolQueries != null && symbolQueries.Count > 1;
            var exactZeroHint = multiNameExactHint
                ? BuildExactZeroHint(
                    exact,
                    () => reader.AnySearchSymbols(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                    r => r.Name)
                : BuildExactZeroHint(
                    exact && symbolQueries != null && symbolQueries.Count > 0,
                    () => reader.CountSearchSymbols(symbolQueries, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false) > 0,
                    () => reader.CountSearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                    () => reader.SearchSymbols(symbolQueries, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                    r => r.Name);
            WriteExactSymbolWarningIfNeeded(hasExactPredicate, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (!options.Json)
                {
                    Console.Error.WriteLine("No symbols found.");
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                {
                    if (hasExactPredicate)
                        WriteJsonResultWithExactSignal(r, exactSignal, jsonOptions);
                    else
                        Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
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
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("files", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--count", "--path", "--exclude-path", "--exclude-tests", "--since"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "files"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedExtraPositionals("files", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
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
                    ? JsonSerializer.Serialize(new QueryCountJsonResult(counts.Count), CliJsonSerializerContext.Default.QueryCountJsonResult)
                    : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var results = reader.ListFiles(options.Query, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            if (results.Count == 0)
            {
                if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "files").ToJsonString(jsonOptions));
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No files found.");
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
            }
            else
            {
                foreach (var r in results)
                    Console.WriteLine($"{r.Lang ?? "?",-12} {r.Lines,6} lines  {r.Path}");
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("excerpt", cmdArgs, ["--db", "--json", "--start", "--end", "--before", "--after", "--max-line-width", "--focus-line", "--focus-column", "--focus-length"]))
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
        return WithDb(options.DbPath, reader =>
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
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(excerpt, jsonOptions));
            }
            else
            {
                Console.WriteLine($"{excerpt.Path}:{excerpt.StartLine}-{excerpt.EndLine}");
                WriteNumberedExcerpt(excerpt.StartLine, excerpt.Content);
            }
            return CommandExitCodes.Success;
        });
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

        var options = ParseArgs(preparedFindArgs, jsonDefault: false, allowNamedQuery: true);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
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

        return WithDb(options.DbPath, reader =>
        {
            if (options.CountOnly)
            {
                var counts = reader.CountFindInFiles(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact);
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        var payload = BuildJsonZeroResultPayload(reader, jsonOptions, includeFiles: true, extraFields: static payload =>
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
                    ? JsonSerializer.Serialize(new QueryFindCountJsonResult(counts.Count, counts.FileCount, counts.FileCount), CliJsonSerializerContext.Default.QueryFindCountJsonResult)
                    : $"{counts.Count}");
                return CommandExitCodes.Success;
            }

            var results = reader.FindInFiles(options.Query, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.ContextBefore, options.ContextAfter, options.Exact, options.MaxLineWidth);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "results", extraFields: payload =>
                    {
                        payload["query"] = options.Query;
                        payload["path"] = JsonSerializer.SerializeToNode(options.PathPatterns, jsonOptions);
                        payload["exclude_tests"] = options.ExcludeTests;
                        payload["before"] = options.ContextBefore;
                        payload["after"] = options.ContextAfter;
                        payload["exact"] = options.Exact;
                        payload["file_count"] = 0;
                    });
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine("No matches found.");
                    WriteZeroResultHints(options, reader, filterHint: "try broadening --path or adding another --path value; --path is required for find.");
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
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
        HashSet<string> allowedWithValues =
        [
            "--db", "--limit", "--top", "--lang", "--path", "--exclude-path", "--before", "--after", "--max-line-width", "--query"
        ];
        HashSet<string> allowedFlags =
        [
            "--json", "--exclude-tests", "--count", "--exact"
        ];

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
                        return $"Error: {arg} requires a value";
                    value = args[i + 1];
                    i++;
                }
                if ((arg == "--limit" || arg == "--top") && (!int.TryParse(value, out var limit) || limit <= 0))
                    return $"Error: {arg} requires a positive integer, got '{value}'";
                if (arg == "--max-line-width" && (!int.TryParse(value, out var widthValue) || widthValue < 0))
                    return $"Error: {arg} requires a non-negative integer, got '{value}'";
                if (arg == "--max-line-width" && int.TryParse(value, out var widthCeil) && widthCeil > LineWidthFormatter.MaxAllowedLineWidth)
                    return $"Error: --max-line-width must be less than or equal to {LineWidthFormatter.MaxAllowedLineWidth} (got '{value}').";
                if ((arg == "--before" || arg == "--after") && (!int.TryParse(value, out var context) || context < 0))
                    return $"Error: {arg} requires a non-negative integer, got '{value}'";
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
                return $"Error: unsupported option for find: {rawArg}";

            queryCount++;
            if (queryCount > 1)
                return "Error: find accepts exactly one query argument";
        }

        return null;
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("map", cmdArgs, ["--db", "--json", "--limit", "--top", "--lang", "--path", "--exclude-path", "--exclude-tests"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "map"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("map", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
        {
            var map = reader.GetRepoMap(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            WorkspaceMetadataEnricher.Enrich(map, options.DbPath, options.DbPathExplicit);

            // Return not-found only when a narrowing filter is active and produces zero files.
            // Unfiltered empty indexes return success (valid state for health probes).
            // フィルタ指定時に該当0件なら未検出を返す。フィルタなしの空DBは正常（ヘルスチェック用途）。
            var hasFilter = options.PathPatterns.Count > 0 || options.ExcludePaths.Count > 0
                || options.ExcludeTests || options.Lang != null;
            if (map.FileCount == 0 && hasFilter)
            {
                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(map, jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine("No files found matching the given filters.");
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(map, jsonOptions));
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
                WriteRepoMapSection("Languages", map.Languages.Select(item => $"{item.Lang,-12} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                WriteRepoMapSection("Modules", map.Modules.Select(item => $"{item.Module,-24} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                WriteRepoMapSection("Top files", map.TopFiles.Select(item => $"{item.Path}  [score {item.Score}, {item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                WriteRepoMapSection("Largest files", map.LargestFiles.Select(item => $"{item.Path}  [{item.Lines} lines, {item.Size} bytes]"));
                WriteRepoMapSection("Symbol-rich files", map.SymbolRichFiles.Select(item => $"{item.Path}  [{item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                WriteRepoMapSection("Reference-rich files", map.ReferenceRichFiles.Select(item => $"{item.Path}  [{item.ReferenceCount} refs, {item.SymbolCount} syms]"));
                WriteRepoMapSection("Entrypoints", map.Entrypoints.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.Line}  [score {item.Score}]"));
            }

            return CommandExitCodes.Success;
        });
    }

    public static int RunInspect(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("inspect", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("inspect", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--path", "--exclude-path", "--exclude-tests", "--body", "--max-line-width", "--exact", "--exact-name", "--exact-substring"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "inspect", out var exact, out var exactError))
        {
            Console.Error.WriteLine(exactError);
            return CommandExitCodes.UsageError;
        }
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

        return WithDb(options.DbPath, reader =>
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
                var payload = JsonSerializer.SerializeToNode(analysis, jsonOptions)!.AsObject();
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

            return CommandExitCodes.Success;
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
        var options = ParseArgs(cmdArgs[1..], jsonDefault: false);
        if (TryWriteUnsupportedOptionError("outline", cmdArgs[1..], ["--db", "--json"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "outline"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("outline", options))
            return CommandExitCodes.UsageError;

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, cmdArgs[0], options.DbPathExplicit);
        return WithDb(options.DbPath, reader =>
        {
            var outline = reader.GetOutline(filePath);
            if (outline == null)
            {
                if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new QueryPathErrorJsonResult(filePath, "file not found in index"), CliJsonSerializerContext.Default.QueryPathErrorJsonResult));
                else
                    Console.Error.WriteLine($"Error: '{filePath}' not found in index.");
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(outline, jsonOptions));
            }
            else
            {
                var outlineContent = reader.GetExcerpt(filePath, 1, outline.TotalLines)?.Content;

                Console.WriteLine($"# {outline.Path}  ({outline.Lang ?? "unknown"}, {outline.TotalLines} lines, {outline.SymbolCount} symbols)");
                Console.WriteLine();
                foreach (var sym in outline.Symbols)
                {
                    // Indent nested symbols by computed tree depth / コンテナ連鎖の深さでインデント
                    var indent = sym.Depth > 0 ? new string(' ', 4 * sym.Depth) : "";
                    var ret = sym.ReturnType != null ? $": {sym.ReturnType} " : "";
                    var sig = sym.Signature ?? $"{sym.Kind} {sym.Name}";
                    // Avoid duplicating visibility when signature already contains it
                    // シグネチャに既に visibility が含まれている場合は重複を避ける
                    var vis = sym.Visibility != null && !sig.TrimStart().StartsWith(sym.Visibility, StringComparison.Ordinal)
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
        var previewOptionError = ValidatePreviewOptions("status", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("status", cmdArgs, ["--db", "--json"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "status"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("status", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
        {
            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit);
            // Attach runtime metadata / ランタイムメタデータを付加
            status.SymbolKinds = reader.GetSymbolKindCounts();
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            if (appVersion != null)
                status.Version = appVersion;

            // Build one-line summary for AI orientation / AI向けの1行サマリーを構築
            var topLangs = status.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key);
            var freshness = BuildStatusFreshnessLabel(status);
            var dirty = status.GitIsDirty == true ? ", dirty" : "";
            if (IsFoldOnlyReadinessDegraded(status))
            {
                status.DegradedReason = BuildFoldNotReadyExplanation(status.FoldReadyReason);
                status.RecommendedAction = BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit);
                status.AlternativeAction = BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit);
            }

            var degraded = IsStatusDegraded(status)
                ? ", DEGRADED"
                : "";
            status.Summary = $"{status.Files} files, {status.Symbols} symbols, {status.References} refs across {status.Languages.Count} languages ({string.Join(", ", topLangs)}); index {freshness}{dirty}{degraded}";

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(status, jsonOptions));
            }
            else
            {
                if (status.Summary != null)
                    Console.WriteLine(status.Summary);
                Console.WriteLine();
                if (status.Version != null)
                    Console.WriteLine($"Version : cdidx v{status.Version}");
                Console.WriteLine($"Files   : {status.Files:N0}");
                Console.WriteLine($"Chunks  : {status.Chunks:N0}");
                Console.WriteLine($"Symbols : {status.Symbols:N0}");
                Console.WriteLine($"Refs    : {status.References:N0}");
                if (status.IndexedAt != null)
                    Console.WriteLine($"Indexed : {status.IndexedAt:O}");
                if (status.LatestModified != null)
                    Console.WriteLine($"Source  : {status.LatestModified:O}");
                if (status.GitHead != null)
                    Console.WriteLine($"Git HEAD: {status.GitHead}");
                if (status.GitIsDirty != null)
                    Console.WriteLine($"Git Dirty: {status.GitIsDirty}");
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
                    Console.WriteLine($"Graph   : {status.GraphSupportedLanguages.Count} languages ({string.Join(", ", status.GraphSupportedLanguages)})");
                if (!status.GraphTableAvailable)
                    Console.WriteLine("WARN    : symbol_references table missing — reference / caller / callee / unused counts are degraded to 0.");
                if (!status.IssuesTableAvailable)
                    Console.WriteLine("WARN    : file_issues table missing — validate output is degraded to empty.");
                if (!status.SqlGraphContractReady)
                    Console.WriteLine($"WARN    : SQL graph/dependency results may be stale. Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots.");
                if (!status.HotspotFamilyReady && status.HotspotFamilyDegradedReason != null)
                {
                    Console.WriteLine($"WARN    : {status.HotspotFamilyDegradedReason}");
                    Console.WriteLine("Hint    : rerun `cdidx index <projectPath>` to restore authoritative cross-file hotspot families.");
                }
                if (!status.CSharpSymbolNameReady)
                    Console.WriteLine($"WARN    : C# exact-name for operators / conversion operators / indexers is degraded. Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical symbol names in place.");
                // #435: tell the user when deps / impact metadata-attribute edges fall back
                // to the legacy signature / name-suffix heuristic (impostor classes may be
                // silently promoted or demoted until the authoritative resolver is re-run).
                // #435: deps / impact の metadata-attribute edge が legacy heuristic に
                // 縮退しているときは明示する。
                if (!status.CSharpMetadataTargetReady)
                    Console.WriteLine("WARN    : C# deps / impact metadata-attribute edges fall back to the signature / name-suffix heuristic. Run `cdidx index .` to re-stamp authoritative is_metadata_target values.");
                // #86: tell the user when `--exact` is running on the ASCII NOCASE fallback.
                // #86: --exact が ASCII NOCASE fallback で動いているときは明示する。
                if (!status.FoldReady)
                {
                    if (IsFoldOnlyReadinessDegraded(status) && status.DegradedReason != null && status.RecommendedAction != null && status.AlternativeAction != null)
                    {
                        Console.WriteLine($"WARN    : {status.DegradedReason}");
                        Console.WriteLine($"Hint    : run `{status.RecommendedAction}` to restamp folded-name columns in place.");
                        Console.WriteLine($"Hint    : or run `{status.AlternativeAction}` for a full rebuild.");
                    }
                    else
                    {
                        Console.WriteLine($"WARN    : {BuildFoldNotReadyWarning(status.FoldReadyReason, BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit), BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit))}");
                    }
                }
                var totalLangs = FileIndexer.GetLanguageExtensions().Values.Distinct().Count();
                var symbolLangs = SymbolExtractor.GetSupportedLanguages().Count;
                Console.WriteLine($"Support : {totalLangs} detected, {symbolLangs} with symbols, {status.GraphSupportedLanguages?.Count ?? 0} with graph");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunImpact(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("impact", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("impact", cmdArgs, ["--", "--query", "--db", "--json", "--limit", "--top", "--lang", "--count", "--path", "--exclude-path", "--exclude-tests", "--depth"], options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "impact"))
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

        return WithDb(options.DbPath, reader =>
        {
            var maxDepth = options.ContextAfterExplicit ? options.ContextAfter : 5; // --depth is parsed into ContextAfter; 0 means resolve-only
            var analysis = reader.AnalyzeImpact(options.Query, maxDepth, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
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
                                zeroPayload["max_depth"] = maxDepth;
                                zeroPayload["actual_depth"] = 0;
                                zeroPayload["truncated"] = analysis.Truncated;
                                zeroPayload["impact_mode"] = analysis.ImpactMode;
                                zeroPayload["heuristic"] = analysis.Heuristic;
                                zeroPayload["file_impacts"] = new JsonArray();
                                zeroPayload["definition_count"] = analysis.DefinitionCount;
                                zeroPayload["definition_file_count"] = analysis.DefinitionFileCount;
                                zeroPayload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
                                zeroPayload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
                                zeroPayload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
                                zeroPayload["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, jsonOptions);
                                if (analysis.ZeroResultReason != null)
                                    zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                                if (analysis.Suggestion != null)
                                    zeroPayload["suggestion"] = analysis.Suggestion;
                                AddSqlGraphContractJsonFields(zeroPayload, sqlGraphSignal);
                            });
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.Error.WriteLine("Depth 0 requested: resolved the symbol only; callers were not traversed.");
                        WriteImpactResolutionHint(analysis);
                        WriteGraphSupportHint(options.Lang);
                    }
                    return CommandExitCodes.Success;
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
                        if (analysis.ZeroResultReason != null)
                            payload["zero_result_reason"] = analysis.ZeroResultReason;
                        if (analysis.Suggestion != null)
                            payload["suggestion"] = analysis.Suggestion;
                        if (!analysis.GraphTableAvailable)
                            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        AddFreshnessHint(payload, reader);
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
                            zeroPayload["max_depth"] = maxDepth;
                            zeroPayload["actual_depth"] = 0;
                            zeroPayload["truncated"] = analysis.Truncated;
                            zeroPayload["impact_mode"] = analysis.ImpactMode;
                            zeroPayload["heuristic"] = analysis.Heuristic;
                            zeroPayload["file_impacts"] = new JsonArray();
                            zeroPayload["definition_count"] = analysis.DefinitionCount;
                            zeroPayload["definition_file_count"] = analysis.DefinitionFileCount;
                            zeroPayload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
                            zeroPayload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
                            zeroPayload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
                            zeroPayload["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, jsonOptions);
                            if (analysis.ZeroResultReason != null)
                                zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                            if (analysis.Suggestion != null)
                                zeroPayload["suggestion"] = analysis.Suggestion;
                            AddSqlGraphContractJsonFields(zeroPayload, sqlGraphSignal);
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
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
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
                    AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
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
                    ["max_depth"] = maxDepth,
                    ["actual_depth"] = analysis.Callers.Count > 0 ? analysis.Callers.Max(r => r.Depth) : 0,
                    ["truncated"] = analysis.Truncated,
                    ["impact_mode"] = analysis.ImpactMode,
                    ["heuristic"] = analysis.Heuristic,
                    ["callers"] = JsonSerializer.SerializeToNode(analysis.Callers, jsonOptions),
                    ["file_impacts"] = JsonSerializer.SerializeToNode(analysis.FileImpacts, jsonOptions),
                    ["definition_count"] = analysis.DefinitionCount,
                    ["definition_file_count"] = analysis.DefinitionFileCount,
                    ["has_multiple_definitions"] = analysis.HasMultipleDefinitions,
                    ["has_class_like_definitions"] = analysis.HasClassLikeDefinitions,
                    ["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles,
                    ["definitions"] = JsonSerializer.SerializeToNode(analysis.Definitions, jsonOptions),
                };
                if (analysis.Suggestion != null)
                    payload["suggestion"] = analysis.Suggestion;
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
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
                        }
                    }
                }

                var truncNote = analysis.Truncated ? " [TRUNCATED]" : "";
                if (hasHeuristicHints)
                    Console.Error.WriteLine($"\n({hintCount} heuristic dependency hints across {hintFileCount} files{truncNote})");
                else
                    Console.Error.WriteLine($"\n({confirmedCount} callers across {confirmedFileCount} files, max depth {maxDepth}{truncNote})");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunDeps(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("deps", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("deps", cmdArgs, ["--db", "--json", "--limit", "--top", "--lang", "--path", "--exclude-path", "--exclude-tests", "--reverse"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "deps"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("deps", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
        {
            var reverse = cmdArgs.Any(a => a == "--reverse");
            var results = reader.GetFileDependencies(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse);
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
                    WriteDegradedGraphZeroResult(reader, "edges", json: true, graphAvailable: false, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (options.Json)
                    Console.WriteLine(BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: "edges", graphTableAvailable: true, degraded: !sqlGraphSignal.Ready, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal)).ToJsonString(jsonOptions));
                else
                {
                    Console.Error.WriteLine("No file dependencies found.");
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "edges", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["count"] = results.Count,
                    ["edges"] = JsonSerializer.SerializeToNode(results, jsonOptions)
                };
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
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

    public static int RunHotspots(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        bool groupByName = cmdArgs.Any(a => a == "--group-by-name");
        var previewOptionError = ValidatePreviewOptions("hotspots", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("hotspots", cmdArgs, ["--db", "--json", "--limit", "--top", "--kind", "--lang", "--count", "--path", "--exclude-path", "--exclude-tests", "--group-by-name"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "hotspots"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("hotspots", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
        {
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
            if (groupByName)
            {
                var groupedResults = reader.GetGroupedSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
                var effectiveSqlGraphSignal = groupedResults.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, groupedResults.Select(result => result.Symbol.Lang), options.Lang);
                if (groupedResults.Count == 0)
                {
                    if (options.CountOnly)
                    {
                        if (options.Json)
                        {
                            var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: true, graphAvailable: reader._hasReferencesTable);
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            Console.WriteLine(payload.ToJsonString(jsonOptions));
                        }
                        else
                            WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, new ExactQuerySignal(true, HasMissingIndex: false, HasMissingTable: false, null));
                    }
                    else if (options.Json)
                    {
                        var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: false, graphAvailable: reader._hasReferencesTable);
                        AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.Error.WriteLine("No symbol hotspots found.");
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
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
                    var items = groupedResults.Select(g => new
                    {
                        name = g.Symbol.Name,
                        kind = g.Symbol.Kind,
                        path = g.Symbol.Path,
                        line = g.Symbol.Line,
                        reference_count = g.ReferenceCount,
                        visibility = g.Symbol.Visibility,
                        container = g.Symbol.ContainerName,
                        definition_sites = g.DefinitionSites,
                        paths = g.Paths,
                    });
                    var payload = new JsonObject
                    {
                        ["count"] = groupedResults.Count,
                        ["definition_site_total"] = definitionSiteTotal,
                        ["grouped_by"] = HotspotsGroupedByNameKind,
                        ["hotspots"] = JsonSerializer.SerializeToNode(items, jsonOptions)
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
                        Console.WriteLine($"{g.ReferenceCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{multi}");
                    }
                    Console.Error.WriteLine($"({groupedResults.Count} unique name/kind groups, {definitionSiteTotal} definition sites)");
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            var results = reader.GetSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
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
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: true, graphAvailable: false, jsonOptions, extraFields: payload =>
                    {
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
                            AddHotspotFamilyJsonFields(payload, hotspotSignal);
                            AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        }).ToJsonString(jsonOptions));
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No symbol hotspots found.");
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
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
                var items = results.Select(r => new { name = r.Symbol.Name, kind = r.Symbol.Kind, path = r.Symbol.Path, line = r.Symbol.Line, reference_count = r.ReferenceCount, visibility = r.Symbol.Visibility, container = r.Symbol.ContainerName });
                var payload = new JsonObject
                {
                    ["count"] = results.Count,
                    ["hotspots"] = JsonSerializer.SerializeToNode(items, jsonOptions)
                };
                AddHotspotFamilyJsonFields(payload, hotspotSignal);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                Console.WriteLine(payload.ToJsonString(jsonOptions));
            }
            else
            {
                foreach (var (s, refCount) in results)
                {
                    var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                    Console.WriteLine($"{refCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}");
                }
                Console.Error.WriteLine($"({results.Count} symbol hotspots)");
                WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunUnused(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("unused", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("unused", cmdArgs, ["--db", "--json", "--limit", "--top", "--kind", "--lang", "--count", "--path", "--exclude-path", "--exclude-tests"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "unused"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("unused", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
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
                var countSummary = reader.CountUnusedSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
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
                        ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(new Dictionary<string, int>(), jsonOptions),
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

            var results = reader.GetUnusedSymbols(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
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
                        jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine("No unused symbols found.");
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "symbols", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return options.Json ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                Console.WriteLine(BuildUnusedJsonPayload(results, graphSupported, graphSupportReason, sqlGraphSignal, reader._hasReferencesTable, jsonOptions));
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

    private static readonly string[] OrderedUnusedBuckets =
    [
        "likely_unused_private",
        "maybe_unused_nonpublic",
        "public_or_exported_no_refs",
        "reflection_or_config_suspect",
    ];

    private static Dictionary<string, int> BuildUnusedBucketCounts(IEnumerable<UnusedSymbolResult> results)
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

    private static string BuildUnusedJsonPayload(IEnumerable<UnusedSymbolResult> results, bool? graphSupported, string? graphSupportReason, SqlGraphContractSignal sqlGraphSignal, bool hasReferencesTable, JsonSerializerOptions jsonOptions)
    {
        var resultList = results as IReadOnlyCollection<UnusedSymbolResult> ?? results.ToArray();
        var payload = new JsonObject
        {
            ["count"] = resultList.Count,
            ["graph_supported"] = graphSupported,
            ["graph_support_reason"] = graphSupportReason,
            ["returned_bucket_counts"] = JsonSerializer.SerializeToNode(BuildUnusedBucketCounts(resultList), jsonOptions),
            ["symbols"] = JsonSerializer.SerializeToNode(resultList, jsonOptions)
        };

        if (!hasReferencesTable)
        {
            payload["graph_table_available"] = false;
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }

        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
        return payload.ToJsonString(jsonOptions);
    }

    private static string GetUnusedBucketHeading(string bucket) => bucket switch
    {
        "likely_unused_private" => "Likely unused private",
        "maybe_unused_nonpublic" => "Maybe unused non-public",
        "public_or_exported_no_refs" => "Public/exported with no refs",
        "reflection_or_config_suspect" => "Reflection/config suspects",
        _ => bucket,
    };

    public static int RunValidate(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("validate", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            Console.Error.WriteLine(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("validate", cmdArgs, ["--db", "--json", "--kind", "--path"]))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "validate"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("validate", options))
            return CommandExitCodes.UsageError;

        return WithDb(options.DbPath, reader =>
        {
            var issues = reader.GetIssues(options.Kind, options.PathPatterns);
            var issuesAvailable = reader._hasIssuesTable;
            if (issues.Count == 0)
            {
                if (options.Json)
                    Console.WriteLine(new JsonObject
                    {
                        ["count"] = 0,
                        ["issues"] = new JsonArray(),
                        ["issues_table_available"] = issuesAvailable,
                        ["degraded"] = !issuesAvailable,
                    }.ToJsonString(jsonOptions));
                else if (!issuesAvailable)
                    Console.Error.WriteLine("WARN: file_issues table missing in this index (legacy or read-only DB) — validate output is degraded, not a real clean signal.");
                else
                    Console.Error.WriteLine("No encoding issues found.");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["count"] = issues.Count,
                    ["issues"] = JsonSerializer.SerializeToNode(issues, jsonOptions),
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (TryWriteUnsupportedOptionError("languages", cmdArgs, ["--json"]))
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
            Console.WriteLine(JsonSerializer.Serialize(new LanguagesJsonResult(entries), CliJsonSerializerContext.Default.LanguagesJsonResult));
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

    public static QueryCommandOptions ParseArgs(string[] args, bool jsonDefault, bool allowNamedQuery = false)
    {
        string dbPath = Path.Combine(".cdidx", "codeindex.db");
        bool? json = null;
        int limit = 20;
        string? lang = null;
        string? kind = null;
        string? query = null;
        bool rawFts = false;
        bool includeBody = false;
        bool countOnly = false;
        int? startLine = null;
        int? endLine = null;
        int contextBefore = 0;
        int contextAfter = 0;
        int? focusLine = null;
        int? focusColumn = null;
        int focusLength = 1;
        int snippetLines = SearchSnippetFormatter.DefaultSnippetLines;
        int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth;
        bool contextAfterExplicit = false;
        var pathPatterns = new List<string>();
        var excludePaths = new List<string>();
        bool excludeTests = false;
        DateTime? since = null;
        bool noDedup = false;
        bool exact = false;
        List<string>? parseErrors = null;
        bool exactName = false;
        bool exactSubstring = false;
        bool dbPathExplicit = false;
        var extraNames = new List<string>();

        void AddParseError(string error)
        {
            parseErrors ??= [];
            parseErrors.Add(error);
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
            Console.Error.WriteLine($"Warning: {canonicalName} specified more than once; using the last value '{newValue}'.");
        }

        for (int i = 0; i < args.Length; i++)
        {
            var currentArg = args[i];
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
                case "--json":
                    json = true;
                    break;
                case "--limit":
                case "--top":
                    if (!TryReadRawOptionValue(args, ref i, "--limit", inlineValue, out var limitValue, out var missingLimitError))
                        AddParseError(missingLimitError!);
                    else if (TryParsePositiveInt(limitValue!, "--limit", out var parsedLimit, out var limitError))
                    {
                        WarnIfDuplicateSingleValueOption("--limit", limitValue!);
                        limit = parsedLimit;
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
                case "--fts":
                    rawFts = true;
                    break;
                case "--body":
                    includeBody = true;
                    break;
                case "--count":
                    countOnly = true;
                    break;
                case "--no-dedup":
                    noDedup = true;
                    break;
                case "--exact":
                    exact = true;
                    break;
                case "--exact-name":
                    exactName = true;
                    break;
                case "--exact-substring":
                    exactSubstring = true;
                    break;
                case "--depth":
                    if (!TryReadRawOptionValue(args, ref i, "--depth", inlineValue, out var depthValue, out var missingDepthError))
                        AddParseError(missingDepthError!);
                    else if (TryParseNonNegativeInt(depthValue!, "--depth", out var parsedDepth, out var depthError))
                    {
                        WarnIfDuplicateSingleValueOption("--depth", depthValue!);
                        contextAfter = parsedDepth; // reused as depth for impact / impact用に再利用
                        contextAfterExplicit = true;
                    }
                    else
                        AddParseError(depthError!);
                    break;
                case "--reverse":
                    break; // handled by specific commands / 特定コマンドで処理
                case "--group-by-name":
                    break;
                case "--path":
                    if (TryReadStringOptionValue(args, ref i, "--path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var pathPattern, out var pathError))
                        pathPatterns.Add(pathPattern!); // Repeatable; multiple values OR together / 繰り返し可、複数値は OR で結合
                    else
                        AddParseError(pathError!);
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
                    if (!TryReadRawOptionValue(args, ref i, "--start", inlineValue, out var startValue, out var missingStartError))
                        AddParseError(missingStartError!);
                    else if (TryParsePositiveInt(startValue!, "--start", out var parsedStart, out var startError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", startValue!);
                        startLine = parsedStart;
                    }
                    else
                        AddParseError(startError!);
                    break;
                case "--end":
                    if (!TryReadRawOptionValue(args, ref i, "--end", inlineValue, out var endValue, out var missingEndError))
                        AddParseError(missingEndError!);
                    else if (TryParsePositiveInt(endValue!, "--end", out var parsedEnd, out var endError))
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
                        snippetLines = SearchSnippetFormatter.ClampSnippetLines(parsedSnippetLines);
                    }
                    else
                        AddParseError(snippetLinesError!);
                    break;
                case "--max-line-width":
                    if (!TryReadRawOptionValue(args, ref i, "--max-line-width", inlineValue, out var maxLineWidthValue, out var missingMaxLineWidthError))
                        AddParseError(missingMaxLineWidthError!);
                    else if (TryParseNonNegativeInt(maxLineWidthValue!, "--max-line-width", out var parsedMaxLineWidth, out var maxLineWidthError))
                    {
                        if (parsedMaxLineWidth > LineWidthFormatter.MaxAllowedLineWidth)
                            AddParseError($"--max-line-width must be less than or equal to {LineWidthFormatter.MaxAllowedLineWidth} (got '{maxLineWidthValue}').");
                        else
                        {
                            WarnIfDuplicateSingleValueOption("--max-line-width", maxLineWidthValue!);
                            maxLineWidth = parsedMaxLineWidth;
                        }
                    }
                    else
                        AddParseError(maxLineWidthError!);
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        if (allowNamedQuery && query == null)
                            query = args[i];
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

        return new QueryCommandOptions
        {
            DbPath = dbPath,
            DbPathExplicit = dbPathExplicit,
            Json = json ?? jsonDefault,
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
            FocusLine = focusLine,
            FocusColumn = focusColumn,
            FocusLength = focusLength,
            SnippetLines = snippetLines,
            MaxLineWidth = maxLineWidth,
            PathPatterns = pathPatterns,
            ExcludePaths = excludePaths,
            ExcludeTests = excludeTests,
            CountOnly = countOnly,
            Since = since,
            NoDedup = noDedup,
            Exact = exact,
            ExactName = exactName,
            ExactSubstring = exactSubstring,
            ExtraNames = extraNames,
            ParseError = parseErrors == null ? null : string.Join(Environment.NewLine, parseErrors),
        };
    }

    internal static string? NormalizeLangFilterValue(string? langValue)
    {
        if (langValue == null)
            return null;
        var normalized = langValue
            .ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return LangFilterAliases.TryGetValue(normalized, out var canonical) ? canonical : normalized;
    }

    internal static IReadOnlyList<string> GetLanguageAliases(string lang)
        => LanguageDisplayAliases.TryGetValue(lang, out var aliases) ? aliases : [];

    internal static IReadOnlyCollection<string> GetCompletionLanguageAliases()
        => LanguageDisplayAliases.Values.SelectMany(aliases => aliases).ToArray();

    private static bool TryResolveSearchExactMode(QueryCommandOptions options, out bool exact, out string? error)
    {
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

    // Preview option validation now lives in the command-specific unsupported-option allowlists.
    // Keep this shim so the existing call sites stay simple while the actual fail-closed logic
    // runs through ParseArgs() + TryWriteUnsupportedOptionError().
    // preview 系オプションの検証はコマンド別 allowlist に寄せたため、この shim は常に null を返す。
    private static string? ValidatePreviewOptions(string commandName, string[] args, bool allowMaxLineWidth, bool allowFocusOptions) => null;

    private static int WithDb(string dbPath, Func<DbReader, int> action)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            Console.Error.WriteLine("Error: --db requires a value.");
            Console.Error.WriteLine("Hint: pass a concrete database path with `--db <path>` or omit `--db` to use `.cdidx/codeindex.db`.");
            return CommandExitCodes.UsageError;
        }

        // Allow SQLite URI forms (file:///abs/path?immutable=1 etc.) so users and AI agents
        // on read-only mounts / sandboxes can opt into the immutable read-only escape hatch
        // explicitly when the automatic DbContext fallback cannot recover. File.Exists is
        // skipped for URI-shaped inputs because they may carry query params and schemes that
        // are meaningless to the filesystem API but are understood by SQLite.
        // URI 形式の --db を受け入れるため、file: で始まる値は File.Exists チェックをスキップ。
        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Error: database not found at {Path.GetFullPath(dbPath)}");
            Console.Error.WriteLine("Hint: create or refresh the index with `cdidx index <projectPath>` (or `cdidx .`) and then rerun this command.");
            return CommandExitCodes.DatabaseError;
        }

        Database.DbDebug.ResetContext();
        try
        {
            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            return action(reader);
        }
        catch (FtsQuerySyntaxException ex)
        {
            Console.Error.WriteLine($"Error: FTS5 query syntax: {ex.Message}");
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

            if (ex is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 13)
            {
                Console.Error.WriteLine("Error: SQLite temp-store exhausted while evaluating this query.");
                Console.Error.WriteLine("Hint: narrow the query with `--lang`, `--path`, or `--kind`, then retry with a freshly updated cdidx build if the problem persists.");
                Database.DbDebug.DumpToStderr(ex);
                return CommandExitCodes.DatabaseError;
            }

            Console.Error.WriteLine($"Error: database error: {ex.Message}");
            Console.Error.WriteLine("Hint: check `--db`, or rebuild the index with `cdidx index <projectPath>` if the DB may be stale or corrupted.");
            Database.DbDebug.DumpToStderr(ex);
            return CommandExitCodes.DatabaseError;
        }
        finally
        {
            Database.DbDebug.ResetContext();
        }
    }

    private static void WriteNumberedExcerpt(int startLine, string content)
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine($"  {startLine + i,4}: {lines[i]}");
    }

    private static bool TryWriteParseError(QueryCommandOptions options, string commandName)
    {
        if (options.ParseError == null)
            return false;

        Console.Error.WriteLine(options.ParseError);
        Console.Error.WriteLine("Hint: fix the invalid or missing option value, then rerun with the command shape below.");
        Console.Error.WriteLine($"Usage: {GetUsageLineOrThrow(commandName)}");
        return true;
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
                Console.Error.WriteLine("Error: --group-by-name is only supported by 'hotspots'.");
                Console.Error.WriteLine("Hint: remove `--group-by-name` here, or rerun with `cdidx hotspots --group-by-name ...`.");
                Console.Error.WriteLine($"Usage: {GetUsageLineOrThrow(commandName)}");
                return true;
            }

            if (normalizedArg == arg && ValueTakingOptions.Contains(normalizedArg) && i + 1 < cmdArgs.Length)
                i++;

            Console.Error.WriteLine($"Error: {arg} is not supported for {commandName}.");
            Console.Error.WriteLine($"Hint: remove `{arg}` and rerun, or use only the options shown in `{commandName} --help`.");
            Console.Error.WriteLine($"Usage: {GetUsageLineOrThrow(commandName)}");
            return true;
        }

        return false;
    }

    private static bool TryWriteUnexpectedExtraPositionals(string commandName, QueryCommandOptions options)
    {
        if (options.ExtraNames.Count == 0)
            return false;

        Console.Error.WriteLine($"Error: unexpected extra positional argument(s) for {commandName}: {string.Join(", ", options.ExtraNames.Select(name => $"`{name}`"))}.");
        Console.Error.WriteLine("Hint: quote multi-word queries as a single argument, or remove the extra positional values.");
        Console.Error.WriteLine($"Usage: {GetUsageLineOrThrow(commandName)}");
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

        Console.Error.WriteLine($"Error: {commandName} does not accept positional arguments: {string.Join(", ", unexpected)}.");
        Console.Error.WriteLine("Hint: remove the extra positional argument(s) and use the documented flags only.");
        Console.Error.WriteLine($"Usage: {GetUsageLineOrThrow(commandName)}");
        return true;
    }

    private static string GetUsageLineOrThrow(string commandName) =>
        ConsoleUi.GetUsageLine(commandName)
        ?? throw new InvalidOperationException($"Missing usage line for command '{commandName}'.");

    // Human-readable reference_kind label for a grouped caller/callee row. When the
    // group spans multiple kinds (e.g. `call` + `subscribe`), render them joined with
    // `+` so the operator sees that the grouped row hides mixed semantics (issue #501).
    // 単一ラベルに畳まれた reference_kind を人間向けに整形する。複数 kind が混在する
    // 行 (`call` + `subscribe` など) は `+` 区切りで並べ、畳まれて見えなくなる意味の
    // 違いを運用者が気付けるようにする (issue #501)。
    private static string FormatReferenceKindLabel(string primary, IReadOnlyList<string> kinds, bool hasMixed)
    {
        if (!hasMixed || kinds == null || kinds.Count <= 1)
            return primary ?? string.Empty;
        return string.Join("+", kinds);
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
    {
        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine($"Hint: {hint}");
        Console.Error.WriteLine($"Usage: {usage}");
    }

    private static void WriteValidationError(string message, string hint)
    {
        Console.Error.WriteLine($"Error: {message}");
        Console.Error.WriteLine($"Hint: {hint}");
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

        if (freshness.IndexedAt.HasValue && (DateTime.UtcNow - freshness.IndexedAt.Value).TotalHours > 24)
            Console.Error.WriteLine("Hint: the index may be stale. Run 'cdidx index <projectPath>' to refresh.");
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
            ? JsonSerializer.SerializeToNode(freshness.IndexedAt.Value)
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
        bool includeFiles = false,
        bool? graphTableAvailable = null,
        bool? degraded = null,
        ExactQuerySignal? exactSignal = null,
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
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, jsonOptions);
        extraFields?.Invoke(payload);
        AddFreshnessHint(payload, reader);

        return payload;
    }

    private static JsonObject BuildGroupedHotspotsZeroJsonPayload(DbReader reader, JsonSerializerOptions jsonOptions, bool countOnly, bool graphAvailable)
    {
        var payload = BuildJsonZeroResultPayload(
            reader,
            jsonOptions,
            resultsKey: countOnly ? null : "hotspots",
            includeFiles: countOnly,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
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
           && signal.DegradedReason?.Contains("sql_graph_contract_ready=false", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCSharpCanonicalNameSignal(ExactQuerySignal signal)
        => !signal.ExactIndexAvailable
           && !signal.HasMissingIndex
           && !signal.HasMissingTable
           && signal.DegradedReason?.Contains("csharp_symbol_name_ready=false", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsStatusDegraded(StatusResult status)
        => !status.GraphTableAvailable
           || !status.IssuesTableAvailable
           || !status.SqlGraphContractReady
           || !status.HotspotFamilyReady
           || !status.CSharpSymbolNameReady
           || !status.CSharpMetadataTargetReady
           || !status.FoldReady;

    private static bool IsFoldOnlyReadinessDegraded(StatusResult status)
        => !status.FoldReady
           && status.GraphTableAvailable
           && status.IssuesTableAvailable
           && status.SqlGraphContractReady
           && status.HotspotFamilyReady
           && status.CSharpSymbolNameReady
           && status.CSharpMetadataTargetReady;

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => foldReadyReason switch
        {
            "missing_fold_backfill" => "--exact falls back to ASCII COLLATE NOCASE because legacy rows without `name_folded` remain.",
            "stale_fold_key_version" => "--exact falls back to ASCII COLLATE NOCASE because unchanged rows still carry an older fold-key version.",
            "stale_fold_key_fingerprint" => "--exact falls back to ASCII COLLATE NOCASE because unchanged rows still carry folded keys generated under an older runtime fingerprint.",
            _ => "--exact falls back to ASCII COLLATE NOCASE because some folded-name rows were not restamped under the current runtime."
        };

    private static string BuildFoldNotReadyWarning(string? foldReadyReason, string backfillCommand, string rebuildCommand)
        => $"{BuildFoldNotReadyExplanation(foldReadyReason)} Run `{backfillCommand}` to restamp folded-name columns in place, or `{rebuildCommand}` for a full rebuild.";

    private static string BuildStatusFreshnessLabel(StatusResult status)
    {
        if (!status.IndexedAt.HasValue || !status.LatestModified.HasValue)
            return "unknown";

        if (status.GitIsDirty == true)
            return "stale";

        return status.IndexedAt.Value >= status.LatestModified.Value ? "fresh" : "stale";
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
        if (status.Languages.Count > 0 && !status.Languages.ContainsKey(lang))
            Console.Error.WriteLine($"Hint: '{lang}' not found in index. Available: {string.Join(", ", status.Languages.Keys.OrderBy(l => l))}");
    }

    // All valid symbol kinds emitted by SymbolExtractor / SymbolExtractor が出力する全有効シンボル種別
    private static readonly string[] AllValidKinds =
        ["class", "delegate", "enum", "event", "function", "import", "interface", "namespace", "property", "struct"];
    // Reference kinds valid on `references --kind`. Includes the compile-time type-position
    // `type_reference` edge emitted by ReferenceExtractor for C#/Java base lists, declaration
    // types, generic constraints, `throws`, `is`/`as`/`instanceof`, and XML-doc `cref` targets.
    // `references --kind` で有効な reference kind。ReferenceExtractor が C#/Java の継承リスト、
    // 宣言型、generic 制約、`throws`、`is`/`as`/`instanceof`、XML-doc `cref` 対象向けに出力する
    // compile-time な `type_reference` エッジを含む。
    private static readonly string[] AllValidReferenceKinds =
        ["annotation", "attribute", "call", "instantiate", "subscribe", "type_reference"];
    // Reference kinds that `callers` / `callees` can legitimately return. Metadata kinds
    // (`attribute` / `annotation`) and type-position edges (`type_reference`) are structurally
    // not call-graph edges, so those queries are rejected at the CLI / MCP boundary.
    // `callers` / `callees` が正しく返せる reference kind。metadata 種別 (`attribute` / `annotation`)
    // や型位置エッジ (`type_reference`) は構造的に call-graph エッジではないため、CLI / MCP 境界で弾く。
    private static readonly string[] CallGraphOnlyReferenceKinds =
        ["call", "instantiate", "subscribe"];

    private static void WriteKindHint(string? kind, DbReader reader)
    {
        if (kind == null) return;
        if (!AllValidKinds.Contains(kind))
        {
            Console.Error.WriteLine($"Hint: '{kind}' is not a known kind. Available: {string.Join(", ", AllValidKinds)}");
            return;
        }
        // Kind is valid but not found in this index — hint that no symbols of this kind exist
        // 種別は有効だがインデックスに存在しない場合のヒント
        var existingKinds = reader.GetDistinctKinds();
        if (!existingKinds.Contains(kind))
            Console.Error.WriteLine($"Hint: no '{kind}' symbols in the index. Indexed kinds: {string.Join(", ", existingKinds)}");
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
    // CLI 境界で弾き、正しい列挙パスである `references --kind <kind>` に誘導する。
    private static readonly HashSet<string> NonCallGraphReferenceKinds = new(StringComparer.Ordinal)
    {
        "attribute", "annotation", "type_reference",
    };

    /// <summary>
    /// Reject non-call-graph reference kinds (`attribute` / `annotation` / `type_reference`) on
    /// commands (`callers` / `callees`) whose data model cannot answer those queries correctly.
    /// Returns true if the kind was rejected; the caller should then return
    /// `CommandExitCodes.UsageError`.
    /// `callers` / `callees` のようにデータモデル的に metadata / 型位置参照に答えられない
    /// コマンドで `--kind attribute` / `--kind annotation` / `--kind type_reference` を弾く。
    /// 弾いた場合 true を返すので、呼び出し側は `CommandExitCodes.UsageError` を返すこと。
    /// </summary>
    private static bool TryRejectNonCallGraphKindForGraphCommand(string command, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || !NonCallGraphReferenceKinds.Contains(kind))
            return false;

        if (kind == "type_reference")
            Console.Error.WriteLine($"Error: '--kind type_reference' is not supported on '{command}'. Type-position references are compile-time edges (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so `{command} --kind type_reference` cannot return accurate call-graph rows.");
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
            Console.Error.WriteLine($"Note: '{analysis.Query}' resolved to '{analysis.ResolvedName}' ({kinds}) as {analysis.DefinitionCount} definition(s) across {analysis.DefinitionFileCount} file(s): {string.Join(", ", pathPreview)}{extra}");
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
        ExactQuerySignal? exactSignal = null, Action<JsonObject>? extraFields = null)
    {
        if (graphAvailable) return;
        if (json)
        {
            var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: false, degraded: true, exactSignal: exactSignal, extraFields: extraFields);
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
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, jsonOptions);
        extraFields?.Invoke(payload);
        if (count == 0)
            AddFreshnessHint(payload, reader);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphZeroJsonResult(DbReader reader, string resultsKey, JsonSerializerOptions jsonOptions, bool graphAvailable,
        ExactQuerySignal? exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: graphAvailable);
        if (!graphAvailable)
        {
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (exactSignal != null)
            AddExactGraphJsonFields(payload, exactSignal.Value);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, jsonOptions);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphJsonResult<T>(T result, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonOptions)!.AsObject();
        AddExactGraphJsonFields(payload, exactSignal);
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResult<T>(T result, JsonSerializerOptions jsonOptions, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonOptions)!.AsObject();
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResultWithExactSignal<T>(T result, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonOptions)!.AsObject();
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

    private static bool TryParsePositiveInt(string rawValue, string optionName, out int value, out string? error)
    {
        if (string.Equals(optionName, "--max-line-width", StringComparison.Ordinal))
            return TryParseNonNegativeInt(rawValue, optionName, out value, out error);

        if (int.TryParse(rawValue, out value) && value > 0)
        {
            error = null;
            return true;
        }

        value = 0;
        error = $"Error: {optionName} requires a positive integer, got '{rawValue}'. Hint: retry with `{optionName} 1` or another positive integer.";
        return false;
    }

    private static bool TryParseNonNegativeInt(string rawValue, string optionName, out int value, out string? error)
    {
        if (int.TryParse(rawValue, out value) && value >= 0)
        {
            error = null;
            return true;
        }

        value = 0;
        error = $"Error: {optionName} requires a non-negative integer, got '{rawValue}'. Hint: retry with `{optionName} 0` or another non-negative integer.";
        return false;
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
            error = $"Error: {optionName} requires a value.";
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
            error = $"Error: {optionName} requires a value.";
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
                error = $"Error: {optionName} requires a value.";
                return false;
            }

            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"Error: {optionName} requires a value.";
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
            error = $"Error: {optionName} requires a value.";
            return false;
        }
        if (optionName != "--query" && IsRejectedSeparatedStringValue(candidate, allowSeparatedDashPrefixedLiteralValue))
        {
            value = null;
            error = allowSeparatedDashPrefixedLiteralValue && candidate.StartsWith("--", StringComparison.Ordinal)
                ? $"Error: {optionName} requires a value. Hint: if the literal value starts with `--`, pass it as `{optionName}=<value>`."
                : $"Error: {optionName} requires a value.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            value = null;
            error = $"Error: {optionName} requires a value.";
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
        if (!ValueTakingOptions.Contains(candidate))
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
    /// Offsetless inputs are treated as local time (matching prior behavior).
    /// ISO 8601形式のみで--since値をパースする。MM/dd/yyyyなどロケール依存の曖昧な形式は拒否する。
    /// オフセットなしの入力はローカル時刻として扱う（従来の動作を維持）。
    /// </summary>
    internal static bool TryParseIso8601Since(string value, out DateTime result)
    {
        if (DateTimeOffset.TryParseExact(value, Iso8601Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            result = dto.UtcDateTime;
            return true;
        }
        result = default;
        return false;
    }
}

public sealed class QueryCommandOptions
{
    public string DbPath { get; init; } = Path.Combine(".cdidx", "codeindex.db");
    public bool DbPathExplicit { get; init; }
    public bool Json { get; init; }
    public int Limit { get; init; } = 20;
    public string? Lang { get; init; }
    public string? Kind { get; init; }
    public string? Query { get; init; }
    public bool RawFts { get; init; }
    public bool IncludeBody { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public bool ContextAfterExplicit { get; init; }
    public int? FocusLine { get; init; }
    public int? FocusColumn { get; init; }
    public int FocusLength { get; init; } = 1;
    public int SnippetLines { get; init; } = SearchSnippetFormatter.DefaultSnippetLines;
    public int MaxLineWidth { get; init; } = LineWidthFormatter.DefaultMaxLineWidth;
    public List<string> PathPatterns { get; init; } = [];
    public List<string> ExcludePaths { get; init; } = [];
    public bool ExcludeTests { get; init; }
    public bool CountOnly { get; init; }
    public DateTime? Since { get; init; }
    public bool NoDedup { get; init; }
    public bool Exact { get; init; }
    public bool ExactName { get; init; }
    public bool ExactSubstring { get; init; }
    public List<string> ExtraNames { get; init; } = [];
    public string? ParseError { get; init; }
}
