using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunMap(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("map", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowIssueDraftsFormat: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("map", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("map")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "map"))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("map", options, RepoMapOutputFormats, "Use `--format json`, `--format compact`, or `--format issue-drafts` for map output; use `cdidx files --count` when you need only a file count."))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("map", options))
            return CommandExitCodes.UsageError;
        if (options.MapSummaryOnly && options.MapSections != null)
            return CommandErrorWriter.Write(
                "--summary-only cannot be combined with --sections.",
                CommandExitCodes.UsageError,
                "choose --summary-only for aggregate fields only, or --sections <tree,languages,hotspots,metrics> for selected detail sections.",
                ConsoleUi.GetUsageLine("map"));
        var mapEmitsJson = options.Json || options.OutputFormat is OutputFormatCompact or OutputFormatIssueDrafts;
        if (options.MaxJsonBytes.HasValue && !mapEmitsJson)
            return CommandErrorWriter.Write(
                "--max-json-bytes is only supported with JSON map output.",
                CommandExitCodes.UsageError,
                "Use `cdidx map --compact --max-json-bytes <n>`, `cdidx map --json --sections <tree,languages,hotspots,metrics> --max-json-bytes <n>`, or remove --max-json-bytes.",
                ConsoleUi.GetUsageLine("map"));

        return WithDb(options, jsonOptions, reader =>
        {
            var compactLimit = GetCompactSectionLimit(options);
            var mapLimit = options.Compact ? GetCompactSourceLimit(compactLimit) : options.Limit;
            var map = reader.GetRepoMap(mapLimit, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.MinEntrypointConfidence);
            WorkspaceMetadataEnricher.Enrich(map, options.DbPath, options.DbPathExplicit);
            if (options.ContextAfterExplicit)
                ApplyRepoMapDepth(map, options.ContextAfter);
            var compactTruncation = options.Compact ? ApplyRepoMapCompactCaps(map, compactLimit, options) : null;

            // Return not-found only when a narrowing filter is active and produces zero files.
            // Unfiltered empty indexes return success (valid state for health probes).
            // フィルタ指定時に該当0件なら未検出を返す。フィルタなしの空DBは正常（ヘルスチェック用途）。
            var hasFilter = options.PathPatterns.Count > 0 || options.ExcludePaths.Count > 0
                || options.ExcludeTests || options.Lang != null;
            if (options.OutputFormat == OutputFormatIssueDrafts)
            {
                var issueDraftsJson = BuildRepoMapIssueDraftsPayload(map, options, jsonOptions);
                var issueDraftsExitCode = WriteJsonObjectWithOptionalByteLimit(
                    issueDraftsJson,
                    options,
                    "map issue-draft",
                    "Reduce --limit, narrow --path/--lang filters, or increase --max-json-bytes.",
                    "map");
                return issueDraftsExitCode != CommandExitCodes.Success
                    ? issueDraftsExitCode
                    : map.FileCount == 0 && hasFilter ? ZeroResultExitCode(options) : CommandExitCodes.Success;
            }
            if (map.FileCount == 0 && hasFilter)
            {
                if (options.Json)
                {
                    var payload = BuildRepoMapJsonPayload(map, options, jsonOptions, compactTruncation);
                    var json = payload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
                    var zeroJsonExitCode = WriteJsonObjectWithOptionalByteLimit(
                        json,
                        options,
                        "map",
                        "Use `--summary-only`, narrow --sections/--path/--lang filters, switch to --compact, or increase --max-json-bytes.",
                        "map");
                    if (zeroJsonExitCode != CommandExitCodes.Success)
                        return zeroJsonExitCode;
                }
                else
                {
                    CommandErrorWriter.WriteStderr("No files found matching the given filters.");
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                var payload = BuildRepoMapJsonPayload(map, options, jsonOptions, compactTruncation);
                var json = payload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "map",
                    "Use `--summary-only`, narrow --sections/--path/--lang filters, switch to --compact, or increase --max-json-bytes.",
                    "map");
            }
            else
            {
                Console.WriteLine($"Files      : {map.FileCount:N0}");
                Console.WriteLine($"Lines      : {map.TotalLines:N0}");
                Console.WriteLine($"Symbols    : {map.TotalSymbols:N0}");
                Console.WriteLine($"References : {map.TotalReferences:N0}");
                if (map.IndexedAt != null)
                    Console.WriteLine($"Scope Indexed At     : {map.IndexedAt:O}");
                if (map.LatestModified != null)
                    Console.WriteLine($"Scope Modified       : {map.LatestModified:O}");
                if (map.WorkspaceIndexedAt != null)
                    Console.WriteLine($"Workspace Indexed At : {map.WorkspaceIndexedAt:O}");
                if (map.WorkspaceLatestModified != null)
                    Console.WriteLine($"Workspace Modified   : {map.WorkspaceLatestModified:O}");
                if (map.GitHead != null)
                    Console.WriteLine($"Git HEAD   : {map.GitHead}");
                if (map.GitIsDirty != null)
                    Console.WriteLine($"Git Dirty  : {map.GitIsDirty}");
                if (!map.GraphTableAvailable)
                    Console.WriteLine("WARN       : symbol_references table missing — reference counts are synthesized 0. Do not use ReferenceRich / reference-derived ranking as authoritative.");
                if (MapSectionEnabled(options, "languages"))
                    WriteRepoMapSection("Languages", map.Languages.Select(item => $"{item.Lang,-12} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                if (MapSectionEnabled(options, "tree"))
                    WriteRepoMapSection("Modules", map.Modules.Select(item => $"{item.Module,-24} {item.Files,4} files  {item.Symbols,5} syms  {item.References,5} refs"));
                if (MapSectionEnabled(options, "hotspots"))
                {
                    WriteRepoMapSection("Top files", map.TopFiles.Select(item => $"{item.Path}  [score {item.Score}, {item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                    WriteRepoMapSection("Symbol-rich files", map.SymbolRichFiles.Select(item => $"{item.Path}  [{item.SymbolCount} syms, {item.ReferenceCount} refs]"));
                    WriteRepoMapSection("Reference-rich files", map.ReferenceRichFiles.Select(item => $"{item.Path}  [{item.ReferenceCount} refs, {item.SymbolCount} syms]"));
                    WriteRepoMapSection("Entrypoints", map.Entrypoints.Select(item => $"{item.Kind,-10} {item.Name,-24} {item.Path}:{item.Line}  [score {item.Score}, confidence {item.Confidence:0.###}, {item.MatchType}, hint #{item.HintRank}]"));
                }
                if (MapSectionEnabled(options, "metrics"))
                    WriteRepoMapSection("Largest files", map.LargestFiles.Select(item =>
                {
                    var size = options.RawBytes ? $"{item.Size.ToString(CultureInfo.InvariantCulture)} bytes" : ConsoleUi.FormatBytes(item.Size);
                    return $"{item.Path}  [{item.Lines} lines, {size}]";
                }));
            }

            return CommandExitCodes.Success;
        });
    }

    private static bool MapSectionEnabled(QueryCommandOptions options, string section)
        => !options.MapSummaryOnly && (options.MapSections == null || options.MapSections.Contains(section, StringComparer.Ordinal));

    private static void ApplyRepoMapDepth(RepoMapResult map, int depth)
    {
        map.Modules = map.Modules
            .Where(module => GetPathDepth(module.Module) <= depth)
            .ToList();
    }

    private static int GetPathDepth(string path)
        => string.IsNullOrEmpty(path) ? 0 : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static JsonObject BuildRepoMapJsonPayload(RepoMapResult map, QueryCommandOptions options, JsonSerializerOptions jsonOptions, JsonObject? compactTruncation = null)
    {
        var payload = JsonSerializer.SerializeToNode(map, CliJsonSerializerContextFactory.Create(jsonOptions).RepoMapResult)!.AsObject();
        if (options.MapSummaryOnly)
        {
            KeepRepoMapJsonProperties(payload, RepoMapSummaryJsonProperties);
            payload["summary_only"] = true;
            payload["sections"] = new JsonArray();
            AddJsonByteLimitField(payload, options);
            return payload;
        }

        if (options.MapSections == null)
        {
            if (options.ContextAfterExplicit)
                payload["depth"] = options.ContextAfter;
            if (options.Compact && compactTruncation != null)
            {
                AddCompactJsonFields(payload, GetCompactSectionLimit(options), compactTruncation);
                payload["next_commands"] = BuildRepoMapNextCommands(options);
            }
            AddJsonByteLimitField(payload, options);
            return payload;
        }

        var keep = new HashSet<string>(RepoMapSummaryJsonProperties, StringComparer.Ordinal);
        foreach (var section in options.MapSections)
            AddRepoMapSectionJsonProperties(keep, section);

        KeepRepoMapJsonProperties(payload, keep);
        payload["sections"] = new JsonArray(options.MapSections.Select(section => JsonValue.Create(section)).ToArray<JsonNode?>());
        payload["section_properties"] = BuildRepoMapSectionProperties(options.MapSections);
        if (options.ContextAfterExplicit)
            payload["depth"] = options.ContextAfter;
        if (options.Compact && compactTruncation != null)
        {
            AddCompactJsonFields(payload, GetCompactSectionLimit(options), compactTruncation);
            payload["next_commands"] = BuildRepoMapNextCommands(options);
        }
        AddJsonByteLimitField(payload, options);
        return payload;
    }

    private static JsonArray BuildRepoMapNextCommands(QueryCommandOptions options)
    {
        var commands = new JsonArray
        {
            BuildRepoMapReplayCommand(options, ["--summary-only"]),
        };

        if (options.MapSections == null)
        {
            commands.Add(BuildRepoMapReplayCommand(options, ["--sections", "tree", "--limit", GetCompactSectionLimit(options).ToString(CultureInfo.InvariantCulture)]));
            commands.Add(BuildRepoMapReplayCommand(options, ["--sections", "hotspots", "--limit", GetCompactSectionLimit(options).ToString(CultureInfo.InvariantCulture)]));
        }
        else
        {
            commands.Add(BuildRepoMapReplayCommand(options, ["--sections", string.Join(',', options.MapSections), "--limit", GetCompactSectionLimit(options).ToString(CultureInfo.InvariantCulture)]));
        }

        return commands;
    }

    private static string BuildRepoMapReplayCommand(QueryCommandOptions options, string[] mapArgs)
    {
        var args = new List<string>
        {
            "cdidx",
            "map",
            options.Compact ? "--compact" : "--json",
        };
        args.AddRange(mapArgs);
        AddRepoMapReplayOptions(args, options);
        return string.Join(" ", args.Select(QuoteReplayShellArg));
    }

    private static void AddRepoMapReplayOptions(List<string> args, QueryCommandOptions options)
    {
        if (options.DbPathExplicit)
            AddReplayValueOption(args, "--db", options.DbPath);
        if (!string.IsNullOrWhiteSpace(options.Lang))
            AddReplayValueOption(args, "--lang", options.Lang);
        foreach (var pathPattern in options.PathPatterns)
            AddReplayValueOption(args, "--path", pathPattern);
        foreach (var excludePath in options.ExcludePaths)
            AddReplayValueOption(args, "--exclude-path", excludePath);
        if (options.ExcludeTests)
            args.Add("--exclude-tests");
        if (options.ContextAfterExplicit)
            AddReplayValueOption(args, "--depth", options.ContextAfter.ToString(CultureInfo.InvariantCulture));
        if (options.MinEntrypointConfidence > 0)
            AddReplayValueOption(args, "--min-entrypoint-confidence", options.MinEntrypointConfidence.ToString("0.###", CultureInfo.InvariantCulture));
        if (options.MaxJsonBytes.HasValue)
            AddReplayValueOption(args, "--max-json-bytes", options.MaxJsonBytes.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildRepoMapIssueDraftsPayload(RepoMapResult map, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var candidates = map.LargestFiles
            .Where(IsRepoMapOversizedFileCandidate)
            .Select(BuildRepoMapIssueDraftJson)
            .ToArray();
        var sourceLimit = options.Compact ? GetCompactSourceLimit(GetCompactSectionLimit(options)) : options.Limit;
        var largestFilesTruncated = map.FileCount > map.LargestFiles.Count && map.LargestFiles.Count >= sourceLimit;
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["format"] = OutputFormatIssueDrafts,
            ["count"] = candidates.Length,
            ["issue_drafts"] = new JsonArray(candidates),
            ["groups"] = BuildRepoMapIssueDraftGroupsJson(candidates),
            ["thresholds"] = new JsonObject
            {
                ["line_threshold"] = MapIssueDraftLineThreshold,
                ["byte_threshold"] = MapIssueDraftByteThreshold,
            },
            ["truncation"] = new JsonObject
            {
                ["largest_files"] = new JsonObject
                {
                    ["source_section"] = "largest_files",
                    ["returned"] = map.LargestFiles.Count,
                    ["source_limit"] = sourceLimit,
                    ["total_files"] = map.FileCount,
                    ["truncated"] = largestFilesTruncated,
                },
            },
            ["query_context"] = BuildQueryContextJson(options, jsonOptions),
        };
        if (map.ProjectRoot != null)
            payload["project_root"] = map.ProjectRoot;
        if (map.GitHead != null)
            payload["git_head"] = map.GitHead;
        if (map.GitIsDirty != null)
            payload["git_is_dirty"] = map.GitIsDirty;
        if (map.IndexedHeadCommit != null)
            payload["indexed_head_commit"] = map.IndexedHeadCommit;
        if (map.WorktreeHeadChanged != null)
            payload["worktree_head_changed"] = map.WorktreeHeadChanged;
        AddJsonByteLimitField(payload, options);
        return payload.ToJsonString(GetJsonNodeSerializationOptions(jsonOptions));
    }

    private static JsonObject BuildRepoMapIssueDraftGroupsJson(IReadOnlyList<JsonObject> candidates)
    {
        var representativePaths = new JsonArray();
        foreach (var candidate in candidates.Take(DefaultCompactSectionLimit))
        {
            var path = candidate["candidate"]?["path"]?.GetValue<string>();
            if (path != null)
                representativePaths.Add(path);
        }

        return new JsonObject
        {
            ["oversized_file"] = new JsonObject
            {
                ["kind"] = "oversized_file",
                ["count"] = candidates.Count,
                ["source_section"] = "largest_files",
                ["representative_paths"] = representativePaths,
                ["representative_paths_truncated"] = candidates.Count > representativePaths.Count,
            },
        };
    }

    private static bool IsRepoMapOversizedFileCandidate(RepoFileSummaryResult file)
        => file.Lines >= MapIssueDraftLineThreshold || file.Size >= MapIssueDraftByteThreshold;

    private static JsonObject BuildRepoMapIssueDraftJson(RepoFileSummaryResult file)
    {
        var reasonTags = new JsonArray();
        if (file.Lines >= MapIssueDraftLineThreshold)
            reasonTags.Add("line_threshold_exceeded");
        if (file.Size >= MapIssueDraftByteThreshold)
            reasonTags.Add("byte_threshold_exceeded");

        return new JsonObject
        {
            ["kind"] = "oversized_file",
            ["title"] = $"Split oversized file: {file.Path}",
            ["body"] = BuildRepoMapIssueDraftBody(file, reasonTags),
            ["labels"] = new JsonArray("maintenance", "refactor"),
            ["candidate"] = new JsonObject
            {
                ["path"] = file.Path,
                ["lang"] = file.Lang,
                ["lines"] = file.Lines,
                ["size_bytes"] = file.Size,
                ["symbol_count"] = file.SymbolCount,
                ["reference_count"] = file.ReferenceCount,
                ["line_threshold"] = MapIssueDraftLineThreshold,
                ["byte_threshold"] = MapIssueDraftByteThreshold,
                ["line_threshold_exceeded"] = file.Lines >= MapIssueDraftLineThreshold,
                ["byte_threshold_exceeded"] = file.Size >= MapIssueDraftByteThreshold,
                ["reason_tags"] = reasonTags.DeepClone(),
                ["source_section"] = "largest_files",
            },
        };
    }

    private static string BuildRepoMapIssueDraftBody(RepoFileSummaryResult file, JsonArray reasonTags)
    {
        var reasons = string.Join(", ", reasonTags.Select(tag => tag?.GetValue<string>()).Where(tag => tag != null));
        var builder = new StringBuilder();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"`{file.Path}` is an oversized maintenance candidate from `cdidx map --format issue-drafts`.");
        builder.AppendLine();
        builder.AppendLine("## Evidence");
        builder.AppendLine();
        builder.AppendLine($"- Lines: {file.Lines.ToString(CultureInfo.InvariantCulture)} (threshold: >= {MapIssueDraftLineThreshold.ToString(CultureInfo.InvariantCulture)})");
        builder.AppendLine($"- Size: {file.Size.ToString(CultureInfo.InvariantCulture)} bytes (threshold: >= {MapIssueDraftByteThreshold.ToString(CultureInfo.InvariantCulture)})");
        builder.AppendLine($"- Symbols: {file.SymbolCount.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- References: {file.ReferenceCount.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Reason tags: {reasons}");
        builder.AppendLine();
        builder.AppendLine("## Checklist");
        builder.AppendLine();
        builder.AppendLine("- [ ] Identify cohesive regions, types, or command paths that can move together.");
        builder.AppendLine("- [ ] Preserve public behavior and CLI/MCP output contracts.");
        builder.AppendLine("- [ ] Add or keep focused tests for moved behavior.");
        return builder.ToString().TrimEnd();
    }

    private static readonly HashSet<string> RepoMapSummaryJsonProperties = new(StringComparer.Ordinal)
    {
        "api_version",
        "file_count",
        "total_lines",
        "total_symbols",
        "total_references",
        "indexed_at",
        "latest_modified",
        "workspace_indexed_at",
        "workspace_latest_modified",
        "project_root",
        "git_head",
        "git_is_dirty",
        "indexed_head_commit",
        "worktree_head_changed",
        "graph_table_available",
    };

    private static readonly IReadOnlyDictionary<string, string[]> RepoMapSectionJsonProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["languages"] = ["languages"],
        ["tree"] = ["modules"],
        ["hotspots"] = ["top_files", "symbol_rich_files", "reference_rich_files", "entrypoints"],
        ["metrics"] = ["largest_files"],
    };

    private static void KeepRepoMapJsonProperties(JsonObject payload, IReadOnlySet<string> keep)
    {
        foreach (var propertyName in payload.Select(property => property.Key).Where(key => !keep.Contains(key)).ToList())
            payload.Remove(propertyName);
    }

    private static void AddRepoMapSectionJsonProperties(HashSet<string> keep, string section)
    {
        if (!RepoMapSectionJsonProperties.TryGetValue(section, out var properties))
            return;

        foreach (var property in properties)
            keep.Add(property);
    }

    private static JsonObject BuildRepoMapSectionProperties(IEnumerable<string> sections)
    {
        var payload = new JsonObject();
        foreach (var section in sections)
        {
            if (!RepoMapSectionJsonProperties.TryGetValue(section, out var properties))
                continue;

            payload[section] = new JsonArray(properties.Select(property => JsonValue.Create(property)).ToArray<JsonNode?>());
        }

        return payload;
    }

    private static JsonObject ApplyRepoMapCompactCaps(RepoMapResult map, int sectionLimit, QueryCommandOptions options)
    {
        var sections = new JsonObject();
        if (MapSectionEnabled(options, "languages"))
            TruncateCompactSection(map.Languages, sectionLimit, sections, "languages");
        if (MapSectionEnabled(options, "tree"))
            TruncateCompactSection(map.Modules, sectionLimit, sections, "modules");
        if (MapSectionEnabled(options, "hotspots"))
        {
            TruncateCompactSection(map.TopFiles, sectionLimit, sections, "top_files");
            TruncateCompactSection(map.SymbolRichFiles, sectionLimit, sections, "symbol_rich_files");
            TruncateCompactSection(map.ReferenceRichFiles, sectionLimit, sections, "reference_rich_files");
            TruncateCompactSection(map.Entrypoints, sectionLimit, sections, "entrypoints");
        }
        if (MapSectionEnabled(options, "metrics"))
            TruncateCompactSection(map.LargestFiles, sectionLimit, sections, "largest_files");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }
}
