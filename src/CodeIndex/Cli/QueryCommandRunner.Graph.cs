using System.Text.Json;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunReferences(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("references", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("references", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("references"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "references", AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "references"))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "references"))
            return CommandExitCodes.UsageError;
        if (!TryResolveNameExactMode(options, "references", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "references"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "references requires a symbol query argument",
                GetUsageLineOrThrow("references"),
                "Add the symbol name you want to trace, for example: `cdidx references QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "references requires a symbol query argument",
                GetUsageLineOrThrow("references"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("references", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("references", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountSearchReferencesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountSearchReferences(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                    () => reader.CountSearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    () => reader.SearchReferences(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                    r => r.SymbolName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.SearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.MaxLineWidth);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false) > 0,
                () => reader.CountSearchReferences(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                () => reader.SearchReferences(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false),
                r => r.SymbolName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "references", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No references found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "references", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.Line, r.Column, $"{r.ReferenceKind} {r.SymbolName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                foreach (var r in results)
                {
                    var owner = r.ContainerName != null ? $"  in {r.ContainerName}" : "";
                    Console.WriteLine($"{r.ReferenceKind,-12} {r.SymbolName,-32} {r.Path}:{r.Line}:{r.Column}{owner}");
                    Console.WriteLine($"  {r.Context}");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var refFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} references in {refFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallers(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callers", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callers", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("callers"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callers", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callers", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "callers", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "callers requires a symbol query argument",
                GetUsageLineOrThrow("callers"),
                "Add the callee symbol name after the command, for example: `cdidx callers QueryCommandRunner`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "callers requires a symbol query argument",
                GetUsageLineOrThrow("callers"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("callers", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("callers", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCallersTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                    () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                    () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                    r => r.CalleeName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.GetCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallers(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                () => reader.CountCallers(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                () => reader.GetCallers(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                r => r.CalleeName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callers", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No callers found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, null, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CallerKind ?? "?",-10} {r.CallerName ?? "<top-level>",-32} {r.Path}:{r.FirstLine}  -> {r.CalleeName} ({r.ReferenceCount} refs)");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var callerFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} callers in {callerFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallees(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var previewOptionError = ValidatePreviewOptions("callees", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        if (TryWriteUnsupportedOptionError("callees", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("callees"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callees", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callees", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryResolveNameExactMode(options, "callees", out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (TryWriteBlankQueryError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "callees requires a caller query argument",
                GetUsageLineOrThrow("callees"),
                "Add the caller symbol name after the command, for example: `cdidx callees RunIndex`.");
            return CommandExitCodes.UsageError;
        }
        if (IsBareVerbatimQueryToken(options.Query))
        {
            WriteUsageError(
                "callees requires a caller query argument",
                GetUsageLineOrThrow("callees"),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("callees", options))
            return CommandExitCodes.UsageError;

        return WithDb(options, jsonOptions, reader =>
        {
            WriteGraphReferenceKindHint("callees", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(options.Query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null;
            if (options.CountOnly)
            {
                var counts = reader.CountCalleesTotal(options.Query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    exact && reader._hasReferencesTable,
                    () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                    () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                    () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                    r => r.CallerName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload => AddSqlGraphContractJsonFields(payload, effectiveSqlGraphSignal));
                return CommandExitCodes.Success;
            }

            var results = reader.GetCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode);
            if (options.IncludeBody)
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                exact && reader._hasReferencesTable,
                () => reader.CountCallees(options.Query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds) > 0,
                () => reader.CountCallees(options.Query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds),
                () => reader.GetCallees(options.Query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode),
                r => r.CallerName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            if (results.Count == 0)
            {
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callees", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No callees found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callees", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, null, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, 1, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, exactSignal, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, jsonOptions, extraFields: payload => AddSqlGraphContractJsonFields(payload, sqlGraphSignal));
                }
            }
            else
            {
                var kindColumnWidth = ComputeReferenceKindColumnWidth(results, r => FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts));
                foreach (var r in results)
                {
                    var kindLabel = FormatReferenceKindLabel(r.ReferenceKind, r.ReferenceKinds, r.HasMixedReferenceKinds, r.ReferenceKindCounts);
                    Console.WriteLine($"{kindLabel.PadRight(kindColumnWidth)} {r.CalleeName,-32} {r.Path}:{r.FirstLine}  <- {r.CallerName ?? "<top-level>"} ({r.ReferenceCount} refs)");
                    WriteOptionalBodyExcerpt(r.BodyStartLine, r.BodyContent);
                }
                var calleeFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} callees in {calleeFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    // Human-readable reference_kind label for a grouped caller/callee row. Counts
    // keep high-volume relationships visible without requiring JSON re-querying.
    // grouped caller/callee 行の人間向け reference_kind ラベル。count を併記して、
    // JSON で再取得しなくても高頻度の関係が見えるようにする。
    private static string FormatReferenceKindLabel(string primary, IReadOnlyList<string> kinds, bool hasMixed, IReadOnlyDictionary<string, int>? counts)
    {
        if (counts == null || counts.Count == 0)
        {
            if (!hasMixed || kinds == null || kinds.Count <= 1)
                return primary ?? string.Empty;
            return string.Join("+", kinds);
        }

        var orderedKinds = kinds is { Count: > 0 } && kinds.Any(kind => counts.TryGetValue(kind, out var count) && count > 0)
            ? kinds
            : counts.Keys.Where(kind => counts[kind] > 0).OrderBy(kind => kind, StringComparer.Ordinal).ToArray();
        return string.Join(", ", orderedKinds
            .Where(kind => counts.TryGetValue(kind, out var count) && count > 0)
            .Select(kind => counts[kind] == 1 ? kind : $"{kind} x{counts[kind]}"));
    }

    // Pick a column width that fits every label in the current batch so mixed-kind
    // labels like `call+subscribe` do not overrun the neighbouring column. The
    // minimum matches the historic single-kind width (`instantiate` = 11) with a
    // small buffer so short-label batches still align consistently (issue #501).
    // 現在のバッチ内の全ラベルが収まる列幅を選び、`call+subscribe` のような
    // mixed ラベルが隣接列を押し出さないようにする。最小幅は従来の単一 kind
    // （`instantiate` = 11）と整合するよう余裕付きで設定する（issue #501）。
    private const int ReferenceKindColumnMinWidth = 12;

    private static int ComputeReferenceKindColumnWidth<T>(IEnumerable<T> rows, Func<T, string> labelSelector)
    {
        var max = ReferenceKindColumnMinWidth;
        foreach (var row in rows)
        {
            var label = labelSelector(row);
            if (label != null && label.Length > max)
                max = label.Length;
        }
        return max;
    }
}
