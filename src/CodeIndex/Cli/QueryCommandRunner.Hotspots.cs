using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunHotspots(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        bool groupByName = cmdArgs.Any(a => a == "--group-by-name");
        var previewOptionError = ValidatePreviewOptions("hotspots", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
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
            CommandErrorWriter.WriteStderr(groupByError);
            CommandErrorWriter.WriteStderr("Usage: cdidx hotspots [--db <path>] [--json] [--summary-only] [--max-json-bytes <n>] [--limit <n>] [--kind <kind>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]");
            return CommandExitCodes.UsageError;
        }
        if (options.MaxJsonBytes.HasValue && !options.Json)
        {
            WriteUsageError(
                "--max-json-bytes is only supported with hotspots JSON output.",
                GetUsageLineOrThrow("hotspots"),
                "Use `cdidx hotspots --json --max-json-bytes <n>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.SummaryOnly && !options.Json)
        {
            WriteUsageError(
                "--summary-only is only supported with hotspots JSON output.",
                GetUsageLineOrThrow("hotspots"),
                "Use `cdidx hotspots --json --summary-only`.");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphLiveness("hotspots", options.CountOnly ? "count_hotspots" : "read_hotspots", options, groupBy: groupBy);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var zeroResultSqlGraphSignal = NarrowSqlGraphContractSignal(
                baseSqlGraphSignal,
                reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
            if (groupBy == HotspotsGroupedByNameKind)
            {
                if (options.CountOnly)
                {
                    var countSummary = reader.CountGroupedSymbolHotspots(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                    WriteGraphLiveness("hotspots", "write_output", options, groupBy: groupBy, rows: countSummary.Count);
                    var countSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
                    if (options.Json)
                    {
                        var payload = countSummary.Count == 0
                            ? BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: true, graphAvailable: reader._hasReferencesTable, queryOptions: options)
                            : new JsonObject
                            {
                                ["count"] = countSummary.Count,
                                ["files"] = countSummary.FileCount,
                                ["definition_site_total"] = countSummary.DefinitionSiteTotal,
                                ["grouped_by"] = HotspotsGroupedByNameKind,
                            };
                        if (options.SummaryOnly)
                            payload["summary_only"] = true;
                        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                        AddSqlGraphContractJsonFields(payload, countSqlGraphSignal);
                        var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                        return writeExitCode == CommandExitCodes.Success
                            ? (countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success)
                            : writeExitCode;
                    }
                    else
                    {
                        Console.WriteLine($"{countSummary.Count}");
                        WriteSqlGraphContractWarningIfNeeded(json: false, countSqlGraphSignal, reader, options);
                    }
                    return countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success;
                }

                var groupedResults = reader.GetGroupedSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                WriteGraphLiveness("hotspots", "write_output", options, groupBy: groupBy, rows: groupedResults.Count);
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
                            if (options.SummaryOnly)
                                payload["summary_only"] = true;
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                            return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                        }
                        else
                            WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, new ExactQuerySignal(true, HasMissingIndex: false, HasMissingTable: false, null));
                    }
                    else if (options.Json)
                    {
                        var payload = BuildGroupedHotspotsZeroJsonPayload(reader, jsonOptions, countOnly: false, graphAvailable: reader._hasReferencesTable, queryOptions: options);
                        if (options.SummaryOnly)
                            payload["summary_only"] = true;
                        AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                        var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                        return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                    }
                    else
                    {
                        CommandErrorWriter.WriteStderr(BuildZeroResultLine("No symbol hotspots found", options));
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return ZeroResultExitCode(options);
                }

                var definitionSiteTotal = groupedResults.Sum(g => g.DefinitionSites);

                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = groupedResults.Count,
                        ["definition_site_total"] = definitionSiteTotal,
                        ["grouped_by"] = HotspotsGroupedByNameKind,
                    };
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    else
                    {
                        var items = groupedResults
                            .Select(g => new GroupedSymbolHotspotJsonResult(
                                g.Symbol.Name,
                                g.Symbol.Kind,
                                g.Symbol.Path,
                                g.Symbol.Line,
                                g.ReferenceCount,
                                g.ReferenceScore,
                                g.RankingScore,
                                g.GenericNamePenalty,
                                g.Symbol.Visibility,
                                g.Symbol.ContainerName,
                                g.DefinitionSites,
                                g.Paths,
                                g.PathsTruncated,
                                BuildGroupedHotspotRepresentative(g),
                                g.DefinitionSiteDetails.Select(ToGroupedHotspotSiteJson).ToList()))
                            .ToList();
                        payload["hotspots"] = JsonSerializer.SerializeToNode(items, CliJsonSerializerContextFactory.Create(jsonOptions).ListGroupedSymbolHotspotJsonResult);
                    }
                    payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    return WriteHotspotsJsonPayload(payload, options, jsonOptions);
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
                    CommandErrorWriter.WriteStderr($"({groupedResults.Count} unique name/kind groups, {definitionSiteTotal} definition sites)");
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            if (groupBy == HotspotsGroupedByFile)
            {
                var fileHotspotSignal = reader.GetHotspotFamilySignal(options.Lang);
                if (options.CountOnly)
                {
                    var countSummary = reader.CountFileSymbolHotspots(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                    WriteGraphLiveness("hotspots", "write_output", options, groupBy: groupBy, rows: countSummary.Count);
                    var countSqlGraphSignal = countSummary.Count == 0
                        ? zeroResultSqlGraphSignal
                        : NarrowSqlGraphContractSignal(
                            baseSqlGraphSignal,
                            reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
                    if (options.Json)
                    {
                        var payload = new JsonObject
                        {
                            ["count"] = countSummary.Count,
                            ["files"] = countSummary.FileCount,
                            ["graph_table_available"] = reader._hasReferencesTable,
                            ["grouped_by"] = groupBy,
                        };
                        if (options.SummaryOnly)
                            payload["summary_only"] = true;
                        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                        AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                        AddSqlGraphContractJsonFields(payload, countSqlGraphSignal);
                        if (countSummary.Count == 0)
                            AddFreshnessHint(payload, reader);
                        var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                        return writeExitCode == CommandExitCodes.Success
                            ? (countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success)
                            : writeExitCode;
                    }
                    else
                    {
                        Console.WriteLine($"{countSummary.Count}");
                        WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                        WriteSqlGraphContractWarningIfNeeded(json: false, countSqlGraphSignal, reader, options);
                    }
                    return countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success;
                }

                var fileResults = reader.GetFileSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                WriteGraphLiveness("hotspots", "write_output", options, groupBy: groupBy, rows: fileResults.Count);
                var effectiveSqlGraphSignal = fileResults.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, fileResults.Select(result => result.Lang), options.Lang);

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
                            if (options.SummaryOnly)
                                payload["summary_only"] = true;
                            payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                            AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                            AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            AddFreshnessHint(payload, reader);
                            var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                            return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
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
                        var payload = BuildJsonZeroResultPayload(
                            reader,
                            jsonOptions,
                            resultsKey: options.SummaryOnly ? null : "hotspots",
                            graphTableAvailable: reader._hasReferencesTable,
                            degraded: !reader._hasReferencesTable || !fileHotspotSignal.Ready,
                            extraFields: payload =>
                            {
                                payload["grouped_by"] = groupBy;
                                AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                                AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                            });
                        if (options.SummaryOnly)
                            payload["summary_only"] = true;
                        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                        var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                        return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                    }
                    else
                    {
                        CommandErrorWriter.WriteStderr("No symbol hotspots found.");
                        WriteZeroResultHints(options, reader);
                        WriteKindHint(options.Kind, reader);
                        WriteLangHint(options.Lang, reader);
                        WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                        WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                        WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    }
                    return ZeroResultExitCode(options);
                }

                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = fileResults.Count,
                        ["files"] = fileResults.Count,
                        ["grouped_by"] = groupBy,
                    };
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    else
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
                        payload["hotspots"] = hotspots;
                    }
                    AddHotspotFamilyJsonFields(payload, fileHotspotSignal);
                    AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal);
                    payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                    if (options.Compact)
                    {
                        payload["compact"] = true;
                        payload["omitted_sections"] = new JsonArray();
                    }
                    return WriteHotspotsJsonPayload(payload, options, jsonOptions);
                }
                else
                {
                    foreach (var result in fileResults)
                    {
                        Console.WriteLine($"{result.ReferenceCount,5} refs  {result.SymbolCount,5} symbols  {result.Path}");
                    }
                    CommandErrorWriter.WriteStderr($"({fileResults.Count} file hotspots; grouped_by={groupBy})");
                    WriteHotspotFamilyWarningIfNeeded(json: false, fileHotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, effectiveSqlGraphSignal, reader, options);
                }
                return CommandExitCodes.Success;
            }

            var hotspotSignal = reader.GetHotspotFamilySignal(options.Lang);
            if (options.CountOnly)
            {
                var countSummary = reader.CountSymbolHotspots(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                WriteGraphLiveness("hotspots", "write_output", options, groupBy: groupBy, rows: countSummary.Count);
                var countSqlGraphSignal = countSummary.Count == 0
                    ? zeroResultSqlGraphSignal
                    : NarrowSqlGraphContractSignal(
                        baseSqlGraphSignal,
                        reader.ScopeMayIncludeSqlSymbols(options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests));
                if (options.Json)
                {
                    var payload = new JsonObject
                    {
                        ["count"] = countSummary.Count,
                        ["files"] = countSummary.FileCount,
                        ["graph_table_available"] = reader._hasReferencesTable,
                        ["grouped_by"] = groupBy,
                    };
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                    if (!reader._hasReferencesTable)
                        payload["degraded"] = true;
                    AddHotspotFamilyJsonFields(payload, hotspotSignal);
                    AddSqlGraphContractJsonFields(payload, countSqlGraphSignal);
                    if (countSummary.Count == 0)
                        AddFreshnessHint(payload, reader);
                    var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success
                        ? (countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success)
                        : writeExitCode;
                }
                else
                {
                    Console.WriteLine($"{countSummary.Count}");
                    if (!reader._hasReferencesTable)
                        CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, countSqlGraphSignal, reader, options);
                }
                return countSummary.Count == 0 ? ZeroResultExitCode(options) : CommandExitCodes.Success;
            }

            var results = reader.GetSymbolHotspots(options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            WriteGraphLiveness("hotspots", "write_output", options, groupBy: groupBy, rows: results.Count);
            var sqlGraphSignal = results.Count == 0
                ? zeroResultSqlGraphSignal
                : NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Symbol.Lang), options.Lang);
            if (results.Count == 0)
            {
                if (options.CountOnly)
                {
                    if (!options.Json)
                    {
                        Console.WriteLine("0");
                        if (!reader._hasReferencesTable)
                            CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
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
                        if (options.SummaryOnly)
                            payload["summary_only"] = true;
                        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                        if (!reader._hasReferencesTable)
                            payload["degraded"] = true;
                        AddHotspotFamilyJsonFields(payload, hotspotSignal);
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        AddFreshnessHint(payload, reader);
                        var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                        return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                    }
                }
                else if (options.Json && !reader._hasReferencesTable)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: options.SummaryOnly ? null : "hotspots",
                        graphTableAvailable: false,
                        degraded: true,
                        queryOptions: options,
                        extraFields: payload =>
                        {
                            payload["grouped_by"] = groupBy;
                            AddHotspotFamilyJsonFields(payload, hotspotSignal);
                            AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                        });
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                    var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: options.SummaryOnly ? null : "hotspots",
                        graphTableAvailable: true,
                        degraded: !hotspotSignal.Ready,
                        extraFields: payload =>
                    {
                        payload["grouped_by"] = groupBy;
                        AddHotspotFamilyJsonFields(payload, hotspotSignal);
                        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                    });
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                    var writeExitCode = WriteHotspotsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No symbol hotspots found", options));
                    WriteZeroResultHints(options, reader);
                    WriteKindHint(options.Kind, reader);
                    WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "hotspots", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["count"] = results.Count,
                    ["grouped_by"] = groupBy,
                };
                if (options.SummaryOnly)
                    payload["summary_only"] = true;
                else
                {
                    var items = results
                        .Select(r => new SymbolHotspotJsonResult(
                            r.Symbol.Name,
                            r.Symbol.Kind,
                            r.Symbol.Path,
                            r.Symbol.Line,
                            r.ReferenceCount,
                            r.ReferenceScore,
                            r.RankingScore,
                            r.GenericNamePenalty,
                            r.Symbol.Visibility,
                            r.Symbol.ContainerName))
                        .ToList();
                    payload["hotspots"] = JsonSerializer.SerializeToNode(items, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolHotspotJsonResult);
                }
                payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
                AddHotspotFamilyJsonFields(payload, hotspotSignal);
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                return WriteHotspotsJsonPayload(payload, options, jsonOptions);
            }
            else
            {
                foreach (var r in results)
                {
                    var s = r.Symbol;
                    var vis = s.Visibility != null ? $" [{s.Visibility}]" : "";
                    Console.WriteLine($"{FormatHotspotScore(r.ReferenceScore),5} score {r.ReferenceCount,5} refs  {ConsoleUi.ColorizeKind(s.Kind, 12)} {s.Name,-40} {s.Path}:{s.Line}{vis}");
                }
                CommandErrorWriter.WriteStderr($"({results.Count} symbol hotspots; grouped_by={groupBy})");
                WriteHotspotFamilyWarningIfNeeded(json: false, hotspotSignal);
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        });
    }

    private static int WriteHotspotsJsonPayload(JsonObject payload, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
        => WriteJsonPayloadWithOptionalByteLimit(
            payload,
            options,
            jsonOptions,
            "hotspots",
            "hotspots",
            "Use --summary-only, reduce --limit, or increase --max-json-bytes.");

    private static GroupedSymbolHotspotSiteJsonResult BuildGroupedHotspotRepresentative(GroupedHotspotResult result)
    {
        var representative = result.DefinitionSiteDetails.FirstOrDefault(site =>
            string.Equals(site.Path, result.Symbol.Path, StringComparison.Ordinal)
            && site.Line == result.Symbol.Line);
        if (representative != null)
            return ToGroupedHotspotSiteJson(representative);

        return new GroupedSymbolHotspotSiteJsonResult(
            result.Symbol.Path,
            result.Symbol.Lang,
            result.Symbol.Line,
            result.Symbol.Visibility,
            result.Symbol.ContainerName,
            LogicalTargetKey: null);
    }

    private static GroupedSymbolHotspotSiteJsonResult ToGroupedHotspotSiteJson(GroupedHotspotDefinitionSite site)
        => new(
            site.Path,
            site.Lang,
            site.Line,
            site.Visibility,
            site.Container,
            site.LogicalTargetKey);
}
