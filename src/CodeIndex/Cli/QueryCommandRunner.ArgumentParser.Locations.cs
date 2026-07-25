using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private bool TryParseLocationOption(string normalizedArg, string currentArg, string? inlineValue, string[] args, ref int i)
        {
            switch (normalizedArg)
            {
                case "--path":
                    if (TryReadStringOptionValue(args, ref i, "--path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var pathPattern, out var pathError))
                    {
                        pathPatterns.Add(pathPattern!); // Repeatable; multiple values OR together / 繰り返し可、複数値は OR で結合
                        userPathPatterns.Add(pathPattern!);
                    }
                    else
                        AddParseError(pathError!);
                    break;
                case "--project":
                    if (TryReadStringOptionValue(args, ref i, "--project", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var projectName, out var projectError))
                        projectFilters.Add(projectName!);
                    else
                        AddParseError(projectError!);
                    break;
                case "--solution":
                    if (TryReadStringOptionValue(args, ref i, "--solution", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var solutionValue, out var solutionError))
                    {
                        WarnIfDuplicateSingleValueOption("--solution", solutionValue!);
                        solutionFilter = solutionValue;
                    }
                    else
                        AddParseError(solutionError!);
                    break;
                case "--exclude-path":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var excludePath, out var excludePathError))
                        excludePaths.Add(excludePath!);
                    else
                        AddParseError(excludePathError!);
                    break;
                case "--exclude-tests":
                    excludeTests = true;
                    break;
                case "--no-semantic-tokens":
                    noSemanticTokens = true;
                    break;
                case "--exclude-comments":
                    excludeComments = true;
                    break;
                case "--exclude-strings":
                    excludeStrings = true;
                    break;
                case "--exclude-fixtures":
                    excludeFixtures = true;
                    break;
                case "--actionable":
                    unusedActionable = true;
                    break;
                case "--include-generated":
                case "--generated":
                    includeGenerated = true;
                    break;
                case "--since":
                    if (!TryReadStringOptionValue(args, ref i, "--since", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sinceValue, out var sinceError))
                        AddParseError(sinceError!);
                    else if (TryParseIso8601Since(sinceValue!, out var parsedSince))
                    {
                        WarnIfDuplicateSingleValueOption("--since", sinceValue!);
                        since = parsedSince;
                    }
                    else
                        AddParseError($"Error: could not parse --since value '{ConsoleUi.FormatBoundedValue(sinceValue)}' as a date/time. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).");
                    break;
                case "--line":
                    if (!TryReadRawOptionValue(args, ref i, "--line", inlineValue, out var lineValue, out var missingLineError))
                        AddParseError(missingLineError!);
                    else if (TryParsePositiveInt(lineValue!, "--line", out var parsedLine, out var lineError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", lineValue!);
                        startLine = parsedLine;
                        if (!endLineExplicit)
                            endLine = parsedLine;
                    }
                    else
                        AddParseError(lineError!);
                    break;
                case "--start":
                case "--start-line":
                    var startFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, startFlag, inlineValue, out var startValue, out var missingStartError))
                        AddParseError(missingStartError!);
                    else if (TryParsePositiveInt(startValue!, startFlag, out var parsedStart, out var startError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", startValue!);
                        startLine = parsedStart;
                    }
                    else
                        AddParseError(startError!);
                    break;
                case "--end":
                case "--end-line":
                    var endFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, endFlag, inlineValue, out var endValue, out var missingEndError))
                        AddParseError(missingEndError!);
                    else if (TryParsePositiveInt(endValue!, endFlag, out var parsedEnd, out var endError))
                    {
                        WarnIfDuplicateSingleValueOption("--end", endValue!);
                        endLine = parsedEnd;
                        endLineExplicit = true;
                    }
                    else
                        AddParseError(endError!);
                    break;
                case "--context":
                    if (!TryReadRawOptionValue(args, ref i, "--context", inlineValue, out var contextValue, out var missingContextError))
                        AddParseError(missingContextError!);
                    else if (TryParseNonNegativeInt(contextValue!, "--context", out var parsedContext, out var contextError))
                    {
                        WarnIfDuplicateSingleValueOption("--context", contextValue!);
                        contextBefore = parsedContext;
                        contextAfter = parsedContext;
                        contextAfterExplicit = true;
                        symmetricContext = parsedContext;
                    }
                    else
                        AddParseError(contextError!);
                    break;
                case "--before":
                    if (!TryReadRawOptionValue(args, ref i, "--before", inlineValue, out var beforeValue, out var missingBeforeError))
                        AddParseError(missingBeforeError!);
                    else if (TryParseNonNegativeInt(beforeValue!, "--before", out var parsedBefore, out var beforeError))
                    {
                        WarnIfDuplicateSingleValueOption("--before", beforeValue!);
                        contextBefore = parsedBefore;
                        explicitContextBefore = parsedBefore;
                    }
                    else
                        AddParseError(beforeError!);
                    break;
                case "--after":
                    if (!TryReadRawOptionValue(args, ref i, "--after", inlineValue, out var afterValue, out var missingAfterError))
                        AddParseError(missingAfterError!);
                    else if (TryParseNonNegativeInt(afterValue!, "--after", out var parsedAfter, out var afterError))
                    {
                        WarnIfDuplicateSingleValueOption("--after", afterValue!);
                        contextAfter = parsedAfter;
                        explicitContextAfter = parsedAfter;
                    }
                    else
                        AddParseError(afterError!);
                    break;
                case "--focus-line":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-line", inlineValue, out var focusLineValue, out var missingFocusLineError))
                        AddParseError(missingFocusLineError!);
                    else if (TryParsePositiveInt(focusLineValue!, "--focus-line", out var parsedFocusLine, out var focusLineError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-line", focusLineValue!);
                        focusLine = parsedFocusLine;
                    }
                    else
                        AddParseError(focusLineError!);
                    break;
                case "--focus-column":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-column", inlineValue, out var focusColumnValue, out var missingFocusColumnError))
                        AddParseError(missingFocusColumnError!);
                    else if (TryParsePositiveInt(focusColumnValue!, "--focus-column", out var parsedFocusColumn, out var focusColumnError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-column", focusColumnValue!);
                        focusColumn = parsedFocusColumn;
                    }
                    else
                        AddParseError(focusColumnError!);
                    break;
                case "--focus-length":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-length", inlineValue, out var focusLengthValue, out var missingFocusLengthError))
                        AddParseError(missingFocusLengthError!);
                    else if (TryParsePositiveInt(focusLengthValue!, "--focus-length", out var parsedFocusLength, out var focusLengthError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-length", focusLengthValue!);
                        focusLength = parsedFocusLength;
                    }
                    else
                        AddParseError(focusLengthError!);
                    break;
                case "--name":
                    if (TryReadStringOptionValue(args, ref i, "--name", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var extraName, out var nameError))
                        extraNames.Add(extraName!); // Repeatable; OR-joined with other --name values and extra positional names / 繰り返し可、他の --name や追加の positional 引数と OR 結合
                    else
                        AddParseError($"{nameError} / --name には値（シンボル名パターン）が必要です。");
                    break;
                case "--snippet-lines":
                    if (!TryReadRawOptionValue(args, ref i, "--snippet-lines", inlineValue, out var snippetLinesValue, out var missingSnippetLinesError))
                        AddParseError(missingSnippetLinesError!);
                    else if (TryParseNonNegativeInt(snippetLinesValue!, "--snippet-lines", out var parsedSnippetLines, out var snippetLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-lines", snippetLinesValue!);
                        snippetLines = parsedSnippetLines;
                        snippetLinesExplicit = true;
                    }
                    else
                        AddParseError(snippetLinesError!);
                    break;
                case "--snippet-focus":
                    if (!TryReadStringOptionValue(args, ref i, "--snippet-focus", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var snippetFocusValue, out var snippetFocusError))
                    {
                        AddParseError(snippetFocusError!);
                    }
                    else if (TryParseSnippetFocusMode(snippetFocusValue!, out var parsedSnippetFocus))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-focus", snippetFocusValue!);
                        snippetFocus = parsedSnippetFocus;
                    }
                    else
                    {
                        AddParseError($"Error: invalid --snippet-focus value '{ConsoleUi.FormatBoundedValue(snippetFocusValue)}'. Use leftmost, quality, or proximity.");
                    }
                    break;
                case "--max-line-width":
                    if (!TryReadRawOptionValue(args, ref i, "--max-line-width", inlineValue, out var maxLineWidthValue, out var missingMaxLineWidthError))
                        AddParseError(missingMaxLineWidthError!);
                    else if (TryParseNonNegativeInt(maxLineWidthValue!, "--max-line-width", out var parsedMaxLineWidth, out var maxLineWidthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-line-width", maxLineWidthValue!);
                        maxLineWidth = parsedMaxLineWidth;
                        maxLineWidthExplicit = true;
                    }
                    else
                        AddParseError(maxLineWidthError!);
                    break;
                default:
                    return false;
            }

            return true;
        }
    }
}
