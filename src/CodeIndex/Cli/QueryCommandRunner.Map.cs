using System.Globalization;
using System.Text.Json;

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
}
