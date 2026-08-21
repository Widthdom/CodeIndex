using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static string FormatSearchVisibilitySuffix(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility)
            || string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $" [{visibility}]";
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
            var callsiteExcerpt = BuildCallsiteExcerpt(
                reader,
                result.Path,
                result.Line,
                NormalizeCallsiteColumn(result.Column),
                Math.Max(1, result.SymbolName.Length),
                snippetLines,
                maxLineWidth);
            ApplyCallsiteExcerpt(result, callsiteExcerpt);
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
            var callsiteExcerpt = BuildCallsiteExcerpt(
                reader,
                result.Path,
                result.FirstLine,
                NormalizeCallsiteColumn(result.FirstColumn),
                Math.Max(1, result.CalleeName.Length),
                snippetLines,
                maxLineWidth);
            ApplyCallsiteExcerpt(result, callsiteExcerpt);
        }
    }

    private static void AttachBodyExcerpts(DbReader reader, IEnumerable<CalleeResult> results, int snippetLines, int maxLineWidth)
    {
        foreach (var result in results)
        {
            var excerpt = BuildSymbolBodyExcerpt(reader, result.Path, result.Lang, result.CalleeName, snippetLines, maxLineWidth)
                ?? BuildBodyExcerpt(reader, result.Path, result.FirstLine, snippetLines, maxLineWidth);
            ApplyBodyExcerpt(result, excerpt);
            var callsiteExcerpt = BuildCallsiteExcerpt(
                reader,
                result.Path,
                result.FirstLine,
                NormalizeCallsiteColumn(result.FirstColumn),
                result.FirstLength ?? Math.Max(1, result.CalleeName.Length),
                snippetLines,
                maxLineWidth);
            ApplyCallsiteExcerpt(result, callsiteExcerpt);
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
            var callsiteExcerpt = BuildCallsiteExcerpt(
                reader,
                result.Path,
                result.FirstLine,
                result.FirstColumn,
                Math.Max(1, result.CalleeName.Length),
                snippetLines,
                maxLineWidth);
            ApplyCallsiteExcerpt(result, callsiteExcerpt);
        }
    }

    private static FileExcerptResult? BuildSymbolBodyExcerpt(DbReader reader, string path, string? lang, string symbolName, int snippetLines, int maxLineWidth)
    {
        var symbols = reader.SearchSymbols(
            symbolName,
            limit: 1,
            kind: null,
            lang: lang,
            pathPatterns: [path],
            excludePathPatterns: null,
            excludeTests: false,
            since: null,
            exact: true);
        var symbol = symbols.FirstOrDefault();
        if (symbol == null)
            return null;

        var startLine = symbol.StartLine;
        var naturalEndLine = symbol.BodyEndLine ?? symbol.EndLine;
        var cappedLines = SearchSnippetFormatter.ClampSnippetLines(snippetLines);
        var cappedEndLine = (int)Math.Min(naturalEndLine, (long)startLine + cappedLines - 1);
        var excerpt = reader.GetExcerpt(path, startLine, cappedEndLine, maxLineWidth: maxLineWidth, focusLine: startLine);
        if (excerpt != null && cappedEndLine < naturalEndLine)
        {
            excerpt.RequestedStartLine = startLine;
            excerpt.RequestedEndLine = naturalEndLine;
            excerpt.EffectiveStartLine = excerpt.StartLine;
            excerpt.EffectiveEndLine = excerpt.EndLine;
            var recoveryStartLine = cappedEndLine + 1;
            var recoveryEndLine = (int)Math.Min(naturalEndLine, (long)recoveryStartLine + cappedLines - 1);
            AddExcerptTruncation(excerpt, "body_line_cap", recoveryStartLine, recoveryEndLine);
        }
        return excerpt;
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

    private static FileExcerptResult? BuildCallsiteExcerpt(
        DbReader reader,
        string path,
        int line,
        int? column,
        int length,
        int snippetLines,
        int maxLineWidth)
    {
        if (line <= 0 || column is null)
            return null;

        var cappedLines = SearchSnippetFormatter.ClampSnippetLines(snippetLines);
        var linesBefore = cappedLines / 2;
        var startLine = (int)Math.Max(1L, (long)line - linesBefore);
        var endLine = (int)Math.Min(int.MaxValue, (long)startLine + cappedLines - 1);
        return reader.GetExcerpt(
            path,
            startLine,
            endLine,
            maxLineWidth: maxLineWidth,
            focusLine: line,
            focusColumn: column,
            focusLength: Math.Max(1, length));
    }

    private static int? NormalizeCallsiteColumn(int? column)
        => column is > 0 ? column : null;

    private static void ApplyBodyExcerpt(ReferenceResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyBodyExcerpt(CallerResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyBodyExcerpt(CalleeResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyBodyExcerpt(ImpactResult result, FileExcerptResult? excerpt)
    {
        if (excerpt == null)
            return;
        result.BodyContent = excerpt.Content;
        result.BodyStartLine = excerpt.StartLine;
        result.BodyEndLine = excerpt.EndLine;
        result.BodyContentTruncated = excerpt.ContentTruncated;
        result.BodyRequestedStartLine = excerpt.RequestedStartLine;
        result.BodyRequestedEndLine = excerpt.RequestedEndLine;
        result.BodyEffectiveStartLine = excerpt.EffectiveStartLine;
        result.BodyEffectiveEndLine = excerpt.EffectiveEndLine;
        result.BodyContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.BodyContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyCallsiteExcerpt(ReferenceResult result, FileExcerptResult? excerpt)
    {
        ApplyCallsiteSelection(result, referenceCount: 1);
        result.CallsiteLine = result.Line;
        result.CallsiteColumn = NormalizeCallsiteColumn(result.Column);
        result.CallsiteLength = Math.Max(1, result.SymbolName.Length);
        if (excerpt == null)
        {
            result.CallsiteContentUnavailableReason = GetCallsiteUnavailableReason(result.CallsiteColumn);
            return;
        }
        result.CallsiteContent = excerpt.Content;
        result.CallsiteStartLine = excerpt.StartLine;
        result.CallsiteEndLine = excerpt.EndLine;
        result.CallsiteContentTruncated = excerpt.ContentTruncated;
        result.CallsiteRequestedStartLine = excerpt.RequestedStartLine;
        result.CallsiteRequestedEndLine = excerpt.RequestedEndLine;
        result.CallsiteEffectiveStartLine = excerpt.EffectiveStartLine;
        result.CallsiteEffectiveEndLine = excerpt.EffectiveEndLine;
        result.CallsiteContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.CallsiteContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyCallsiteExcerpt(CallerResult result, FileExcerptResult? excerpt)
    {
        ApplyCallsiteSelection(result, result.ReferenceCount);
        result.CallsiteLine = result.FirstLine;
        result.CallsiteColumn = NormalizeCallsiteColumn(result.FirstColumn);
        result.CallsiteLength = Math.Max(1, result.CalleeName.Length);
        if (excerpt == null)
        {
            result.CallsiteContentUnavailableReason = GetCallsiteUnavailableReason(result.CallsiteColumn);
            return;
        }
        result.CallsiteContent = excerpt.Content;
        result.CallsiteStartLine = excerpt.StartLine;
        result.CallsiteEndLine = excerpt.EndLine;
        result.CallsiteContentTruncated = excerpt.ContentTruncated;
        result.CallsiteRequestedStartLine = excerpt.RequestedStartLine;
        result.CallsiteRequestedEndLine = excerpt.RequestedEndLine;
        result.CallsiteEffectiveStartLine = excerpt.EffectiveStartLine;
        result.CallsiteEffectiveEndLine = excerpt.EffectiveEndLine;
        result.CallsiteContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.CallsiteContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyCallsiteExcerpt(CalleeResult result, FileExcerptResult? excerpt)
    {
        ApplyCallsiteSelection(result, result.ReferenceCount);
        result.CallsiteLine = result.FirstLine;
        result.CallsiteColumn = NormalizeCallsiteColumn(result.FirstColumn);
        result.CallsiteLength = result.FirstLength ?? Math.Max(1, result.CalleeName.Length);
        if (excerpt == null)
        {
            result.CallsiteContentUnavailableReason = GetCallsiteUnavailableReason(result.CallsiteColumn);
            return;
        }
        result.CallsiteContent = excerpt.Content;
        result.CallsiteStartLine = excerpt.StartLine;
        result.CallsiteEndLine = excerpt.EndLine;
        result.CallsiteContentTruncated = excerpt.ContentTruncated;
        result.CallsiteRequestedStartLine = excerpt.RequestedStartLine;
        result.CallsiteRequestedEndLine = excerpt.RequestedEndLine;
        result.CallsiteEffectiveStartLine = excerpt.EffectiveStartLine;
        result.CallsiteEffectiveEndLine = excerpt.EffectiveEndLine;
        result.CallsiteContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.CallsiteContentRecovery = excerpt.ContentRecovery;
    }

    private static void ApplyCallsiteExcerpt(ImpactResult result, FileExcerptResult? excerpt)
    {
        ApplyCallsiteSelection(result, result.ReferenceCount);
        result.CallsiteLine = result.FirstLine;
        result.CallsiteColumn = result.FirstColumn;
        result.CallsiteLength = Math.Max(1, result.CalleeName.Length);
        if (excerpt == null)
        {
            result.CallsiteContentUnavailableReason = GetCallsiteUnavailableReason(result.CallsiteColumn);
            return;
        }
        result.CallsiteContent = excerpt.Content;
        result.CallsiteStartLine = excerpt.StartLine;
        result.CallsiteEndLine = excerpt.EndLine;
        result.CallsiteContentTruncated = excerpt.ContentTruncated;
        result.CallsiteRequestedStartLine = excerpt.RequestedStartLine;
        result.CallsiteRequestedEndLine = excerpt.RequestedEndLine;
        result.CallsiteEffectiveStartLine = excerpt.EffectiveStartLine;
        result.CallsiteEffectiveEndLine = excerpt.EffectiveEndLine;
        result.CallsiteContentTruncationReasons = CopyTruncationReasons(excerpt);
        result.CallsiteContentRecovery = excerpt.ContentRecovery;
    }

    private static string GetCallsiteUnavailableReason(int? column)
        => column is null ? "callsite_column_unavailable" : "callsite_excerpt_unavailable";

    private static void ApplyCallsiteSelection(ReferenceResult result, int referenceCount)
    {
        var boundedReferenceCount = Math.Max(1, referenceCount);
        result.CallsiteSelection = "first_reference";
        result.CallsiteReferenceCount = boundedReferenceCount;
        result.CallsiteOmittedReferenceCount = Math.Max(0, boundedReferenceCount - 1);
    }

    private static void ApplyCallsiteSelection(CallerResult result, int referenceCount)
    {
        var boundedReferenceCount = Math.Max(1, referenceCount);
        result.CallsiteSelection = "first_reference";
        result.CallsiteReferenceCount = boundedReferenceCount;
        result.CallsiteOmittedReferenceCount = Math.Max(0, boundedReferenceCount - 1);
    }

    private static void ApplyCallsiteSelection(CalleeResult result, int referenceCount)
    {
        var boundedReferenceCount = Math.Max(1, referenceCount);
        result.CallsiteSelection = "first_reference";
        result.CallsiteReferenceCount = boundedReferenceCount;
        result.CallsiteOmittedReferenceCount = Math.Max(0, boundedReferenceCount - 1);
    }

    private static void ApplyCallsiteSelection(ImpactResult result, int referenceCount)
    {
        var boundedReferenceCount = Math.Max(1, referenceCount);
        result.CallsiteSelection = "first_reference";
        result.CallsiteReferenceCount = boundedReferenceCount;
        result.CallsiteOmittedReferenceCount = Math.Max(0, boundedReferenceCount - 1);
    }

    private static void AddExcerptTruncation(FileExcerptResult excerpt, string reason, int recoveryStartLine, int recoveryEndLine)
    {
        excerpt.ContentTruncated = true;
        if (!excerpt.ContentTruncationReasons.Any(existing => string.Equals(existing, reason, StringComparison.Ordinal)))
            excerpt.ContentTruncationReasons.Add(reason);
        excerpt.ContentRecovery ??= FileExcerptResult.CreateRecoveryHint(excerpt.Path, recoveryStartLine, recoveryEndLine);
    }

    private static List<string>? CopyTruncationReasons(FileExcerptResult excerpt)
        => excerpt.ContentTruncationReasons.Count > 0 ? [.. excerpt.ContentTruncationReasons] : null;

    private static void ApplyBodyRecoveryCommands(IEnumerable<DefinitionResult> results, string dbPath, bool redactPaths)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath, redactPaths);
    }

    private static int WriteDefinitionJsonResult(DefinitionResult result, QueryCommandOptions options, ExactQuerySignal? exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult)!.AsObject();
        ApplyBodyModeDefinitionContentPolicy(payload, options);
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        return WriteJsonPayloadWithOptionalByteLimit(
            payload,
            options,
            jsonOptions,
            "definition",
            "definition",
            "Use --format compact for locations, omit --body unless body snippets are needed, reduce --limit, or increase --max-json-bytes.");
    }

    private static void ApplyBodyModeDefinitionContentPolicy(JsonObject payload, QueryCommandOptions options)
    {
        var reason = options.IncludeBody ? "body_content_field" : "definition_content_not_requested";
        OmitDefinitionContent(payload, reason);
        if (!options.IncludeBody)
            OmitDefinitionBodyContent(payload);
    }

    private static void ApplyInspectDefinitionContentPolicy(JsonObject payload, QueryCommandOptions options)
    {
        var reason = options.IncludeBody ? "body_content_field" : "inspect_body_not_requested";
        if (payload.TryGetPropertyValue("definitions", out var definitionsNode) && definitionsNode is JsonArray definitions)
        {
            foreach (var definition in definitions.OfType<JsonObject>())
                ApplyInspectDefinitionContentPolicy(definition, options, reason);
        }

        if (payload.TryGetPropertyValue("candidate_bundles", out var bundlesNode) && bundlesNode is JsonArray bundles)
        {
            foreach (var bundle in bundles.OfType<JsonObject>())
            {
                if (bundle.TryGetPropertyValue("definition", out var definitionNode) && definitionNode is JsonObject definition)
                    ApplyInspectDefinitionContentPolicy(definition, options, reason);
            }
        }
    }

    private static void ApplyInspectDefinitionContentPolicy(JsonObject definition, QueryCommandOptions options, string reason)
    {
        OmitDefinitionContent(definition, reason);
        if (!options.IncludeBody)
            OmitDefinitionBodyContent(definition);
    }

    private static void OmitDefinitionContent(JsonObject definition, string reason)
    {
        if (!definition.Remove("content"))
            return;

        definition["content_omitted"] = true;
        definition["content_omitted_reason"] = reason;
    }

    private static void OmitDefinitionBodyContent(JsonObject definition)
    {
        definition.Remove("body_content");
        definition.Remove("body_content_start_line");
        definition.Remove("body_content_end_line");
        definition.Remove("body_content_next_start_line");
        definition.Remove("body_content_truncated");
        definition.Remove("body_requested_start_line");
        definition.Remove("body_requested_end_line");
        definition.Remove("body_effective_start_line");
        definition.Remove("body_effective_end_line");
        definition.Remove("body_content_truncation_reasons");
        definition.Remove("body_content_recovery");
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<ReferenceResult> results, string dbPath, bool redactPaths)
    {
        foreach (var result in results)
        {
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath, redactPaths);
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.CallsiteContentRecovery, result.Path, dbPath, redactPaths);
        }
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<CallerResult> results, string dbPath, bool redactPaths)
    {
        foreach (var result in results)
        {
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath, redactPaths);
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.CallsiteContentRecovery, result.Path, dbPath, redactPaths);
        }
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<CalleeResult> results, string dbPath, bool redactPaths)
    {
        foreach (var result in results)
        {
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath, redactPaths);
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.CallsiteContentRecovery, result.Path, dbPath, redactPaths);
        }
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<ImpactResult> results, string dbPath, bool redactPaths)
    {
        foreach (var result in results)
        {
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath, redactPaths);
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.CallsiteContentRecovery, result.Path, dbPath, redactPaths);
        }
    }

    private static void ApplyBodyRecoveryCommands(SymbolAnalysisResult result, string dbPath, bool redactPaths)
    {
        ApplyBodyRecoveryCommands(result.Definitions, dbPath, redactPaths);
        ApplyBodyRecoveryCommands(result.References, dbPath, redactPaths);
        ApplyBodyRecoveryCommands(result.Callers, dbPath, redactPaths);
        ApplyBodyRecoveryCommands(result.Callees, dbPath, redactPaths);
        if (result.CandidateBundles != null)
        {
            foreach (var bundle in result.CandidateBundles)
            {
                ApplyBodyRecoveryCommands([bundle.Definition], dbPath, redactPaths);
                ApplyBodyRecoveryCommands(bundle.References, dbPath, redactPaths);
                ApplyBodyRecoveryCommands(bundle.Callers, dbPath, redactPaths);
                ApplyBodyRecoveryCommands(bundle.Callees, dbPath, redactPaths);
            }
        }
    }

    private static void WriteOptionalBodyExcerpt(int? startLine, string? content, string indent = "")
    {
        if (startLine == null || content == null)
            return;

        Console.WriteLine($"{indent}  Body:");
        WriteNumberedExcerpt(startLine.Value, content, indent + "  ");
    }

    private static void WriteOptionalCallsiteExcerpt(
        int? line,
        int? column,
        int? startLine,
        string? content,
        int? omittedReferenceCount,
        string indent = "")
    {
        if (line == null || startLine == null || content == null)
            return;

        var columnSuffix = column.HasValue ? $", column {column.Value}" : string.Empty;
        var omittedSuffix = omittedReferenceCount is > 0 ? $"; {omittedReferenceCount.Value} additional reference(s) omitted" : string.Empty;
        Console.WriteLine($"{indent}  Call site (line {line.Value}{columnSuffix}; first reference{omittedSuffix}):");
        WriteNumberedExcerpt(startLine.Value, content, indent + "  ");
    }
}
