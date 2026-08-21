using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private bool TryParseFilterOption(string normalizedArg, string currentArg, string? inlineValue, string[] args, ref int i)
        {
            switch (normalizedArg)
            {
                case "--kind":
                    if (TryReadStringOptionValue(args, ref i, "--kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var kindValue, out var kindError))
                    {
                        WarnIfDuplicateSingleValueOption("--kind", kindValue!);
                        // Normalize to lowercase so '--kind FUNCTION' == '--kind function'. AllValidKinds entries
                        // and every DB 'symbols.kind' row are lowercase.
                        // '--kind FUNCTION' と '--kind function' を同一視するため lowercase 正規化する。AllValidKinds
                        // と DB の `symbols.kind` はすべて lowercase。
                        kind = kindValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(kindError!);
                    break;
                case "--bucket":
                    if (TryReadStringOptionValue(args, ref i, "--bucket", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var unusedBucketValue, out var unusedBucketError))
                    {
                        WarnIfDuplicateSingleValueOption("--bucket", unusedBucketValue!);
                        unusedBucket = unusedBucketValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(unusedBucketError!);
                    break;
                case "--confidence":
                case "--min-confidence":
                    var confidenceFlag = normalizedArg;
                    if (TryReadStringOptionValue(args, ref i, confidenceFlag, inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var minUnusedConfidenceValue, out var minUnusedConfidenceError))
                    {
                        WarnIfDuplicateSingleValueOption("--min-confidence", minUnusedConfidenceValue!);
                        minUnusedConfidence = minUnusedConfidenceValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(minUnusedConfidenceError!);
                    break;
                case "--severity":
                    if (TryReadStringOptionValue(args, ref i, "--severity", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var severityValue, out var severityError))
                    {
                        WarnIfDuplicateSingleValueOption("--severity", severityValue!);
                        severity = severityValue?.ToLowerInvariant();
                    }
                    else
                    {
                        AddParseError(severityError!);
                    }
                    break;
                case "--visibility":
                    if (TryReadStringOptionValue(args, ref i, "--visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var visibilityValue, out var visibilityError))
                        AddVisibilityFilterValues("--visibility", visibilityValue!, visibilityFilters, AddParseError);
                    else
                        AddParseError(visibilityError!);
                    break;
                case "--exclude-visibility":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludeVisibilityValue, out var excludeVisibilityError))
                        AddVisibilityFilterValues("--exclude-visibility", excludeVisibilityValue!, excludeVisibilityFilters, AddParseError);
                    else
                        AddParseError(excludeVisibilityError!);
                    break;
                case "--rank-by":
                    if (TryReadStringOptionValue(args, ref i, "--rank-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var rankByValue, out var rankByError))
                    {
                        WarnIfDuplicateSingleValueOption("--rank-by", rankByValue!);
                        if (TryParseReferenceRankMode(rankByValue!, out var parsedRankMode))
                            rankMode = parsedRankMode;
                        else
                            AddParseError($"Error: --rank-by must be one of weighted, count, kind; got '{rankByValue}'.");
                    }
                    else
                        AddParseError(rankByError!);
                    break;
                case "--sort":
                    if (TryReadStringOptionValue(args, ref i, "--sort", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sortRawValue, out var sortError))
                    {
                        WarnIfDuplicateSingleValueOption("--sort", sortRawValue!);
                        var normalizedSortValue = sortRawValue!;
                        if (allowOutlineSort && TryParseOutlineSortMode(normalizedSortValue, out _))
                        {
                            sortExplicit = true;
                        }
                        else if (!allowOutlineSort && TryParseSymbolSortMode(normalizedSortValue, out var parsedSortMode))
                        {
                            symbolSortMode = parsedSortMode;
                            sortExplicit = true;
                        }
                        else
                        {
                            var allowedSortValues = allowOutlineSort
                                ? "source, kind, references, size, span, complexity, path, or name"
                                : "hotspot, references, size, complexity, path";
                            AddParseError($"Error: --sort must be one of {allowedSortValues}; got '{normalizedSortValue}'.");
                        }
                        sortValue = normalizedSortValue;
                    }
                    else
                        AddParseError(sortError!);
                    break;
                case "--sections":
                    if (TryReadStringOptionValue(args, ref i, "--sections", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sectionsValue, out var sectionsError))
                    {
                        WarnIfDuplicateSingleValueOption("--sections", sectionsValue!);
                        mapSections = ParseMapSections(sectionsValue!, AddParseError);
                    }
                    else
                        AddParseError(sectionsError!);
                    break;
                case "--summary-only":
                    summaryOnly = true;
                    mapSummaryOnly = true;
                    break;
                case "--fields":
                    if (TryReadStringOptionValue(args, ref i, "--fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var fieldsValue, out var fieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--fields", fieldsValue!);
                        inspectFields = ParseInspectFields(
                            fieldsValue!,
                            AddParseError,
                            out var includeBodyFromFields,
                            out inspectFieldValidationError);
                        includeBody |= includeBodyFromFields;
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError(fieldsError!);
                    }
                    break;
                default:
                    return false;
            }

            return true;
        }
    }
}
