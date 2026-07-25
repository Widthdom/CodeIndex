using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private bool TryParseStatusOption(string normalizedArg, string currentArg, string? inlineValue, string[] args, ref int i)
        {
            switch (normalizedArg)
            {
                case "--check":
                    if (allowStatusCheck)
                    {
                        checkWorkspace = true;
                        statusCheckExplicit = true;
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --check is not supported by this command.");
                    }
                    break;
                case "--outline-fields":
                    if (TryReadStringOptionValue(args, ref i, "--outline-fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var outlineFieldsValue, out var outlineFieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--outline-fields", outlineFieldsValue!);
                        outlineFields = ParseOutlineProjectionFields(outlineFieldsValue!, AddParseError);
                        outlineFieldsExplicit = true;
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError(outlineFieldsError!);
                    }
                    break;
                case "--stale-after":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--stale-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var staleAfterValue, out var staleAfterError))
                        {
                            WarnIfDuplicateSingleValueOption("--stale-after", staleAfterValue!);
                            if (TryParseStaleAfter(staleAfterValue!, out var parsedStaleAfter, out var parseStaleAfterError))
                            {
                                staleAfter = parsedStaleAfter;
                                checkWorkspace = true;
                            }
                            else
                                AddParseError(parseStaleAfterError!);
                        }
                        else
                        {
                            AddParseError(staleAfterError!);
                        }
                    }
                    else
                    {
                        AddParseError("Error: --stale-after is not supported by this command.");
                    }
                    break;
                case "--explain":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--explain", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var explainValue, out var explainError))
                        {
                            WarnIfDuplicateSingleValueOption("--explain", explainValue!);
                            statusExplainField = explainValue;
                        }
                        else
                            AddParseError(explainError!);
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --explain is not supported by this command.");
                    }
                    break;
                case "--log-path":
                    if (allowStatusCheck)
                    {
                        statusLogPath = true;
                    }
                    else
                    {
                        AddParseError("Error: --log-path is not supported by this command.");
                    }
                    break;
                case "--config":
                    if (allowStatusCheck)
                    {
                        statusConfig = true;
                    }
                    else
                    {
                        AddParseError("Error: --config is only supported by status.");
                    }
                    break;
                case "--log-format":
                case "--log-retain-count":
                case "--log-max-size-mb":
                    if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError($"Error: unsupported option: {ConsoleUi.FormatBoundedValue(currentArg)}. Use `--` before a query literal that starts with `-`.");
                    }
                    break;
                default:
                    return false;
            }

            return true;
        }
    }
}
