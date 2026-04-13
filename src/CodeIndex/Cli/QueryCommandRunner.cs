using System.Globalization;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

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

    public static int RunSearch(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            return CommandExitCodes.UsageError;
        }
        if (options.Query == null)
        {
            Console.Error.WriteLine("Error: search requires a query argument");
            Console.Error.WriteLine("Usage: cdidx search <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--snippet-lines <n>] [--fts]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.Search(options.Query, options.Limit, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, options.Exact);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                    Console.WriteLine(options.Json ? JsonSerializer.Serialize(new { count = 0, files = 0 }, jsonOptions) : "0");
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No results found.");
                    WriteZeroResultHints(options, reader);
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(SearchSnippetFormatter.ToCompactResult(r, options.Query, options.SnippetLines, options.Exact), jsonOptions));
            }
            else
            {
                foreach (var r in results)
                {
                    Console.WriteLine($"{r.Path}:{r.StartLine}-{r.EndLine}");
                    var snippetLines = SearchSnippetFormatter.Format(r.Content, options.Query, options.SnippetLines);
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: definition requires a symbol query argument");
            Console.Error.WriteLine("Usage: cdidx definition <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>] [--body]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.GetDefinitions(options.Query, options.Limit, options.Kind, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, options.Exact);
            if (results.Count == 0)
            {
                ExactZeroHint? zeroHint = null;
                if (options.Exact && options.Query != null)
                {
                    // definitions wrap symbols; reuse CountSymbolMatches for the saturated-probe
                    // fallback. Codex #88 4th-pass optimization: single-probe-first.
                    zeroHint = ExactZeroHint.FromProbe<DefinitionResult>(
                        n => reader.GetDefinitions(options.Query, n, options.Kind, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                        () => reader.CountSymbolMatches(new[] { options.Query }, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since),
                        r => r.Name);
                }
                EmitExactZeroResult(options, jsonOptions, "definitions", zeroHint,
                    extraHumanHints: () => { WriteKindHint(options.Kind, reader); WriteZeroResultHints(options, reader, "Try 'search' for full-text matches instead of symbol lookup."); });
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: references requires a symbol query argument");
            Console.Error.WriteLine("Usage: cdidx references <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.SearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact);
            if (results.Count == 0)
            {
                // #88 exact-zero hint: only meaningful when the graph table is actually available
                // (otherwise the degraded-graph note takes precedence and the hint would
                // compound-confuse users).
                // #88: graph 使用可能時のみ hint。degraded 時は既存の degraded 通知を優先。
                ExactZeroHint? zeroHint = null;
                if (options.Exact && options.Query != null && reader._hasReferencesTable)
                {
                    // Single-probe-first; codex #88 4th-pass optimization.
                    zeroHint = ExactZeroHint.FromProbe<ReferenceResult>(
                        n => reader.SearchReferences(options.Query, n, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                        () => reader.CountReferenceMatches(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                        r => r.SymbolName);
                }

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new Dictionary<string, object?> { ["count"] = 0, ["files"] = 0, ["graph_table_available"] = reader._hasReferencesTable, ["degraded"] = !reader._hasReferencesTable };
                        if (zeroHint != null) payload["exact_zero_hint"] = zeroHint;
                        Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        if (zeroHint != null) Console.Error.WriteLine(zeroHint.ToHumanLine());
                    }
                }
                else if (options.Json)
                {
                    if (!reader._hasReferencesTable)
                        WriteDegradedGraphZeroResult("references", json: true, graphAvailable: false, jsonOptions);
                    else if (zeroHint != null)
                        // Use the command-specific empty-array key ("references") to match the
                        // existing WriteDegradedGraphZeroResult envelope shape for this command.
                        // Consumers that special-cased the graph degraded envelope on `references`
                        // already handle this shape. Codex #88 review (4th pass).
                        // graph 系コマンドは既存 degraded envelope と同じコマンド固有キーを使う。
                        Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["count"] = 0,
                            ["references"] = Array.Empty<object>(),
                            ["exact_zero_hint"] = zeroHint,
                        }, jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine("No references found.");
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult("references", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    if (zeroHint != null) Console.Error.WriteLine(zeroHint.ToHumanLine());
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: callers requires a symbol query argument");
            Console.Error.WriteLine("Usage: cdidx callers <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.GetCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact);
            if (results.Count == 0)
            {
                ExactZeroHint? zeroHint = null;
                if (options.Exact && options.Query != null && reader._hasReferencesTable)
                {
                    // Single-probe-first; codex #88 4th-pass optimization.
                    zeroHint = ExactZeroHint.FromProbe<CallerResult>(
                        n => reader.GetCallers(options.Query, n, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                        () => reader.CountCallerMatches(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                        r => r.CalleeName);
                }

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new Dictionary<string, object?> { ["count"] = 0, ["files"] = 0, ["graph_table_available"] = reader._hasReferencesTable, ["degraded"] = !reader._hasReferencesTable };
                        if (zeroHint != null) payload["exact_zero_hint"] = zeroHint;
                        Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        if (zeroHint != null) Console.Error.WriteLine(zeroHint.ToHumanLine());
                    }
                }
                else if (options.Json)
                {
                    if (!reader._hasReferencesTable)
                        WriteDegradedGraphZeroResult("callers", json: true, graphAvailable: false, jsonOptions);
                    else if (zeroHint != null)
                        // Use the command-specific empty-array key ("callers") matching the
                        // existing WriteDegradedGraphZeroResult shape for this command.
                        // Codex #88 review (4th pass) — envelope parity.
                        Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["count"] = 0,
                            ["callers"] = Array.Empty<object>(),
                            ["exact_zero_hint"] = zeroHint,
                        }, jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine("No callers found.");
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult("callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    if (zeroHint != null) Console.Error.WriteLine(zeroHint.ToHumanLine());
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
            }
            else
            {
                foreach (var r in results)
                    Console.WriteLine($"{r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                var callerFileCount = results.Select(r => r.Path).Distinct().Count();
                Console.Error.WriteLine($"({results.Count} callers in {callerFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallees(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: callees requires a caller query argument");
            Console.Error.WriteLine("Usage: cdidx callees <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--kind <kind>]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.GetCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact);
            if (results.Count == 0)
            {
                ExactZeroHint? zeroHint = null;
                if (options.Exact && options.Query != null && reader._hasReferencesTable)
                {
                    // callees filters on container_name, so CallerName carries the relaxed match name.
                    // Single-probe-first; codex #88 4th-pass optimization.
                    zeroHint = ExactZeroHint.FromProbe<CalleeResult>(
                        n => reader.GetCallees(options.Query, n, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                        () => reader.CountCalleeMatches(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                        r => r.CallerName ?? string.Empty);
                }

                if (options.CountOnly)
                {
                    if (options.Json)
                    {
                        var payload = new Dictionary<string, object?> { ["count"] = 0, ["files"] = 0, ["graph_table_available"] = reader._hasReferencesTable, ["degraded"] = !reader._hasReferencesTable };
                        if (zeroHint != null) payload["exact_zero_hint"] = zeroHint;
                        Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        if (zeroHint != null) Console.Error.WriteLine(zeroHint.ToHumanLine());
                    }
                }
                else if (options.Json)
                {
                    if (!reader._hasReferencesTable)
                        WriteDegradedGraphZeroResult("callees", json: true, graphAvailable: false, jsonOptions);
                    else if (zeroHint != null)
                        // Command-specific empty-array key ("callees") matching existing
                        // WriteDegradedGraphZeroResult shape. Codex #88 review (4th pass).
                        Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["count"] = 0,
                            ["callees"] = Array.Empty<object>(),
                            ["exact_zero_hint"] = zeroHint,
                        }, jsonOptions));
                }
                else
                {
                    Console.Error.WriteLine("No callees found.");
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult("callees", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    if (zeroHint != null) Console.Error.WriteLine(zeroHint.ToHumanLine());
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                foreach (var r in results)
                    Console.WriteLine(JsonSerializer.Serialize(r, jsonOptions));
            }
            else
            {
                foreach (var r in results)
                    Console.WriteLine($"{r.ReferenceKind,-12} {r.CalleeName,-32} {r.Path}:{r.FirstLine}  <- {r.CallerName ?? "<top-level>"} ({r.ReferenceCount} refs)");
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
        return (deduped.Count == 0 ? null : deduped, hadExplicitInput);
    }

    public static int RunSymbols(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            return CommandExitCodes.UsageError;
        }
        var (symbolQueries, hadExplicitInput) = BuildSymbolQueryList(options);
        if (hadExplicitInput && symbolQueries == null)
        {
            // Fail closed: an explicit name/query was provided but normalized to empty (e.g. "|",
            // --name ""). Returning null here would broaden into an unfiltered symbol dump. /
            // 明示入力が正規化で空になった場合、null のまま検索すると全件検索に化けるので必ず拒否する。
            Console.Error.WriteLine("Error: symbol name list is empty after normalization. Check for empty --name values or bare '|' separators. / シンボル名リストが正規化の結果空です。--name の空値や単独の '|' を確認してください。");
            return CommandExitCodes.UsageError;
        }
        if (symbolQueries != null && symbolQueries.Count > MaxSymbolQueryNames)
        {
            Console.Error.WriteLine($"Error: too many symbol names ({symbolQueries.Count}); maximum is {MaxSymbolQueryNames}. Split the request into smaller batches. / シンボル名が多すぎます（{symbolQueries.Count}件、上限は {MaxSymbolQueryNames} 件）。分割してください。");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.SearchSymbols(symbolQueries, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, options.Exact);
            if (results.Count == 0)
            {
                // #88 exact-zero hint: re-run with exact=false to tell callers whether --exact
                // filtered out real rows (typo/casing) or the name is genuinely absent.
                // #88: --exact 0 件時は substring で再検索してヒントを出す。
                ExactZeroHint? zeroHint = null;
                if (options.Exact && symbolQueries != null && symbolQueries.Count > 0)
                {
                    // Single-probe first: LIMIT = SampleLimit + 1 covers both existence AND
                    // the true count when the relaxed set is small (<= SampleLimit, the common
                    // typo case). Only saturated probes fall back to CountSymbolMatches for the
                    // accurate total. Codex #88 4th-pass review: avoid 2 LIKE scans per exact miss.
                    // 単一 probe でほとんどのケースを 1 クエリで済ませ、飽和時のみ COUNT にフォールバック。
                    zeroHint = ExactZeroHint.FromProbe<SymbolResult>(
                        n => reader.SearchSymbols(symbolQueries, n, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false),
                        () => reader.CountSymbolMatches(symbolQueries, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since),
                        r => r.Name);
                }
                EmitExactZeroResult(options, jsonOptions, label: "symbols", zeroHint,
                    extraHumanHints: () => { WriteKindHint(options.Kind, reader); WriteZeroResultHints(options, reader); });
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (options.ParseError != null)
        {
            Console.Error.WriteLine(options.ParseError);
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.ListFiles(options.Query, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                    Console.WriteLine(options.Json ? JsonSerializer.Serialize(new { count = 0 }, jsonOptions) : "0");
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No files found.");
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader);
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (options.Query == null)
        {
            Console.Error.WriteLine("Error: excerpt requires a path argument");
            Console.Error.WriteLine("Usage: cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--db <path>] [--json]");
            return CommandExitCodes.UsageError;
        }

        if (options.StartLine == null)
        {
            Console.Error.WriteLine("Error: excerpt requires --start <line>");
            return CommandExitCodes.UsageError;
        }

        var endLine = options.EndLine ?? options.StartLine.Value;
        if (endLine < options.StartLine.Value)
        {
            Console.Error.WriteLine($"Error: --start ({options.StartLine.Value}) must be less than or equal to --end ({endLine}).");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var excerpt = reader.GetExcerpt(options.Query, options.StartLine.Value, endLine, options.ContextBefore, options.ContextAfter);
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

    public static int RunMap(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            var map = reader.GetRepoMap(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            WorkspaceMetadataEnricher.Enrich(map, options.DbPath);

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
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: inspect requires a symbol query argument");
            Console.Error.WriteLine("Usage: cdidx inspect <query> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--body]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var analysis = reader.AnalyzeSymbol(options.Query, options.Limit, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Exact);
            WorkspaceMetadataEnricher.Enrich(analysis, options.DbPath);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(analysis, jsonOptions));
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
                if (!analysis.GraphTableAvailable)
                    Console.WriteLine("Graph Table          : MISSING — empty References/Callers/Callees are degraded, NOT real zero-hit results.");
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
            Console.Error.WriteLine("Error: outline requires a file path.");
            Console.Error.WriteLine("Usage: cdidx outline <path> [--db <path>] [--json]");
            return CommandExitCodes.UsageError;
        }

        var filePath = cmdArgs[0].Replace('\\', '/');
        var options = ParseArgs(cmdArgs[1..], jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            var outline = reader.GetOutline(filePath);
            if (outline == null)
            {
                if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new { path = filePath, error = "file not found in index" }, jsonOptions));
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
                Console.WriteLine($"# {outline.Path}  ({outline.Lang ?? "unknown"}, {outline.TotalLines} lines, {outline.SymbolCount} symbols)");
                Console.WriteLine();
                foreach (var sym in outline.Symbols)
                {
                    // Indent nested symbols under their container / コンテナ内のシンボルをインデント
                    var indent = sym.ContainerName != null ? "    " : "";
                    var ret = sym.ReturnType != null ? $": {sym.ReturnType} " : "";
                    var sig = sym.Signature ?? $"{sym.Kind} {sym.Name}";
                    // Avoid duplicating visibility when signature already contains it
                    // シグネチャに既に visibility が含まれている場合は重複を避ける
                    var vis = sym.Visibility != null && !sig.TrimStart().StartsWith(sym.Visibility, StringComparison.Ordinal)
                        ? $"{sym.Visibility} "
                        : "";
                    Console.WriteLine($"  {sym.Line,5}  {indent}{vis}{sig} {ret}");
                }
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunStatus(string[] cmdArgs, JsonSerializerOptions jsonOptions, string? appVersion = null)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, options.DbPath);
            // Attach runtime metadata / ランタイムメタデータを付加
            status.SymbolKinds = reader.GetSymbolKindCounts();
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            if (appVersion != null)
                status.Version = appVersion;

            // Build one-line summary for AI orientation / AI向けの1行サマリーを構築
            var topLangs = status.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key);
            var freshness = status.IndexedAt.HasValue
                ? (DateTime.UtcNow - status.IndexedAt.Value).TotalMinutes < 5 ? "fresh" : "stale"
                : "unknown";
            var dirty = status.GitIsDirty == true ? ", dirty" : "";
            var degraded = (!status.GraphTableAvailable || !status.IssuesTableAvailable) ? ", DEGRADED" : "";
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
                // #86: tell the user when `--exact` is running on the ASCII NOCASE fallback.
                // #86: --exact が ASCII NOCASE fallback で動いているときは明示する。
                if (!status.FoldReady)
                    Console.WriteLine("WARN    : --exact falls back to ASCII COLLATE NOCASE. Non-ASCII casing (e.g. Ä/ä) won't match. Run `cdidx index . --rebuild` to upgrade.");
                var totalLangs = FileIndexer.GetLanguageExtensions().Values.Distinct().Count();
                var symbolLangs = SymbolExtractor.GetSupportedLanguages().Count;
                Console.WriteLine($"Support : {totalLangs} detected, {symbolLangs} with symbols, {status.GraphSupportedLanguages?.Count ?? 0} with graph");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunImpact(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Console.Error.WriteLine("Error: impact requires a symbol query argument");
            Console.Error.WriteLine("Usage: cdidx impact <symbol> [--db <path>] [--json] [--limit <n>] [--lang <lang>] [--path <pattern>] [--exclude-path <pattern>] [--exclude-tests] [--depth <n>]");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options.DbPath, reader =>
        {
            var maxDepth = options.ContextAfter > 0 ? options.ContextAfter : 5; // --depth is parsed into ContextAfter
            var (results, truncated) = reader.GetTransitiveCallers(options.Query, maxDepth, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                    Console.WriteLine(options.Json ? JsonSerializer.Serialize(new { count = 0, files = 0, graph_table_available = reader._hasReferencesTable, degraded = !reader._hasReferencesTable }, jsonOptions) : "0");
                else if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult("callers", json: true, graphAvailable: false, jsonOptions);
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No impact found.");
                    WriteGraphSupportHint(options.Lang);
                    WriteDegradedGraphZeroResult("callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { query = options.Query, count = results.Count, max_depth = maxDepth, truncated, callers = results }, jsonOptions));
            }
            else
            {
                var grouped = results.GroupBy(r => r.Depth).OrderBy(g => g.Key);
                foreach (var group in grouped)
                {
                    Console.Error.WriteLine($"--- Depth {group.Key} ---");
                    foreach (var r in group)
                    {
                        var indent = new string(' ', (r.Depth - 1) * 2);
                        Console.WriteLine($"  {indent}{r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                    }
                }
                var fileCount = results.Select(r => r.Path).Distinct().Count();
                var truncNote = truncated ? " [TRUNCATED]" : "";
                Console.Error.WriteLine($"\n({results.Count} callers across {fileCount} files, max depth {maxDepth}{truncNote})");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunDeps(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            var reverse = cmdArgs.Any(a => a == "--reverse");
            var results = reader.GetFileDependencies(options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse);
            if (results.Count == 0)
            {
                if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult("edges", json: true, graphAvailable: false, jsonOptions);
                else if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new { count = 0, edges = Array.Empty<object>(), graph_table_available = true, degraded = false }, jsonOptions));
                else
                {
                    Console.Error.WriteLine("No file dependencies found.");
                    WriteDegradedGraphZeroResult("edges", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return CommandExitCodes.NotFound;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { count = results.Count, edges = results }, jsonOptions));
            }
            else
            {
                foreach (var r in results)
                {
                    var syms = r.Symbols.Length > 60 ? r.Symbols[..57] + "..." : r.Symbols;
                    Console.WriteLine($"{r.SourcePath,-45} -> {r.TargetPath,-45} ({r.ReferenceCount} refs: {syms})");
                }
                Console.Error.WriteLine($"({results.Count} dependency edges)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunHotspots(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            var results = reader.GetSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                    Console.WriteLine(options.Json ? JsonSerializer.Serialize(new { count = 0, files = 0, graph_table_available = reader._hasReferencesTable, degraded = !reader._hasReferencesTable }, jsonOptions) : "0");
                else if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult("hotspots", json: true, graphAvailable: false, jsonOptions);
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No symbol hotspots found.");
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult("hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Symbol.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                var items = results.Select(r => new { name = r.Symbol.Name, kind = r.Symbol.Kind, path = r.Symbol.Path, line = r.Symbol.Line, reference_count = r.ReferenceCount, visibility = r.Symbol.Visibility, container = r.Symbol.ContainerName });
                Console.WriteLine(JsonSerializer.Serialize(new { count = results.Count, hotspots = items }, jsonOptions));
            }
            else
            {
                foreach (var (s, refCount) in results)
                {
                    var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                    Console.WriteLine($"{refCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}");
                }
                Console.Error.WriteLine($"({results.Count} symbol hotspots)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunUnused(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            // Warn if user specified an unsupported language / 未対応言語の場合は警告
            if (options.Lang != null && !ReferenceExtractor.SupportsLanguage(options.Lang) && !options.Json)
                Console.Error.WriteLine($"Warning: '{options.Lang}' does not support reference extraction. Results may contain false positives.");

            var results = reader.GetUnusedSymbols(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                    Console.WriteLine(options.Json ? JsonSerializer.Serialize(new { count = 0, files = 0, graph_table_available = reader._hasReferencesTable, degraded = !reader._hasReferencesTable }, jsonOptions) : "0");
                else if (options.Json && !reader._hasReferencesTable)
                    WriteDegradedGraphZeroResult("unused", json: true, graphAvailable: false, jsonOptions);
                else if (!options.Json)
                {
                    Console.Error.WriteLine("No unused symbols found.");
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult("unused", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return options.CountOnly ? CommandExitCodes.Success : CommandExitCodes.NotFound;
            }

            if (options.CountOnly)
            {
                var fc = results.Select(r => r.Path).Distinct().Count();
                Console.WriteLine(options.Json
                    ? JsonSerializer.Serialize(new { count = results.Count, files = fc }, jsonOptions)
                    : $"{results.Count}");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { count = results.Count, symbols = results }, jsonOptions));
            }
            else
            {
                foreach (var s in results)
                {
                    var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                    var container = s.ContainerName != null ? $" in {s.ContainerName}" : "";
                    Console.WriteLine($"{ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}{container}");
                }
                Console.Error.WriteLine($"({results.Count} potentially unused symbols)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunValidate(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(cmdArgs, jsonDefault: false);

        return WithDb(options.DbPath, reader =>
        {
            var issues = reader.GetIssues(options.Kind, options.PathPatterns);
            var issuesAvailable = reader._hasIssuesTable;
            if (issues.Count == 0)
            {
                if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new { count = 0, issues = Array.Empty<object>(), issues_table_available = issuesAvailable, degraded = !issuesAvailable }, jsonOptions));
                else if (!issuesAvailable)
                    Console.Error.WriteLine("WARN: file_issues table missing in this index (legacy or read-only DB) — validate output is degraded, not a real clean signal.");
                else
                    Console.Error.WriteLine("No encoding issues found.");
                return CommandExitCodes.Success;
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { count = issues.Count, issues }, jsonOptions));
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
        var json = cmdArgs.Any(a => a == "--json");

        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();

        // Build a consolidated view: language -> (extensions, hasSymbols, hasGraph)
        // 統合ビュー: 言語 -> (拡張子, シンボル対応, グラフ対応)
        var allLangs = new Dictionary<string, (List<string> Extensions, bool Symbols, bool Graph)>(StringComparer.Ordinal);

        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                info = (new List<string>(), symbolLangs.Contains(lang), graphLangs.Contains(lang));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        // Sort by language name / 言語名でソート
        var sorted = allLangs.OrderBy(kv => kv.Key).ToList();

        if (json)
        {
            var entries = sorted.Select(kv => new
            {
                lang = kv.Key,
                extensions = kv.Value.Extensions.OrderBy(e => e).ToList(),
                symbol_extraction = kv.Value.Symbols,
                graph_queries = kv.Value.Graph,
            });
            Console.WriteLine(JsonSerializer.Serialize(new { languages = entries }, jsonOptions));
        }
        else
        {
            Console.WriteLine($"{"Language",-14} {"Extensions",-36} {"Symbols",-9} {"Graph",-7}");
            Console.WriteLine(new string('-', 66));
            foreach (var (lang, info) in sorted)
            {
                var exts = string.Join(" ", info.Extensions.OrderBy(e => e));
                var sym = info.Symbols ? "yes" : "-";
                var graph = info.Graph ? "yes" : "-";
                Console.WriteLine($"{lang,-14} {exts,-36} {sym,-9} {graph,-7}");
            }
            Console.Error.WriteLine($"\n({sorted.Count} languages)");
        }
        return CommandExitCodes.Success;
    }

    public static QueryCommandOptions ParseArgs(string[] args, bool jsonDefault)
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
        int snippetLines = SearchSnippetFormatter.DefaultSnippetLines;
        var pathPatterns = new List<string>();
        var excludePaths = new List<string>();
        bool excludeTests = false;
        DateTime? since = null;
        bool noDedup = false;
        bool exact = false;
        string? parseError = null;
        var extraNames = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--no-json":
                    json = false;
                    break;
                case "--limit" when i + 1 < args.Length:
                case "--top" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out limit) || limit <= 0)
                    {
                        Console.Error.WriteLine($"Error: --limit requires a positive integer, got '{args[i]}'");
                        limit = 20;
                    }
                    break;
                case "--lang" when i + 1 < args.Length:
                    lang = args[++i];
                    break;
                case "--kind" when i + 1 < args.Length:
                    kind = args[++i];
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
                case "--depth" when i + 1 < args.Length:
                    contextAfter = ParseNonNegativeInt(args[++i], "--depth"); // reused as depth for impact / impact用に再利用
                    break;
                case "--reverse":
                    break; // handled by specific commands / 特定コマンドで処理
                case "--path" when i + 1 < args.Length:
                    // Repeatable; multiple values OR together / 繰り返し可、複数値は OR で結合
                    pathPatterns.Add(args[++i]);
                    break;
                case "--exclude-path" when i + 1 < args.Length:
                    excludePaths.Add(args[++i]);
                    break;
                case "--exclude-tests":
                    excludeTests = true;
                    break;
                case "--since" when i + 1 < args.Length:
                    if (TryParseIso8601Since(args[++i], out var parsedSince))
                        since = parsedSince;
                    else
                        parseError = $"Error: could not parse --since value '{args[i]}' as a date/time. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).";
                    break;
                case "--since":
                    parseError = "Error: --since requires a value. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).";
                    break;
                case "--start" when i + 1 < args.Length:
                    startLine = ParsePositiveInt(args[++i], "--start");
                    break;
                case "--end" when i + 1 < args.Length:
                    endLine = ParsePositiveInt(args[++i], "--end");
                    break;
                case "--before" when i + 1 < args.Length:
                    contextBefore = ParseNonNegativeInt(args[++i], "--before");
                    break;
                case "--after" when i + 1 < args.Length:
                    contextAfter = ParseNonNegativeInt(args[++i], "--after");
                    break;
                case "--name" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                    // Repeatable; OR-joined with other --name values and extra positional names / 繰り返し可、他の --name や追加の positional 引数と OR 結合
                    extraNames.Add(args[++i]);
                    break;
                case "--name":
                    parseError = "Error: --name requires a value (symbol name pattern). / --name には値（シンボル名パターン）が必要です。";
                    break;
                case "--snippet-lines" when i + 1 < args.Length:
                    snippetLines = SearchSnippetFormatter.ClampSnippetLines(ParsePositiveInt(args[++i], "--snippet-lines") ?? SearchSnippetFormatter.DefaultSnippetLines);
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Warning: unknown option '{args[i]}' (ignored) / 不明なオプション '{args[i]}'（無視されます）");
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
            SnippetLines = snippetLines,
            PathPatterns = pathPatterns,
            ExcludePaths = excludePaths,
            ExcludeTests = excludeTests,
            CountOnly = countOnly,
            Since = since,
            NoDedup = noDedup,
            Exact = exact,
            ExtraNames = extraNames,
            ParseError = parseError,
        };
    }

    private static int WithDb(string dbPath, Func<DbReader, int> action)
    {
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
            Console.Error.WriteLine("Run 'cdidx index <projectPath>' first to create the index.");
            return CommandExitCodes.DatabaseError;
        }

        Database.DbDebug.ResetContext();
        try
        {
            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection);
            return action(reader);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: database error: {ex.Message}");
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
    private static void WriteZeroResultHints(QueryCommandOptions options, DbReader reader, string? alternativeHint = null)
    {
        var (fileCount, indexedAt) = reader.GetFreshnessHint();
        if (fileCount == 0)
        {
            Console.Error.WriteLine("Hint: the index is empty. Run 'cdidx index <projectPath>' first.");
            return;
        }

        if (options.Lang != null || options.PathPatterns.Count > 0 || options.ExcludeTests || options.ExcludePaths.Count > 0)
            Console.Error.WriteLine("Hint: try removing --lang, --path, --exclude-path, or --exclude-tests to broaden the search.");

        if (alternativeHint != null)
            Console.Error.WriteLine($"Hint: {alternativeHint}");

        if (indexedAt.HasValue && (DateTime.UtcNow - indexedAt.Value).TotalHours > 24)
            Console.Error.WriteLine("Hint: the index may be stale. Run 'cdidx index <projectPath>' to refresh.");
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

    private static void WriteGraphSupportHint(string? lang)
    {
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            Console.Error.WriteLine($"Note: call-graph queries are not indexed for '{lang}'. Use search, definition, excerpt, or files instead.");
    }

    // Emit a zero-result payload that distinguishes "real 0 hits" from "graph table missing
    // (degraded)". Without this, AI agents and humans cannot tell the index from a legacy /
    // read-only DB apart from a DB that genuinely has no callers for the query.
    // graph テーブル欠損による 0 と本物の 0 を JSON で区別できるようにする。
    /// <summary>
    /// Emit the zero-result payload for symbol / graph commands, attaching the #88 exact-zero
    /// hint when present. Shared across `symbols` / `definition` / `references` / `callers` /
    /// `callees` so every `--exact` path has the same contract.
    /// zero 件時の出力を共通化し、#88 の `exact_zero_hint` を付与する。
    /// </summary>
    private static void EmitExactZeroResult(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string label,
        ExactZeroHint? hint,
        Action? extraHumanHints = null,
        string? filesKey = "files")
    {
        if (options.CountOnly)
        {
            if (options.Json)
            {
                var payload = new Dictionary<string, object?> { ["count"] = 0 };
                if (filesKey != null) payload[filesKey] = 0;
                if (hint != null) payload["exact_zero_hint"] = hint;
                Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
            }
            else
            {
                Console.WriteLine("0");
                if (hint != null) Console.Error.WriteLine(hint.ToHumanLine());
            }
        }
        else if (options.Json)
        {
            if (hint != null)
            {
                // Match the envelope shape used by WriteDegradedGraphZeroResult so JSON consumers
                // can discriminate a summary envelope from a result row with the same `results: []`
                // marker. Codex #88 review: CLI --json is a row-stream by default and the hint
                // envelope needs to look unambiguously like "this is a zero-result summary", not a
                // row DTO. `results: []` combined with a sibling `exact_zero_hint` is the signal.
                // 既存の degraded envelope と同じ `results: []` marker を付け、consumer が行とエンベロープを
                // 確実に判別できるようにする。
                Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["count"] = 0,
                    ["results"] = Array.Empty<object>(),
                    ["exact_zero_hint"] = hint,
                }, jsonOptions));
            }
        }
        else
        {
            Console.Error.WriteLine($"No {label} found.");
            extraHumanHints?.Invoke();
            if (hint != null) Console.Error.WriteLine(hint.ToHumanLine());
        }
    }

    private static void WriteDegradedGraphZeroResult(string resultsKey, bool json, bool graphAvailable, JsonSerializerOptions jsonOptions)
    {
        if (graphAvailable) return;
        if (json)
        {
            var payload = new Dictionary<string, object?>
            {
                ["count"] = 0,
                [resultsKey] = Array.Empty<object>(),
                ["graph_table_available"] = false,
                ["degraded"] = true,
                ["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.",
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
        }
        else
        {
            Console.Error.WriteLine("WARN: symbol_references table missing — this 0-result is degraded, not authoritative.");
        }
    }

    private static int? ParsePositiveInt(string rawValue, string optionName)
    {
        if (!int.TryParse(rawValue, out var value) || value <= 0)
        {
            Console.Error.WriteLine($"Error: {optionName} requires a positive integer, got '{rawValue}'");
            return null;
        }

        return value;
    }

    private static int ParseNonNegativeInt(string rawValue, string optionName)
    {
        if (!int.TryParse(rawValue, out var value) || value < 0)
        {
            Console.Error.WriteLine($"Error: {optionName} requires a non-negative integer, got '{rawValue}'");
            return 0;
        }

        return value;
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
    public int SnippetLines { get; init; } = SearchSnippetFormatter.DefaultSnippetLines;
    public List<string> PathPatterns { get; init; } = [];
    public List<string> ExcludePaths { get; init; } = [];
    public bool ExcludeTests { get; init; }
    public bool CountOnly { get; init; }
    public DateTime? Since { get; init; }
    public bool NoDedup { get; init; }
    public bool Exact { get; init; }
    public List<string> ExtraNames { get; init; } = [];
    public string? ParseError { get; init; }
}
