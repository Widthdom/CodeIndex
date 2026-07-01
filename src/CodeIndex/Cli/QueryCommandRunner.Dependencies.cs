using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

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
}
