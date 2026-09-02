using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private bool TryParseResultOption(string normalizedArg, string currentArg, string? inlineValue, string[] args, ref int i)
        {
            switch (normalizedArg)
            {
                case "--fts":
                    rawFts = true;
                    break;
                case "--body":
                    includeBody = true;
                    break;
                case "--body-start":
                    if (!TryReadRawOptionValue(args, ref i, "--body-start", inlineValue, out var bodyStartValue, out var missingBodyStartError))
                        AddParseError(missingBodyStartError!);
                    else if (TryParsePositiveInt(bodyStartValue!, "--body-start", out var parsedBodyStartLine, out var bodyStartError))
                    {
                        WarnIfDuplicateSingleValueOption("--body-start", bodyStartValue!);
                        bodyStartLine = parsedBodyStartLine;
                        includeBody = true;
                    }
                    else
                        AddParseError(bodyStartError!);
                    break;
                case "--body-lines":
                case "--body-line-count":
                    var bodyLinesFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, bodyLinesFlag, inlineValue, out var bodyLinesValue, out var missingBodyLinesError))
                        AddParseError(missingBodyLinesError!);
                    else if (TryParsePositiveInt(bodyLinesValue!, bodyLinesFlag, out var parsedBodyLines, out var bodyLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--body-lines", bodyLinesValue!);
                        bodyLines = parsedBodyLines;
                        includeBody = true;
                    }
                    else
                        AddParseError(bodyLinesError!);
                    break;
                case "--start-column":
                    if (!TryReadRawOptionValue(args, ref i, "--start-column", inlineValue, out var startColumnValue, out var missingStartColumnError))
                        AddParseError(missingStartColumnError!);
                    else if (TryParsePositiveInt(startColumnValue!, "--start-column", out var parsedStartColumn, out var startColumnError))
                    {
                        WarnIfDuplicateSingleValueOption("--start-column", startColumnValue!);
                        startColumn = parsedStartColumn;
                    }
                    else
                        AddParseError(startColumnError!);
                    break;
                case "--end-column":
                    if (!TryReadRawOptionValue(args, ref i, "--end-column", inlineValue, out var endColumnValue, out var missingEndColumnError))
                        AddParseError(missingEndColumnError!);
                    else if (TryParsePositiveInt(endColumnValue!, "--end-column", out var parsedEndColumn, out var endColumnError))
                    {
                        WarnIfDuplicateSingleValueOption("--end-column", endColumnValue!);
                        endColumn = parsedEndColumn;
                    }
                    else
                        AddParseError(endColumnError!);
                    break;
                case "--count":
                    countFlagRequested = true;
                    break;
                case "--group-partials":
                    groupPartials = true;
                    break;
                case "--selector":
                    if (TryReadStringOptionValue(args, ref i, "--selector", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var selectorValue, out var selectorError))
                    {
                        WarnIfDuplicateSingleValueOption("--selector", selectorValue!);
                        selector = selectorValue;
                    }
                    else
                        AddParseError(selectorError!);
                    break;
                case "--cycles":
                    dependencyCycles = true;
                    break;
                case "--all-cycle-nodes":
                    includeAllDependencyCycleNodes = true;
                    break;
                case "--suppress-noise":
                    dependencySuppressNoise = true;
                    break;
                case "--symbol":
                    if (TryReadStringOptionValue(args, ref i, "--symbol", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dependencySymbolValue, out var dependencySymbolError))
                        AddDependencySymbolFilter("--symbol", dependencySymbolValue!, dependencySymbols);
                    else
                        AddParseError(dependencySymbolError!);
                    break;
                case "--symbol-family":
                    if (TryReadStringOptionValue(args, ref i, "--symbol-family", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dependencySymbolFamilyValue, out var dependencySymbolFamilyError))
                        AddDependencySymbolFilter("--symbol-family", dependencySymbolFamilyValue!, dependencySymbolFamilies);
                    else
                        AddParseError(dependencySymbolFamilyError!);
                    break;
                case "--strict-not-found":
                    strictNotFound = true;
                    break;
                case "--allow-partial":
                    allowPartial = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--by-bucket":
                    break;
                case "--all":
                    all = true;
                    break;
                case "--no-dedup":
                    noDedup = true;
                    break;
                case "--no-visibility-rank":
                    noVisibilityRank = true;
                    break;
                case "--exact":
                    exact = true;
                    break;
                case "--regex":
                    regex = true;
                    break;
                case "--exact-name":
                    exactName = true;
                    break;
                case "--exact-substring":
                    exactSubstring = true;
                    break;
                case "--token-boundary":
                    tokenBoundary = true;
                    break;
                case "--prefix":
                    prefix = true;
                    break;
                case "--max-hops":
                case "--depth":
                    var depthOptionName = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, depthOptionName, inlineValue, out var depthValue, out var missingDepthError))
                        AddParseError(missingDepthError!);
                    else if (TryParseNonNegativeInt(depthValue!, depthOptionName, out var parsedDepth, out var depthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-hops", depthValue!);
                        contextAfter = parsedDepth; // reused as depth for impact / impact用に再利用
                        contextAfterExplicit = true;
                        if (depthOptionName == "--depth")
                            impactDeprecatedDepthUsed = true;
                    }
                    else
                        AddParseError(depthError!);
                    break;
                case "--reverse":
                    break; // handled by specific commands / 特定コマンドで処理
                case "--group-by-name":
                    break;
                case "--group-by":
                    if (TryReadStringOptionValue(args, ref i, "--group-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var groupByValue, out var groupByError))
                    {
                        WarnIfDuplicateSingleValueOption("--group-by", groupByValue!);
                        groupBy = groupByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(groupByError!);
                    break;
                case "--unique":
                    if (TryReadStringOptionValue(args, ref i, "--unique", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var uniqueValue, out var uniqueError))
                    {
                        WarnIfDuplicateSingleValueOption("--unique", uniqueValue!);
                        uniqueBy = uniqueValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(uniqueError!);
                    break;
                case "--count-by":
                    if (TryReadStringOptionValue(args, ref i, "--count-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var countByValue, out var countByError))
                    {
                        WarnIfDuplicateSingleValueOption("--count-by", countByValue!);
                        countBy = countByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(countByError!);
                    break;
                case "--origin":
                case "--match-origin":
                    var originOptionName = normalizedArg;
                    if (TryReadStringOptionValue(args, ref i, originOptionName, inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var originValue, out var originError))
                        AddSearchMatchOrigins(originOptionName, originValue!, matchOrigins, AddParseError);
                    else
                        AddParseError(originError!);
                    break;
                case "--exclude-origin":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-origin", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludedOriginValue, out var excludedOriginError))
                        AddSearchMatchOrigins("--exclude-origin", excludedOriginValue!, excludeOrigins, AddParseError);
                    else
                        AddParseError(excludedOriginError!);
                    break;
                case "--result-kind":
                    if (TryReadStringOptionValue(args, ref i, "--result-kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var resultKindValue, out var resultKindError))
                        AddSearchResultKinds(resultKindValue!, resultKinds, AddParseError);
                    else
                        AddParseError(resultKindError!);
                    break;
                case "--search-fields":
                    if (TryReadStringOptionValue(args, ref i, "--search-fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var searchFieldsValue, out var searchFieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--search-fields", searchFieldsValue!);
                        searchFields = ParseSearchProjectionFields(searchFieldsValue!, AddParseError);
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                        AddParseError(searchFieldsError!);
                    break;
                case "--first-per-file":
                    firstPerFile = true;
                    break;
                case "--results-only":
                    resultsOnly = true;
                    json = true;
                    if (!outputFormatExplicit)
                        outputFormat = OutputFormatJson;
                    break;
                case "--next-steps":
                    nextSteps = true;
                    break;
                case "--sample":
                    if (!TryReadRawOptionValue(args, ref i, "--sample", inlineValue, out var sampleValue, out var missingSampleError))
                        AddParseError(missingSampleError!);
                    else if (TryParsePositiveInt(sampleValue!, "--sample", out var parsedSample, out var sampleError))
                    {
                        WarnIfDuplicateSingleValueOption("--sample", sampleValue!);
                        sampleSize = parsedSample;
                    }
                    else
                        AddParseError(sampleError!);
                    break;
                case "--per-file-limit":
                    groupedPerFileLimitExplicit = true;
                    if (!TryReadRawOptionValue(args, ref i, "--per-file-limit", inlineValue, out var perFileLimitValue, out var missingPerFileLimitError))
                        AddParseError(missingPerFileLimitError!);
                    else if (TryParsePositiveInt(perFileLimitValue!, "--per-file-limit", out var parsedPerFileLimit, out var perFileLimitError))
                    {
                        WarnIfDuplicateSingleValueOption("--per-file-limit", perFileLimitValue!);
                        groupedPerFileLimit = Math.Min(parsedPerFileLimit, MaxSearchGroupedPerFileLimit);
                    }
                    else
                        AddParseError(perFileLimitError!);
                    break;
                case "--max-json-bytes":
                    if (!TryReadRawOptionValue(args, ref i, "--max-json-bytes", inlineValue, out var maxJsonBytesValue, out var missingMaxJsonBytesError))
                        AddParseError(missingMaxJsonBytesError!);
                    else if (TryParsePositiveInt(maxJsonBytesValue!, "--max-json-bytes", out var parsedMaxJsonBytes, out var maxJsonBytesError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-json-bytes", maxJsonBytesValue!);
                        requestedMaxJsonBytes = parsedMaxJsonBytes;
                        maxJsonBytes = Math.Min(parsedMaxJsonBytes, MaxSearchJsonByteLimit);
                    }
                    else
                        AddParseError(maxJsonBytesError!);
                    break;
                case "--with-paths":
                    withPaths = true;
                    break;
                case "--bytes":
                    rawBytes = true;
                    break;
                case "--raw-kinds":
                    rawKinds = true;
                    break;
                case "--include-qualified-common-calls":
                    includeQualifiedCommonCalls = true;
                    break;
                case "--include-member-reads":
                    includeMemberReads = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--profile":
                    profile = true;
                    break;
                case "--slow-query-ms":
                    if (!TryReadRawOptionValue(args, ref i, "--slow-query-ms", inlineValue, out var slowQueryValue, out var missingSlowQueryError))
                        AddParseError(missingSlowQueryError!);
                    else if (TryParseNonNegativeInt(slowQueryValue!, "--slow-query-ms", out var parsedSlowQueryMs, out var slowQueryError))
                    {
                        WarnIfDuplicateSingleValueOption("--slow-query-ms", slowQueryValue!);
                        slowQueryMs = parsedSlowQueryMs;
                    }
                    else
                        AddParseError(slowQueryError!);
                    break;
                case "--min-entrypoint-confidence":
                    if (!TryReadRawOptionValue(args, ref i, "--min-entrypoint-confidence", inlineValue, out var minEntrypointConfidenceValue, out var missingMinEntrypointConfidenceError))
                        AddParseError(missingMinEntrypointConfidenceError!);
                    else if (TryParseConfidence(minEntrypointConfidenceValue!, out var parsedMinEntrypointConfidence))
                    {
                        WarnIfDuplicateSingleValueOption("--min-entrypoint-confidence", minEntrypointConfidenceValue!);
                        minEntrypointConfidence = parsedMinEntrypointConfidence;
                    }
                    else
                        AddParseError($"Error: --min-entrypoint-confidence must be a number from 0.0 through 1.0; got '{ConsoleUi.FormatBoundedValue(minEntrypointConfidenceValue)}'.");
                    break;
                default:
                    return false;
            }

            return true;
        }
    }
}
