using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunOutline(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        if (cmdArgs.Length == 0 || cmdArgs[0].StartsWith('-'))
        {
            WriteUsageError(
                "outline requires a file path.",
                GetUsageLineOrThrow("outline"),
                "Pass the indexed file path, for example: `cdidx outline src/CodeIndex/Program.cs`.");
            return CommandExitCodes.UsageError;
        }

        var previewOptionError = ValidatePreviewOptions("outline", cmdArgs[1..], allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs[1..],
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false,
            allowOutlineSort: true);
        if (TryWriteUnsupportedOptionError("outline", cmdArgs[1..], CliFlagSchema.GetAcceptedFlagNamesForCommand("outline")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "outline"))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidOutlineKindFilterError(options))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteUnexpectedPositionals("outline", options))
            return CommandExitCodes.UsageError;
        if (options.SearchCursor.HasValue || options.UnusedCursorOffset.HasValue)
        {
            WriteUsageError(
                "outline --cursor must use an outline pagination cursor.",
                GetUsageLineOrThrow("outline"),
                "Use the `next_cursor` value returned by `cdidx outline <path> --json --limit <n>`.");
            return CommandExitCodes.UsageError;
        }

        var filePath = DbPathResolver.ResolveQueryFilePath(options.DbPath, cmdArgs[0], options.DbPathExplicit);
        var outlineSortMode = options.SortExplicit && options.SortValue != null && TryParseOutlineSortMode(options.SortValue, out var parsedSortMode)
            ? parsedSortMode
            : OutlineSortMode.Source;
        var includeReferenceCounts = OutlineNeedsReferenceCounts(options, outlineSortMode);
        var includeDerivedMetadata = OutlineNeedsDerivedMetadata(options, outlineSortMode);
        return WithDb(options, jsonOptions, reader =>
        {
            var outline = reader.GetOutline(filePath, includeReferenceCounts: includeReferenceCounts);
            if (outline == null)
            {
                if (options.Json)
                    Console.WriteLine(JsonSerializer.Serialize(new QueryPathErrorJsonResult(filePath, "file not found in index"), CliJsonSerializerContextFactory.Create(jsonOptions).QueryPathErrorJsonResult));
                else
                    CommandErrorWriter.WriteStderr($"Error: '{filePath}' not found in index.");
                return CommandExitCodes.NotFound;
            }

            var kindFilters = BuildOutlineKindFilters(options.Kind);
            var filteredSymbols = ApplyOutlineKindFilters(outline.Symbols, kindFilters);
            var displaySourceSymbols = ApplyOutlineSort(filteredSymbols, outlineSortMode, includeDerivedMetadata);
            if (options.Json)
            {
                if (options.Compact)
                {
                    var payload = BuildOutlineJsonPayload(outline, displaySourceSymbols, kindFilters, outlineSortMode, options, jsonOptions, compact: true);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else if (HasOutlineJsonControls(options, kindFilters))
                {
                    var payload = BuildOutlineJsonPayload(outline, displaySourceSymbols, kindFilters, outlineSortMode, options, jsonOptions, compact: false);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    var payload = JsonSerializer.SerializeToNode(outline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
            }
            else
            {
                var outlineContent = reader.GetExcerpt(filePath, 1, outline.TotalLines)?.Content;

                Console.WriteLine($"# {outline.Path}  ({outline.Lang ?? "unknown"}, {outline.TotalLines} lines, {filteredSymbols.Count} symbols)");
                Console.WriteLine();
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
                    Console.WriteLine($"  {sym.Line,5}  {indent}{vis}{sig} {ret}");
                }

                // AI-orientation hint for C# files that look like top-level-statements programs:
                // no class / struct / interface / enum / namespace / record / delegate at all
                // means the executable body lives between the imports and local functions and
                // will not appear in outline at all. Emitting a short note on stderr keeps the
                // main human-readable block clean while giving AI consumers a reason for the gap.
                // AI向けヒント: C# のトップレベルステートメント想定のファイル
                // （class / struct / interface / enum / namespace / record / delegate が一切無い）は、
                // 実行本体が import と local function の間に書かれるため outline に現れない。
                // 人間向け本体を汚さないよう、理由を短く stderr に出す。
                if (LooksLikeCsharpTopLevelStatements(outline, outlineContent))
                {
                    CommandErrorWriter.WriteStderr();
                    CommandErrorWriter.WriteStderr("Note: no type/namespace declarations found; this file likely uses C# top-level statements.");
                    CommandErrorWriter.WriteStderr("      Outline lists imports and local functions only; the executable body is not indexed as symbols.");
                }
            }
            return CommandExitCodes.Success;
        });
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
        JsonSerializerOptions jsonOptions,
        bool compact)
    {
        var totalMatchingSymbols = filteredSymbols.Count;
        var offset = Math.Min(options.OutlineCursorOffset ?? 0, totalMatchingSymbols);
        var remainingSymbols = offset == 0
            ? filteredSymbols.ToList()
            : filteredSymbols.Skip(offset).ToList();

        if (compact)
        {
            var compactLimit = GetCompactSectionLimit(options);
            var compactOutline = BuildOutlineView(outline, remainingSymbols, totalMatchingSymbols);
            var compactTruncation = ApplyOutlineCompactCaps(compactOutline, compactLimit);
            var payload = JsonSerializer.SerializeToNode(compactOutline, CliJsonSerializerContextFactory.Create(jsonOptions).OutlineResult)!.AsObject();
            AddOutlinePagingJsonFields(payload, kindFilters, sortMode, options.SortExplicit, totalMatchingSymbols, offset, compactOutline.Symbols.Count, jsonOptions);
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
        AddOutlinePagingJsonFields(pagedPayload, kindFilters, sortMode, options.SortExplicit, totalMatchingSymbols, offset, pageSymbols.Count, jsonOptions);
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
        JsonSerializerOptions jsonOptions)
    {
        var nextOffset = offset + returnedSymbolCount;
        var hasMore = nextOffset < totalSymbolCount;
        payload["total_symbol_count"] = totalSymbolCount;
        payload["returned_symbol_count"] = returnedSymbolCount;
        payload["cursor_offset"] = offset;
        payload["next_cursor"] = hasMore ? JsonValue.Create(FormatOutlineCursor(nextOffset)) : null;
        payload["has_more"] = hasMore;
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
                case "kind":
                    payload["kind"] = symbol.Kind;
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

    /// <summary>
    /// Heuristic: hint only when a non-trivial C# file has no type/namespace declarations and
    /// its reconstructed content still contains uncovered file-scope executable code after
    /// skipping symbol-covered lines, imports, metadata-only attribute lines, comments, and
    /// preprocessor directives. This keeps the note off common files such as GlobalUsings.cs,
    /// AssemblyInfo.cs, and local-function-only files while preserving statement-only Program.cs
    /// files.
    /// Tiny files (snippets, partials under ~20 lines) are excluded to avoid noise.
    /// ヒューリスティック: 20 行以上の C# ファイルで型/名前空間宣言が無く、かつ
    /// import 行、metadata-only 属性行、コメント、プリプロセッサ行を除いても
    /// file-scope の実行コードが残る場合だけヒントを出す。これにより GlobalUsings.cs や
    /// AssemblyInfo.cs の誤検出を避けつつ、
    /// statement-only の Program.cs は拾い続ける。小さい断片はノイズ回避のため除外。
    /// </summary>
    private static bool LooksLikeCsharpTopLevelStatements(OutlineResult outline, string? content)
    {
        if (outline.Lang != "csharp") return false;
        if (outline.TotalLines < 20) return false;
        foreach (var sym in outline.Symbols)
        {
            if (sym.Kind is "class" or "struct" or "interface" or "enum" or "namespace" or "delegate" or "record")
                return false;
        }

        if (string.IsNullOrWhiteSpace(content))
            return false;

        var coveredLines = new bool[Math.Max(outline.TotalLines, 0) + 1];
        foreach (var sym in outline.Symbols)
        {
            var startLine = sym.StartLine > 0 ? sym.StartLine : sym.Line;
            var endLine = sym.EndLine >= startLine ? sym.EndLine : startLine;
            startLine = Math.Max(1, startLine);
            endLine = Math.Min(outline.TotalLines, endLine);
            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
                coveredLines[lineNumber] = true;
        }

        var inBlockComment = false;
        var currentLineNumber = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            currentLineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (currentLineNumber < coveredLines.Length && coveredLines[currentLineNumber])
                continue;

            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = false;
                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/", StringComparison.Ordinal))
                    inBlockComment = true;
                continue;
            }

            if (line.StartsWith("using ", StringComparison.Ordinal))
            {
                if (line.StartsWith("using var ", StringComparison.Ordinal))
                    return true;
                if (line.StartsWith("using (", StringComparison.Ordinal))
                    return true;
                continue;
            }
            if (line.StartsWith("global using ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("extern alias ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[assembly:", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("[module:", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*/", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;
            return true;
        }

        return false;
    }
}
