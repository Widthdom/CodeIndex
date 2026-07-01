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

    private static void AddExcerptTruncation(FileExcerptResult excerpt, string reason, int recoveryStartLine, int recoveryEndLine)
    {
        excerpt.ContentTruncated = true;
        if (!excerpt.ContentTruncationReasons.Any(existing => string.Equals(existing, reason, StringComparison.Ordinal)))
            excerpt.ContentTruncationReasons.Add(reason);
        excerpt.ContentRecovery ??= FileExcerptResult.CreateRecoveryHint(excerpt.Path, recoveryStartLine, recoveryEndLine);
    }

    private static List<string>? CopyTruncationReasons(FileExcerptResult excerpt)
        => excerpt.ContentTruncationReasons.Count > 0 ? [.. excerpt.ContentTruncationReasons] : null;

    private static void ApplyBodyRecoveryCommands(IEnumerable<DefinitionResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void WriteDefinitionJsonResult(DefinitionResult result, QueryCommandOptions options, ExactQuerySignal? exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, CliJsonSerializerContextFactory.Create(jsonOptions).DefinitionResult)!.AsObject();
        ApplyBodyModeDefinitionContentPolicy(payload, options);
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void ApplyBodyModeDefinitionContentPolicy(JsonObject payload, QueryCommandOptions options)
    {
        if (!options.IncludeBody)
            return;

        OmitDefinitionContent(payload, "body_content_field");
    }

    private static void ApplyInspectDefinitionContentPolicy(JsonObject payload, QueryCommandOptions options)
    {
        if (!payload.TryGetPropertyValue("definitions", out var definitionsNode) || definitionsNode is not JsonArray definitions)
            return;

        var reason = options.IncludeBody ? "body_content_field" : "inspect_body_not_requested";
        foreach (var definition in definitions.OfType<JsonObject>())
        {
            OmitDefinitionContent(definition, reason);
            if (!options.IncludeBody)
                OmitDefinitionBodyContent(definition);
        }
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

    private static void ApplyBodyRecoveryCommands(IEnumerable<ReferenceResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<CallerResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<CalleeResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(IEnumerable<ImpactResult> results, string dbPath)
    {
        foreach (var result in results)
            ExcerptRecoveryCommandFormatter.ApplyDbPath(result.BodyContentRecovery, result.Path, dbPath);
    }

    private static void ApplyBodyRecoveryCommands(SymbolAnalysisResult result, string dbPath)
    {
        ApplyBodyRecoveryCommands(result.Definitions, dbPath);
        ApplyBodyRecoveryCommands(result.References, dbPath);
        ApplyBodyRecoveryCommands(result.Callers, dbPath);
        ApplyBodyRecoveryCommands(result.Callees, dbPath);
    }

    private static void WriteOptionalBodyExcerpt(int? startLine, string? content, string indent = "")
    {
        if (startLine == null || content == null)
            return;

        Console.WriteLine($"{indent}  Body:");
        WriteNumberedExcerpt(startLine.Value, content, indent + "  ");
    }
}
