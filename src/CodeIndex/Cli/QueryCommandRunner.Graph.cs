using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunReferences(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        if (!TryParseGraphCommandOptions("references", cmdArgs, out var options, out var optionExitCode))
            return optionExitCode;
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteInvalidKindFilterError(options, "references", AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (TryWriteParseError(options, "references", options.LanguageValidationError ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "references"))
            return CommandExitCodes.UsageError;
        if (!TryValidateGraphSymbolQuery(
                "references",
                options,
                "references requires a symbol query argument",
                "Add the symbol name you want to trace, for example: `cdidx references QueryCommandRunner`.",
                out var exact,
                out var queryExitCode))
        {
            return queryExitCode;
        }
        var requestedQuery = options.Query ?? options.Selector!;

        return WithDb(options, jsonOptions, reader =>
        {
            if (!TryResolveGraphSelector("references", options, reader, jsonOptions, out var selectedDefinition, out var selectorExitCode))
                return selectorExitCode;
            var query = selectedDefinition?.Name ?? requestedQuery;
            var selectorMatchesLanguage = selectedDefinition == null
                || DbReader.GraphSelectorMatchesLanguageFilter(selectedDefinition, options.Lang);
            var identityMetadata = reader.GetReferenceGraphQueryIdentityMetadata(
                query,
                selectedDefinition,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.Kind,
                exact,
                options.IncludeQualifiedCommonCalls);
            if (!options.Json)
                WriteGraphIdentityWarningIfNeeded(query, identityMetadata);

            WriteGraphReferenceKindHint("references", options.Kind, options.Json);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = selectedDefinition?.Lang ?? (exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null);
            if (options.CountOnly)
            {
                var counts = !selectorMatchesLanguage
                    ? new QueryCountResult(0, 0)
                    : selectedDefinition != null
                        ? reader.CountSearchReferencesForCandidate(
                            selectedDefinition,
                            options.PathPatterns,
                            options.ExcludePaths,
                            options.ExcludeTests,
                            options.Kind,
                            includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls,
                            requireAuthoritativeIdentity: true)
                        : reader.CountSearchReferencesTotal(query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.IncludeQualifiedCommonCalls);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    selectedDefinition == null && exact && reader._hasReferencesTable,
                    () => reader.CountSearchReferences(query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls) > 0,
                    () => reader.CountSearchReferences(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls),
                    () => reader.SearchReferences(query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls),
                    r => r.SymbolName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload =>
                    {
                        AddReferenceGraphContractJsonFields(payload, effectiveSqlGraphSignal, hdlGraphSignal);
                        AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                    });
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload =>
                {
                    AddReferenceGraphContractJsonFields(payload, effectiveSqlGraphSignal, hdlGraphSignal);
                    AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                });
                return CommandExitCodes.Success;
            }

            var results = !selectorMatchesLanguage
                ? []
                : selectedDefinition != null
                    ? reader.SearchReferencesForCandidate(
                        selectedDefinition,
                        options.Limit,
                        options.PathPatterns,
                        options.ExcludePaths,
                        options.ExcludeTests,
                        options.MaxLineWidth,
                        offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("references"),
                        referenceKind: options.Kind,
                        includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls,
                        requireAuthoritativeIdentity: true)
                    : reader.SearchReferences(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.MaxLineWidth, offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("references"), includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls);
            if (options.IncludeBody && JsonEnvelopeWrapper.ShouldMaterializeBody("references"))
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath, options.RedactPaths ?? true);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetReferencesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                selectedDefinition == null && exact && reader._hasReferencesTable,
                () => reader.CountSearchReferences(query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls) > 0,
                () => reader.CountSearchReferences(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls),
                () => reader.SearchReferences(query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls),
                r => r.SymbolName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(
                    options,
                    jsonOptions,
                    extraFields: payload => AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions)))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "references", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload =>
                    {
                        AddReferenceGraphContractJsonFields(payload, sqlGraphSignal, hdlGraphSignal);
                        AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                    });
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No references found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang, reader);
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
                    jsonOptions,
                    payload => AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions)))
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
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, exactSignal, jsonOptions, extraFields: payload =>
                        {
                            AddReferenceGraphContractJsonFields(payload, sqlGraphSignal, hdlGraphSignal);
                            AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                        });
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceResult, jsonOptions, extraFields: payload =>
                        {
                            AddReferenceGraphContractJsonFields(payload, sqlGraphSignal, hdlGraphSignal);
                            AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                        });
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
                    WriteOptionalCallsiteExcerpt(
                        r.CallsiteLine,
                        r.CallsiteColumn,
                        r.CallsiteStartLine,
                        r.CallsiteContent,
                        r.CallsiteOmittedReferenceCount);
                }
                var refFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} references in {refFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallers(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        if (!TryParseGraphCommandOptions("callers", cmdArgs, out var options, out var optionExitCode))
            return optionExitCode;
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteParseError(options, "callers", options.LanguageValidationError ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "callers"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callers", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callers", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryValidateGraphSymbolQuery(
                "callers",
                options,
                "callers requires a symbol query argument",
                "Add the callee symbol name after the command, for example: `cdidx callers QueryCommandRunner`.",
                out var exact,
                out var queryExitCode))
        {
            return queryExitCode;
        }
        var requestedQuery = options.Query ?? options.Selector!;

        return WithDb(options, jsonOptions, reader =>
        {
            if (!TryResolveGraphSelector("callers", options, reader, jsonOptions, out var selectedDefinition, out var selectorExitCode))
                return selectorExitCode;
            var query = selectedDefinition?.Name ?? requestedQuery;
            var selectorMatchesLanguage = selectedDefinition == null
                || DbReader.GraphSelectorMatchesLanguageFilter(selectedDefinition, options.Lang);
            var identityMetadata = reader.GetCallerGraphQueryIdentityMetadata(
                query,
                selectedDefinition,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.Kind,
                exact,
                options.RawKinds,
                options.IncludeQualifiedCommonCalls,
                options.IncludeMemberReads);
            if (!options.Json)
                WriteGraphIdentityWarningIfNeeded(query, identityMetadata);

            WriteGraphReferenceKindHint("callers", options.Kind, options.Json);
            WriteReferenceGraphCompletenessWarningIfNeeded(options.Json, reader);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = selectedDefinition?.Lang ?? (exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null);
            if (options.CountOnly)
            {
                var counts = !selectorMatchesLanguage
                    ? new QueryCountResult(0, 0)
                    : selectedDefinition != null
                        ? reader.CountCallersForCandidate(
                            selectedDefinition,
                            options.PathPatterns,
                            options.ExcludePaths,
                            options.ExcludeTests,
                            options.Kind,
                            options.RawKinds,
                            options.IncludeQualifiedCommonCalls,
                            options.IncludeMemberReads)
                        : reader.CountCallersTotal(query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.IncludeQualifiedCommonCalls, options.IncludeMemberReads);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    selectedDefinition == null && exact && reader._hasReferencesTable,
                    () => reader.CountCallers(query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads) > 0,
                    () => reader.CountCallers(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                    () => reader.GetCallers(query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                    r => r.CalleeName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload =>
                    {
                        AddGraphContractJsonFields(payload, reader, jsonOptions, effectiveSqlGraphSignal, hdlGraphSignal);
                        AddCallerIdentityRootJsonFields(payload, counts);
                        AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                        if (selectedDefinition == null && !exact)
                            payload["graph_evidence_confidence"] = "name_discovery";
                    });
                    if (!options.Json)
                        WriteCallerIdentityRootWarningIfNeeded(counts);
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload =>
                {
                    AddGraphContractJsonFields(payload, reader, jsonOptions, effectiveSqlGraphSignal, hdlGraphSignal);
                    AddCallerIdentityRootJsonFields(payload, counts);
                    AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                    if (selectedDefinition == null && !exact)
                        payload["graph_evidence_confidence"] = "name_discovery";
                });
                if (!options.Json)
                    WriteCallerIdentityRootWarningIfNeeded(counts);
                return CommandExitCodes.Success;
            }

            var results = !selectorMatchesLanguage
                ? []
                : selectedDefinition != null
                    ? reader.GetCallersForCandidate(
                        selectedDefinition,
                        options.Limit,
                        options.PathPatterns,
                        options.ExcludePaths,
                        options.ExcludeTests,
                        offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("callers"),
                        referenceKind: options.Kind,
                        rawKinds: options.RawKinds,
                        rankMode: options.RankMode,
                        includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls,
                        includeMemberReads: options.IncludeMemberReads)
                    : reader.GetCallers(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode, offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("callers"), includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads);
            var callerIdentityCounts = selectedDefinition != null
                ? selectorMatchesLanguage
                    ? reader.CountCallersForCandidate(
                        selectedDefinition,
                        options.PathPatterns,
                        options.ExcludePaths,
                        options.ExcludeTests,
                        options.Kind,
                        options.RawKinds,
                        options.IncludeQualifiedCommonCalls,
                        options.IncludeMemberReads)
                    : new QueryCountResult(0, 0)
                : exact
                    ? reader.CountCallersTotal(query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: true, options.RawKinds, options.IncludeQualifiedCommonCalls, options.IncludeMemberReads)
                : (QueryCountResult?)null;
            if (options.IncludeBody && JsonEnvelopeWrapper.ShouldMaterializeBody("callers"))
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath, options.RedactPaths ?? true);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCallersExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                selectedDefinition == null && exact && reader._hasReferencesTable,
                () => reader.CountCallers(query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads) > 0,
                () => reader.CountCallers(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                () => reader.GetCallers(query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                r => r.CalleeName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
            if (!options.Json && callerIdentityCounts is { } humanCounts)
                WriteCallerIdentityRootWarningIfNeeded(humanCounts);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(
                    options,
                    jsonOptions,
                    extraFields: payload => AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions)))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callers", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload =>
                    {
                        AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                        if (callerIdentityCounts is { } counts)
                            AddCallerIdentityRootJsonFields(payload, counts);
                        else if (selectedDefinition == null)
                            payload["graph_evidence_confidence"] = "name_discovery";
                        AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                    });
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No callers found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callers", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, Math.Max(1, r.FirstColumn), $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions,
                    payload => AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions)))
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
                    WriteSarif(results.Select(r => (r.Path, r.FirstLine, Math.Max(1, r.FirstColumn), $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}", r.ReferenceKind)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, exactSignal, jsonOptions, extraFields: payload =>
                        {
                            AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                            if (callerIdentityCounts is { } counts)
                                AddCallerIdentityRootJsonFields(payload, counts);
                            AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                            AddReferenceRankingQueryContextJson(payload, options, jsonOptions);
                        });
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CallerResult, jsonOptions, extraFields: payload =>
                        {
                            AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                            if (selectedDefinition == null)
                                payload["graph_evidence_confidence"] = "name_discovery";
                            AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                            AddReferenceRankingQueryContextJson(payload, options, jsonOptions);
                        });
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
                    WriteOptionalCallsiteExcerpt(
                        r.CallsiteLine,
                        r.CallsiteColumn,
                        r.CallsiteStartLine,
                        r.CallsiteContent,
                        r.CallsiteOmittedReferenceCount);
                }
                var callerFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} callers in {callerFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    public static int RunCallees(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        if (!TryParseGraphCommandOptions("callees", cmdArgs, out var options, out var optionExitCode))
            return optionExitCode;
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteParseError(options, "callees", options.LanguageValidationError ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteSnippetLinesZeroUnsupportedError(options, "callees"))
            return CommandExitCodes.UsageError;
        if (TryRejectNonCallGraphKindForGraphCommand("callees", options.Kind))
            return CommandExitCodes.UsageError;
        if (TryWriteInvalidKindFilterError(options, "callees", CallGraphOnlyReferenceKinds, AllValidReferenceKinds, AllValidKinds))
            return CommandExitCodes.InvalidArgument;
        if (!TryValidateGraphSymbolQuery(
                "callees",
                options,
                "callees requires a caller query argument",
                "Add the caller symbol name after the command, for example: `cdidx callees RunIndex`.",
                out var exact,
                out var queryExitCode))
        {
            return queryExitCode;
        }
        var requestedQuery = options.Query ?? options.Selector!;

        return WithDb(options, jsonOptions, reader =>
        {
            if (!TryResolveGraphSelector("callees", options, reader, jsonOptions, out var selectedDefinition, out var selectorExitCode))
                return selectorExitCode;
            var query = selectedDefinition?.Name ?? requestedQuery;
            var selectorMatchesLanguage = selectedDefinition == null
                || DbReader.GraphSelectorMatchesLanguageFilter(selectedDefinition, options.Lang);
            var identityMetadata = reader.GetCalleeGraphQueryIdentityMetadata(
                query,
                selectedDefinition,
                options.Lang,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                options.Kind,
                exact,
                options.RawKinds,
                options.IncludeQualifiedCommonCalls,
                options.IncludeMemberReads);
            if (!options.Json)
                WriteGraphIdentityWarningIfNeeded(query, identityMetadata);

            WriteGraphReferenceKindHint("callees", options.Kind, options.Json);
            WriteReferenceGraphCompletenessWarningIfNeeded(options.Json, reader);
            var baseSqlGraphSignal = reader.GetSqlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var hdlGraphSignal = reader.GetHdlGraphContractSignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests);
            var exactGraphLanguage = selectedDefinition?.Lang ?? (exact
                ? reader.GetExactGraphSupportedDefinitionLanguage(query, options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests)
                : null);
            if (options.CountOnly)
            {
                var counts = !selectorMatchesLanguage
                    ? new QueryCountResult(0, 0)
                    : selectedDefinition != null
                    ? reader.CountCalleesForCandidate(
                        selectedDefinition,
                        options.PathPatterns,
                        options.ExcludePaths,
                        options.ExcludeTests,
                        options.Kind,
                        options.RawKinds,
                        options.IncludeQualifiedCommonCalls,
                        options.IncludeMemberReads)
                    : reader.CountCalleesTotal(query, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.IncludeQualifiedCommonCalls, options.IncludeMemberReads);
                var effectiveSqlGraphSignal = NarrowSqlGraphContractSignal(
                    baseSqlGraphSignal,
                    counts.IncludesSql || DbReader.IsSqlLanguage(options.Lang) || DbReader.IsSqlLanguage(exactGraphLanguage));
                var exactSignalForCount = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: effectiveSqlGraphSignal.Relevant);
                var exactZeroHintForCount = BuildExactZeroHint(
                    selectedDefinition == null && exact && reader._hasReferencesTable,
                    () => reader.CountCallees(query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads) > 0,
                    () => reader.CountCallees(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                    () => reader.GetCallees(query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                    r => r.CallerName);
                WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignalForCount, reader, options);
                WriteSqlGraphContractWarningIfNeeded(options.Json, effectiveSqlGraphSignal, reader, options);
                WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
                if (counts.Count == 0)
                {
                    WriteGraphCountResult(reader, 0, 0, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, exactZeroHintForCount, extraFields: payload =>
                    {
                        AddGraphContractJsonFields(payload, reader, jsonOptions, effectiveSqlGraphSignal, hdlGraphSignal);
                        AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                    });
                    return CommandExitCodes.Success;
                }

                WriteGraphCountResult(reader, counts.Count, counts.FileCount, options, jsonOptions, reader._hasReferencesTable, exactSignalForCount, extraFields: payload =>
                {
                    AddGraphContractJsonFields(payload, reader, jsonOptions, effectiveSqlGraphSignal, hdlGraphSignal);
                    AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                });
                return CommandExitCodes.Success;
            }

            var results = !selectorMatchesLanguage
                ? []
                : selectedDefinition != null
                ? reader.GetCalleesForCandidate(
                    selectedDefinition,
                    options.Limit,
                    options.PathPatterns,
                    options.ExcludePaths,
                    options.ExcludeTests,
                    offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("callees"),
                    referenceKind: options.Kind,
                    rawKinds: options.RawKinds,
                    rankMode: options.RankMode,
                    includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls,
                    includeMemberReads: options.IncludeMemberReads)
                : reader.GetCallees(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact, options.RawKinds, options.RankMode, offset: JsonEnvelopeWrapper.GetBoundedResponseOffset("callees"), includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads);
            if (options.IncludeBody && JsonEnvelopeWrapper.ShouldMaterializeBody("callees"))
                AttachBodyExcerpts(reader, results, options.SnippetLines, options.MaxLineWidth);
            ApplyBodyRecoveryCommands(results, options.DbPath, options.RedactPaths ?? true);
            var sqlGraphSignal = NarrowSqlGraphContractSignalByLanguages(baseSqlGraphSignal, results.Select(result => result.Lang), options.Lang, exactGraphLanguage);
            var exactSignal = reader.GetCalleesExactQuerySignal(options.Lang, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, includeSqlGraphContractSignal: sqlGraphSignal.Relevant);
            var exactZeroHint = BuildExactZeroHint(
                selectedDefinition == null && exact && reader._hasReferencesTable,
                () => reader.CountCallees(query, ExactZeroHintProbeLimit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads) > 0,
                () => reader.CountCallees(query, options.Limit, options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                () => reader.GetCallees(query, Math.Min(options.Limit, ExactZeroHintSampleLimit), options.Lang, options.Kind, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, exact: false, rawKinds: options.RawKinds, rankMode: options.RankMode, includeQualifiedCommonCalls: options.IncludeQualifiedCommonCalls, includeMemberReads: options.IncludeMemberReads),
                r => r.CallerName);
            WriteExactGraphWarningIfNeeded(exact, options.Json, exactSignal, reader, options);
            WriteSqlGraphContractWarningIfNeeded(options.Json, sqlGraphSignal, reader, options);
            WriteHdlGraphContractWarningIfNeeded(options.Json, hdlGraphSignal);
            if (results.Count == 0)
            {
                if (options.Json && TryWriteEmptyFormattedResult(
                    options,
                    jsonOptions,
                    extraFields: payload => AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions)))
                    return ZeroResultExitCode(options);
                if (options.Json)
                    WriteGraphZeroJsonResult(reader, "callees", jsonOptions, graphAvailable: reader._hasReferencesTable, exact ? exactSignal : (ExactQuerySignal?)null, exactZeroHint, queryOptions: options, extraFields: payload =>
                    {
                        AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                        AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                    });
                else if (!options.Json)
                {
                    CommandErrorWriter.WriteStderr(BuildZeroResultLine("No callees found", options));
                    WriteExactZeroHint(exactZeroHint);
                    WriteGraphSupportHint(options.Lang, reader);
                    WriteLangHint(options.Lang, reader);
                    WriteDegradedGraphZeroResult(reader, "callees", json: false, graphAvailable: reader._hasReferencesTable, jsonOptions);
                }
                return ZeroResultExitCode(options);
            }

            if (options.Json)
            {
                if (TryWriteFormattedLocations(
                    options,
                    results.Select(r => new FormattedLocation(r.Path, r.FirstLine, r.FirstColumn, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")),
                    jsonOptions,
                    payload => AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions)))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(results.Select(ToLspLocation), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(results.Select(r => (r.Path, r.FirstLine, r.FirstColumn ?? 0, $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}")));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(
                        results.Select(r => new SarifLocation(
                            r.Path,
                            r.FirstLine,
                            r.FirstColumn ?? 1,
                            r.FirstColumn.HasValue && r.FirstLength.HasValue
                                ? r.FirstColumn.Value + Math.Max(1, r.FirstLength.Value)
                                : null,
                            $"{r.CallerName ?? "<top-level>"} -> {r.CalleeName}",
                            r.ReferenceKind)),
                        jsonOptions);
                    return CommandExitCodes.Success;
                }
                foreach (var r in results)
                {
                    if (exact)
                        WriteGraphJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, exactSignal, jsonOptions, extraFields: payload =>
                        {
                            AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                            AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                            AddReferenceRankingQueryContextJson(payload, options, jsonOptions);
                        });
                    else
                        WriteJsonResult(r, CliJsonSerializerContextFactory.Create(jsonOptions).CalleeResult, jsonOptions, extraFields: payload =>
                        {
                            AddGraphContractJsonFields(payload, reader, jsonOptions, sqlGraphSignal, hdlGraphSignal);
                            AddGraphIdentityJsonFields(payload, identityMetadata, jsonOptions);
                            AddReferenceRankingQueryContextJson(payload, options, jsonOptions);
                        });
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
                    WriteOptionalCallsiteExcerpt(
                        r.CallsiteLine,
                        r.CallsiteColumn,
                        r.CallsiteStartLine,
                        r.CallsiteContent,
                        r.CallsiteOmittedReferenceCount);
                }
                var calleeFileCount = results.Select(r => r.Path).Distinct().Count();
                CommandErrorWriter.WriteStderr($"({results.Count} callees in {calleeFileCount} files)");
            }
            return CommandExitCodes.Success;
        });
    }

    private static bool TryParseGraphCommandOptions(
        string command,
        string[] cmdArgs,
        out QueryCommandOptions options,
        out int exitCode)
    {
        var previewOptionError = ValidatePreviewOptions(
            command,
            cmdArgs,
            allowMaxLineWidth: true,
            allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            options = null!;
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        options = ParseArgs(cmdArgs, jsonDefault: false, allowNamedQuery: true);
        options.ReferenceRankingActive = command is "callers" or "callees";
        if (TryWriteUnsupportedOptionError(
                command,
                cmdArgs,
                CliFlagSchema.GetAcceptedFlagNamesForCommand(command),
                options.Query))
        {
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (options.ParseError == null
            && !TryValidateGraphSnippetLinesOption(command, options))
        {
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        exitCode = CommandExitCodes.Success;
        return true;
    }

    internal static bool TryValidateBoundedGraphSnippetLinesOption(
        string command,
        string[] args,
        bool bodyOutputHidden)
    {
        if (command is not ("references" or "callers" or "callees" or "impact"))
            return true;

        var options = ParseArgs(args, jsonDefault: false, allowNamedQuery: true);
        return options.ParseError != null
               || TryValidateGraphSnippetLinesOption(command, options, bodyOutputHidden);
    }

    private static bool TryValidateGraphSnippetLinesOption(
        string command,
        QueryCommandOptions options,
        bool bodyOutputHidden = false)
    {
        if (!options.SnippetLinesExplicit || options.SnippetLines == 0)
            return true;

        if (!options.IncludeBody)
        {
            CommandErrorWriter.Write(
                $"--snippet-lines requires --body for {command}.",
                "Add --body to emit bounded definition and call-site evidence, or omit --snippet-lines.",
                GetUsageLineOrThrow(command),
                CommandErrorCodes.UsageError);
            return false;
        }

        if (bodyOutputHidden
            || options.CountOnly
            || options.OutputFormat is not (OutputFormatText or OutputFormatJson))
        {
            CommandErrorWriter.Write(
                $"--snippet-lines with --body requires text or JSON result output for {command}.",
                "Remove --count and use --format text or --format json, or omit --snippet-lines for location-only output.",
                GetUsageLineOrThrow(command),
                CommandErrorCodes.UsageError);
            return false;
        }

        return true;
    }

    private static bool TryValidateGraphSymbolQuery(
        string command,
        QueryCommandOptions options,
        string requiredQueryMessage,
        string querySuggestion,
        out bool exact,
        out int exitCode)
    {
        exact = false;
        if (!TryResolveNameExactMode(options, command, out exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (options.Selector != null && !string.IsNullOrWhiteSpace(options.Query))
        {
            WriteUsageError(
                "--selector cannot be combined with a symbol query argument",
                GetUsageLineOrThrow(command),
                "Remove the positional/--query value and pass only the generation-bound selector emitted by inspect.");
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (options.Selector == null && TryWriteBlankQueryError(options, command))
        {
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Query) && string.IsNullOrWhiteSpace(options.Selector))
        {
            WriteUsageError(requiredQueryMessage, GetUsageLineOrThrow(command), querySuggestion);
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (options.Selector == null && IsBareVerbatimQueryToken(options.Query!))
        {
            WriteUsageError(
                requiredQueryMessage,
                GetUsageLineOrThrow(command),
                "Add a real symbol name after the command; bare verbatim prefixes like `@` are not valid queries.");
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        if (TryWriteUnexpectedExtraPositionals(command, options))
        {
            exitCode = CommandExitCodes.UsageError;
            return false;
        }

        exitCode = CommandExitCodes.Success;
        return true;
    }

    private static string? GetGraphSelectorValue(QueryCommandOptions options)
    {
        if (options.Selector != null)
            return options.Selector;
        return SymbolSelector.TryParse(options.Query, out _)
            ? options.Query
            : null;
    }

    private static bool TryResolveGraphSelector(
        string command,
        QueryCommandOptions options,
        DbReader reader,
        JsonSerializerOptions jsonOptions,
        out DefinitionResult? selectedDefinition,
        out int exitCode)
    {
        selectedDefinition = null;
        exitCode = CommandExitCodes.Success;
        var selectorValue = GetGraphSelectorValue(options);
        if (selectorValue == null)
            return true;

        var resolution = reader.ResolveGraphSymbolSelector(selectorValue);
        if (resolution.Status == GraphSymbolSelectorStatus.Success)
        {
            selectedDefinition = resolution.Definition;
            return true;
        }

        var (message, suggestion, status, code, category) = resolution.Status switch
        {
            GraphSymbolSelectorStatus.GenerationRequired => (
                $"symbol selector requires a generation fingerprint: {selectorValue}",
                "Rerun inspect and pass its complete `id:<positive-integer>@g:<fingerprint>` selector.",
                CommandExitCodes.UsageError,
                CommandErrorCodes.UsageError,
                "usage"),
            GraphSymbolSelectorStatus.Stale => (
                $"symbol selector is stale or belongs to another database: {selectorValue}",
                "Rerun inspect against this database and use the current emitted selector.",
                CommandExitCodes.NotFound,
                CommandErrorCodes.QueryNotFound,
                "not_found"),
            GraphSymbolSelectorStatus.NotFound => (
                $"symbol selector was not found in the active index: {selectorValue}",
                "Rerun inspect and use a selector emitted by the active database.",
                CommandExitCodes.NotFound,
                CommandErrorCodes.QueryNotFound,
                "not_found"),
            _ => (
                $"invalid symbol selector: {ConsoleUi.FormatBoundedValue(selectorValue)}",
                "Pass a selector emitted by inspect in the form `--selector 'id:<positive-integer>@g:<fingerprint>'`.",
                CommandExitCodes.UsageError,
                CommandErrorCodes.UsageError,
                "usage"),
        };
        exitCode = CommandErrorWriter.WriteJsonOrHuman(
            options.Json,
            jsonOptions,
            message,
            status,
            suggestion,
            GetUsageLineOrThrow(command),
            errorCode: code,
            category: category,
            command: command);
        return false;
    }

    private static void AddGraphIdentityJsonFields(
        JsonObject payload,
        GraphQueryIdentityMetadata metadata,
        JsonSerializerOptions jsonOptions)
    {
        if (!metadata.Applies)
            return;

        payload["identity_scoped"] = metadata.IdentityScoped;
        payload["identity_scope_reason"] = metadata.IdentityScopeReason;
        if (metadata.Selected != null)
            payload["selected_symbol"] = JsonSerializer.SerializeToNode(metadata.Selected, jsonOptions);
        if (metadata.Candidates.Count > 0)
        {
            payload["candidate_count"] = metadata.Candidates.Count;
            payload["candidates"] = new JsonArray(
                metadata.Candidates
                    .Select(candidate => JsonSerializer.SerializeToNode(candidate, jsonOptions))
                    .ToArray());
            payload["candidates_truncated"] = metadata.CandidatesTruncated;
        }
    }

    private static void WriteGraphIdentityWarningIfNeeded(
        string query,
        GraphQueryIdentityMetadata metadata)
    {
        if (!metadata.Applies || metadata.IdentityScoped)
            return;

        CommandErrorWriter.WriteStderr(
            $"Warning: '{query}' matches {metadata.Candidates.Count} symbol identities; results aggregate them and are not identity-scoped.");
        foreach (var candidate in metadata.Candidates)
        {
            CommandErrorWriter.WriteStderr(
                $"  candidate: {candidate.QualifiedName} ({candidate.Selector})");
        }
        if (metadata.CandidatesTruncated)
            CommandErrorWriter.WriteStderr("  candidate list truncated; narrow the query or inspect it for the complete bounded candidate view.");
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
