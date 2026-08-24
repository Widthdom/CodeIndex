using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal sealed record OutlinePageBuildResult(
        JsonObject? Payload,
        OutlineResult? Outline,
        string? Error,
        bool NotFound,
        IReadOnlyList<OutlineSymbol>? PageSymbols = null);

    public static int RunOutline(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var wantsJson = cmdArgs.Any(static arg =>
            arg == "--compact"
            || arg == "--json"
            || arg.StartsWith("--json=", StringComparison.Ordinal));
        var usage = GetUsageLineOrThrow("outline");
        if (cmdArgs.Length == 0 || cmdArgs[0].StartsWith('-'))
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                "outline requires a file path.",
                CommandExitCodes.UsageError,
                "Pass the indexed file path, for example: `cdidx outline src/CodeIndex/Program.cs`.",
                usage,
                CommandErrorCodes.UsageError,
                command: "outline");
        }

        var previewOptionError = ValidatePreviewOptions("outline", cmdArgs[1..], allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                previewOptionError,
                CommandExitCodes.UsageError,
                "Remove the unsupported preview option and rerun outline.",
                usage,
                CommandErrorCodes.UsageError,
                command: "outline");
        }
        var options = ParseArgs(
            cmdArgs[1..],
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false,
            allowOutlineSort: true);
        if (TryWriteUnsupportedOptionError(
                "outline",
                cmdArgs[1..],
                CliFlagSchema.GetAcceptedFlagNamesForCommand("outline"),
                jsonOptions: jsonOptions))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "outline", jsonOptions))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidOutlineKindFilterError(options, jsonOptions))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("outline", options, jsonOptions))
            return CommandExitCodes.UsageError;
        if (options.SearchCursor.HasValue
            || options.UnusedCursorOffset.HasValue
            || options.DependencyCycleCursor.HasValue)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                "outline --cursor must use an outline pagination cursor.",
                CommandExitCodes.UsageError,
                "Use the `next_cursor` value returned by `cdidx outline <path> --json --limit <n>`.",
                usage,
                CommandErrorCodes.UsageError,
                command: "outline");
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, cmdArgs[0], options.DbPathExplicit);
        var outlineSortMode = options.SortExplicit && options.SortValue != null && TryParseOutlineSortMode(options.SortValue, out var parsedSortMode)
            ? parsedSortMode
            : OutlineSortMode.Source;
        var includeReferenceCounts = OutlineNeedsReferenceCounts(options, outlineSortMode);
        var includeDerivedMetadata = OutlineNeedsDerivedMetadata(options, outlineSortMode);
        var kindFilters = BuildOutlineKindFilters(options.Kind);
        return WithDb(options, jsonOptions, reader =>
        {
            var cursorComponents = new List<string?>
            {
                filePath,
                FormatOutlineSortMode(outlineSortMode),
            };
            cursorComponents.AddRange(kindFilters.Order(StringComparer.Ordinal));
            var cursorContext = BuildPaginationCursorContext(reader, "outline", cursorComponents);
            var cursorValidationError = ValidateScopedOffsetCursor(options, "outline", cursorContext);
            if (cursorValidationError != null)
            {
                return CommandErrorWriter.WriteJsonOrHuman(
                    options.Json,
                    jsonOptions,
                    cursorValidationError,
                    CommandExitCodes.UsageError,
                    "Restart outline pagination without --cursor and use the new next_cursor value.",
                    usage,
                    CommandErrorCodes.UsageError,
                    command: "outline");
            }

            var outline = reader.GetOutline(filePath, includeReferenceCounts: includeReferenceCounts);
            if (outline == null)
            {
                var diagnosticFilePath = Path.IsPathRooted(filePath)
                    ? DiagnosticSanitizer.ForPath(filePath)
                    : ConsoleUi.FormatBoundedValue(filePath);
                return CommandErrorWriter.WriteJsonOrHuman(
                    options.Json,
                    jsonOptions,
                    $"'{(options.Json ? diagnosticFilePath : filePath)}' was not found in the active index.",
                    CommandExitCodes.NotFound,
                    "Check the indexed path spelling or refresh the index with `cdidx index <projectPath>`.",
                    usage,
                    CommandErrorCodes.FileNotFound,
                    "not_found",
                    "outline",
                    diagnosticFilePath);
            }

            var filteredSymbols = ApplyOutlineKindFilters(outline.Symbols, kindFilters);
            var displaySourceSymbols = ApplyOutlineSort(filteredSymbols, outlineSortMode, includeDerivedMetadata);
            var boundedLimit = JsonEnvelopeWrapper.GetBoundedResponseLimit("outline");
            var boundedOffset = boundedLimit.HasValue
                ? JsonEnvelopeWrapper.GetBoundedResponseOffset("outline")
                : (int?)null;
            if (boundedLimit.HasValue)
                JsonEnvelopeWrapper.ReportBoundedResponseTotal("outline", displaySourceSymbols.Count, authoritative: true);
            if (options.Json)
            {
                if (options.Compact)
                {
                    var payload = BuildOutlineJsonPayload(outline, displaySourceSymbols, kindFilters, outlineSortMode, options, cursorContext, jsonOptions, compact: true, boundedOffset);
                    AddActiveSqliteDiagnostics(payload);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else if (HasOutlineJsonControls(options, kindFilters))
                {
                    var payload = BuildOutlineJsonPayload(outline, displaySourceSymbols, kindFilters, outlineSortMode, options, cursorContext, jsonOptions, compact: false, boundedOffset);
                    AddActiveSqliteDiagnostics(payload);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    var payload = JsonSerializer.SerializeToNode(outline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
                    AddActiveSqliteDiagnostics(payload);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
            }
            else
            {
                Console.WriteLine($"# {outline.Path}  ({outline.Lang ?? "unknown"}, {outline.TotalLines} lines, {filteredSymbols.Count} symbols)");
                Console.WriteLine();
                if (outline.TopLevelSymbolSupport == "reindex_required")
                {
                    CommandErrorWriter.WriteStderr(
                        "Note: this index predates C# top-level synthetic symbols; reindex with the current cdidx binary.");
                }
                var duplicateNames = filteredSymbols
                    .GroupBy(sym => sym.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var displaySymbols = ApplyOutlineHumanPaging(displaySourceSymbols, options);
                foreach (var sym in displaySymbols)
                {
                    // Indent nested symbols by computed tree depth / コンテナ連鎖の深さでインデント
                    var indent = sym.Depth > 0 ? new string(' ', 4 * sym.Depth) : "";
                    var useDisplayName = sym.Kind is "function" or "method" or "constructor"
                        && duplicateNames.Contains(sym.Name)
                        && !string.IsNullOrWhiteSpace(sym.DisplayName);
                    var ret = !useDisplayName && sym.ReturnType != null ? $": {sym.ReturnType} " : "";
                    var sig = useDisplayName ? sym.DisplayName : sym.Signature ?? $"{sym.Kind} {sym.Name}";
                    // Avoid duplicating visibility when signature already contains it
                    // シグネチャに既に visibility が含まれている場合は重複を避ける
                    var vis = !useDisplayName && sym.Visibility != null && !sig.TrimStart().StartsWith(sym.Visibility, StringComparison.Ordinal)
                        ? $"{sym.Visibility} "
                        : "";
                    var syntheticDetails = sym.IsSynthetic == true
                        ? $" [synthetic {sym.Kind}/{sym.SubKind}, lines {sym.StartLine}-{sym.EndLine}, selector {sym.Selector}]"
                        : "";
                    Console.WriteLine($"  {sym.Line,5}  {indent}{vis}{sig} {ret}{syntheticDetails}");
                }
            }
            return CommandExitCodes.Success;
        });
    }

    internal static bool TryNormalizeOutlineProjectionFields(
        string rawValue,
        out List<string>? fields,
        out string? error)
    {
        var errors = new List<string>();
        fields = ParseOutlineProjectionFields(rawValue, errors.Add);
        error = errors.Count == 0
            ? null
            : errors[0]
                .Replace("Error: ", string.Empty, StringComparison.Ordinal)
                .Replace("--outline-fields", "fields", StringComparison.Ordinal);
        return error == null;
    }

    internal static OutlinePageBuildResult BuildOutlinePage(
        DbReader reader,
        string filePath,
        IReadOnlyList<string>? fields,
        bool fieldsExplicit,
        string? requestedSort,
        int limit,
        string? cursor,
        JsonSerializerOptions jsonOptions)
    {
        var sortExplicit = !string.IsNullOrWhiteSpace(requestedSort);
        var outlineSortMode = OutlineSortMode.Source;
        if (sortExplicit && !TryParseOutlineSortMode(requestedSort!, out outlineSortMode))
        {
            return new(
                null,
                null,
                "sort must be one of source, name, kind, references, size, complexity, or path.",
                NotFound: false);
        }

        int? cursorOffset = null;
        if (cursor != null)
        {
            if (!TryParseScopedOffsetCursor(cursor, out var parsedCursor)
                || !string.Equals(parsedCursor.Scope, "outline", StringComparison.Ordinal))
            {
                return new(
                    null,
                    null,
                    "cursor must be an outline pagination cursor; restart without cursor.",
                    NotFound: false);
            }
            cursorOffset = parsedCursor.Offset;
        }

        var options = new QueryCommandOptions
        {
            Json = true,
            Limit = limit,
            LimitExplicit = true,
            OutlineFields = fields?.ToList(),
            OutlineFieldsExplicit = fieldsExplicit,
            SortValue = requestedSort,
            SortExplicit = sortExplicit,
            CursorValue = cursor,
            OutlineCursorOffset = cursorOffset,
        };
        var includeReferenceCounts = OutlineNeedsReferenceCounts(options, outlineSortMode);
        var includeDerivedMetadata = OutlineNeedsDerivedMetadata(options, outlineSortMode);
        var cursorComponents = new List<string?>
        {
            filePath,
            FormatOutlineSortMode(outlineSortMode),
        };
        var cursorContext = BuildPaginationCursorContext(reader, "outline", cursorComponents);
        var cursorValidationError = ValidateScopedOffsetCursor(options, "outline", cursorContext);
        if (cursorValidationError != null)
            return new(null, null, cursorValidationError, NotFound: false);

        var outline = reader.GetOutline(filePath, includeReferenceCounts: includeReferenceCounts);
        if (outline == null)
            return new(null, null, null, NotFound: true);

        var displaySymbols = ApplyOutlineSort(outline.Symbols, outlineSortMode, includeDerivedMetadata);
        var pageOffset = Math.Min(cursorOffset ?? 0, displaySymbols.Count);
        var pageSymbols = displaySymbols.Skip(pageOffset).Take(limit).ToList();
        var payload = BuildOutlineJsonPayload(
            outline,
            displaySymbols,
            [],
            outlineSortMode,
            options,
            cursorContext,
            jsonOptions,
            compact: false);
        return new(payload, outline, null, NotFound: false, pageSymbols);
    }

    private static JsonObject ApplyOutlineCompactCaps(OutlineResult outline, int sectionLimit)
        => ApplyOutlineSymbolLimit(outline, sectionLimit);

    private static JsonObject ApplyOutlineSymbolLimit(OutlineResult outline, int sectionLimit)
    {
        var sections = new JsonObject();
        TruncateCompactSection(outline.Symbols, sectionLimit, sections, "symbols");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static bool HasOutlineJsonControls(QueryCommandOptions options, IReadOnlyList<string> kindFilters)
        => options.OutlineFieldsExplicit
           || kindFilters.Count > 0
           || options.LimitExplicit
           || options.OutlineCursorOffset.HasValue
           || options.SortExplicit;

    private static bool TryParseOutlineSortMode(string value, out OutlineSortMode sortMode)
    {
        switch (value.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "source":
            case "line":
            case "lines":
                sortMode = OutlineSortMode.Source;
                return true;
            case "name":
                sortMode = OutlineSortMode.Name;
                return true;
            case "kind":
                sortMode = OutlineSortMode.Kind;
                return true;
            case "references":
            case "reference":
            case "refs":
            case "ref":
                sortMode = OutlineSortMode.References;
                return true;
            case "size":
            case "span":
            case "spans":
                sortMode = OutlineSortMode.Size;
                return true;
            case "complexity":
                sortMode = OutlineSortMode.Complexity;
                return true;
            case "path":
                sortMode = OutlineSortMode.Path;
                return true;
            default:
                sortMode = OutlineSortMode.Source;
                return false;
        }
    }

    private static string FormatOutlineSortMode(OutlineSortMode sortMode)
        => sortMode switch
        {
            OutlineSortMode.Name => "name",
            OutlineSortMode.Kind => "kind",
            OutlineSortMode.References => "references",
            OutlineSortMode.Size => "size",
            OutlineSortMode.Complexity => "complexity",
            OutlineSortMode.Path => "path",
            _ => "source",
        };

    private static List<string> BuildOutlineKindFilters(string? rawKind)
    {
        if (string.IsNullOrWhiteSpace(rawKind))
            return [];

        return rawKind
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(kind => kind.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<OutlineSymbol> ApplyOutlineKindFilters(IReadOnlyList<OutlineSymbol> symbols, IReadOnlyList<string> kindFilters)
    {
        if (kindFilters.Count == 0)
            return symbols.ToList();

        var filterSet = kindFilters.ToHashSet(StringComparer.Ordinal);
        return symbols.Where(symbol => filterSet.Contains(symbol.Kind.ToLowerInvariant())).ToList();
    }

    private static bool OutlineNeedsReferenceCounts(QueryCommandOptions options, OutlineSortMode sortMode)
        => sortMode is OutlineSortMode.References or OutlineSortMode.Complexity
           || (options.OutlineFieldsExplicit
               && (options.OutlineFields is null
                   || options.OutlineFields.Contains("reference_count", StringComparer.Ordinal)
                   || options.OutlineFields.Contains("complexity_score", StringComparer.Ordinal)));

    private static bool OutlineNeedsDerivedMetadata(QueryCommandOptions options, OutlineSortMode sortMode)
        => sortMode != OutlineSortMode.Source
           || options.SortExplicit
           || options.OutlineFieldsExplicit;

    private static List<OutlineSymbol> ApplyOutlineSort(IReadOnlyList<OutlineSymbol> symbols, OutlineSortMode sortMode, bool includeDerivedMetadata)
    {
        if (includeDerivedMetadata)
        {
            foreach (var symbol in symbols)
                ApplyOutlineSortMetadata(symbol, sortMode);
        }

        if (sortMode == OutlineSortMode.Source)
            return symbols.ToList();

        if (!includeDerivedMetadata)
        {
            foreach (var symbol in symbols)
                ApplyOutlineSortMetadata(symbol, sortMode);
        }

        return sortMode switch
        {
            OutlineSortMode.Name => symbols
                .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
                .ToList(),
            OutlineSortMode.Kind => symbols
                .OrderBy(symbol => symbol.Kind, StringComparer.Ordinal)
                .ThenByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.References => symbols
                .OrderByDescending(symbol => symbol.ReferenceCount ?? 0)
                .ThenByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.Size => symbols
                .OrderByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.Complexity => symbols
                .OrderByDescending(GetOutlineComplexityScore)
                .ThenByDescending(GetOutlineSizeLines)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutlineSortMode.Path => symbols
                .OrderBy(symbol => symbol.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => symbols.ToList(),
        };
    }

    private static void ApplyOutlineSortMetadata(OutlineSymbol symbol, OutlineSortMode sortMode)
    {
        symbol.SortMode = FormatOutlineSortMode(sortMode);
        symbol.SizeLines = GetOutlineSizeLines(symbol);
        symbol.ComplexityScore = GetOutlineComplexityScore(symbol);
    }

    private static int GetOutlineSizeLines(OutlineSymbol symbol)
        => symbol.EndLine >= symbol.StartLine
            ? Math.Max(1, symbol.EndLine - symbol.StartLine + 1)
            : 1;

    private static double GetOutlineComplexityScore(OutlineSymbol symbol)
    {
        var visibilityBonus = symbol.Visibility switch
        {
            "public" or "pub" or "open" or "export" => 8.0,
            "protected" or "internal" or "protected internal" => 4.0,
            _ => 0.0,
        };
        var kindBonus = symbol.Kind is "class" or "struct" or "interface" or "enum" or "namespace" or "record"
            ? 6.0
            : 0.0;
        return (GetOutlineSizeLines(symbol) * 16.0) + ((symbol.ReferenceCount ?? 0) * 0.75) + visibilityBonus + kindBonus;
    }

    private static List<OutlineSymbol> ApplyOutlineHumanPaging(IReadOnlyList<OutlineSymbol> symbols, QueryCommandOptions options)
    {
        if (!options.LimitExplicit && !options.OutlineCursorOffset.HasValue)
            return symbols.ToList();

        var offset = Math.Min(options.OutlineCursorOffset ?? 0, symbols.Count);
        return symbols.Skip(offset).Take(options.Limit).ToList();
    }

    private static JsonObject BuildOutlineJsonPayload(
        OutlineResult outline,
        IReadOnlyList<OutlineSymbol> filteredSymbols,
        IReadOnlyList<string> kindFilters,
        OutlineSortMode sortMode,
        QueryCommandOptions options,
        PaginationCursorContext cursorContext,
        JsonSerializerOptions jsonOptions,
        bool compact,
        int? boundedOffset = null)
    {
        var totalMatchingSymbols = filteredSymbols.Count;
        var offset = Math.Min(boundedOffset ?? options.OutlineCursorOffset ?? 0, totalMatchingSymbols);
        var remainingSymbols = offset == 0
            ? filteredSymbols.ToList()
            : filteredSymbols.Skip(offset).ToList();

        if (compact)
        {
            var compactLimit = GetCompactSectionLimit(options);
            var compactOutline = BuildOutlineView(outline, remainingSymbols, totalMatchingSymbols);
            var compactTruncation = ApplyOutlineCompactCaps(compactOutline, compactLimit);
            var payload = JsonSerializer.SerializeToNode(compactOutline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
            AddOutlinePagingJsonFields(payload, kindFilters, sortMode, options.SortExplicit, totalMatchingSymbols, offset, compactOutline.Symbols.Count, cursorContext, jsonOptions);
            ApplyOutlineFieldSelection(payload, compactOutline.Symbols, options, jsonOptions);
            AddCompactJsonFields(payload, compactLimit, compactTruncation);
            return payload;
        }

        var shouldPage = options.LimitExplicit || options.OutlineCursorOffset.HasValue;
        var pageSymbols = shouldPage
            ? remainingSymbols.Take(options.Limit).ToList()
            : remainingSymbols;
        var pagedOutline = BuildOutlineView(outline, pageSymbols, totalMatchingSymbols);
        var pagedPayload = JsonSerializer.SerializeToNode(pagedOutline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
        AddOutlinePagingJsonFields(pagedPayload, kindFilters, sortMode, options.SortExplicit, totalMatchingSymbols, offset, pageSymbols.Count, cursorContext, jsonOptions);
        ApplyOutlineFieldSelection(pagedPayload, pageSymbols, options, jsonOptions);
        return pagedPayload;
    }

    private static OutlineResult BuildOutlineView(OutlineResult outline, List<OutlineSymbol> symbols, int symbolCount)
        => new()
        {
            Path = outline.Path,
            Lang = outline.Lang,
            TotalLines = outline.TotalLines,
            SymbolCount = symbolCount,
            TopLevelSymbolSupport = outline.TopLevelSymbolSupport,
            TopLevelSymbolLimitation = outline.TopLevelSymbolLimitation,
            Symbols = symbols,
        };

    private static void AddOutlinePagingJsonFields(
        JsonObject payload,
        IReadOnlyList<string> kindFilters,
        OutlineSortMode sortMode,
        bool sortExplicit,
        int totalSymbolCount,
        int offset,
        int returnedSymbolCount,
        PaginationCursorContext cursorContext,
        JsonSerializerOptions jsonOptions)
    {
        var nextOffset = offset + returnedSymbolCount;
        var hasMore = nextOffset < totalSymbolCount;
        payload["total_symbol_count"] = totalSymbolCount;
        payload["returned_symbol_count"] = returnedSymbolCount;
        payload["cursor_offset"] = offset;
        payload["next_cursor"] = hasMore ? JsonValue.Create(FormatOutlineCursor(nextOffset, cursorContext)) : null;
        payload["has_more"] = hasMore;
        if (cursorContext.ResultStableAt != null)
            payload["result_stable_at"] = cursorContext.ResultStableAt;
        if (kindFilters.Count > 0)
            payload["kind_filter"] = JsonSerializer.SerializeToNode(kindFilters.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (sortExplicit || sortMode != OutlineSortMode.Source)
            payload["sort"] = FormatOutlineSortMode(sortMode);
    }

    private static void ApplyOutlineFieldSelection(
        JsonObject payload,
        IReadOnlyList<OutlineSymbol> symbols,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        if (!options.OutlineFieldsExplicit)
            return;

        if (options.OutlineFields == null)
        {
            payload["selected_fields"] = JsonSerializer.SerializeToNode(new List<string> { "all" }, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
            return;
        }

        payload["selected_fields"] = JsonSerializer.SerializeToNode(options.OutlineFields, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        var projectedSymbols = new JsonArray();
        foreach (var symbol in symbols)
            projectedSymbols.Add(BuildProjectedOutlineSymbol(symbol, options.OutlineFields));
        payload["symbols"] = projectedSymbols;
    }

    private static JsonObject BuildProjectedOutlineSymbol(OutlineSymbol symbol, IReadOnlyList<string> fields)
    {
        var payload = new JsonObject();
        foreach (var field in fields)
        {
            switch (field)
            {
                case "symbol_id":
                    payload["symbol_id"] = symbol.SymbolId;
                    break;
                case "kind":
                    payload["kind"] = symbol.Kind;
                    break;
                case "sub_kind":
                    payload["sub_kind"] = symbol.SubKind;
                    break;
                case "is_synthetic":
                    payload["is_synthetic"] = symbol.IsSynthetic;
                    break;
                case "selector":
                    payload["selector"] = symbol.Selector;
                    break;
                case "qualified_name":
                    payload["qualified_name"] = symbol.QualifiedName;
                    break;
                case "name":
                    payload["name"] = symbol.Name;
                    break;
                case "display_name":
                    payload["display_name"] = symbol.DisplayName;
                    break;
                case "path":
                    payload["path"] = symbol.Path;
                    break;
                case "line":
                    payload["line"] = symbol.Line;
                    break;
                case "start_line":
                    payload["start_line"] = symbol.StartLine;
                    break;
                case "end_line":
                    payload["end_line"] = symbol.EndLine;
                    break;
                case "depth":
                    payload["depth"] = symbol.Depth;
                    break;
                case "body_start_line":
                    payload["body_start_line"] = symbol.BodyStartLine;
                    break;
                case "body_end_line":
                    payload["body_end_line"] = symbol.BodyEndLine;
                    break;
                case "signature":
                    payload["signature"] = symbol.Signature;
                    break;
                case "signature_truncated":
                    payload["signature_truncated"] = symbol.SignatureTruncated;
                    break;
                case "signature_original_length":
                    payload["signature_original_length"] = symbol.SignatureOriginalLength;
                    break;
                case "container_kind":
                    payload["container_kind"] = symbol.ContainerKind;
                    break;
                case "container_name":
                    payload["container_name"] = symbol.ContainerName;
                    break;
                case "visibility":
                    payload["visibility"] = symbol.Visibility;
                    break;
                case "return_type":
                    payload["return_type"] = symbol.ReturnType;
                    break;
                case "sort_mode":
                    payload["sort_mode"] = symbol.SortMode;
                    break;
                case "reference_count":
                    payload["reference_count"] = symbol.ReferenceCount;
                    break;
                case "size_lines":
                    payload["size_lines"] = symbol.SizeLines;
                    break;
                case "complexity_score":
                    payload["complexity_score"] = symbol.ComplexityScore;
                    break;
            }
        }
        return payload;
    }

}
