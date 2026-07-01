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
            var inspectQuery = pathLineInspectMode
                ? $"{inspectPath}:{options.StartLine!.Value}"
                : options.Query!;
            var analysis = pathLineInspectMode
                ? reader.AnalyzeFileLine(
                    inspectPath!,
                    options.StartLine!.Value,
                    inspectLimit,
                    options.Lang,
                    options.IncludeBody,
                    options.PathPatterns,
                    options.ExcludePaths,
                    options.ExcludeTests,
                    options.MaxLineWidth,
                    options.BodyStartLine,
                    options.BodyLines,
                    kind: options.Kind)
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
                    kind: options.Kind);
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
                ApplyInspectDefinitionContentPolicy(payload, options);
                AddInspectBodyModeJsonFields(payload, options, analysis);
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
                WriteInspectBodyModeHint(analysis, options);
                if (sourceExcerpt != null)
                {
                    Console.WriteLine($"Source Excerpt      : {sourceExcerpt.Path}:{sourceExcerpt.StartLine}-{sourceExcerpt.EndLine}");
                    WriteNumberedExcerpt(sourceExcerpt.StartLine, sourceExcerpt.Content);
                }
                WriteRepoMapSection("Definitions", analysis.Definitions.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("Nearby symbols", analysis.NearbySymbols.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.StartLine}-{item.EndLine}"));
                WriteRepoMapSection("References", analysis.References.Select(item => $"{item.Path}:{item.Line}:{item.Column}  {item.Context}"));
                WriteRepoMapSection("Callers", analysis.Callers.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
                WriteRepoMapSection("Callees", analysis.Callees.Select(item => $"{item.CallerName ?? "<top-level>"} -> {item.CalleeName}  ({item.ReferenceCount} refs)"));
            }

            return IsEmptySymbolAnalysis(analysis) && sourceExcerpt == null ? ZeroResultExitCode(options) : CommandExitCodes.Success;
        });
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
