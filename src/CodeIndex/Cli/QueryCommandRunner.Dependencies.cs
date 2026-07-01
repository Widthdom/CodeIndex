using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunImpact(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("impact", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("impact", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("impact"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "impact"))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "impact"))
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
                CommandErrorWriter.WriteStderr("Warning: --depth is deprecated for impact; use --max-hops instead.");
            var analysis = reader.AnalyzeImpact(options.Query, maxDepth, options.Limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.WithPaths);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, analysis.Callers, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(analysis.Callers, options.DbPath);
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
                        CommandErrorWriter.WriteStderr("Depth 0 requested: resolved the symbol only; callers were not traversed.");
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
                            ["files"] = 0,
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
                        AddImpactOptionWarnings(payload, options);
                        AddCountEnvelopeJsonFields(payload, reader, jsonOptions, options);
                        Console.WriteLine(payload.ToJsonString(jsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("0");
                        if (!analysis.GraphTableAvailable)
                            CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
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
                    CommandErrorWriter.WriteStderr($"No impact found for '{analysis.Query}'.");
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
                        ["files"] = visibleFileCount,
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
                    AddCountEnvelopeJsonFields(payload, reader, jsonOptions, options);
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
                    CommandErrorWriter.WriteStderr($"No symbol-level callers found for '{analysis.ResolvedName}'. Possible file-level dependents follow.");
                    WriteImpactResolutionHint(analysis);
                    CommandErrorWriter.WriteStderr("WARN: these file-level dependents are heuristic only; the current graph does not record resolved target file/type for each call.");
                    if (analysis.Truncated)
                        CommandErrorWriter.WriteStderr("WARN: heuristic file-level dependents were truncated by the current limit.");
                    foreach (var edge in analysis.FileImpacts)
                        Console.WriteLine($"  {edge.SourcePath,-40} -> {edge.TargetPath} ({edge.ReferenceCount} refs: {edge.Symbols})");
                }
                else
                {
                    var grouped = analysis.Callers.GroupBy(r => r.Depth).OrderBy(g => g.Key);
                    foreach (var group in grouped)
                    {
                        CommandErrorWriter.WriteStderr($"--- Depth {group.Key} ---");
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
                    CommandErrorWriter.WriteStderr($"\n({hintCount} heuristic dependency hints across {hintFileCount} files{truncNote})");
                else
                    CommandErrorWriter.WriteStderr($"\n({confirmedCount} callers across {confirmedFileCount} files, max depth {maxDepth}{truncNote})");
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
        => RunDeps(cmdArgs, jsonOptions, CancellationToken.None);

    public static int RunDeps(string[] cmdArgs, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var previewOptionError = ValidatePreviewOptions("deps", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        if (!TryExtractDepsFormat(cmdArgs, out var depsFormat, out var parseArgs, out var depsFormatError))
        {
            CommandErrorWriter.WriteStderr(depsFormatError);
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
        if (TryWriteWorkspaceDependencyFanOutError(options))
            return CommandExitCodes.UsageError;
        var emitsJson = DepsEmitsJson(options, depsFormat);
        if (options.MaxJsonBytes.HasValue && !emitsJson)
        {
            WriteUsageError(
                "--max-json-bytes is only supported with deps JSON output.",
                GetUsageLineOrThrow("deps"),
                "Use `cdidx deps --json --max-json-bytes <n>` or `cdidx deps --format json-graph --max-json-bytes <n>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.SummaryOnly && !emitsJson)
        {
            WriteUsageError(
                "--summary-only is only supported with deps JSON output.",
                GetUsageLineOrThrow("deps"),
                "Use `cdidx deps --json --summary-only` or `cdidx deps --format json-graph --summary-only`.");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryWriteInvalidWorkspaceDependencyDatabaseError(options, out var workspaceDbExitCode))
                return workspaceDbExitCode;

            var reverse = cmdArgs.Any(a => a == "--reverse");
            List<FileDependencyResult> results;
            List<FileDependencyResult> cycleCandidates;
            var cycleCandidateRowCount = 0;
            var cycleCandidateLimit = GetDependencyCycleGraphLimit(options.Limit);
            if (options.DependencyCycles)
            {
                WriteGraphLiveness("deps", "read_cycle_candidates", options, depsFormat);
                cycleCandidates = GetWorkspaceFileDependencyCycleCandidates(
                    reader,
                    options,
                    reverse,
                    checked(cycleCandidateLimit + 1),
                    out cycleCandidateRowCount,
                    cancellationToken);
                results = cycleCandidates.Take(cycleCandidateLimit).ToList();
                cycleCandidates = results;
            }
            else
            {
                WriteGraphLiveness("deps", "read_edges", options, depsFormat);
                results = GetWorkspaceFileDependencies(reader, options, reverse, options.Limit, cancellationToken);
                cycleCandidates = results;
            }
            WriteGraphLiveness("deps", "shape_output", options, depsFormat, rows: results.Count);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            if (results.Count == 0)
            {
                var zeroSqlGraphSignal = baseSqlGraphSignal;
                var zeroSymbolFilter = ApplyDependencySymbolFilters([], options).Summary;
                if (depsFormat is OutputFormatJsonGraph)
                {
                    var writeExitCode = WriteDependencyGraph([], depsFormat, jsonOptions, reader, options, zeroSqlGraphSignal, zeroSymbolFilter);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                if (options.Json && !reader._hasReferencesTable)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: options.SummaryOnly ? null : "edges",
                        graphTableAvailable: false,
                        degraded: true,
                        queryOptions: options,
                        extraFields: payload => AddDependencySchemaJsonFields(payload, options, jsonOptions, zeroSqlGraphSignal, zeroSymbolFilter));
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                    var writeExitCode = WriteDepsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: options.SummaryOnly ? null : "edges", graphTableAvailable: true, degraded: !zeroSqlGraphSignal.Ready, queryOptions: options, extraFields: payload => AddDependencySchemaJsonFields(payload, options, jsonOptions, zeroSqlGraphSignal, zeroSymbolFilter));
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    var writeExitCode = WriteDepsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No file dependencies found", options));
                    WriteSqlGraphContractWarningIfNeeded(json: false, zeroSqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "edges", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            List<List<string>> cycles = [];
            List<FileDependencyResult> outputEdges;
            DependencySymbolFilterResult symbolFilter;
            DependencyCycleAnalysis? dependencyCycleAnalysis = null;
            if (options.DependencyCycles)
            {
                symbolFilter = ApplyDependencySymbolFilters(cycleCandidates, options);
                WriteGraphLiveness("deps", "analyze_cycles", options, depsFormat, rows: symbolFilter.Edges.Count);
                var analysis = AnalyzeDependencyCycles(
                    symbolFilter.Edges,
                    cycleCandidateLimit,
                    cycleCandidateRowCount,
                    options.Limit,
                    cancellationToken);
                outputEdges = analysis.Edges;
                cycles = analysis.Cycles;
                dependencyCycleAnalysis = analysis;
            }
            else
            {
                symbolFilter = ApplyDependencySymbolFilters(results, options);
                outputEdges = symbolFilter.Edges;
            }
            var sqlGraphSignalPaths = options.DependencyCycles
                ? cycles.Count > 0
                    ? cycles.SelectMany(static cycle => cycle)
                    : symbolFilter.Edges.SelectMany(static result => new[] { result.SourcePath, result.TargetPath })
                : outputEdges.SelectMany(static result => new[] { result.SourcePath, result.TargetPath });
            var sqlGraphSignal = NarrowSqlGraphContractSignalByPaths(
                reader,
                baseSqlGraphSignal,
                sqlGraphSignalPaths,
                options.Lang);
            if (!options.DependencyCycles && outputEdges.Count == 0)
            {
                if (depsFormat is OutputFormatDot or OutputFormatGraphMl or OutputFormatJsonGraph)
                {
                    var writeExitCode = WriteDependencyGraph(outputEdges, depsFormat, jsonOptions, reader, options, sqlGraphSignal, symbolFilter.Summary);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        jsonOptions,
                        resultsKey: options.SummaryOnly ? null : "edges",
                        graphTableAvailable: true,
                        degraded: !sqlGraphSignal.Ready,
                        queryOptions: options,
                        extraFields: payload => AddDependencySchemaJsonFields(payload, options, jsonOptions, sqlGraphSignal, symbolFilter.Summary));
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    var writeExitCode = WriteDepsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No file dependencies found", options));
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                }
                return ZeroResultExitCode(options);
            }
            if (options.DependencyCycles && cycles.Count == 0)
            {
                if (options.Json)
                {
                    var payload = new JsonObject { ["count"] = 0 };
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    else
                        payload["cycles"] = new JsonArray();
                    if (dependencyCycleAnalysis != null)
                        AddDependencyCycleAnalysisJsonFields(payload, dependencyCycleAnalysis);
                    AddDependencySchemaJsonFields(payload, options, jsonOptions, sqlGraphSignal, symbolFilter.Summary);
                    AddFreshnessHint(payload, reader);
                    WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: outputEdges.Count, cycleCount: 0);
                    var writeExitCode = WriteDepsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No dependency cycles found", options));
                    if (dependencyCycleAnalysis is { Truncated: true })
                        CommandErrorWriter.WriteStderr(BuildDependencyCycleTruncationWarning(dependencyCycleAnalysis));
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                }
                return ZeroResultExitCode(options);
            }

            if (depsFormat is OutputFormatDot or OutputFormatGraphMl or OutputFormatJsonGraph)
            {
                WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: outputEdges.Count, cycleCount: cycles.Count);
                var writeExitCode = WriteDependencyGraph(
                    outputEdges,
                    depsFormat,
                    jsonOptions,
                    reader,
                    options,
                    sqlGraphSignal,
                    symbolFilter.Summary,
                    dependencyCycleAnalysis == null ? null : payload => AddDependencyCycleAnalysisJsonFields(payload, dependencyCycleAnalysis));
                if ((depsFormat is OutputFormatDot or OutputFormatGraphMl) && dependencyCycleAnalysis is { Truncated: true })
                    CommandErrorWriter.WriteStderr(BuildDependencyCycleTruncationWarning(dependencyCycleAnalysis));
                return writeExitCode;
            }

            if (options.Json)
            {
                var payload = new JsonObject
                {
                    ["count"] = options.DependencyCycles ? cycles.Count : outputEdges.Count,
                };
                if (options.SummaryOnly)
                    payload["summary_only"] = true;
                if (options.DependencyCycles)
                {
                    if (!options.SummaryOnly)
                        payload["cycles"] = BuildDependencyCyclesJson(cycles);
                    if (dependencyCycleAnalysis != null)
                        AddDependencyCycleAnalysisJsonFields(payload, dependencyCycleAnalysis);
                }
                else if (!options.SummaryOnly)
                    payload["edges"] = JsonSerializer.SerializeToNode(outputEdges, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileDependencyResult);
                AddDependencySchemaJsonFields(payload, options, jsonOptions, sqlGraphSignal, symbolFilter.Summary);
                AddFreshnessHint(payload, reader);
                WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: outputEdges.Count, cycleCount: cycles.Count);
                return WriteDepsJsonPayload(payload, options, jsonOptions);
            }
            else
            {
                if (options.DependencyCycles)
                {
                    foreach (var cycle in cycles)
                        Console.WriteLine(string.Join(" -> ", cycle.Concat([cycle[0]])));
                    var truncationNote = dependencyCycleAnalysis is { Truncated: true }
                        ? $"; {BuildDependencyCycleTruncationSummary(dependencyCycleAnalysis)}"
                        : string.Empty;
                    CommandErrorWriter.WriteStderr($"({cycles.Count} dependency cycles{truncationNote})");
                    if (dependencyCycleAnalysis is { Truncated: true })
                        CommandErrorWriter.WriteStderr(BuildDependencyCycleTruncationWarning(dependencyCycleAnalysis));
                    WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
                    return CommandExitCodes.Success;
                }

                foreach (var r in outputEdges)
                {
                    var syms = r.Symbols.Length > 60 ? r.Symbols[..57] + "..." : r.Symbols;
                    Console.WriteLine($"{r.SourcePath,-45} -> {r.TargetPath,-45} ({r.ReferenceCount} refs: {syms})");
                }
                CommandErrorWriter.WriteStderr($"({outputEdges.Count} dependency edges)");
                WriteSqlGraphContractWarningIfNeeded(json: false, sqlGraphSignal, reader, options);
            }
            return CommandExitCodes.Success;
        }, cancellationToken: cancellationToken);
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
                error = $"Error: deps --format must be one of edgelist, dot, graphml, or json-graph; got '{ConsoleUi.FormatBoundedValue(rawFormat)}'.";
                return false;
        }
    }

    internal static List<FileDependencyResult> FilterCycleEdges(List<FileDependencyResult> results, out List<List<string>> cycles)
        => FilterCycleEdges(results, out cycles, CancellationToken.None);

    private static List<FileDependencyResult> FilterCycleEdges(IReadOnlyList<FileDependencyResult> results, out List<List<string>> cycles, CancellationToken cancellationToken)
    {
        cycles = FindDependencyCycles(results, cancellationToken);
        if (cycles.Count == 0)
            return [];
        var cycleNodes = cycles.SelectMany(cycle => cycle).ToHashSet(StringComparer.Ordinal);
        return results
            .Where(edge => cycleNodes.Contains(edge.SourcePath) && cycleNodes.Contains(edge.TargetPath))
            .ToList();
    }

    internal static List<List<string>> FindDependencyCycles(IReadOnlyList<FileDependencyResult> edges)
        => FindDependencyCycles(edges, CancellationToken.None);

    private static List<List<string>> FindDependencyCycles(IReadOnlyList<FileDependencyResult> edges, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            indexes[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var target in adjacency[node])
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexes.ContainsKey(node))
                Visit(node);
        }

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

    private sealed record DependencyCycleAnalysis(
        List<FileDependencyResult> Edges,
        List<List<string>> Cycles,
        bool Truncated,
        string TerminationReason,
        string? TruncatedReason,
        int CandidateEdgeCount,
        int CandidateEdgeLimit,
        int DisplayLimit,
        string DetectionMode);

    private static DependencyCycleAnalysis AnalyzeDependencyCycles(
        IReadOnlyList<FileDependencyResult> candidateEdges,
        int candidateEdgeLimit,
        int candidateRowCount,
        int displayLimit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edges = FilterCycleEdges(candidateEdges, out var allCycles, cancellationToken);
        var cyclesTruncated = allCycles.Count > displayLimit;
        var candidateTruncated = candidateRowCount > candidateEdgeLimit;
        var cycles = allCycles.Take(displayLimit).ToList();
        var cycleNodes = cycles.SelectMany(static cycle => cycle).ToHashSet(StringComparer.Ordinal);
        var outputEdges = edges
            .Where(edge => cycleNodes.Count == 0 || (cycleNodes.Contains(edge.SourcePath) && cycleNodes.Contains(edge.TargetPath)))
            .Take(displayLimit)
            .ToList();
        var truncatedReason = candidateTruncated
            ? "candidate_edge_limit"
            : cyclesTruncated
                ? "display_limit"
                : null;
        var terminationReason = truncatedReason switch
        {
            "candidate_edge_limit" => "candidate_limit_reached",
            "display_limit" => "display_limit_reached",
            _ => "completed",
        };

        return new DependencyCycleAnalysis(
            outputEdges,
            cycles,
            candidateTruncated || cyclesTruncated,
            terminationReason,
            truncatedReason,
            Math.Min(candidateRowCount, candidateEdgeLimit),
            candidateEdgeLimit,
            displayLimit,
            DependencyCycleDetectionMode);
    }

    private static void AddDependencyCycleAnalysisJsonFields(JsonObject payload, DependencyCycleAnalysis analysis)
        => AddDependencyCycleAnalysisJsonFields(
            payload,
            analysis.Truncated,
            analysis.TerminationReason,
            analysis.TruncatedReason,
            analysis.CandidateEdgeCount,
            analysis.CandidateEdgeLimit,
            analysis.DisplayLimit,
            analysis.DetectionMode);

    internal static void AddDependencyCycleAnalysisJsonFields(
        JsonObject payload,
        bool truncated,
        string terminationReason,
        string? truncatedReason,
        int candidateEdgeCount,
        int candidateEdgeLimit,
        int displayLimit,
        string detectionMode,
        JsonArray? nextStepFlags = null)
    {
        payload["truncated"] = truncated;
        payload["termination_reason"] = terminationReason;
        if (truncatedReason != null)
            payload["truncated_reason"] = truncatedReason;
        payload["candidate_edge_count"] = candidateEdgeCount;
        payload["candidate_edge_limit"] = candidateEdgeLimit;
        payload["cycle_detection_mode"] = detectionMode;
        payload["cycle_result_scope"] = BuildDependencyCycleResultScope(truncatedReason);
        payload["cycle_result_note"] = BuildDependencyCycleResultNote(truncatedReason);
        payload["next_step_flags"] = nextStepFlags ?? BuildDependencyCycleNextStepFlagsJson(truncatedReason, candidateEdgeLimit, displayLimit);
    }

    private static string BuildDependencyCycleTruncationSummary(DependencyCycleAnalysis analysis)
        => analysis.TruncatedReason == "display_limit"
            ? $"partial: showing first {analysis.Cycles.Count} cycles"
            : $"partial: candidate edge limit reached after {analysis.CandidateEdgeCount} candidate edges";

    private static string BuildDependencyCycleTruncationWarning(DependencyCycleAnalysis analysis)
    {
        var nextSteps = BuildDependencyCycleNextStepFlags(
            analysis.TruncatedReason,
            analysis.CandidateEdgeLimit,
            analysis.DisplayLimit);
        var nextStepsText = nextSteps.Count == 0
            ? string.Empty
            : $" Next steps: {string.Join(", ", nextSteps)}.";
        return "Warning: dependency cycle detection returned partial results "
               + $"({BuildDependencyCycleTruncationSummary(analysis)}). "
               + BuildDependencyCycleResultNote(analysis.TruncatedReason)
               + nextStepsText;
    }

    private static string BuildDependencyCycleResultScope(string? truncatedReason)
        => truncatedReason switch
        {
            "candidate_edge_limit" => "partial_candidate_edge_sample",
            "display_limit" => "partial_display_limit",
            _ => "complete_candidate_edges",
        };

    private static string BuildDependencyCycleResultNote(string? truncatedReason)
        => truncatedReason switch
        {
            "candidate_edge_limit" => "Candidate edge limit reached before cdidx could prove cycle completeness; returned cycles are a bounded sample, not a complete or ranked cycle set.",
            "display_limit" => "More cycles were detected than displayed; returned cycles are the first displayed cycles from the bounded candidate scan.",
            _ => "Cycle detection completed for all candidate edges selected by the current filters.",
        };

    private static JsonArray BuildDependencyCycleNextStepFlagsJson(
        string? truncatedReason,
        int candidateEdgeLimit,
        int displayLimit)
        => new(BuildDependencyCycleNextStepFlags(truncatedReason, candidateEdgeLimit, displayLimit)
            .Select(flag => JsonValue.Create(flag))
            .ToArray<JsonNode?>());

    internal static JsonArray BuildMcpDependencyCycleNextStepFlagsJson(
        string? truncatedReason,
        int candidateEdgeLimit,
        int displayLimit)
        => new(BuildMcpDependencyCycleNextStepFlags(truncatedReason, candidateEdgeLimit, displayLimit)
            .Select(flag => JsonValue.Create(flag))
            .ToArray<JsonNode?>());

    private static List<string> BuildDependencyCycleNextStepFlags(
        string? truncatedReason,
        int candidateEdgeLimit,
        int displayLimit)
    {
        var flags = new List<string>();
        switch (truncatedReason)
        {
            case "candidate_edge_limit":
                AddHigherLimitFlag(flags, candidateEdgeLimit);
                flags.Add("--suppress-noise");
                flags.Add("--symbol <name>");
                flags.Add("--symbol-family <prefix>");
                flags.Add("--path <narrower-glob>");
                break;
            case "display_limit":
                AddHigherLimitFlag(flags, displayLimit);
                flags.Add("--path <narrower-glob>");
                break;
        }

        return flags;
    }

    private static List<string> BuildMcpDependencyCycleNextStepFlags(
        string? truncatedReason,
        int candidateEdgeLimit,
        int displayLimit)
    {
        var flags = new List<string>();
        switch (truncatedReason)
        {
            case "candidate_edge_limit":
                AddHigherLimitArgument(flags, candidateEdgeLimit);
                flags.Add("path=<narrower-glob>");
                break;
            case "display_limit":
                AddHigherLimitArgument(flags, displayLimit);
                flags.Add("path=<narrower-glob>");
                break;
        }

        return flags;
    }

    private static void AddHigherLimitFlag(List<string> flags, int currentLimit)
    {
        var nextLimit = GetHigherDependencyCycleLimit(currentLimit);
        if (nextLimit > currentLimit)
            flags.Add($"--limit {nextLimit}");
    }

    private static void AddHigherLimitArgument(List<string> flags, int currentLimit)
    {
        var nextLimit = GetHigherDependencyCycleLimit(currentLimit);
        if (nextLimit > currentLimit)
            flags.Add($"limit={nextLimit}");
    }

    private static int GetHigherDependencyCycleLimit(int currentLimit)
    {
        var upperBound = NumericFlagUpperBounds.TryGetValue("--limit", out var maxLimit)
            ? maxLimit
            : int.MaxValue;
        if (currentLimit >= upperBound)
            return upperBound;

        var doubled = currentLimit > upperBound / 2
            ? upperBound
            : currentLimit * 2;
        return Math.Min(upperBound, Math.Max(currentLimit + 1, doubled));
    }

    private static DependencySymbolFilterResult ApplyDependencySymbolFilters(IReadOnlyList<FileDependencyResult> edges, QueryCommandOptions options)
    {
        var applied = options.DependencySuppressNoise || options.DependencySymbols.Count > 0 || options.DependencySymbolFamilies.Count > 0;
        if (!applied)
        {
            var unchangedSymbolCount = edges.Sum(edge => SplitDependencySymbols(edge.Symbols).Count);
            return new DependencySymbolFilterResult(
                edges.ToList(),
                new DependencySymbolFilterSummary(
                    Applied: false,
                    SuppressNoise: false,
                    Symbols: [],
                    SymbolFamilies: [],
                    EdgesBefore: edges.Count,
                    EdgesAfter: edges.Count,
                    SymbolsBefore: unchangedSymbolCount,
                    SymbolsAfter: unchangedSymbolCount));
        }

        var filteredEdges = new List<FileDependencyResult>(edges.Count);
        var symbolsBefore = 0;
        var symbolsAfter = 0;
        foreach (var edge in edges)
        {
            var symbols = SplitDependencySymbols(edge.Symbols);
            symbolsBefore += symbols.Count;
            var keptSymbols = symbols
                .Where(symbol => KeepDependencySymbol(symbol, options))
                .ToList();
            symbolsAfter += keptSymbols.Count;
            if (keptSymbols.Count == 0)
                continue;
            filteredEdges.Add(CopyDependencyEdge(edge, string.Join(",", keptSymbols)));
        }

        return new DependencySymbolFilterResult(
            filteredEdges,
            new DependencySymbolFilterSummary(
                Applied: true,
                SuppressNoise: options.DependencySuppressNoise,
                Symbols: options.DependencySymbols.ToArray(),
                SymbolFamilies: options.DependencySymbolFamilies.ToArray(),
                EdgesBefore: edges.Count,
                EdgesAfter: filteredEdges.Count,
                SymbolsBefore: symbolsBefore,
                SymbolsAfter: symbolsAfter));
    }

    private static bool KeepDependencySymbol(string symbol, QueryCommandOptions options)
    {
        if (options.DependencySuppressNoise && DependencyNoiseProfile.IsNoiseSymbol(symbol))
            return false;

        var hasNameFilters = options.DependencySymbols.Count > 0 || options.DependencySymbolFamilies.Count > 0;
        if (!hasNameFilters)
            return true;

        return options.DependencySymbols.Contains(symbol, StringComparer.Ordinal)
               || options.DependencySymbolFamilies.Any(family => symbol.StartsWith(family, StringComparison.Ordinal));
    }

    private static List<string> SplitDependencySymbols(string symbols)
        => symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static FileDependencyResult CopyDependencyEdge(FileDependencyResult edge, string symbols)
        => new()
        {
            ResultKind = edge.ResultKind,
            SourcePath = edge.SourcePath,
            TargetPath = edge.TargetPath,
            SourceDb = edge.SourceDb,
            TargetDb = edge.TargetDb,
            ReferenceCount = edge.ReferenceCount,
            RankingScore = edge.RankingScore,
            Symbols = symbols,
        };

    private static void AddDependencySchemaJsonFields(
        JsonObject payload,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SqlGraphContractSignal sqlGraphSignal,
        DependencySymbolFilterSummary symbolFilter)
    {
        payload["api_version"] = JsonOutputContract.ApiVersion;
        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
        AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
        AddDependencySymbolFilterJsonFields(payload, symbolFilter, jsonOptions);
    }

    private static void AddDependencySymbolFilterJsonFields(JsonObject payload, DependencySymbolFilterSummary symbolFilter, JsonSerializerOptions jsonOptions)
    {
        if (!symbolFilter.Applied)
            return;

        var filter = new JsonObject
        {
            ["suppress_noise"] = symbolFilter.SuppressNoise,
            ["edges_before"] = symbolFilter.EdgesBefore,
            ["edges_after"] = symbolFilter.EdgesAfter,
            ["edges_removed"] = symbolFilter.EdgesBefore - symbolFilter.EdgesAfter,
            ["symbols_before"] = symbolFilter.SymbolsBefore,
            ["symbols_after"] = symbolFilter.SymbolsAfter,
            ["symbols_removed"] = symbolFilter.SymbolsBefore - symbolFilter.SymbolsAfter,
        };
        if (symbolFilter.Symbols.Count > 0)
            filter["symbol"] = JsonSerializer.SerializeToNode(symbolFilter.Symbols.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (symbolFilter.SymbolFamilies.Count > 0)
            filter["symbol_family"] = JsonSerializer.SerializeToNode(symbolFilter.SymbolFamilies.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        payload["symbol_filter"] = filter;
    }

    private static JsonArray BuildDependencySymbolsJson(string symbols)
        => new(SplitDependencySymbols(symbols).Select(symbol => JsonValue.Create(symbol)).ToArray<JsonNode?>());

    private static bool DepsEmitsJson(QueryCommandOptions options, string depsFormat)
        => depsFormat == OutputFormatJsonGraph || (options.Json && depsFormat == OutputFormatEdgeList);

    private static int WriteDepsJsonPayload(JsonObject payload, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
        => WriteJsonPayloadWithOptionalByteLimit(
            payload,
            options,
            jsonOptions,
            "deps",
            "deps",
            "Use --summary-only, reduce --limit, or increase --max-json-bytes.");

    private sealed record DependencySymbolFilterResult(List<FileDependencyResult> Edges, DependencySymbolFilterSummary Summary);

    private sealed record DependencySymbolFilterSummary(
        bool Applied,
        bool SuppressNoise,
        IReadOnlyList<string> Symbols,
        IReadOnlyList<string> SymbolFamilies,
        int EdgesBefore,
        int EdgesAfter,
        int SymbolsBefore,
        int SymbolsAfter);

    private static int WriteDependencyGraph(
        IReadOnlyList<FileDependencyResult> edges,
        string format,
        JsonSerializerOptions jsonOptions,
        DbReader reader,
        QueryCommandOptions options,
        SqlGraphContractSignal sqlGraphSignal,
        DependencySymbolFilterSummary symbolFilter,
        Action<JsonObject>? addExtraJsonFields = null)
    {
        switch (format)
        {
            case OutputFormatDot:
                Console.WriteLine("digraph deps {");
                foreach (var edge in edges)
                    Console.WriteLine($"  \"{EscapeDot(edge.SourcePath)}\" -> \"{EscapeDot(edge.TargetPath)}\" [label=\"{edge.ReferenceCount}\"];");
                Console.WriteLine("}");
                return CommandExitCodes.Success;
            case OutputFormatGraphMl:
                Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                Console.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\"><graph edgedefault=\"directed\">");
                foreach (var node in edges.SelectMany(edge => new[] { edge.SourcePath, edge.TargetPath }).Distinct(StringComparer.Ordinal))
                    Console.WriteLine($"<node id=\"{System.Security.SecurityElement.Escape(node)}\" />");
                foreach (var edge in edges)
                    Console.WriteLine($"<edge source=\"{System.Security.SecurityElement.Escape(edge.SourcePath)}\" target=\"{System.Security.SecurityElement.Escape(edge.TargetPath)}\"><data key=\"references\">{edge.ReferenceCount}</data></edge>");
                Console.WriteLine("</graph></graphml>");
                return CommandExitCodes.Success;
            case OutputFormatJsonGraph:
                return WriteDependencyJsonGraph(edges, jsonOptions, reader, options, sqlGraphSignal, symbolFilter, addExtraJsonFields);
            default:
                return CommandExitCodes.Success;
        }
    }

    private static int WriteDependencyJsonGraph(
        IReadOnlyList<FileDependencyResult> edges,
        JsonSerializerOptions jsonOptions,
        DbReader reader,
        QueryCommandOptions options,
        SqlGraphContractSignal sqlGraphSignal,
        DependencySymbolFilterSummary symbolFilter,
        Action<JsonObject>? addExtraJsonFields = null)
    {
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<string>();
        foreach (var edge in edges)
        {
            if (seenNodes.Add(edge.SourcePath))
                nodes.Add(edge.SourcePath);
            if (seenNodes.Add(edge.TargetPath))
                nodes.Add(edge.TargetPath);
        }

        var payload = new JsonObject { ["count"] = edges.Count };
        if (options.SummaryOnly)
            payload["summary_only"] = true;
        else
        {
            payload["nodes"] = new JsonArray(nodes.Select(node => (JsonNode?)new JsonObject { ["id"] = node }).ToArray());
            payload["edges"] = new JsonArray(edges.Select(edge => (JsonNode?)new JsonObject
            {
                ["source"] = edge.SourcePath,
                ["target"] = edge.TargetPath,
                ["reference_count"] = edge.ReferenceCount,
                ["ranking_score"] = edge.RankingScore,
                ["symbols"] = BuildDependencySymbolsJson(edge.Symbols),
            }).ToArray());
        }
        addExtraJsonFields?.Invoke(payload);
        AddDependencySchemaJsonFields(payload, options, jsonOptions, sqlGraphSignal, symbolFilter);
        AddFreshnessHint(payload, reader);
        return WriteDepsJsonPayload(payload, options, jsonOptions);
    }

    private static string EscapeDot(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static int GetDependencyCycleGraphLimit(int displayLimit)
    {
        var requestedLimit = Math.Max(displayLimit, DefaultDependencyCycleGraphLimit);
        return NumericFlagUpperBounds.TryGetValue("--limit", out var maxLimit)
            ? Math.Min(requestedLimit, maxLimit)
            : requestedLimit;
    }

    private static List<FileDependencyResult> GetWorkspaceFileDependencies(DbReader primaryReader, QueryCommandOptions options, bool reverse, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = primaryReader.GetFileDependencies(limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.WorkspaceDbPaths.Count == 0)
            return results;

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var primaryDb = memberDbs[0];
        TagFileDependencyResults(results, primaryDb);
        foreach (var normalizedDbPath in memberDbs.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = new DbContext(normalizedDbPath, cancellationToken);
            db.TryMigrateForRead();
            var reader = new DbReader(db) { IncludeGenerated = primaryReader.IncludeGenerated };
            var memberResults = reader.GetFileDependencies(limit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, reverse, cancellationToken);
            TagFileDependencyResults(memberResults, normalizedDbPath);
            results.AddRange(memberResults);
        }

        foreach (var sourceDb in memberDbs)
            foreach (var targetDb in memberDbs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(sourceDb, targetDb, StringComparison.Ordinal))
                    continue;
                results.AddRange(GetCrossDatabaseFileDependencies(sourceDb, targetDb, options, reverse, limit));
            }

        return results
            .OrderByDescending(result => result.RankingScore)
            .ThenByDescending(result => result.ReferenceCount)
            .ThenBy(result => result.SourceDb, StringComparer.Ordinal)
            .ThenBy(result => result.SourcePath, StringComparer.Ordinal)
            .ThenBy(result => result.TargetDb, StringComparer.Ordinal)
            .ThenBy(result => result.TargetPath, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static List<FileDependencyResult> GetWorkspaceFileDependencyCycleCandidates(
        DbReader primaryReader,
        QueryCommandOptions options,
        bool reverse,
        int limit,
        out int candidateRowCount,
        CancellationToken cancellationToken)
    {
        candidateRowCount = 0;
        var results = primaryReader.GetFileDependencyCycleCandidates(
            limit,
            out var primaryCandidateRows,
            options.Lang,
            options.PathPatterns,
            options.ExcludePaths,
            options.ExcludeTests,
            reverse,
            cancellationToken);
        candidateRowCount += primaryCandidateRows;
        if (options.WorkspaceDbPaths.Count == 0)
            return results.Take(limit).ToList();

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var primaryDb = memberDbs[0];
        TagFileDependencyResults(results, primaryDb);
        if (results.Count >= limit)
            return results.Take(limit).ToList();
        foreach (var normalizedDbPath in memberDbs.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = new DbContext(normalizedDbPath, cancellationToken);
            db.TryMigrateForRead();
            var reader = new DbReader(db) { IncludeGenerated = primaryReader.IncludeGenerated };
            var memberResults = reader.GetFileDependencyCycleCandidates(
                limit,
                out var memberCandidateRows,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                reverse,
                cancellationToken);
            candidateRowCount += memberCandidateRows;
            TagFileDependencyResults(memberResults, normalizedDbPath);
            results.AddRange(memberResults);
            if (results.Count >= limit)
                return results.Take(limit).ToList();
        }

        foreach (var sourceDb in memberDbs)
            foreach (var targetDb in memberDbs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(sourceDb, targetDb, StringComparison.Ordinal))
                    continue;
                var crossDbResults = GetCrossDatabaseFileDependencies(sourceDb, targetDb, options, reverse, limit);
                candidateRowCount += crossDbResults.Count;
                results.AddRange(crossDbResults);
                if (results.Count >= limit)
                    return results.Take(limit).ToList();
            }

        return results.Take(limit).ToList();
    }

    internal static List<string> BuildWorkspaceDependencyDatabaseList(QueryCommandOptions options)
    {
        var primaryDb = Path.GetFullPath(DbPathResolver.NormalizeDbPath(options.DbPath));
        var comparer = PathCasing.IsIgnoreCase(primaryDb)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return options.WorkspaceDbPaths
            .Select(path => Path.GetFullPath(DbPathResolver.NormalizeDbPath(path)))
            .Prepend(primaryDb)
            .Distinct(comparer)
            .ToList();
    }

    private static bool TryWriteWorkspaceDependencyFanOutError(QueryCommandOptions options)
    {
        if (options.WorkspaceDbPaths.Count == 0)
            return false;

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var pairCount = memberDbs.Count * (memberDbs.Count - 1);
        if (memberDbs.Count <= MaxWorkspaceDependencyDatabaseCount &&
            pairCount <= MaxWorkspaceDependencyDatabasePairCount)
            return false;

        var maxAdditional = MaxWorkspaceDependencyDatabaseCount - 1;
        var additionalCount = Math.Max(0, memberDbs.Count - 1);
        CommandErrorWriter.WriteStderr($"Error: deps --workspace-db accepts at most {maxAdditional} distinct additional databases ({MaxWorkspaceDependencyDatabaseCount} total including --db), which is {MaxWorkspaceDependencyDatabasePairCount} ordered cross-database pairs; got {additionalCount} additional ({memberDbs.Count} total, {pairCount} pairs).");
        CommandErrorWriter.WriteStderr("Hint: pass fewer --workspace-db values or run deps separately for smaller workspace member groups.");
        return true;
    }

    private static bool TryWriteInvalidWorkspaceDependencyDatabaseError(QueryCommandOptions options, out int exitCode)
    {
        exitCode = CommandExitCodes.Success;
        if (options.WorkspaceDbPaths.Count == 0)
            return false;

        foreach (var dbPath in BuildWorkspaceDependencyDatabaseList(options).Skip(1))
        {
            if (DbContext.TryValidateExistingCodeIndexDb(
                    dbPath,
                    requireWritable: false,
                    requireSupportedUserVersion: true,
                    out var validationMessage,
                    out var isNotFound,
                    out var isSchemaTooNew))
                continue;

            var errorCode = isNotFound
                ? CommandErrorCodes.DbNotFound
                : isSchemaTooNew
                    ? CommandErrorCodes.SchemaTooNew
                    : CommandErrorCodes.DbError;
            CommandErrorWriter.WriteStderr($"Error [{errorCode}]: attached workspace database cannot be used for cross-database dependency query: {validationMessage}");
            CommandErrorWriter.WriteStderr(isNotFound
                ? "Hint: pass an existing CodeIndex database to `--workspace-db`, or run `cdidx index <workspacePath>` for that workspace member first."
                : isSchemaTooNew
                    ? "Hint: run the query with a current cdidx binary, or rebuild that workspace member database with this cdidx version before using `--workspace-db`."
                    : "Hint: pass only CodeIndex databases created by `cdidx index` to `--workspace-db`; remove stale, empty, or unrelated SQLite files from the workspace database list.");
            exitCode = CommandExitCodes.DatabaseError;
            return true;
        }

        return false;
    }

    private static List<FileDependencyResult> GetCrossDatabaseFileDependencies(string sourceDbPath, string targetDbPath, QueryCommandOptions options, bool reverse, int limit)
    {
        using var sourceDb = new DbContext(sourceDbPath);
        sourceDb.TryMigrateForRead();
        var connection = sourceDb.Connection;
        AttachCrossDatabaseTarget(connection, targetDbPath);

        using var cmd = connection.CreateCommand();
        var sourcePathExpr = reverse ? "dst.path" : "src.path";
        var targetPathExpr = reverse ? "src.path" : "dst.path";
        cmd.CommandText = $@"
            WITH edges AS (
            SELECT {sourcePathExpr} AS source_path,
                   {targetPathExpr} AS target_path,
                   r.symbol_name
            FROM symbol_references r
            JOIN files src ON src.id = r.file_id
            JOIN targetdb.symbols s ON s.name = r.symbol_name
            JOIN targetdb.files dst ON dst.id = s.file_id
            WHERE 1 = 1";
        if (options.Lang != null)
        {
            cmd.CommandText += " AND src.lang = @lang AND dst.lang = @lang";
            SqliteCommandPolicy.Add(cmd, "@lang", options.Lang);
        }
        AddCrossDatabasePathFilters(cmd, "src", options.PathPatterns, include: !reverse);
        AddCrossDatabasePathFilters(cmd, "dst", options.PathPatterns, include: reverse);
        AddCrossDatabaseExcludeFilters(cmd, "src", options.ExcludePaths, include: !reverse);
        AddCrossDatabaseExcludeFilters(cmd, "dst", options.ExcludePaths, include: reverse);
        if (options.ExcludeTests)
            cmd.CommandText += reverse
                ? $" AND NOT {BuildCrossDatabaseTestPathCondition("dst")}"
                : $" AND NOT {BuildCrossDatabaseTestPathCondition("src")}";
        cmd.CommandText += @"
            ),
            edge_totals AS (
                SELECT source_path,
                       target_path,
                       COUNT(*) AS reference_count
                FROM edges
                GROUP BY source_path, target_path
            ),
            distinct_edge_symbols AS (
                SELECT DISTINCT source_path, target_path, symbol_name
                FROM edges
            ),
            ranked_edge_symbols AS (
                SELECT source_path,
                       target_path,
                       symbol_name,
                       ROW_NUMBER() OVER (PARTITION BY source_path, target_path ORDER BY symbol_name) AS symbol_rank
                FROM distinct_edge_symbols
            )
            SELECT edge_totals.source_path,
                   edge_totals.target_path,
                   edge_totals.reference_count,
                   COALESCE(GROUP_CONCAT(CASE WHEN ranked_edge_symbols.symbol_rank <= @symbolSampleLimit THEN ranked_edge_symbols.symbol_name END), '') AS symbols
            FROM edge_totals
            LEFT JOIN ranked_edge_symbols
              ON ranked_edge_symbols.source_path = edge_totals.source_path
             AND ranked_edge_symbols.target_path = edge_totals.target_path
            GROUP BY edge_totals.source_path, edge_totals.target_path, edge_totals.reference_count
            ORDER BY edge_totals.reference_count DESC, edge_totals.source_path, edge_totals.target_path
            LIMIT @limit";
        SqliteCommandPolicy.Add(cmd, "@limit", DependencyNoiseProfile.GetRankingCandidateLimit(limit));
        SqliteCommandPolicy.Add(cmd, "@symbolSampleLimit", DbReader.DependencySymbolSampleLimit);

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
        foreach (var result in results)
            result.RankingScore = DependencyNoiseProfile.ComputeRankingScore(result.ReferenceCount, result.Symbols);

        return results
            .OrderByDescending(result => result.RankingScore)
            .ThenByDescending(result => result.ReferenceCount)
            .ThenBy(result => result.SourceDb, StringComparer.Ordinal)
            .ThenBy(result => result.SourcePath, StringComparer.Ordinal)
            .ThenBy(result => result.TargetDb, StringComparer.Ordinal)
            .ThenBy(result => result.TargetPath, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static void AttachCrossDatabaseTarget(SqliteConnection connection, string targetDbPath)
    {
        try
        {
            AttachCrossDatabaseTargetCore(connection, targetDbPath);
        }
        catch (SqliteException) when (!SqliteFileUri.StartsWithFileScheme(targetDbPath) && File.Exists(LongPath.EnsureWindowsPrefix(targetDbPath)))
        {
            AttachCrossDatabaseTargetCore(connection, DbContext.ToReadOnlyUri(targetDbPath));
        }
    }

    private static void AttachCrossDatabaseTargetCore(SqliteConnection connection, string targetDbPath)
    {
        using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE @targetDb AS targetdb";
        attach.Parameters.Add("@targetDb", SqliteType.Text).Value = targetDbPath;
        attach.ExecuteNonQuery();
    }

    private static void AddCrossDatabasePathFilters(SqliteCommand cmd, string alias, IReadOnlyList<string> patterns, bool include)
    {
        if (!include || patterns.Count == 0)
            return;
        SqliteDynamicSql.EnsureParameterBudget(patterns.Count, "cross-database path filters");
        var parts = new List<string>(patterns.Count);
        for (var i = 0; i < patterns.Count; i++)
        {
            var name = SqliteDynamicSql.BuildParameterName($"crossPath{alias}", i);
            parts.Add($"{alias}.path LIKE {name} ESCAPE '\\'");
            cmd.Parameters.Add(name, SqliteType.Text).Value = CrossDatabaseGlobToLikePattern(patterns[i]);
        }
        cmd.CommandText += " AND (" + string.Join(" OR ", parts) + ")";
    }

    private static void AddCrossDatabaseExcludeFilters(SqliteCommand cmd, string alias, IReadOnlyList<string> patterns, bool include)
    {
        if (!include || patterns.Count == 0)
            return;
        SqliteDynamicSql.EnsureParameterBudget(patterns.Count, "cross-database exclude path filters");
        for (var i = 0; i < patterns.Count; i++)
        {
            var name = SqliteDynamicSql.BuildParameterName($"crossExclude{alias}", i);
            cmd.CommandText += $" AND {alias}.path NOT LIKE {name} ESCAPE '\\'";
            cmd.Parameters.Add(name, SqliteType.Text).Value = CrossDatabaseGlobToLikePattern(patterns[i]);
        }
    }

    internal static string BuildCrossDatabaseTestPathConditionForTesting(string alias)
        => BuildCrossDatabaseTestPathCondition(alias);

    private static string BuildCrossDatabaseTestPathCondition(string alias)
        => DbReader.TestPathCondition.Replace("f.path", $"{alias}.path", StringComparison.Ordinal);

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

}
