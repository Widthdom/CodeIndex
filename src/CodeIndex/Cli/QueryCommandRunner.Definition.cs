using System.Text.Json;
using CodeIndex.Database;

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
        if (TryWriteUnsupportedOptionError("definition", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("definition"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "definition"))
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
            Console.WriteLine(options.Json
                ? JsonSerializer.Serialize(new QueryCountFilesJsonResult(0, 0, options.Query), CliJsonSerializerContextFactory.Create(jsonOptions).QueryCountFilesJsonResult)
                : "0");
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
                var counts = reader.CountDefinitionsTotal(options.Query, options.Kind, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
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
                    Console.WriteLine(options.Json
                        ? BuildCountJsonPayload(reader, jsonOptions, count: 0, files: 0, query: options.Query, exactZeroHint: exactZeroHintForCount, exactSignal: exact ? exactSignalForCount : null, queryOptions: options).ToJsonString(jsonOptions)
                        : "0");
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    var payload = BuildCountJsonPayload(reader, jsonOptions, counts.Count, counts.FileCount, query: options.Query, exactSignal: exact ? exactSignalForCount : null, queryOptions: options);
                    Console.WriteLine(payload.ToJsonString(jsonOptions));
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                }
                return CommandExitCodes.Success;
            }

            var results = reader.GetDefinitions(options.Query, options.Limit, options.Kind, options.Lang, options.IncludeBody, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
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
                if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No definitions found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteKindHint(options.Kind, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteZeroResultHints(options, reader, "Try 'search' for full-text matches instead of symbol lookup.");
                }
                return ZeroResultExitCode(options);
            }

            ApplyBodyRecoveryCommands(results, options.DbPath);
            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.StartLine, null, $"{r.Kind} {r.Name}")),
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
                    WriteSarif(results.Select(r => (r.Path, r.StartLine, 1, $"{r.Kind} {r.Name}", "definition")), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    WriteDefinitionJsonResult(r, options, exact ? exactSignal : null, jsonOptions);
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
                var defFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} definitions in {defFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunGoto(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var all = cmdArgs.Any(arg => arg == "--all");
        var filteredArgs = cmdArgs.Where(arg => arg != "--all").ToArray();
        var options = ParseArgs(filteredArgs, jsonDefault: true, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("goto", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("goto"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "goto"))
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
            var limit = all ? options.Limit : Math.Max(options.Limit, 2);
            var results = reader.GetDefinitions(options.Query, limit, options.Kind, options.Lang, includeBody: false, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, options.Since, exact, visibilityFilters: options.VisibilityFilters, excludeVisibilityFilters: options.ExcludeVisibilityFilters);
            if (results.Count == 0)
            {
                if (!options.Json)
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No definitions found", options));
                return CommandExitCodes.NotFound;
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

            Console.WriteLine(JsonSerializer.Serialize(ToLspLocation(results[0]), CliJsonSerializerContextFactory.Create(jsonOptions).LspLocation));
            return CommandExitCodes.Success;
        });
    }
}
