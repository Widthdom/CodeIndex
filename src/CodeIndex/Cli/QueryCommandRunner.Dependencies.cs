using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int BroadDependencySummaryCandidateFileLimit = 250;

    public static int RunImpact(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("impact", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteUnsupportedOptionError("impact", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("impact"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "impact", options.LanguageValidationError ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "impact"))
            return CommandExitCodes.UsageError;
        if (!TryValidateGraphSnippetLinesOption("impact", options))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "impact", out _, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
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
            WriteReferenceGraphCompletenessWarningIfNeeded(options.Json, reader);
            var maxDepth = options.ContextAfterExplicit ? options.ContextAfter : 5; // --max-hops/--depth is parsed into ContextAfter; 0 means resolve-only
            if (!options.Json && options.ImpactDeprecatedDepthUsed)
                CommandErrorWriter.WriteStderr("Warning: --depth is deprecated for impact; use --max-hops instead.");
            var analysis = reader.AnalyzeImpact(
                options.Query,
                maxDepth,
                options.Limit,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.WithPaths,
                JsonEnvelopeWrapper.GetBoundedResponseOffset("impact"),
                JsonEnvelopeWrapper.GetBoundedImpactCollection(),
                options.IncludeMemberReads);
            if (options.IncludeBody
                && !options.CountOnly
                && options.OutputFormat is (OutputFormatText or OutputFormatJson)
                && JsonEnvelopeWrapper.ShouldMaterializeBody("impact"))
            {
                AttachBodyExcerpts(reader, analysis.Callers, options.SnippetLines, options.MaxLineWidth);
            }
            ApplyBodyRecoveryCommands(analysis.Callers, options.DbPath, options.RedactPaths ?? true);
            var sqlGraphSignal = NarrowSqlGraphContractSignal(
                reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests),
                DbReader.IsSqlLanguage(options.Lang)
                    || DbReader.ContainsSqlLanguage(analysis.Definitions.Select(definition => definition.Lang))
                    || DbReader.ContainsSqlLanguage(analysis.Callers.Select(caller => caller.Lang))
                    || reader.AnyFilePathHasLanguage(analysis.FileImpacts.SelectMany(impact => new[] { impact.SourcePath, impact.TargetPath }), "sql"));
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var confirmedCount = analysis.Callers.Count;
            var confirmedFileCount = analysis.Callers.Select(r => r.Path).Distinct().Count();
            var hintCount = analysis.FileImpacts.Count;
            var hintFileCount = analysis.FileImpacts.Select(r => r.SourcePath).Distinct().Count();
            var hasHeuristicHints = analysis.ImpactMode == "file_dependency_hints";
            var visibleCount = hasHeuristicHints ? hintCount : confirmedCount;
            var visibleFileCount = hasHeuristicHints ? hintFileCount : confirmedFileCount;
            var depthZeroResolved = maxDepth == 0 && analysis.DefinitionCount > 0;

            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
            if (!options.Json)
                WriteImpactIdentityRootWarningIfNeeded(analysis);

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
                                AddImpactDefinitionsJsonFields(zeroPayload, analysis, options, jsonOptions);
                                if (analysis.ZeroResultReason != null)
                                    zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                                AddImpactFailureJsonFields(zeroPayload, analysis, jsonOptions);
                                if (analysis.Suggestion != null)
                                    zeroPayload["suggestion"] = analysis.Suggestion;
                                AddGraphContractJsonFields(zeroPayload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                                AddImpactOptionWarnings(zeroPayload, options);
                            });
                        var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                            payload,
                            options,
                            jsonOptions,
                            "impact",
                            "impact",
                            "Reduce --limit, lower --max-hops, use --count, or increase --max-json-bytes.");
                        if (writeExitCode != CommandExitCodes.Success)
                            return writeExitCode;
                    }
                    else
                    {
                        CommandErrorWriter.WriteStderr("Depth 0 requested: resolved the symbol only; callers were not traversed.");
                        WriteImpactResolutionHint(analysis);
                        WriteGraphSupportHint(options.Lang, reader);
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
                        AddImpactTraversalRootJsonFields(payload, analysis);
                        AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                        if (analysis.ZeroResultReason != null)
                            payload["zero_result_reason"] = analysis.ZeroResultReason;
                        AddImpactFailureJsonFields(payload, analysis, jsonOptions);
                        if (analysis.Suggestion != null)
                            payload["suggestion"] = analysis.Suggestion;
                        if (!analysis.GraphTableAvailable)
                            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                        AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                        AddImpactOptionWarnings(payload, options);
                        AddCountEnvelopeJsonFields(payload, reader, jsonOptions, options);
                        ApplyImpactCountAuthority(payload, analysis);
                        var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                            payload,
                            options,
                            jsonOptions,
                            "impact",
                            "impact count",
                            "Reduce --limit, lower --max-hops, use --count, or increase --max-json-bytes.");
                        if (writeExitCode != CommandExitCodes.Success)
                            return writeExitCode;
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
                            AddImpactDefinitionsJsonFields(zeroPayload, analysis, options, jsonOptions);
                            if (analysis.ZeroResultReason != null)
                                zeroPayload["zero_result_reason"] = analysis.ZeroResultReason;
                            AddImpactFailureJsonFields(zeroPayload, analysis, jsonOptions);
                            if (analysis.Suggestion != null)
                                zeroPayload["suggestion"] = analysis.Suggestion;
                            AddGraphContractJsonFields(zeroPayload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                            AddImpactOptionWarnings(zeroPayload, options);
                        });
                    if (!analysis.GraphTableAvailable)
                        payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                    var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                        payload,
                        options,
                        jsonOptions,
                        "impact",
                        "impact",
                        "Reduce --limit, lower --max-hops, use --count, or increase --max-json-bytes.");
                    if (writeExitCode != CommandExitCodes.Success)
                        return writeExitCode;
                }
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr($"No impact found for '{analysis.Query}'.");
                    WriteImpactResolutionHint(analysis);
                    WriteGraphSupportHint(options.Lang, reader);
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
                    AddImpactTraversalRootJsonFields(payload, analysis);
                    AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                    if (analysis.TruncatedReason != null)
                        payload["truncated_reason"] = analysis.TruncatedReason;
                    AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                    AddImpactOptionWarnings(payload, options);
                    AddCountEnvelopeJsonFields(payload, reader, jsonOptions, options);
                    ApplyImpactCountAuthority(payload, analysis);
                    AddActiveSqliteDiagnostics(payload);
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
                };
                AddImpactDefinitionsJsonFields(payload, analysis, options, jsonOptions);
                AddImpactTerminationJsonFields(payload, analysis, jsonOptions);
                if (analysis.TruncatedReason != null)
                    payload["truncated_reason"] = analysis.TruncatedReason;
                if (analysis.Suggestion != null)
                    payload["suggestion"] = analysis.Suggestion;
                AddImpactFailureJsonFields(payload, analysis, jsonOptions);
                AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                AddImpactOptionWarnings(payload, options);
                var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                    payload,
                    options,
                    jsonOptions,
                    "impact",
                    "impact",
                    "Reduce --limit, lower --max-hops, use --count, or increase --max-json-bytes.");
                if (writeExitCode != CommandExitCodes.Success)
                    return writeExitCode;
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
                            WriteOptionalCallsiteExcerpt(
                                r.CallsiteLine,
                                r.CallsiteColumn,
                                r.CallsiteStartLine,
                                r.CallsiteContent,
                                r.CallsiteOmittedReferenceCount,
                                $"  {indent}");
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

    private static void AddImpactDefinitionsJsonFields(JsonObject payload, ImpactAnalysisResult analysis, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        AddImpactTraversalRootJsonFields(payload, analysis);
        var definitions = BuildImpactDefinitionJsonResults(analysis.Definitions);
        var definitionLimit = Math.Max(1, options.Limit);
        var visibleDefinitions = definitions.Take(definitionLimit).ToList();
        var logicalDefinitionCount = analysis.LogicalDefinitionCount;
        var definitionsCollapsed = logicalDefinitionCount < analysis.DefinitionCount;
        payload["definition_count"] = analysis.DefinitionCount;
        payload["definition_file_count"] = analysis.DefinitionFileCount;
        payload["has_multiple_definitions"] = analysis.HasMultipleDefinitions;
        payload["has_class_like_definitions"] = analysis.HasClassLikeDefinitions;
        payload["has_multiple_definition_files"] = analysis.HasMultipleDefinitionFiles;
        payload["definition_output_count"] = visibleDefinitions.Count;
        payload["logical_definition_count"] = logicalDefinitionCount;
        payload["definition_result_scope"] = definitionsCollapsed ? "logical_partial_families" : "definition_sites";
        payload["definitions_collapsed"] = definitionsCollapsed;
        payload["definition_limit"] = definitionLimit;
        payload["definitions"] = JsonSerializer.SerializeToNode(visibleDefinitions, CliJsonSerializerContextFactory.Create(jsonOptions).ListSymbolResult);
        if (visibleDefinitions.Count >= logicalDefinitionCount)
            return;

        payload["definitions_truncated"] = true;
        payload["definitions_omitted"] = logicalDefinitionCount - visibleDefinitions.Count;
        payload["definitions_hint"] = "Raise --limit or narrow with --lang, --kind, --path, or --exclude-path to inspect additional matching definitions.";
    }

    private static void AddImpactTraversalRootJsonFields(JsonObject payload, ImpactAnalysisResult analysis)
    {
        payload["traversal_root_scope"] = analysis.TraversalRootScope;
        payload["identity_root_available"] = analysis.IdentityRootAvailable;
        payload["graph_evidence_confidence"] = analysis.GraphEvidenceConfidence;
        payload["identity_root_resolution_truncated"] = analysis.IdentityRootResolutionTruncated;
        if (analysis.IdentityRootUnavailableReason != null)
            payload["identity_root_unavailable_reason"] = analysis.IdentityRootUnavailableReason;
        payload["authoritative_count"] = analysis.CountIsAuthoritative;
        if (!analysis.CountIsAuthoritative)
        {
            payload["degraded"] = true;
        }
        if (analysis.TraversalPartialFamilyId == null)
            return;

        payload["traversal_partial_family_id"] = analysis.TraversalPartialFamilyId;
        payload["partial_family_member_count"] = analysis.PartialFamilyMemberCount;
        payload["partial_family_member_root_count"] = analysis.PartialFamilyMemberRootCount;
        payload["partial_family_member_root_limit"] = analysis.PartialFamilyMemberRootLimit;
        payload["partial_family_member_root_truncated"] = analysis.PartialFamilyMemberRootTruncated;
        payload["partial_family_member_root_omitted"] = analysis.PartialFamilyMemberRootOmitted;
    }

    internal static void ApplyImpactCountAuthority(JsonObject payload, ImpactAnalysisResult analysis)
    {
        if (analysis.CountIsAuthoritative)
            return;

        payload["degraded"] = true;
        payload["authoritative_count"] = false;
    }

    private static void WriteImpactIdentityRootWarningIfNeeded(ImpactAnalysisResult analysis)
    {
        if (analysis.IdentityRootAvailable)
            return;

        var reason = analysis.IdentityRootUnavailableReason ?? "unknown";
        CommandErrorWriter.WriteStderr(
            $"WARN: impact traversal has no identity-backed root ({reason}); confirmed counts are not authoritative.");
    }

    private static List<SymbolResult> BuildImpactDefinitionJsonResults(IReadOnlyList<SymbolResult> definitions)
        => LogicalPartialSymbolGrouper.Group(definitions);

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
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteUnsupportedOptionError("deps", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("deps")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "deps", options.LanguageValidationError ? jsonOptions : null))
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
                "Use `cdidx deps --json --summary-only`.");
            return CommandExitCodes.UsageError;
        }
        if (options.SummaryOnly && depsFormat == OutputFormatJsonGraph)
        {
            WriteUsageError(
                "deps --summary-only is not supported with --format json-graph because summary mode does not emit graph-shaped nodes or edges.",
                GetUsageLineOrThrow("deps"),
                "Use `cdidx deps --json --summary-only --path <glob>` for a compact summary, or remove --summary-only for json-graph output.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor != null || options.UnusedCursorOffset.HasValue || options.OutlineCursorOffset.HasValue)
        {
            WriteUsageError(
                "deps accepts only dependency-cycle cursors returned by a previous `deps --cycles` response.",
                GetUsageLineOrThrow("deps"),
                "Use the opaque `next_cursor` value without modification.");
            return CommandExitCodes.UsageError;
        }
        if (options.DependencyCycleCursor.HasValue && !options.DependencyCycles)
        {
            WriteUsageError(
                "deps --cursor requires --cycles.",
                GetUsageLineOrThrow("deps"),
                "Use `cdidx deps --cycles --json --cursor <next_cursor>`.");
            return CommandExitCodes.UsageError;
        }
        if (!options.DependencyCycles
            && cmdArgs.Any(static arg => arg == "--graph-budget" || arg.StartsWith("--graph-budget=", StringComparison.Ordinal)))
        {
            WriteUsageError(
                "deps --graph-budget requires --cycles.",
                GetUsageLineOrThrow("deps"),
                "Use `cdidx deps --cycles --graph-budget <n>`.");
            return CommandExitCodes.UsageError;
        }

        var reverse = cmdArgs.Any(static arg => arg == "--reverse");
        var cycleCursorBaseFingerprint = BuildDependencyCycleCursorFingerprint(options, reverse);

        return WithDb(options, jsonOptions, reader =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteReferenceGraphCompletenessWarningIfNeeded(emitsJson, reader);
            if (TryWriteInvalidWorkspaceDependencyDatabaseError(options, out var workspaceDbExitCode))
                return workspaceDbExitCode;
            if (options.SummaryOnly
                && !options.DependencyCycles
                && depsFormat == OutputFormatEdgeList
                && IsBroadDependencySummaryQuery(options))
            {
                var candidateFiles = reader.CountListFiles(null, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests).Count;
                if (candidateFiles > BroadDependencySummaryCandidateFileLimit)
                {
                    WriteUsageError(
                        $"deps --summary-only is too broad for this index ({candidateFiles} candidate files).",
                        GetUsageLineOrThrow("deps"),
                        "Add --path, --lang, or --exclude-tests so the summary can be computed without materializing a workspace-wide graph.");
                    return CommandExitCodes.UsageError;
                }
            }

            List<FileDependencyResult> results;
            List<FileDependencyResult> cycleCandidates;
            var cycleCandidateRowCount = 0;
            var cycleGraphBudget = options.DependencyCycleGraphBudget;
            var cyclePageOffset = options.DependencyCycleCursor?.Offset ?? 0;
            var machineReadable = DepsEmitsJson(options, depsFormat);
            if (options.DependencyCycles)
            {
                WriteGraphLiveness("deps", "read_cycle_candidates", options, depsFormat, machineReadable: machineReadable);
                cycleCandidates = GetWorkspaceFileDependencyCycleCandidates(
                    reader,
                    options,
                    reverse,
                    checked(cycleGraphBudget + 1),
                    out cycleCandidateRowCount,
                    cancellationToken);
                results = cycleCandidates.Take(cycleGraphBudget).ToList();
                cycleCandidates = results;
            }
            else
            {
                WriteGraphLiveness("deps", "read_edges", options, depsFormat, machineReadable: machineReadable);
                results = GetWorkspaceFileDependencies(reader, options, reverse, options.Limit, cancellationToken);
                cycleCandidates = results;
            }
            WriteGraphLiveness("deps", "shape_output", options, depsFormat, rows: results.Count, machineReadable: machineReadable);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            WriteHdlGraphContractWarningIfNeeded(emitsJson, hdlGraphSignal);
            if (results.Count == 0)
            {
                var zeroSqlGraphSignal = baseSqlGraphSignal;
                var zeroSymbolFilter = ApplyDependencySymbolFilters([], options).Summary;
                if (options.DependencyCycles)
                {
                    var zeroCursorFingerprint = BuildDependencyCycleGraphFingerprint(
                        cycleCursorBaseFingerprint,
                        [],
                        cycleCandidateRowCount);
                    if (options.DependencyCycleCursor is { } suppliedCursor
                        && !string.Equals(suppliedCursor.Fingerprint, zeroCursorFingerprint, StringComparison.Ordinal))
                    {
                        WriteDependencyCycleCursorMismatchError();
                        return CommandExitCodes.UsageError;
                    }
                    var zeroAnalysis = AnalyzeDependencyCycles(
                        [],
                        cycleGraphBudget,
                        cycleCandidateRowCount,
                        options.Limit,
                        cyclePageOffset,
                        zeroCursorFingerprint,
                        cancellationToken);
                    if (options.DependencyCycleCursor.HasValue)
                    {
                        WriteUsageError(
                            "deps --cursor points beyond the available dependency-cycle result set.",
                            GetUsageLineOrThrow("deps"),
                            "Start a new dependency-cycle query without --cursor.");
                        return CommandExitCodes.UsageError;
                    }
                    if (depsFormat is OutputFormatDot or OutputFormatGraphMl or OutputFormatJsonGraph)
                    {
                        var writeExitCode = WriteDependencyGraph(
                            [],
                            depsFormat,
                            jsonOptions,
                            reader,
                            options,
                            zeroSqlGraphSignal,
                            zeroSymbolFilter,
                            payload =>
                            {
                                AddDependencyCycleAnalysisJsonFields(payload, zeroAnalysis);
                                AddDependencyGraphAvailabilityJsonFields(payload, reader._hasReferencesTable);
                            });
                        return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                    }
                    if (options.Json)
                    {
                        var payload = new JsonObject { ["count"] = 0 };
                        if (options.SummaryOnly)
                            payload["summary_only"] = true;
                        else
                            payload["cycles"] = new JsonArray();
                        AddDependencyCycleAnalysisJsonFields(payload, zeroAnalysis);
                        AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, zeroSqlGraphSignal, zeroSymbolFilter);
                        AddDependencyGraphAvailabilityJsonFields(payload, reader._hasReferencesTable);
                        AddFreshnessHint(payload, reader);
                        WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: 0, cycleCount: 0, machineReadable: machineReadable);
                        var writeExitCode = WriteDepsJsonPayload(payload, options, jsonOptions);
                        return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                    }

                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No dependency cycles found", options));
                    WriteSqlGraphContractWarningIfNeeded(json: false, zeroSqlGraphSignal, reader, options);
                    WriteDegradedGraphZeroResult(reader, "cycles", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                    return ZeroResultExitCode(options);
                }
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
                        extraFields: payload => AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, zeroSqlGraphSignal, zeroSymbolFilter));
                    if (options.SummaryOnly)
                        payload["summary_only"] = true;
                    payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
                    var writeExitCode = WriteDepsJsonPayload(payload, options, jsonOptions);
                    return writeExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : writeExitCode;
                }
                else if (options.Json)
                {
                    var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: options.SummaryOnly ? null : "edges", graphTableAvailable: true, degraded: !zeroSqlGraphSignal.Ready, queryOptions: options, extraFields: payload => AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, zeroSqlGraphSignal, zeroSymbolFilter));
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
                var cycleCursorFingerprint = BuildDependencyCycleGraphFingerprint(
                    cycleCursorBaseFingerprint,
                    symbolFilter.Edges,
                    cycleCandidateRowCount);
                if (options.DependencyCycleCursor is { } suppliedCursor
                    && !string.Equals(suppliedCursor.Fingerprint, cycleCursorFingerprint, StringComparison.Ordinal))
                {
                    WriteDependencyCycleCursorMismatchError();
                    return CommandExitCodes.UsageError;
                }
                WriteGraphLiveness("deps", "analyze_cycles", options, depsFormat, rows: symbolFilter.Edges.Count, machineReadable: machineReadable);
                var analysis = AnalyzeDependencyCycles(
                    symbolFilter.Edges,
                    cycleGraphBudget,
                    cycleCandidateRowCount,
                    options.Limit,
                    cyclePageOffset,
                    cycleCursorFingerprint,
                    cancellationToken);
                outputEdges = analysis.Edges;
                cycles = analysis.Cycles;
                dependencyCycleAnalysis = analysis;
                if (options.DependencyCycleCursor.HasValue && cyclePageOffset >= analysis.TotalCycleCount)
                {
                    WriteUsageError(
                        "deps --cursor points beyond the available dependency-cycle result set.",
                        GetUsageLineOrThrow("deps"),
                        "Start a new dependency-cycle query without --cursor.");
                    return CommandExitCodes.UsageError;
                }
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
                        extraFields: payload => AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, sqlGraphSignal, symbolFilter.Summary));
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
                    AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, sqlGraphSignal, symbolFilter.Summary);
                    AddFreshnessHint(payload, reader);
                    WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: outputEdges.Count, cycleCount: 0, machineReadable: machineReadable);
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
                WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: outputEdges.Count, cycleCount: cycles.Count, machineReadable: machineReadable);
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
                        payload["cycles"] = dependencyCycleAnalysis == null
                            ? new JsonArray()
                            : BuildDependencyCyclesJson(dependencyCycleAnalysis.Components, dependencyCycleAnalysis.PageOffset);
                    if (dependencyCycleAnalysis != null)
                        AddDependencyCycleAnalysisJsonFields(payload, dependencyCycleAnalysis);
                }
                else if (!options.SummaryOnly)
                    payload["edges"] = JsonSerializer.SerializeToNode(outputEdges, CliJsonSerializerContextFactory.Create(jsonOptions).ListFileDependencyResult);
                AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, sqlGraphSignal, symbolFilter.Summary);
                AddFreshnessHint(payload, reader);
                WriteGraphLiveness("deps", "write_output", options, depsFormat, rows: outputEdges.Count, cycleCount: cycles.Count, machineReadable: machineReadable);
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

    private static void WriteDependencyCycleCursorMismatchError()
        => WriteUsageError(
            "deps --cursor does not match the current dependency-cycle filters, graph budget, or indexed graph.",
            GetUsageLineOrThrow("deps"),
            "Reuse the same filters and --graph-budget without reindexing, or start a new query without --cursor.");

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

    private static bool IsBroadDependencySummaryQuery(QueryCommandOptions options)
        => options.PathPatterns.Count == 0
           && options.ExcludePaths.Count == 0
           && !options.ExcludeTests
           && options.Lang == null
           && options.WorkspaceDbPaths.Count == 0;

    private static bool TryNormalizeDepsFormat(string rawFormat, out string format, out string? error)
    {
        format = rawFormat.ToLowerInvariant();
        error = null;
        switch (format)
        {
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

    internal const string DependencyCycleRankingMode = "reference_count_desc_internal_edge_count_desc_length_desc_path";
    private const string DependencyCycleCursorPrefix = "deps-cycle:v1:";
    private const int MaxDependencyCycleCursorLength = 256;

    internal static List<FileDependencyResult> FilterCycleEdges(List<FileDependencyResult> results, out List<List<string>> cycles)
    {
        var components = FindRankedDependencyCycles(results, CancellationToken.None);
        cycles = components.Select(static component => component.Nodes).ToList();
        return FilterEdgesToComponents(results, components);
    }

    internal static List<List<string>> FindDependencyCycles(IReadOnlyList<FileDependencyResult> edges)
        => FindRankedDependencyCycles(edges, CancellationToken.None)
            .Select(static component => component.Nodes)
            .ToList();

    private static List<DependencyCycleComponent> FindRankedDependencyCycles(
        IReadOnlyList<FileDependencyResult> edges,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adjacencySets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var reverseSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adjacencySets.TryGetValue(edge.SourcePath, out var targets))
                adjacencySets[edge.SourcePath] = targets = new HashSet<string>(StringComparer.Ordinal);
            targets.Add(edge.TargetPath);
            adjacencySets.TryAdd(edge.TargetPath, new HashSet<string>(StringComparer.Ordinal));

            if (!reverseSets.TryGetValue(edge.TargetPath, out var sources))
                reverseSets[edge.TargetPath] = sources = new HashSet<string>(StringComparer.Ordinal);
            sources.Add(edge.SourcePath);
            reverseSets.TryAdd(edge.SourcePath, new HashSet<string>(StringComparer.Ordinal));
        }

        var adjacency = adjacencySets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        var reverse = reverseSets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        var nodes = adjacency.Keys.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finishOrder = new List<string>(nodes.Length);
        foreach (var root in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(root))
                continue;

            var stack = new Stack<(string Node, int NextTarget)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = stack.Pop();
                var targets = adjacency[frame.Node];
                if (frame.NextTarget < targets.Length)
                {
                    stack.Push((frame.Node, frame.NextTarget + 1));
                    var target = targets[frame.NextTarget];
                    if (visited.Add(target))
                        stack.Push((target, 0));
                    continue;
                }

                finishOrder.Add(frame.Node);
            }
        }

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var cycleNodes = new List<List<string>>();
        for (var i = finishOrder.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = finishOrder[i];
            if (!assigned.Add(root))
                continue;

            var component = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = stack.Pop();
                component.Add(node);
                var sources = reverse[node];
                for (var sourceIndex = sources.Length - 1; sourceIndex >= 0; sourceIndex--)
                {
                    var source = sources[sourceIndex];
                    if (assigned.Add(source))
                        stack.Push(source);
                }
            }

            component.Sort(StringComparer.Ordinal);
            var selfCycle = component.Count == 1 && adjacencySets[component[0]].Contains(component[0]);
            if (component.Count > 1 || selfCycle)
                cycleNodes.Add(component);
        }

        if (cycleNodes.Count == 0)
            return [];

        var nodeToComponent = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var componentIndex = 0; componentIndex < cycleNodes.Count; componentIndex++)
            foreach (var node in cycleNodes[componentIndex])
                nodeToComponent[node] = componentIndex;
        var internalEdgeCounts = new int[cycleNodes.Count];
        var referenceCounts = new long[cycleNodes.Count];
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nodeToComponent.TryGetValue(edge.SourcePath, out var sourceComponent)
                || !nodeToComponent.TryGetValue(edge.TargetPath, out var targetComponent)
                || sourceComponent != targetComponent)
                continue;
            internalEdgeCounts[sourceComponent]++;
            referenceCounts[sourceComponent] += edge.ReferenceCount;
        }

        return cycleNodes
            .Select((component, componentIndex) => new DependencyCycleComponent(
                component,
                internalEdgeCounts[componentIndex],
                referenceCounts[componentIndex]))
            .OrderByDescending(static component => component.ReferenceCount)
            .ThenByDescending(static component => component.InternalEdgeCount)
            .ThenByDescending(static component => component.Nodes.Count)
            .ThenBy(static component => component.Nodes[0], StringComparer.Ordinal)
            .ToList();
    }

    private static List<FileDependencyResult> FilterEdgesToComponents(
        IReadOnlyList<FileDependencyResult> edges,
        IReadOnlyList<DependencyCycleComponent> components)
    {
        if (components.Count == 0)
            return [];
        var nodeToComponent = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
            foreach (var node in components[componentIndex].Nodes)
                nodeToComponent[node] = componentIndex;
        return edges
            .Where(edge => nodeToComponent.TryGetValue(edge.SourcePath, out var sourceComponent)
                           && nodeToComponent.TryGetValue(edge.TargetPath, out var targetComponent)
                           && sourceComponent == targetComponent)
            .ToList();
    }

    internal static JsonArray BuildDependencyCyclesJson(
        IReadOnlyList<DependencyCycleComponent> components,
        int pageOffset)
    {
        var array = new JsonArray();
        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            array.Add(new JsonObject
            {
                ["rank"] = pageOffset + i + 1,
                ["length"] = component.Nodes.Count,
                ["internal_edge_count"] = component.InternalEdgeCount,
                ["reference_count"] = component.ReferenceCount,
                ["nodes"] = new JsonArray(component.Nodes.Select(node => JsonValue.Create(node)).ToArray<JsonNode?>())
            });
        }
        return array;
    }

    internal static JsonArray BuildDependencyCyclesJson(IReadOnlyList<List<string>> cycles)
        => BuildDependencyCyclesJson(
            cycles.Select(static cycle => new DependencyCycleComponent(cycle, 0, 0)).ToList(),
            pageOffset: 0);

    internal sealed record DependencyCycleComponent(
        List<string> Nodes,
        int InternalEdgeCount,
        long ReferenceCount);

    internal sealed record DependencyCycleAnalysis(
        List<FileDependencyResult> Edges,
        List<DependencyCycleComponent> Components,
        bool Truncated,
        string TerminationReason,
        string? TruncatedReason,
        int GraphEdgeCount,
        int GraphEdgeBudget,
        bool AnalysisComplete,
        int TotalCycleCount,
        bool TotalCycleCountAuthoritative,
        int PageOffset,
        int PageLimit,
        bool HasMore,
        string? NextCursor,
        string DetectionMode,
        string RankingMode)
    {
        public List<List<string>> Cycles => Components.Select(static component => component.Nodes).ToList();
    }

    internal static DependencyCycleAnalysis AnalyzeDependencyCycles(
        IReadOnlyList<FileDependencyResult> graphEdges,
        int graphEdgeBudget,
        int graphRowCount,
        int displayLimit,
        int pageOffset,
        string cursorFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allComponents = FindRankedDependencyCycles(graphEdges, cancellationToken);
        var graphBudgetReached = graphRowCount > graphEdgeBudget;
        var components = allComponents.Skip(pageOffset).Take(displayLimit).ToList();
        var nextOffset = pageOffset + components.Count;
        var hasMore = nextOffset < allComponents.Count;
        var outputEdges = FilterEdgesToComponents(graphEdges, components);
        var truncatedReason = graphBudgetReached
            ? "graph_edge_budget"
            : hasMore
                ? "page_limit"
                : null;
        var terminationReason = truncatedReason switch
        {
            "graph_edge_budget" => "graph_budget_reached",
            "page_limit" => "page_limit_reached",
            _ => "completed",
        };
        var nextCursor = hasMore
            ? FormatDependencyCycleCursor(new DependencyCycleCursor(nextOffset, cursorFingerprint))
            : null;

        return new DependencyCycleAnalysis(
            outputEdges,
            components,
            graphBudgetReached || hasMore,
            terminationReason,
            truncatedReason,
            Math.Min(graphRowCount, graphEdgeBudget),
            graphEdgeBudget,
            !graphBudgetReached,
            allComponents.Count,
            !graphBudgetReached,
            pageOffset,
            displayLimit,
            hasMore,
            nextCursor,
            DependencyCycleDetectionMode,
            DependencyCycleRankingMode);
    }

    internal static void AddDependencyCycleAnalysisJsonFields(JsonObject payload, DependencyCycleAnalysis analysis, bool mcpArguments = false)
    {
        payload["truncated"] = analysis.Truncated;
        payload["termination_reason"] = analysis.TerminationReason;
        if (analysis.TruncatedReason != null)
            payload["truncated_reason"] = analysis.TruncatedReason;
        payload["analysis_complete"] = analysis.AnalysisComplete;
        payload["graph_edge_count"] = analysis.GraphEdgeCount;
        payload["graph_edge_budget"] = analysis.GraphEdgeBudget;
        payload["candidate_edge_count"] = analysis.GraphEdgeCount;
        payload["candidate_edge_limit"] = analysis.GraphEdgeBudget;
        payload["cycle_detection_mode"] = analysis.DetectionMode;
        payload["cycle_ranking_mode"] = analysis.RankingMode;
        payload["cycle_ranking_stable"] = true;
        payload["cycle_result_scope"] = BuildDependencyCycleResultScope(analysis.TruncatedReason);
        payload["cycle_result_note"] = BuildDependencyCycleResultNote(analysis.TruncatedReason);
        payload["total_cycle_count"] = analysis.TotalCycleCount;
        payload["total_cycle_count_authoritative"] = analysis.TotalCycleCountAuthoritative;
        payload["page_offset"] = analysis.PageOffset;
        payload["page_limit"] = analysis.PageLimit;
        payload["returned_count"] = analysis.Components.Count;
        payload["has_more"] = analysis.HasMore;
        payload["next_cursor"] = analysis.NextCursor;
        payload["next_step_flags"] = BuildDependencyCycleNextStepFlagsJson(analysis, mcpArguments);
    }

    private static string BuildDependencyCycleTruncationSummary(DependencyCycleAnalysis analysis)
        => analysis.TruncatedReason == "page_limit"
            ? $"page complete: showing ranked cycles {analysis.PageOffset + 1}-{analysis.PageOffset + analysis.Components.Count}"
            : $"partial analysis: graph edge budget reached after {analysis.GraphEdgeCount} edges";

    private static string BuildDependencyCycleTruncationWarning(DependencyCycleAnalysis analysis)
    {
        var nextSteps = BuildDependencyCycleNextStepFlags(analysis, mcpArguments: false);
        var nextStepsText = nextSteps.Count == 0
            ? string.Empty
            : $" Next steps: {string.Join(", ", nextSteps)}.";
        return "Warning: dependency cycle results are truncated "
               + $"({BuildDependencyCycleTruncationSummary(analysis)}). "
               + BuildDependencyCycleResultNote(analysis.TruncatedReason)
               + nextStepsText;
    }

    private static string BuildDependencyCycleResultScope(string? truncatedReason)
        => truncatedReason switch
        {
            "graph_edge_budget" => "partial_graph_budget",
            "page_limit" => "complete_graph_page",
            _ => "complete_graph",
        };

    private static string BuildDependencyCycleResultNote(string? truncatedReason)
        => truncatedReason switch
        {
            "graph_edge_budget" => "The graph edge budget was reached before cdidx could prove SCC completeness; increase --graph-budget or narrow the graph filters.",
            "page_limit" => "SCC analysis is complete for the selected graph; continue with next_cursor to retrieve the next stable ranked page.",
            _ => "SCC analysis and the stable ranked result set are complete for the selected graph.",
        };

    private static JsonArray BuildDependencyCycleNextStepFlagsJson(
        DependencyCycleAnalysis analysis,
        bool mcpArguments)
        => new(BuildDependencyCycleNextStepFlags(analysis, mcpArguments)
            .Select(flag => JsonValue.Create(flag))
            .ToArray<JsonNode?>());

    private static List<string> BuildDependencyCycleNextStepFlags(
        DependencyCycleAnalysis analysis,
        bool mcpArguments)
    {
        var flags = new List<string>();
        if (analysis.HasMore && analysis.NextCursor != null)
            flags.Add(mcpArguments ? $"cursor={analysis.NextCursor}" : $"--cursor {analysis.NextCursor}");
        if (analysis.TruncatedReason == "graph_edge_budget")
        {
            var higherBudget = GetHigherDependencyCycleGraphBudget(analysis.GraphEdgeBudget);
            if (higherBudget > analysis.GraphEdgeBudget)
                flags.Add(mcpArguments ? $"graphBudget={higherBudget}" : $"--graph-budget {higherBudget}");
            if (!mcpArguments)
            {
                flags.Add(CliFlagSchema.GetUsageTokenForCommand("deps", "--suppress-noise"));
                flags.Add(CliFlagSchema.GetUsageTokenForCommand("deps", "--symbol"));
                flags.Add(CliFlagSchema.GetUsageTokenForCommand("deps", "--symbol-family"));
            }
            flags.Add(mcpArguments
                ? "path=<narrower-glob>"
                : CliFlagSchema.GetUsageTokenForCommand("deps", "--path", "<narrower-glob>"));
        }

        return flags;
    }

    private static int GetHigherDependencyCycleGraphBudget(int currentBudget)
    {
        if (currentBudget >= MaxDependencyCycleGraphBudget)
            return MaxDependencyCycleGraphBudget;
        var doubled = currentBudget > MaxDependencyCycleGraphBudget / 2
            ? MaxDependencyCycleGraphBudget
            : currentBudget * 2;
        return Math.Min(MaxDependencyCycleGraphBudget, Math.Max(currentBudget + 1, doubled));
    }

    internal static string BuildDependencyCycleCursorFingerprint(QueryCommandOptions options, bool reverse)
    {
        var builder = new StringBuilder();
        AppendCursorFingerprintValue(builder, "db", DbPathResolver.NormalizeDbPath(options.DbPath));
        AppendCursorFingerprintValue(builder, "workspace", string.Join('\u001f', options.WorkspaceDbPaths));
        AppendCursorFingerprintValue(builder, "lang", options.Lang);
        AppendCursorFingerprintValue(builder, "path", string.Join('\u001f', options.PathPatterns));
        AppendCursorFingerprintValue(builder, "exclude", string.Join('\u001f', options.ExcludePaths));
        AppendCursorFingerprintValue(builder, "excludeTests", options.ExcludeTests ? "1" : "0");
        AppendCursorFingerprintValue(builder, "includeGenerated", options.IncludeGenerated ? "1" : "0");
        AppendCursorFingerprintValue(builder, "reverse", reverse ? "1" : "0");
        AppendCursorFingerprintValue(builder, "symbols", string.Join('\u001f', options.DependencySymbols));
        AppendCursorFingerprintValue(builder, "families", string.Join('\u001f', options.DependencySymbolFamilies));
        AppendCursorFingerprintValue(builder, "suppressNoise", options.DependencySuppressNoise ? "1" : "0");
        AppendCursorFingerprintValue(builder, "graphBudget", options.DependencyCycleGraphBudget.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static string BuildDependencyCycleGraphFingerprint(
        string queryFingerprint,
        IReadOnlyList<FileDependencyResult> edges,
        int graphRowCount)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDependencyCycleGraphHashValue(hash, queryFingerprint);
        AppendDependencyCycleGraphHashValue(hash, graphRowCount.ToString(CultureInfo.InvariantCulture));
        foreach (var edge in edges)
        {
            AppendDependencyCycleGraphHashValue(hash, edge.SourceDb);
            AppendDependencyCycleGraphHashValue(hash, edge.SourcePath);
            AppendDependencyCycleGraphHashValue(hash, edge.TargetDb);
            AppendDependencyCycleGraphHashValue(hash, edge.TargetPath);
            AppendDependencyCycleGraphHashValue(hash, edge.ReferenceCount.ToString(CultureInfo.InvariantCulture));
        }

        var digest = hash.GetHashAndReset();
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static void AppendDependencyCycleGraphHashValue(IncrementalHash hash, string? value)
    {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> buffer = byteCount <= 1024
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            var written = Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
            hash.AppendData(buffer[..written]);
            Span<byte> separator = stackalloc byte[1] { 0 };
            hash.AppendData(separator);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AppendCursorFingerprintValue(StringBuilder builder, string name, string? value)
        => builder.Append(name).Append('=').Append(value ?? string.Empty).Append('\n');

    internal static string FormatDependencyCycleCursor(DependencyCycleCursor cursor)
    {
        var payload = Encoding.UTF8.GetBytes(
            cursor.Offset.ToString(CultureInfo.InvariantCulture) + "\n" + cursor.Fingerprint);
        return DependencyCycleCursorPrefix
               + Convert.ToBase64String(payload)
                   .TrimEnd('=')
                   .Replace('+', '-')
                   .Replace('/', '_');
    }

    internal static bool TryParseDependencyCycleCursor(string value, out DependencyCycleCursor cursor)
    {
        cursor = default;
        if (value.Length > MaxDependencyCycleCursorLength
            || !value.StartsWith(DependencyCycleCursorPrefix, StringComparison.Ordinal))
            return false;
        var encoded = value[DependencyCycleCursorPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separator = payload.IndexOf('\n');
            if (separator <= 0
                || !int.TryParse(payload.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                || offset < 0)
                return false;
            var fingerprint = payload[(separator + 1)..];
            if (fingerprint.Length != 32 || fingerprint.Any(static ch => !Uri.IsHexDigit(ch)))
                return false;
            cursor = new DependencyCycleCursor(offset, fingerprint.ToLowerInvariant());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DependencySymbolFilterResult ApplyDependencySymbolFilters(IReadOnlyList<FileDependencyResult> edges, QueryCommandOptions options)
    {
        var applied = options.DependencySuppressNoise || options.DependencySymbols.Count > 0 || options.DependencySymbolFamilies.Count > 0;
        if (!applied)
        {
            var unchangedSymbolCount = edges.Sum(edge => SplitDependencySymbols(edge.Symbols).Count);
            var unchangedReferenceCount = edges.Sum(static edge => (long)edge.ReferenceCount);
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
                    SymbolsAfter: unchangedSymbolCount,
                    ReferencesBefore: unchangedReferenceCount,
                    ReferencesAfter: unchangedReferenceCount,
                    SuppressionReasons: []));
        }

        var filteredEdges = new List<FileDependencyResult>(edges.Count);
        var symbolsBefore = 0;
        var symbolsAfter = 0;
        long referencesBefore = 0;
        long referencesAfter = 0;
        var headingEdgesAffected = 0;
        var headingEdgesRemoved = 0;
        long headingReferencesRemoved = 0;
        foreach (var edge in edges)
        {
            referencesBefore += edge.ReferenceCount;
            var edgeEvidence = edge.Evidence ?? [];
            var preservesExplicitMarkdownLink = edgeEvidence.Any(
                static evidence => evidence.Origin == "markdown_explicit_link");
            var keptEvidence = edgeEvidence;
            var referenceCount = edge.ReferenceCount;
            if (options.DependencySuppressNoise && edgeEvidence.Count > 0)
            {
                keptEvidence = edgeEvidence
                    .Where(static evidence => evidence.Origin != "markdown_heading_name_match")
                    .ToList();
                var removedReferenceCount = edgeEvidence
                    .Where(static evidence => evidence.Origin == "markdown_heading_name_match")
                    .Sum(static evidence => (long)evidence.ReferenceCount);
                if (removedReferenceCount > 0)
                {
                    headingEdgesAffected++;
                    headingReferencesRemoved += removedReferenceCount;
                    referenceCount = (int)Math.Max(0L, edge.ReferenceCount - removedReferenceCount);
                    if (referenceCount == 0)
                        headingEdgesRemoved++;
                }
            }

            var symbols = GetDependencySymbols(edge);
            symbolsBefore += symbols.Count;
            var keptSymbols = symbols
                .Where(symbol => KeepDependencySymbol(symbol, options, preservesExplicitMarkdownLink))
                .ToList();
            if (keptSymbols.Count == 0)
                continue;

            if (referenceCount == 0)
                continue;

            symbolsAfter += keptSymbols.Count;
            referencesAfter += referenceCount;
            filteredEdges.Add(CopyDependencyEdge(
                edge,
                keptSymbols,
                referenceCount,
                keptEvidence));
        }

        IReadOnlyList<DependencySuppressionReasonSummary> suppressionReasons =
            headingReferencesRemoved > 0
                ? [
                    new DependencySuppressionReasonSummary(
                        Reason: "markdown_heading_name_match",
                        EdgesAffected: headingEdgesAffected,
                        EdgesRemoved: headingEdgesRemoved,
                        ReferencesRemoved: headingReferencesRemoved),
                ]
                : [];
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
                SymbolsAfter: symbolsAfter,
                ReferencesBefore: referencesBefore,
                ReferencesAfter: referencesAfter,
                SuppressionReasons: suppressionReasons));
    }

    private static bool KeepDependencySymbol(
        string symbol,
        QueryCommandOptions options,
        bool preservesExplicitMarkdownLink)
    {
        if (options.DependencySuppressNoise
            && !preservesExplicitMarkdownLink
            && DependencyNoiseProfile.IsNoiseSymbol(symbol))
            return false;

        var hasNameFilters = options.DependencySymbols.Count > 0 || options.DependencySymbolFamilies.Count > 0;
        if (!hasNameFilters)
            return true;

        return options.DependencySymbols.Contains(symbol, StringComparer.Ordinal)
               || options.DependencySymbolFamilies.Any(family => symbol.StartsWith(family, StringComparison.Ordinal));
    }

    private static List<string> SplitDependencySymbols(string symbols)
        => symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> GetDependencySymbols(FileDependencyResult edge)
        => edge.SymbolSamples?.ToList() ?? SplitDependencySymbols(edge.Symbols);

    private static FileDependencyResult CopyDependencyEdge(
        FileDependencyResult edge,
        List<string> symbolSamples,
        int referenceCount,
        List<FileDependencyEvidence> evidence)
        => new()
        {
            ResultKind = edge.ResultKind,
            SourcePath = edge.SourcePath,
            TargetPath = edge.TargetPath,
            SourceDb = edge.SourceDb,
            TargetDb = edge.TargetDb,
            ReferenceCount = referenceCount,
            RankingScore = DependencyNoiseProfile.ComputeRankingScore(referenceCount, symbolSamples),
            Symbols = string.Join(",", symbolSamples),
            SymbolSamples = symbolSamples,
            Evidence = evidence,
        };

    private static void AddDependencySchemaJsonFields(
        JsonObject payload,
        DbReader reader,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SqlGraphContractSignal sqlGraphSignal,
        DependencySymbolFilterSummary symbolFilter)
    {
        payload["api_version"] = JsonOutputContract.ApiVersion;
        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
        AddGraphContractJsonFields(
            payload,
            reader,
            jsonOptions,
            sqlGraphSignal,
            reader.GetHdlGraphContractSignal(
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests));
        AddDependencySymbolFilterJsonFields(payload, symbolFilter, jsonOptions);
    }

    private static void AddDependencyGraphAvailabilityJsonFields(JsonObject payload, bool graphTableAvailable)
    {
        payload["graph_table_available"] = graphTableAvailable;
        if (graphTableAvailable)
            return;

        payload["degraded"] = true;
        payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
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
            ["references_before"] = symbolFilter.ReferencesBefore,
            ["references_after"] = symbolFilter.ReferencesAfter,
            ["references_removed"] = symbolFilter.ReferencesBefore - symbolFilter.ReferencesAfter,
        };
        if (symbolFilter.SuppressionReasons.Count > 0)
        {
            filter["suppression_reasons"] = new JsonArray(
                symbolFilter.SuppressionReasons
                    .Select(reason => (JsonNode?)new JsonObject
                    {
                        ["reason"] = reason.Reason,
                        ["edges_affected"] = reason.EdgesAffected,
                        ["edges_removed"] = reason.EdgesRemoved,
                        ["references_removed"] = reason.ReferencesRemoved,
                    })
                    .ToArray());
        }
        if (symbolFilter.Symbols.Count > 0)
            filter["symbol"] = JsonSerializer.SerializeToNode(symbolFilter.Symbols.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (symbolFilter.SymbolFamilies.Count > 0)
            filter["symbol_family"] = JsonSerializer.SerializeToNode(symbolFilter.SymbolFamilies.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        payload["symbol_filter"] = filter;
    }

    private static JsonArray BuildDependencySymbolsJson(FileDependencyResult edge)
        => new(GetDependencySymbols(edge).Select(symbol => JsonValue.Create(symbol)).ToArray<JsonNode?>());

    private static JsonArray BuildDependencyEvidenceJson(IReadOnlyList<FileDependencyEvidence> evidence)
        => new(evidence.Select(item => (JsonNode?)new JsonObject
        {
            ["source_language"] = item.SourceLanguage,
            ["origin"] = item.Origin,
            ["reference_kind"] = item.ReferenceKind,
            ["target_kind"] = item.TargetKind,
            ["reference_count"] = item.ReferenceCount,
        }).ToArray());

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
        int SymbolsAfter,
        long ReferencesBefore,
        long ReferencesAfter,
        IReadOnlyList<DependencySuppressionReasonSummary> SuppressionReasons);

    private sealed record DependencySuppressionReasonSummary(
        string Reason,
        int EdgesAffected,
        int EdgesRemoved,
        long ReferencesRemoved);

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
                ["symbols"] = BuildDependencySymbolsJson(edge),
                ["evidence"] = BuildDependencyEvidenceJson(edge.Evidence ?? []),
            }).ToArray());
        }
        addExtraJsonFields?.Invoke(payload);
        AddDependencySchemaJsonFields(payload, reader, options, jsonOptions, sqlGraphSignal, symbolFilter);
        AddFreshnessHint(payload, reader);
        return WriteDepsJsonPayload(payload, options, jsonOptions);
    }

    private static string EscapeDot(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static List<FileDependencyResult> GetWorkspaceFileDependencies(DbReader primaryReader, QueryCommandOptions options, bool reverse, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = primaryReader.GetFileDependencies(
            limit,
            options.Lang,
            options.PathPatterns,
            options.ExcludePaths,
            options.ExcludeTests,
            reverse,
            cancellationToken,
            options.DependencySymbols,
            options.DependencySymbolFamilies,
            options.DependencySuppressNoise);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.WorkspaceDbPaths.Count == 0)
            return results;

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var primaryDb = memberDbs[0];
        TagFileDependencyResults(results, primaryDb);
        foreach (var normalizedDbPath in memberDbs.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = new DbContext(DbOpenIntent.QueryOnly, normalizedDbPath, cancellationToken);
            var reader = new DbReader(db) { IncludeGenerated = primaryReader.IncludeGenerated };
            var memberResults = reader.GetFileDependencies(
                limit,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                reverse,
                cancellationToken,
                options.DependencySymbols,
                options.DependencySymbolFamilies,
                options.DependencySuppressNoise);
            TagFileDependencyResults(memberResults, normalizedDbPath);
            results.AddRange(memberResults);
        }

        foreach (var sourceDb in memberDbs)
            foreach (var targetDb in memberDbs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(sourceDb, targetDb, StringComparison.Ordinal))
                    continue;
                results.AddRange(GetCrossDatabaseFileDependencies(sourceDb, targetDb, options, reverse, limit, cancellationToken));
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
            cancellationToken,
            options.DependencySymbols,
            options.DependencySymbolFamilies,
            options.DependencySuppressNoise);
        candidateRowCount += primaryCandidateRows;
        if (options.WorkspaceDbPaths.Count == 0)
        {
            if (!options.DependencySuppressNoise)
                return results.Take(limit).ToList();

            candidateRowCount = results.Count(HasRetainedDependencyEvidence);
            return OrderWorkspaceCycleCandidates(results, limit);
        }

        var memberDbs = BuildWorkspaceDependencyDatabaseList(options);
        var primaryDb = memberDbs[0];
        TagFileDependencyResults(results, primaryDb);
        List<FileDependencyResult>? retainedResults = null;
        List<FileDependencyResult>? suppressedResults = null;
        if (options.DependencySuppressNoise)
        {
            candidateRowCount = 0;
            retainedResults = [];
            suppressedResults = [];
            if (AddBoundedWorkspaceCycleCandidates(
                    results,
                    retainedResults,
                    suppressedResults,
                    limit))
            {
                candidateRowCount = retainedResults.Count;
                return OrderWorkspaceCycleCandidates(retainedResults, limit);
            }
            results.Clear();
        }
        else if (results.Count >= limit)
        {
            return results.Take(limit).ToList();
        }

        foreach (var normalizedDbPath in memberDbs.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = new DbContext(DbOpenIntent.QueryOnly, normalizedDbPath, cancellationToken);
            var reader = new DbReader(db) { IncludeGenerated = primaryReader.IncludeGenerated };
            var memberResults = reader.GetFileDependencyCycleCandidates(
                limit,
                out var memberCandidateRows,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                reverse,
                cancellationToken,
                options.DependencySymbols,
                options.DependencySymbolFamilies,
                options.DependencySuppressNoise);
            TagFileDependencyResults(memberResults, normalizedDbPath);
            if (options.DependencySuppressNoise)
            {
                if (AddBoundedWorkspaceCycleCandidates(
                        memberResults,
                        retainedResults!,
                        suppressedResults!,
                        limit))
                {
                    candidateRowCount = retainedResults!.Count;
                    return OrderWorkspaceCycleCandidates(retainedResults, limit);
                }
            }
            else
            {
                candidateRowCount += memberCandidateRows;
                results.AddRange(memberResults);
            }
            if (!options.DependencySuppressNoise && results.Count >= limit)
                return results.Take(limit).ToList();
        }

        foreach (var sourceDb in memberDbs)
            foreach (var targetDb in memberDbs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(sourceDb, targetDb, StringComparison.Ordinal))
                    continue;
                var crossDbResults = GetCrossDatabaseFileDependencies(sourceDb, targetDb, options, reverse, limit, cancellationToken);
                if (options.DependencySuppressNoise)
                {
                    if (AddBoundedWorkspaceCycleCandidates(
                            crossDbResults,
                            retainedResults!,
                            suppressedResults!,
                            limit))
                    {
                        candidateRowCount = retainedResults!.Count;
                        return OrderWorkspaceCycleCandidates(retainedResults, limit);
                    }
                }
                else
                {
                    candidateRowCount += crossDbResults.Count;
                    results.AddRange(crossDbResults);
                }
                if (!options.DependencySuppressNoise && results.Count >= limit)
                    return results.Take(limit).ToList();
            }

        if (!options.DependencySuppressNoise)
            return results.Take(limit).ToList();

        candidateRowCount = retainedResults!.Count;
        return OrderWorkspaceCycleCandidates(retainedResults.Concat(suppressedResults!), limit);
    }

    internal static bool AddBoundedWorkspaceCycleCandidates(
        IEnumerable<FileDependencyResult> candidates,
        List<FileDependencyResult> retainedResults,
        List<FileDependencyResult> suppressedResults,
        int limit)
    {
        foreach (var candidate in candidates)
        {
            if (HasRetainedDependencyEvidence(candidate))
            {
                if (retainedResults.Count < limit)
                    retainedResults.Add(candidate);
            }
            else if (suppressedResults.Count < limit)
            {
                suppressedResults.Add(candidate);
            }

            if (retainedResults.Count >= limit)
                return true;
        }

        return false;
    }

    private static List<FileDependencyResult> OrderWorkspaceCycleCandidates(
        IEnumerable<FileDependencyResult> results,
        int limit)
        => results
            .OrderByDescending(HasRetainedDependencyEvidence)
            .ThenByDescending(result => result.RankingScore)
            .ThenByDescending(result => result.ReferenceCount)
            .ThenBy(result => result.SourceDb, StringComparer.Ordinal)
            .ThenBy(result => result.SourcePath, StringComparer.Ordinal)
            .ThenBy(result => result.TargetDb, StringComparer.Ordinal)
            .ThenBy(result => result.TargetPath, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

    private static bool HasRetainedDependencyEvidence(FileDependencyResult result)
        => result.Evidence is not { Count: > 0 }
           || result.Evidence.Any(static evidence => evidence.Origin != "markdown_heading_name_match");

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

    private static List<FileDependencyResult> GetCrossDatabaseFileDependencies(
        string sourceDbPath,
        string targetDbPath,
        QueryCommandOptions options,
        bool reverse,
        int limit,
        CancellationToken cancellationToken)
    {
        // Declare the target first so reverse-order disposal closes the source ATTACH handle
        // before the target context attempts to delete its private snapshot on Windows.
        // target を先に宣言し、Windows で private snapshot を削除する前に source の
        // ATTACH handle が reverse-order disposal で閉じられるようにする。
        cancellationToken.ThrowIfCancellationRequested();
        using var targetDb = new DbContext(DbOpenIntent.QueryOnly, targetDbPath, cancellationToken);
        using var sourceDb = new DbContext(DbOpenIntent.QueryOnly, sourceDbPath, cancellationToken);
        var connection = sourceDb.Connection;
        var sourceReader = new DbReader(sourceDb);
        var targetReader = new DbReader(targetDb);
        var sourceProjectRoot = sourceReader.GetIndexedProjectRoot();
        var targetProjectRoot = targetReader.GetIndexedProjectRoot();
        var hasTargetQualifier = sourceDb.SchemaCache.GetColumns("symbol_references").Contains("target_qualifier");
        connection.CreateFunction(
            "markdown_cross_database_path_matches",
            (string? sourcePath, string? targetPath, string? targetQualifier)
                => MarkdownCrossDatabasePathMatches(
                    sourceProjectRoot,
                    sourcePath,
                    targetProjectRoot,
                    targetPath,
                    targetQualifier)
                    ? 1
                    : 0);
        // Keep the target context alive for the whole attached query. WAL-backed targets may
        // resolve to a private artifact-preserving snapshot whose cleanup is owned by that
        // context; attaching the original path would let SQLite create/touch source sidecars.
        // attached query 全体で target context を保持する。WAL-backed target は context が
        // cleanup を所有する private snapshot になり得るため、original path を ATTACH して
        // source sidecar を作成・更新させない。
        AttachCrossDatabaseTarget(connection, targetDb.Connection.DataSource);

        using var cmd = connection.CreateCommand();
        var sourcePathExpr = reverse ? "dst.path" : "src.path";
        var targetPathExpr = reverse ? "src.path" : "dst.path";
        var crossReferenceOrderSql = options.DependencySuppressNoise
            ? "edge_totals.retained_reference_count DESC, edge_totals.reference_count DESC, edge_totals.source_path, edge_totals.target_path"
            : "edge_totals.reference_count DESC, edge_totals.source_path, edge_totals.target_path";
        var retainedCrossSymbolFilterSql = options.DependencySuppressNoise
            ? " WHERE origin <> 'markdown_heading_name_match'"
            : string.Empty;
        var crossMarkdownExplicitLinkSql = hasTargetQualifier
            ? "(src.lang = 'markdown' AND r.reference_kind = 'reference' AND r.target_qualifier IS NOT NULL AND markdown_cross_database_path_matches(src.path, dst.path, r.target_qualifier) = 1)"
            : "(0 = 1)";
        var crossMarkdownNoiseEvidenceSql =
            $"({crossMarkdownExplicitLinkSql} OR (src.lang = 'markdown' AND s.kind = 'heading'))";
        cmd.CommandText = $@"
            WITH edges AS (
            SELECT {sourcePathExpr} AS source_path,
                   {targetPathExpr} AS target_path,
                   r.symbol_name,
                   src.lang AS source_lang,
                   CASE
                       WHEN {crossMarkdownExplicitLinkSql}
                           THEN 'markdown_explicit_link'
                       WHEN src.lang = 'markdown' AND s.kind = 'heading'
                           THEN 'markdown_heading_name_match'
                       ELSE 'cross_database_symbol_name_match'
                   END AS origin,
                   r.reference_kind AS raw_reference_kind,
                   CASE WHEN s.kind = 'heading' THEN 'heading' ELSE 'symbol' END AS target_kind
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
        var crossDatabaseSql = cmd.CommandText;
        DbReader.AppendDependencySymbolFilter(
            cmd,
            ref crossDatabaseSql,
            "r.symbol_name",
            options.DependencySymbols,
            options.DependencySymbolFamilies,
            suppressDependencyNoise: false,
            parameterPrefix: "crossDependencyNames");
        DbReader.AppendDependencySymbolFilter(
            cmd,
            ref crossDatabaseSql,
            "r.symbol_name",
            dependencySymbols: null,
            dependencySymbolFamilies: null,
            suppressDependencyNoise: options.DependencySuppressNoise,
            parameterPrefix: "crossDependencyNoise",
            filterScopeSql: $"NOT {crossMarkdownNoiseEvidenceSql}");
        cmd.CommandText = crossDatabaseSql;
        cmd.CommandText += @"
            ),
            edge_totals AS (
                SELECT source_path,
                       target_path,
                       COUNT(*) AS reference_count,
                       SUM(CASE WHEN origin = 'markdown_heading_name_match' THEN 0 ELSE 1 END) AS retained_reference_count
                FROM edges
                GROUP BY source_path, target_path
            ),
            edge_evidence_rows AS (
                SELECT source_path,
                       target_path,
                       source_lang,
                       origin,
                       raw_reference_kind,
                       target_kind,
                       COUNT(*) AS evidence_reference_count
                FROM edges
                GROUP BY source_path,
                         target_path,
                         source_lang,
                         origin,
                         raw_reference_kind,
                         target_kind
            ),
            ordered_edge_evidence AS (
                SELECT source_path,
                       target_path,
                       source_lang || char(31) ||
                       origin || char(31) ||
                       raw_reference_kind || char(31) ||
                       target_kind || char(31) ||
                       evidence_reference_count AS evidence_item
                FROM edge_evidence_rows
                ORDER BY source_path, target_path, source_lang, origin, raw_reference_kind, target_kind
            ),
            edge_evidence_payloads AS (
                SELECT source_path,
                       target_path,
                       GROUP_CONCAT(evidence_item, char(30)) AS evidence_payload
                FROM ordered_edge_evidence
                GROUP BY source_path, target_path
            ),
            distinct_edge_symbols AS (
                SELECT DISTINCT source_path, target_path, symbol_name
                FROM edges" + retainedCrossSymbolFilterSql + @"
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
                   COALESCE(GROUP_CONCAT(CASE WHEN ranked_edge_symbols.symbol_rank <= @symbolSampleLimit THEN ranked_edge_symbols.symbol_name END, char(31)), '') AS symbols,
                   COALESCE(edge_evidence_payloads.evidence_payload, '') AS evidence_payload
            FROM edge_totals
            LEFT JOIN ranked_edge_symbols
              ON ranked_edge_symbols.source_path = edge_totals.source_path
             AND ranked_edge_symbols.target_path = edge_totals.target_path
            LEFT JOIN edge_evidence_payloads
              ON edge_evidence_payloads.source_path = edge_totals.source_path
             AND edge_evidence_payloads.target_path = edge_totals.target_path
            GROUP BY edge_totals.source_path,
                     edge_totals.target_path,
                     edge_totals.reference_count,
                     edge_evidence_payloads.evidence_payload
            ORDER BY " + crossReferenceOrderSql + @"
            LIMIT @limit";
        SqliteCommandPolicy.Add(cmd, "@limit", DependencyNoiseProfile.GetRankingCandidateLimit(limit));
        SqliteCommandPolicy.Add(cmd, "@symbolSampleLimit", DbReader.DependencySymbolSampleLimit);

        var results = new List<FileDependencyResult>();
        using var cancellationRegistration = cancellationToken.Register(static state => ((SqliteCommand)state!).Cancel(), cmd);
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var symbolSamples = reader.IsDBNull(3)
                    ? []
                    : DbReader.ParseDependencySymbols(reader.GetString(3));
                results.Add(new FileDependencyResult
                {
                    SourcePath = reader.GetString(0),
                    TargetPath = reader.GetString(1),
                    SourceDb = reverse ? targetDbPath : sourceDbPath,
                    TargetDb = reverse ? sourceDbPath : targetDbPath,
                    ReferenceCount = reader.GetInt32(2),
                    SymbolSamples = symbolSamples,
                    Symbols = string.Join(",", symbolSamples),
                    Evidence = DbReader.ParseDependencyEvidence(reader.GetString(4)),
                });
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        foreach (var result in results)
        {
            var rankingReferenceCount = options.DependencySuppressNoise
                ? (result.Evidence ?? [])
                    .Where(static evidence => evidence.Origin != "markdown_heading_name_match")
                    .Sum(static evidence => evidence.ReferenceCount)
                : result.ReferenceCount;
            result.RankingScore = result.SymbolSamples is { } symbolSamples
                ? DependencyNoiseProfile.ComputeRankingScore(rankingReferenceCount, symbolSamples)
                : DependencyNoiseProfile.ComputeRankingScore(rankingReferenceCount, result.Symbols);
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

    private static bool MarkdownCrossDatabasePathMatches(
        string? sourceProjectRoot,
        string? sourcePath,
        string? targetProjectRoot,
        string? targetPath,
        string? targetQualifier)
    {
        if (string.IsNullOrWhiteSpace(sourceProjectRoot)
            || string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(targetProjectRoot)
            || string.IsNullOrWhiteSpace(targetPath)
            || string.IsNullOrWhiteSpace(targetQualifier))
            return false;

        var qualifier = targetQualifier.Replace('\\', '/').Trim();
        var fragmentIndex = qualifier.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0)
            qualifier = qualifier[..fragmentIndex];
        var queryIndex = qualifier.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
            qualifier = qualifier[..queryIndex];
        if (qualifier.Length == 0
            || qualifier.Contains("://", StringComparison.Ordinal)
            || qualifier.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var sourceDirectory = Path.GetDirectoryName(sourcePath.Replace('/', Path.DirectorySeparatorChar))
                                  ?? string.Empty;
            var resolvedSourceTarget = qualifier.StartsWith("/", StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(sourceProjectRoot, qualifier.TrimStart('/')))
                : Path.GetFullPath(Path.Combine(
                    sourceProjectRoot,
                    sourceDirectory,
                    qualifier.Replace('/', Path.DirectorySeparatorChar)));
            var resolvedTarget = Path.GetFullPath(Path.Combine(
                targetProjectRoot,
                targetPath.Replace('/', Path.DirectorySeparatorChar)));
            return PathCasing.PathsEqual(resolvedSourceTarget, resolvedTarget);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void AttachCrossDatabaseTarget(SqliteConnection connection, string targetDbPath)
    {
        try
        {
            // The owning QueryOnly connection is opened with Mode=ReadOnly and
            // PRAGMA query_only=ON, so attached databases inherit a non-mutating session.
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
