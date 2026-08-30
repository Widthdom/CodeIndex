using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunDefinition(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("definition", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteUnsupportedOptionError("definition", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("definition"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "definition", options.LanguageValidationError ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "definition", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "definition", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Query is not null && IsBareVerbatimQueryToken(options.Query) && options.CountOnly && string.Equals(options.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Json)
            {
                Console.WriteLine("0");
                return CommandExitCodes.Success;
            }

            var zeroPayload = JsonSerializer.SerializeToNode(
                new QueryCountFilesJsonResult(0, 0, options.Query),
                CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult)!.AsObject();
            if (options.GroupPartials)
                AddLogicalPartialCountJsonFields(zeroPayload, logicalCount: 0, physicalCount: 0, physicalFileCount: 0);
            Console.WriteLine(zeroPayload.ToJsonString(jsonOptions));
            return CommandExitCodes.Success;
        }
        if (TryWriteBlankQueryError(options, "definition"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "definition requires a symbol query argument",
                GetUsageLineOrThrow("definition"),
                "Add the symbol name after the command, for example: `cdidx definition QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "definition requires a symbol query argument",
                GetUsageLineOrThrow("definition"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("definition", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            if (options.CountOnly)
            {
                var physicalCounts = reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
                var counts = options.GroupPartials
                    ? reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters, groupPartials: true)
                    : physicalCounts;
                var exactSignalForCount = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact,
                    () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                    () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                    r => r.Name);
                WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        var zeroPayload = BuildCountJsonPayload(
                            reader,
                            jsonOptions,
                            count: 0,
                            files: 0,
                            query: options.Query,
                            exactZeroHint: exactZeroHintForCount,
                            exactSignal: exact ? exactSignalForCount : null,
                            queryOptions: options,
                            extraFields: options.GroupPartials
                                ? payload => AddLogicalPartialCountJsonFields(payload, logicalCount: 0, physicalCount: 0, physicalFileCount: 0)
                                : null);
                        var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                            zeroPayload,
                            options,
                            jsonOptions,
                            "definition",
                            "definition count",
                            "Narrow the query or increase --max-json-bytes.");
                        if (writeExitCode != CommandExitCodes.Success)
                            return writeExitCode;
                    }
                    else
                    {
                        Console.WriteLine("0");
                        WriteIndexGenerationAuthorityWarningIfNeeded(reader);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(
                        reader,
                        jsonOptions,
                        counts.Count,
                        counts.FileCount,
                        query: options.Query,
                        exactSignal: exact ? exactSignalForCount : null,
                        queryOptions: options,
                        extraFields: options.GroupPartials
                            ? payload => AddLogicalPartialCountJsonFields(payload, counts.Count, physicalCounts.Count, physicalCounts.FileCount)
                            : null);
                    var writeExitCode = WriteJsonPayloadWithOptionalByteLimit(
                        payload,
                        options,
                        jsonOptions,
                        "definition",
                        "definition count",
                        "Narrow the query or increase --max-json-bytes.");
                    if (writeExitCode != CommandExitCodes.Success)
                        return writeExitCode;
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var physicalCountsForResults = options.GroupPartials
                ? reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters)
                : (QueryCountResult?)null;
            var logicalCountsForResults = options.GroupPartials
                ? reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters, groupPartials: true)
                : (QueryCountResult?)null;
            var results = reader.GetDefinitions(
                options.Query,
                options.Limit,
                options.Kind,
                options.Lang,
                options.IncludeBody,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.Since,
                exact,
                visibilityFilters: options.VisibilityFilters,
                excludeVisibilityFilters: options.ExcludeVisibilityFilters,
                groupPartials: options.GroupPartials,
                offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("definition"));
            var exactSignal = reader.GetDefinitionExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since);
            var exactZeroHint = BuildExactZeroHint(
                exact,
                () => reader.CountSearchSymbols(options.Query, ExactZeroHintProbeLimit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters) > 0,
                () => reader.CountSearchSymbols(options.Query, options.Limit, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                () => reader.SearchSymbols(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact: false, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters),
                r => r.Name);
            WriteExactSymbolWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                {
                    const string hint = "Check the symbol spelling or narrow/adjust --kind, --lang, and --path filters.";
                    var notFoundJson = JsonSerializer.SerializeToNode(
                        new CommandErrorJsonResult(
                            "error",
                            BuildZeroResultLine("No definitions found", options),
                            hint,
                            CommandErrorCodes.QueryNotFound,
                            Category: "not_found"),
                        CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult)!.AsObject();
                    AddIndexGenerationAuthorityJsonFields(notFoundJson, reader, jsonOptions);
                    var writeExitCode = WriteJsonObjectWithOptionalByteLimit(
                        notFoundJson.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)),
                        options,
                        "definition not-found response",
                        "Increase --max-json-bytes to allow the structured not-found response.",
                        jsonOptions,
                        "definition");
                    return writeExitCode == CommandExitCodes.Success
                        ? CommandExitCodes.NotFound
                        : writeExitCode;
                }
                if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No definitions found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader, "Try 'search' for full-text matches instead of symbol lookup.");
                    WriteIndexGenerationAuthorityWarningIfNeeded(reader);
                }
                return ZeroResultExitCode(options);
            }

            ApplyBodyRecoveryCommands(results, options.DbPath, options.RedactPaths ?? true);
            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, GetSymbolDisplayLine(r), GetSymbolDisplayColumn(r), $"{r.Kind} {r.Name}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.StartLine, 1, $"{r.Kind} {r.Name}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(
                        results.Select(r => (r.Path, GetSymbolDisplayLine(r), GetSymbolDisplayColumn(r), $"{r.Kind} {r.Name}", "definition")),
                        jsonOptions,
                        level: "note");
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    var writeExitCode = WriteDefinitionJsonResult(r, options, exact ? exactSignal : null, jsonOptions);
                    if (writeExitCode != CommandExitCodes.Success)
                        return writeExitCode;
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var container = r.ContainerName != null ? $" in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.Kind,-10} {r.Name,-40} {r.Path}:{r.StartLine}-{r.EndLine}{container}");
                    WriteNumberedExcerpt(r.StartLine, r.Content);
                    if (options.IncludeBody)
                    {
                        if (r.BodyContent != null && r.BodyStartLine != null)
                        {
                            Console.WriteLine();
                            Console.WriteLine("  Body:");
                            WriteNumberedExcerpt(r.BodyStartLine.Value, r.BodyContent);
                        }
                        else
                        {
                            Console.WriteLine("  Body: unavailable");
                        }
                    }
                    Console.WriteLine();
                }
                if (physicalCountsForResults.HasValue)
                {
                    CommandErrorWriter.WriteStderr($"({results.Count} of {logicalCountsForResults!.Value.Count} logical definitions shown; {physicalCountsForResults.Value.Count} total physical declaration sites in {physicalCountsForResults.Value.FileCount} files)");
                }
                else
                {
                    var defFileCount = results.Select(r => r.Path).Distinct().Count();
                    CommandErrorWriter.WriteStderr($"({results.Count} definitions in {defFileCount} files)");
                }
            }
            return CommandExitCodes.Success;
        });
    }

    private static void AddLogicalPartialCountJsonFields(JsonObject payload, int logicalCount, int physicalCount, int physicalFileCount)
    {
        payload["group_partials"] = true;
        payload["count_kind"] = "logical_partial_families";
        payload["logical_count"] = logicalCount;
        payload["physical_count"] = physicalCount;
        payload["physical_file_count"] = physicalFileCount;
        payload["partials_grouped"] = logicalCount != physicalCount;
    }

    public static int RunGoto(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var all = cmdArgs.Any(arg => arg == "--all");
        var filteredArgs = cmdArgs.Where(arg => arg != "--all").ToArray();
        var options = ParseArgs(filteredArgs, jsonDefault: true, allowNamedQuery: true);
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteUnsupportedOptionError("goto", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("goto"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "goto", options.LanguageValidationError ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "goto", KnownSymbolKindFilters))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "goto", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "goto"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "goto requires a symbol query argument",
                GetUsageLineOrThrow("goto"),
                "Add the symbol name after the command, for example: `cdidx goto QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "goto requires a symbol query argument",
                GetUsageLineOrThrow("goto"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("goto", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            var limit = all
                ? options.LimitExplicit ? options.Limit : int.MaxValue
                : Math.Max(options.Limit, 2);
            var results = reader.GetDefinitions(options.Query, limit, options.Kind, options.Lang, includeBody: false, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters, groupPartials: !all);
            if (results.Count == 0)
            {
                if (options.Json)
                {
                    var payload = JsonSerializer.SerializeToNode(
                        new CommandErrorJsonResult(
                            "error",
                            BuildZeroResultLine("No definitions found", options),
                            "Check the symbol spelling or narrow/adjust --kind, --lang, and --path filters.",
                            CommandErrorCodes.QueryNotFound,
                            Category: "not_found"),
                        CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult)!.AsObject();
                    AddIndexGenerationAuthorityJsonFields(payload, reader, jsonOptions);
                    Console.WriteLine(payload.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)));
                    return CommandExitCodes.NotFound;
                }

                WriteIndexGenerationAuthorityWarningIfNeeded(reader);
                return CommandErrorWriter.WriteJsonOrHuman(
                    false,
                    jsonOptions,
                    BuildZeroResultLine("No definitions found", options),
                    CommandExitCodes.NotFound,
                    "Check the symbol spelling or narrow/adjust --kind, --lang, and --path filters.",
                    errorCode: CommandErrorCodes.QueryNotFound,
                    category: "not_found");
            }

            if (all)
            {
                WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                return CommandExitCodes.Success;
            }

            if (results.Count > 1)
            {
                CommandErrorWriter.WriteStderr($"Error: goto found {results.Count} matching definitions for '{options.Query}'.");
                CommandErrorWriter.WriteStderr("Hint: narrow the query with --kind, --lang, --path, or pass --all to return all LSP locations.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(SerializeQueryJson(ToLspLocation(results[0]), CliJsonSerializerContextFactory.Create(jsonOptions).LspLocation, jsonOptions));
            return CommandExitCodes.Success;
        });
    }
}
