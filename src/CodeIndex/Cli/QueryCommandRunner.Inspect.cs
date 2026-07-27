using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunInspect(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("inspect", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false);
        if (TryWriteUnsupportedOptionError("inspect", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("inspect"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteNonPositiveCoordinateJsonError(options, jsonOptions, "--line", "--start", "--start-line", "--end", "--end-line"))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("inspect", options, InspectOutputFormats, "Use `--format json` or `--format compact` for inspect bundles; count output is not meaningful for one inspect bundle."))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "inspect", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        var pathLineInspectMode = IsInspectPathLineMode(options);
        if (pathLineInspectMode && options.GroupPartials)
        {
            WriteUsageError(
                "--group-partials is only supported for symbol-mode inspect queries.",
                GetUsageLineOrThrow("inspect"),
                "Remove --path/--line and inspect a symbol name, or remove --group-partials to keep coordinate-based physical navigation.");
            return CommandExitCodes.UsageError;
        }
        if (!pathLineInspectMode && TryWriteBlankQueryError(options, "inspect"))
            return CommandExitCodes.UsageError;
        if (!pathLineInspectMode && string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "inspect requires a symbol query argument",
                GetUsageLineOrThrow("inspect"),
                "Add the symbol you want to inspect, for example: `cdidx inspect QueryCommandRunner`, or pass `--path <file> --line <line>` for a source excerpt.");
            return CommandExitCodes.UsageError;
        }
        if (options.Query != null && IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "inspect requires a symbol query argument",
                GetUsageLineOrThrow("inspect"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("inspect", options))
            return CommandExitCodes.UsageError;
        if (!TryReadInspectCursor(cmdArgs, out var cursor, out var cursorError))
        {
            WriteUsageError(
                cursorError!,
                GetUsageLineOrThrow("inspect"),
                "Pass an unchanged inspect query with a next_cursor returned by one graph section.");
            return CommandExitCodes.UsageError;
        }
        InspectGraphCursor? graphCursor = null;
        if (cursor != null && !InspectGraphCursorCodec.TryParse(cursor, out graphCursor))
        {
            WriteUsageError(
                "invalid inspect graph cursor",
                GetUsageLineOrThrow("inspect"),
                "Use a next_cursor returned by the same inspect query and graph section.");
            return CommandExitCodes.UsageError;
        }
        if (options.StartLine.HasValue && options.EndLine.HasValue && options.EndLine.Value < options.StartLine.Value)
        {
            WriteValidationError(
                $"--start-line ({options.StartLine.Value}) must be less than or equal to --end-line ({options.EndLine.Value}).",
                "Use `--start-line` less than or equal to `--end-line`, or omit `--end-line` to read one line.");
            return CommandExitCodes.UsageError;
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var compactLimit = GetCompactSectionLimit(options);
            var inspectLimit = options.Compact ? GetCompactSourceLimit(compactLimit) : options.Limit;
            var inspectPath = pathLineInspectMode ? GetSingleSpecificPathPattern(options.PathPatterns) : null;
            var inspectLine = pathLineInspectMode ? options.StartLine!.Value : options.StartLine ?? 1;
            FileResult? indexedFile = null;
            if (inspectPath != null)
            {
                indexedFile = reader.GetFileByPath(inspectPath);
            }
            else if (options.Query != null)
            {
                var resolvedQueryPath = DbPathResolver.ResolveQueryFilePath(options.DbPath, options.Query, options.DbPathExplicit);
                indexedFile = reader.GetFileByPath(resolvedQueryPath);
                if (indexedFile != null)
                    inspectPath = resolvedQueryPath;
            }

            var fileInspectMode = inspectPath != null;
            var coordinateExplicit = options.StartLine.HasValue || options.EndLine.HasValue;
            if (fileInspectMode && options.GroupPartials)
            {
                WriteUsageError(
                    "--group-partials is only supported for symbol-mode inspect queries.",
                    GetUsageLineOrThrow("inspect"),
                    "Inspect a symbol name instead of a file path, or remove --group-partials to keep physical file navigation.");
                return CommandExitCodes.UsageError;
            }
            if (fileInspectMode && indexedFile == null)
            {
                return CommandErrorWriter.WriteJsonOrHuman(
                    options.Json,
                    jsonOptions,
                    $"indexed file not found: {inspectPath}",
                    CommandExitCodes.NotFound,
                    "Use `cdidx files --json` to confirm the indexed path, then retry with that exact path.",
                    errorCode: CommandErrorCodes.FileNotFound,
                    category: "not_found");
            }

            if (fileInspectMode && coordinateExplicit && (inspectLine > indexedFile!.Lines || (options.EndLine.HasValue && options.EndLine.Value > indexedFile.Lines)))
            {
                var requestedEndLine = options.EndLine ?? inspectLine;
                return CommandErrorWriter.WriteJsonOrHuman(
                    options.Json,
                    jsonOptions,
                    $"requested inspect range {inspectLine}-{requestedEndLine} is outside {inspectPath} (1-{indexedFile.Lines}).",
                    CommandExitCodes.InvalidArgument,
                    $"Use a line range between 1 and {indexedFile.Lines}.",
                    errorCode: CommandErrorCodes.LineOutOfRange,
                    category: "range");
            }

            var inspectQuery = fileInspectMode
                ? $"{inspectPath}:{inspectLine}"
                : options.Query!;
            var queryFingerprint = BuildInspectGraphQueryFingerprint(
                inspectQuery,
                options,
                exact,
                fileInspectMode,
                inspectLimit);
            var generation = InspectGraphCursorCodec.BuildGenerationFingerprint(reader);
            if (graphCursor != null
                && (!string.Equals(graphCursor.QueryFingerprint, queryFingerprint, StringComparison.Ordinal)
                    || !string.Equals(graphCursor.GenerationFingerprint, generation.Fingerprint, StringComparison.Ordinal)))
            {
                WriteUsageError(
                    "inspect graph cursor does not match this query or index generation",
                    GetUsageLineOrThrow("inspect"),
                    "Rerun the original inspect query without --cursor, then use the newly returned section cursor.");
                return CommandExitCodes.UsageError;
            }
            var graphPage = graphCursor == null
                ? null
                : new SymbolGraphPageRequest(
                    graphCursor.Section,
                    graphCursor.Offset,
                    graphCursor.CandidateSelector);
            var analysis = fileInspectMode
                ? reader.AnalyzeFileLine(
                    inspectPath!,
                    inspectLine,
                    inspectLimit,
                    options.Lang,
                    options.IncludeBody,
                    options.PathPatterns,
                    options.ExcludePaths,
                    options.ExcludeTests,
                    options.MaxLineWidth,
                    options.BodyStartLine,
                    options.BodyLines,
                    kind: options.Kind,
                    graphPage: graphPage)
                : reader.AnalyzeSymbol(
                    inspectQuery,
                    inspectLimit,
                    options.Lang,
                    options.IncludeBody,
                    options.PathPatterns,
                    options.ExcludePaths,
                    options.ExcludeTests,
                    exact,
                    options.MaxLineWidth,
                    options.BodyStartLine,
                    options.BodyLines,
                    kind: options.Kind,
                    groupPartials: options.GroupPartials,
                    graphPage: graphPage);
            if (graphCursor?.CandidateSelector != null
                && !(analysis.CandidateBundles?.Any(bundle =>
                    string.Equals(bundle.Selector.Selector, graphCursor.CandidateSelector, StringComparison.Ordinal)) ?? false))
            {
                WriteUsageError(
                    "inspect graph cursor candidate is no longer available",
                    GetUsageLineOrThrow("inspect"),
                    "Rerun the inspect query without --cursor and select a cursor from the current candidate bundle.");
                return CommandExitCodes.UsageError;
            }
            var sourceExcerpt = BuildInspectSourceExcerpt(reader, options, analysis, inspectPath);
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
                var compactTruncation = options.Compact ? ApplySymbolAnalysisCompactCaps(analysis, compactLimit) : null;
                SynchronizeInspectGraphSectionCounts(analysis);
                ApplyInspectGraphContinuationCursors(
                    analysis,
                    queryFingerprint,
                    generation.Fingerprint);
                ApplyBodyRecoveryCommands(analysis, options.DbPath);
                var payload = JsonSerializer.SerializeToNode(analysis, CliJsonSerializerContextFactory.Create(jsonOptions).SymbolAnalysisResult)!.AsObject();
                AddSqlGraphContractJsonFields(payload, sqlGraphSignal);
                if (compactTruncation != null)
                    AddCompactJsonFields(payload, compactLimit, compactTruncation);
                if (sourceExcerpt != null)
                {
                    ExcerptRecoveryCommandFormatter.ApplyDbPath(sourceExcerpt, options.DbPath);
                    sourceExcerpt.SemanticTokens = BuildExcerptSemanticTokens(sourceExcerpt);
                    payload["source_excerpt"] = JsonSerializer.SerializeToNode(sourceExcerpt, CliJsonSerializerContextFactory.Create(jsonOptions).FileExcerptResult);
                }
                ApplyInspectFieldSelection(payload, options, jsonOptions);
                if (options.GroupPartials)
                    AddInspectLogicalPartialJsonFields(payload, analysis);
                ApplyInspectDefinitionContentPolicy(payload, options);
                AddInspectBodyModeJsonFields(payload, options, analysis);
                var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                    payload,
                    options,
                    jsonOptions,
                    "inspect",
                    "inspect",
                    "Use --compact, --fields <csv>, --body-only with --body-lines <n>, or increase --max-json-bytes.");
                if (writeExitCode != CommandExitCodes.Success)
                    return writeExitCode;
            }
            else
            {
                SynchronizeInspectGraphSectionCounts(analysis);
                ApplyInspectGraphContinuationCursors(
                    analysis,
                    queryFingerprint,
                    generation.Fingerprint);
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
                if (analysis.GraphScope != null)
                    Console.WriteLine($"Graph Scope          : {analysis.GraphScope}");
                if (analysis.SelectionRequired)
                    Console.WriteLine("Selection Required   : true — use a candidate selector/path before trusting graph sections.");
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
                WriteInspectBodyModeHint(analysis, options);
                if (options.GroupPartials)
                {
                    var physicalDefinitionCount = analysis.Definitions.Sum(definition => definition.DefinitionSites ?? 1);
                    Console.WriteLine($"Definitions Grouping : {analysis.Definitions.Count} output logical families from {physicalDefinitionCount} output physical declaration sites");
                }
                if (sourceExcerpt != null)
                {
                    Console.WriteLine($"Source Excerpt      : {sourceExcerpt.Path}:{sourceExcerpt.StartLine}-{sourceExcerpt.EndLine}");
                    WriteNumberedExcerpt(sourceExcerpt.StartLine, sourceExcerpt.Content);
                }
                WriteRepoMapSection("Definitions", analysis.Definitions.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("Nearby symbols", analysis.NearbySymbols.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("References", analysis.References.Select(item => $"{item.Path}:{item.Line}:{item.Column}  {item.Context}"));
                WriteInspectGraphSectionStatus("References", analysis.GraphSections.References);
                WriteRepoMapSection("Callers", analysis.Callers.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                WriteInspectGraphSectionStatus("Callers", analysis.GraphSections.Callers);
                WriteRepoMapSection("Callees", analysis.Callees.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                WriteInspectGraphSectionStatus("Callees", analysis.GraphSections.Callees);
                if (analysis.CandidateBundles is { Count: > 1 })
                {
                    foreach (var bundle in analysis.CandidateBundles)
                    {
                        var title = $"Candidate {bundle.Selector.Selector} ({bundle.Selector.QualifiedName})";
                        WriteRepoMapSection(title, [$"{bundle.Definition.Kind,-10} {bundle.Definition.Path}:{bundle.Definition.StartLine}-{bundle.Definition.EndLine}"]);
                        WriteRepoMapSection($"{title} references", bundle.References.Select(item => $"{item.Path}:{item.Line}:{item.Column}  {item.Context}"));
                        WriteInspectGraphSectionStatus($"{title} references", bundle.GraphSections.References);
                        WriteRepoMapSection($"{title} callers", bundle.Callers.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                        WriteInspectGraphSectionStatus($"{title} callers", bundle.GraphSections.Callers);
                        WriteRepoMapSection($"{title} callees", bundle.Callees.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                        WriteInspectGraphSectionStatus($"{title} callees", bundle.GraphSections.Callees);
                    }
                }
            }

            return IsEmptySymbolAnalysis(analysis) && sourceExcerpt == null ? ZeroResultExitCode(options) : CommandExitCodes.Success;
        });
    }

    private static bool TryReadInspectCursor(
        string[] args,
        out string? cursor,
        out string? error)
    {
        cursor = null;
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string? value = null;
            if (arg.StartsWith("--cursor=", StringComparison.Ordinal))
            {
                value = arg["--cursor=".Length..];
            }
            else if (string.Equals(arg, "--cursor", StringComparison.Ordinal))
            {
                if (++index >= args.Length)
                {
                    error = "--cursor requires a value";
                    return false;
                }
                value = args[index];
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                error = "--cursor requires a non-empty value";
                return false;
            }
            if (cursor != null)
            {
                error = "--cursor may be specified only once";
                return false;
            }
            cursor = value;
        }
        return true;
    }

    private static string BuildInspectGraphQueryFingerprint(
        string inspectQuery,
        QueryCommandOptions options,
        bool exact,
        bool fileInspectMode,
        int pageLimit)
    {
        var components = new List<string?>
        {
            "inspect",
            fileInspectMode ? "location" : "symbol",
            inspectQuery,
            options.Lang,
            options.Kind,
            exact ? "exact" : "substring",
            $"page-limit:{pageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            options.ExcludeTests ? "exclude-tests" : "include-tests",
            options.GroupPartials ? "group-partials" : "physical-definitions",
        };
        components.AddRange(options.PathPatterns
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "path:" + path));
        components.AddRange(options.ExcludePaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "exclude:" + path));
        return InspectGraphCursorCodec.BuildQueryFingerprint(components);
    }

    internal static void SynchronizeInspectGraphSectionCounts(SymbolAnalysisResult analysis)
    {
        SynchronizeInspectGraphSections(
            analysis.GraphSections,
            analysis.References.Count,
            analysis.Callers.Count,
            analysis.Callees.Count);
        if (analysis.CandidateBundles == null)
            return;
        foreach (var bundle in analysis.CandidateBundles)
        {
            SynchronizeInspectGraphSections(
                bundle.GraphSections,
                bundle.References.Count,
                bundle.Callers.Count,
                bundle.Callees.Count);
        }
    }

    private static void SynchronizeInspectGraphSections(
        SymbolGraphSections sections,
        int referenceCount,
        int callerCount,
        int calleeCount)
    {
        SynchronizeInspectGraphSection(sections.References, referenceCount);
        SynchronizeInspectGraphSection(sections.Callers, callerCount);
        SynchronizeInspectGraphSection(sections.Callees, calleeCount);
    }

    private static void SynchronizeInspectGraphSection(SymbolGraphSection section, int returned)
    {
        section.Returned = returned;
        section.Truncated = section.Offset + returned < section.Total;
    }

    internal static void ApplyInspectGraphContinuationCursors(
        SymbolAnalysisResult analysis,
        string queryFingerprint,
        string generationFingerprint)
    {
        var primarySelector = analysis.GraphScope == "query_fallback"
            ? null
            : analysis.CandidateBundles?.FirstOrDefault()?.Selector.Selector;
        ApplyInspectGraphContinuationCursors(
            analysis.GraphSections,
            primarySelector,
            queryFingerprint,
            generationFingerprint);
        if (analysis.CandidateBundles == null)
            return;
        foreach (var bundle in analysis.CandidateBundles)
        {
            ApplyInspectGraphContinuationCursors(
                bundle.GraphSections,
                bundle.Selector.Selector,
                queryFingerprint,
                generationFingerprint);
        }
    }

    private static void ApplyInspectGraphContinuationCursors(
        SymbolGraphSections sections,
        string? candidateSelector,
        string queryFingerprint,
        string generationFingerprint)
    {
        ApplyInspectGraphContinuationCursor(
            sections.References,
            "references",
            candidateSelector,
            queryFingerprint,
            generationFingerprint);
        ApplyInspectGraphContinuationCursor(
            sections.Callers,
            "callers",
            candidateSelector,
            queryFingerprint,
            generationFingerprint);
        ApplyInspectGraphContinuationCursor(
            sections.Callees,
            "callees",
            candidateSelector,
            queryFingerprint,
            generationFingerprint);
    }

    private static void ApplyInspectGraphContinuationCursor(
        SymbolGraphSection section,
        string sectionName,
        string? candidateSelector,
        string queryFingerprint,
        string generationFingerprint)
    {
        section.NextCursor = section.Truncated && section.Returned > 0
            ? InspectGraphCursorCodec.Format(
                sectionName,
                checked(section.Offset + section.Returned),
                candidateSelector,
                queryFingerprint,
                generationFingerprint)
            : null;
    }

    private static void WriteInspectGraphSectionStatus(string label, SymbolGraphSection section)
    {
        Console.WriteLine(
            $"{label} completeness: returned {section.Returned} of {section.Total} (offset {section.Offset}, truncated: {section.Truncated.ToString().ToLowerInvariant()})");
        if (section.NextCursor != null)
            Console.WriteLine($"{label} next cursor: {section.NextCursor}");
    }

    private static void AddInspectLogicalPartialJsonFields(JsonObject payload, SymbolAnalysisResult analysis)
    {
        var physicalDefinitionCount = analysis.Definitions.Sum(definition => definition.DefinitionSites ?? 1);
        payload["group_partials"] = true;
        payload["definition_result_scope"] = "logical_partial_families";
        payload["logical_definition_output_count"] = analysis.Definitions.Count;
        payload["physical_definition_output_count"] = physicalDefinitionCount;
        payload["definitions_collapsed"] = physicalDefinitionCount != analysis.Definitions.Count;
    }

    private static void ApplyInspectFieldSelection(JsonObject payload, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.InspectFields == null)
            return;

        payload["selected_fields"] = JsonSerializer.SerializeToNode(options.InspectFields, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "api_version",
            "query",
            "selected_fields",
        };

        foreach (var field in options.InspectFields)
            AddInspectFieldProperties(keep, field);

        if (options.Compact)
        {
            keep.Add("compact");
            keep.Add("compact_limit");
            keep.Add("truncation");
            FilterInspectCompactTruncationSections(payload, options.InspectFields);
        }

        foreach (var propertyName in payload.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            payload.Remove(propertyName);
    }

    private static void AddInspectFieldProperties(HashSet<string> keep, string field)
    {
        if (field is "graph" or "definitions" or "references" or "callers" or "callees" or "candidates")
        {
            keep.Add("candidate_count");
            keep.Add("graph_scope");
            keep.Add("selection_required");
        }
        if (field is "graph" or "references" or "callers" or "callees")
            keep.Add("graph_sections");

        switch (field)
        {
            case "file":
                keep.Add("file");
                break;
            case "workspace":
                keep.Add("workspace_indexed_at");
                keep.Add("workspace_latest_modified");
                keep.Add("project_root");
                keep.Add("git_head");
                keep.Add("git_is_dirty");
                keep.Add("indexed_head_commit");
                keep.Add("worktree_head_changed");
                break;
            case "graph":
                keep.Add("graph_language");
                keep.Add("graph_language_source");
                keep.Add("graph_language_confidence");
                keep.Add("graph_language_candidates");
                keep.Add("graph_language_conflict");
                keep.Add("graph_supported");
                keep.Add("graph_support_reason");
                keep.Add("graph_degraded");
                keep.Add("unsupported_symbol_kind");
                keep.Add("graph_table_available");
                keep.Add("sql_graph_contract_ready");
                keep.Add("sql_graph_contract_degraded_reason");
                keep.Add("exact_zero_hint");
                keep.Add("exact_index_available");
                keep.Add("degraded");
                keep.Add("degraded_reason");
                break;
            case "definitions":
                keep.Add("definitions");
                break;
            case "source_excerpt":
                keep.Add("source_excerpt");
                break;
            case "nearby_symbols":
                keep.Add("nearby_symbols");
                break;
            case "references":
                keep.Add("references");
                break;
            case "callers":
                keep.Add("callers");
                break;
            case "callees":
                keep.Add("callees");
                break;
            case "candidates":
                keep.Add("candidate_bundles");
                break;
        }
    }

    private static void FilterInspectCompactTruncationSections(JsonObject payload, IReadOnlyCollection<string> inspectFields)
    {
        if (!payload.TryGetPropertyValue("truncation", out var truncationNode)
            || truncationNode is not JsonObject truncation
            || !truncation.TryGetPropertyValue("sections", out var sectionsNode)
            || sectionsNode is not JsonObject sections)
        {
            return;
        }

        var keepSections = inspectFields
            .Where(IsInspectListField)
            .ToHashSet(StringComparer.Ordinal);
        var keepCandidateSections = inspectFields.Contains("candidates", StringComparer.Ordinal);
        foreach (var sectionName in sections.Select(section => section.Key)
                     .Where(section => !keepSections.Contains(section)
                                       && !(keepCandidateSections && section.StartsWith("candidate_bundles[", StringComparison.Ordinal)))
                     .ToList())
            sections.Remove(sectionName);
    }

    private static bool IsInspectListField(string field)
        => field is "definitions" or "nearby_symbols" or "references" or "callers" or "callees";

    private static void AddInspectBodyModeJsonFields(JsonObject payload, QueryCommandOptions options, SymbolAnalysisResult analysis)
    {
        var bodyContentPresent = options.IncludeBody && analysis.Definitions.Any(definition => definition.BodyContent != null);
        var bodyContentTruncated = options.IncludeBody && analysis.Definitions.Any(definition => definition.BodyContentTruncated);
        var nextStartLine = options.IncludeBody
            ? analysis.Definitions
                .Where(definition => definition.BodyContentNextStartLine.HasValue)
                .Select(definition => definition.BodyContentNextStartLine!.Value)
                .DefaultIfEmpty()
                .Min()
            : 0;

        var bodyMode = new JsonObject
        {
            ["include_body"] = options.IncludeBody,
            ["definitions_only"] = IsInspectDefinitionsOnlyMode(options),
            ["body_content_present"] = bodyContentPresent,
            ["body_content_truncated"] = bodyContentTruncated,
            ["default_body_lines"] = DbReader.DefinitionBodyMaxLines,
            ["max_body_lines"] = DbReader.DefinitionBodyMaxRequestedLines,
            ["hint"] = BuildInspectBodyModeHint(options, bodyContentPresent, bodyContentTruncated),
        };
        if (options.BodyStartLine.HasValue)
            bodyMode["body_start_line"] = options.BodyStartLine.Value;
        if (options.BodyLines.HasValue)
            bodyMode["body_lines"] = options.BodyLines.Value;
        else if (options.IncludeBody)
            bodyMode["body_lines"] = DbReader.DefinitionBodyMaxLines;
        if (nextStartLine > 0)
            bodyMode["next_body_start_line"] = nextStartLine;

        payload["body_mode"] = bodyMode;
    }

    private static void WriteInspectBodyModeHint(SymbolAnalysisResult analysis, QueryCommandOptions options)
    {
        if (analysis.Definitions.Count == 0)
            return;

        var bodyContentPresent = analysis.Definitions.Any(definition => definition.BodyContent != null);
        var bodyContentTruncated = analysis.Definitions.Any(definition => definition.BodyContentTruncated);
        Console.WriteLine($"Body Hint           : {BuildInspectBodyModeHint(options, bodyContentPresent, bodyContentTruncated)}");
    }

    private static bool IsInspectDefinitionsOnlyMode(QueryCommandOptions options)
        => options.IncludeBody
            && options.InspectFields is { Count: 1 } fields
            && string.Equals(fields[0], "definitions", StringComparison.Ordinal);

    private static string BuildInspectBodyModeHint(QueryCommandOptions options, bool bodyContentPresent, bool bodyContentTruncated)
    {
        if (!options.IncludeBody)
            return "Add `--body` for definition body snippets in JSON, or use `--body-only` for body-focused JSON. Page long bodies with `--body-start <line> --body-lines <n>`.";

        if (!options.Json)
            return "Body content was requested, but human inspect output stays summary-only; use `--json --fields body` or `--body-only` to show `body_content`.";

        if (bodyContentTruncated)
            return "Use each definition's `body_content_next_start_line` with `--body-start <line>` and optionally `--body-lines <n>` to fetch the next body slice.";

        if (bodyContentPresent)
            return "Body content is present under each definition's `body_content` field.";

        return "No definition body content is available for the matched definitions.";
    }

    private static bool IsInspectPathLineMode(QueryCommandOptions options)
        => options.Query == null
            && options.StartLine.HasValue
            && GetSingleSpecificPathPattern(options.PathPatterns) != null;

    private static bool IsInspectSourceExcerptRequested(QueryCommandOptions options)
        => options.StartLine.HasValue
            || options.EndLine.HasValue
            || options.ContextBefore > 0
            || options.ContextAfter > 0;

    private static FileExcerptResult? BuildInspectSourceExcerpt(
        DbReader reader,
        QueryCommandOptions options,
        SymbolAnalysisResult analysis,
        string? inspectPath)
    {
        if (!IsInspectSourceExcerptRequested(options))
            return null;

        var definition = analysis.Definitions.FirstOrDefault();
        var path = inspectPath
            ?? GetSingleSpecificPathPattern(options.PathPatterns)
            ?? definition?.Path
            ?? analysis.File?.Path;
        if (path == null)
            return null;

        var startLine = options.StartLine ?? definition?.StartLine ?? 1;
        var endLine = options.EndLine ?? options.StartLine ?? definition?.EndLine ?? startLine;
        return reader.GetExcerpt(
            path,
            startLine,
            endLine,
            options.ContextBefore,
            options.ContextAfter,
            options.MaxLineWidth,
            options.StartLine ?? startLine);
    }

    private static string? GetSingleSpecificPathPattern(IReadOnlyList<string> pathPatterns)
    {
        if (pathPatterns.Count != 1)
            return null;

        var path = pathPatterns[0];
        return ContainsGlobWildcard(path) ? null : path;
    }

    private static bool ContainsGlobWildcard(string value)
        => value.IndexOfAny(['*', '?', '[', ']']) >= 0;
}
