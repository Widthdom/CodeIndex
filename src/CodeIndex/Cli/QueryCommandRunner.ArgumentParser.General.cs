using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private bool TryParseGeneralOption(string normalizedArg, string currentArg, string? inlineValue, string[] args, ref int i)
        {
            switch (normalizedArg)
            {
                case "--":
                    if (i + 1 >= args.Length)
                    {
                        AddParseError("Error: -- requires a following literal query.");
                    }
                    else if (query == null)
                    {
                        query = args[++i];
                    }
                    else
                    {
                        extraNames.Add(args[++i]);
                    }
                    break;
                case "--db":
                    if (TryReadStringOptionValue(args, ref i, "--db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dbPathValue, out var dbPathError))
                    {
                        WarnIfDuplicateSingleValueOption("--db", dbPathValue!);
                        dbPath = dbPathValue!;
                        dbPathExplicit = true;
                    }
                    else
                        AddParseError(dbPathError!);
                    break;
                case "--read-only":
                case "--immutable":
                    readOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--show-paths":
                    showPaths = true;
                    redactPaths = false;
                    break;
                case "--pretty":
                    break;
                case "--compact":
                    compact = true;
                    json = true;
                    outputFormat = OutputFormatJson;
                    break;
                case "--body-only":
                    includeBody = true;
                    inspectFields = ["definitions"];
                    json = true;
                    outputFormat = OutputFormatJson;
                    break;
                case "--outline-only":
                    inspectFields = ["file", "definitions", "nearby_symbols"];
                    json = true;
                    if (outputFormat == OutputFormatText)
                        outputFormat = OutputFormatJson;
                    break;
                case "--workspace-db":
                    if (TryReadStringOptionValue(args, ref i, "--workspace-db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var workspaceDbPath, out var workspaceDbError))
                        workspaceDbPaths.Add(workspaceDbPath!);
                    else
                        AddParseError(workspaceDbError!);
                    break;
                case "--data-dir":
                    if (TryReadStringOptionValue(args, ref i, "--data-dir", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dataDirValue, out var dataDirError))
                    {
                        WarnIfDuplicateSingleValueOption("--data-dir", dataDirValue!);
                        dataDir = dataDirValue!;
                    }
                    else
                        AddParseError(dataDirError!);
                    break;
                case "--redact-paths":
                    redactPaths = true;
                    showPaths = false;
                    break;
                case "--json":
                    if (inlineValue == null)
                    {
                        json = true;
                        if (outputFormat == OutputFormatText)
                            outputFormat = OutputFormatJson;
                    }
                    else if (TryParseJsonOutputFormat(inlineValue, out var parsedJsonOutputFormat))
                    {
                        json = true;
                        jsonOutputFormat = parsedJsonOutputFormat;
                        jsonOutputFormatExplicit = true;
                        if (outputFormat == OutputFormatText)
                            outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError($"Error: --json format must be one of ndjson or array, got '{ConsoleUi.FormatBoundedValue(inlineValue)}'. Hint: use `--json` or `--json=ndjson` for newline-delimited JSON, or `--json=array` for a single JSON array.");
                    }
                    break;
                case "--indexed-only":
                    languagesIndexedOnly = true;
                    break;
                case "--capability":
                    if (!TryReadStringOptionValue(args, ref i, "--capability", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var capabilityValue, out var capabilityError))
                    {
                        AddParseError(capabilityError!);
                    }
                    else if (TryNormalizeLanguageCapability(capabilityValue!, out var capability))
                    {
                        languageCapabilities.Add(capability);
                    }
                    else
                    {
                        AddParseError($"Error: unsupported --capability value '{ConsoleUi.FormatBoundedValue(capabilityValue)}'. Use all, none, graph, references, symbols, missing-any, missing-graph, missing-references, missing-symbols, or search-only.");
                    }
                    break;
                case "--language":
                    if (TryReadStringOptionValue(args, ref i, "--language", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageValue, out var languageError))
                    {
                        languageLookups.Add(languageValue!);
                        lang = NormalizeLangFilterValue(languageValue);
                    }
                    else
                    {
                        AddParseError(languageError!);
                    }
                    break;
                case "--extension":
                    if (TryReadStringOptionValue(args, ref i, "--extension", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageExtensionValue, out var languageExtensionError))
                        languageExtensionLookups.Add(languageExtensionValue!);
                    else
                        AddParseError(languageExtensionError!);
                    break;
                case "--alias":
                    if (TryReadStringOptionValue(args, ref i, "--alias", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageAliasValue, out var languageAliasError))
                        languageAliasLookups.Add(languageAliasValue!);
                    else
                        AddParseError(languageAliasError!);
                    break;
                case "--format":
                    if (TryReadStringOptionValue(args, ref i, "--format", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var formatValue, out var formatError))
                    {
                        WarnIfDuplicateSingleValueOption("--format", formatValue!);
                        if (TryParseOutputFormat(formatValue!, out var parsedOutputFormat))
                        {
                            outputFormat = parsedOutputFormat;
                            explicitOutputFormat = parsedOutputFormat;
                            outputFormatExplicit = true;
                            if (parsedOutputFormat == OutputFormatCompact)
                                compact = true;
                            outputFormatImpliesStructuredOutput =
                                parsedOutputFormat != OutputFormatText &&
                                parsedOutputFormat != OutputFormatDot &&
                                parsedOutputFormat != OutputFormatGraphMl;
                        }
                        else if (allowIssueDraftsFormat && string.Equals(formatValue, OutputFormatIssueDrafts, StringComparison.OrdinalIgnoreCase))
                        {
                            outputFormat = OutputFormatIssueDrafts;
                            explicitOutputFormat = OutputFormatIssueDrafts;
                            outputFormatExplicit = true;
                            outputFormatImpliesStructuredOutput = true;
                        }
                        else
                        {
                            var allowedFormats = allowIssueDraftsFormat
                                ? "text, json, count, compact, csv, tsv, lsp, qf, sarif, or issue-drafts"
                                : "text, json, count, compact, csv, tsv, lsp, qf, or sarif";
                            AddParseError($"Error: --format must be one of {allowedFormats}; got '{ConsoleUi.FormatBoundedValue(formatValue)}'.");
                        }
                    }
                    else
                    {
                        AddParseError(formatError!);
                    }
                    break;
                case "--limit":
                case "--max-results":
                case "--top":
                    var limitOptionName = normalizedArg == "--top" ? "--limit" : normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, limitOptionName, inlineValue, out var limitValue, out var missingLimitError))
                        AddParseError(missingLimitError!);
                    else if (TryParsePositiveInt(limitValue!, limitOptionName, out var parsedLimit, out var limitError))
                    {
                        WarnIfDuplicateSingleValueOption("--limit", limitValue!);
                        limit = parsedLimit;
                        limitExplicit = true;
                    }
                    else
                        AddParseError(limitError!);
                    break;
                case "--graph-budget":
                    if (!TryReadRawOptionValue(args, ref i, "--graph-budget", inlineValue, out var graphBudgetValue, out var missingGraphBudgetError))
                        AddParseError(missingGraphBudgetError!);
                    else if (TryParsePositiveInt(graphBudgetValue!, "--graph-budget", out var parsedGraphBudget, out var graphBudgetError))
                    {
                        WarnIfDuplicateSingleValueOption("--graph-budget", graphBudgetValue!);
                        dependencyCycleGraphBudget = parsedGraphBudget;
                    }
                    else
                        AddParseError(graphBudgetError!);
                    break;
                case "--total-limit":
                    if (!TryReadRawOptionValue(args, ref i, "--total-limit", inlineValue, out var totalLimitValue, out var missingTotalLimitError))
                        AddParseError(missingTotalLimitError!);
                    else if (TryParseNonNegativeInt(totalLimitValue!, "--total-limit", out var parsedTotalLimit, out var totalLimitError))
                    {
                        WarnIfDuplicateSingleValueOption("--total-limit", totalLimitValue!);
                        totalLimit = parsedTotalLimit;
                    }
                    else
                        AddParseError(totalLimitError!);
                    break;
                default:
                    return false;
            }

            return true;
        }
    }
}
