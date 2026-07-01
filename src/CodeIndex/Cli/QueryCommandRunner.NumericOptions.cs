using System.Globalization;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    // Per-flag upper bounds for numeric CLI options. Without a cap, `--limit 2147483647` or
    // `--snippet-lines 999999` previously parsed silently and either ran with the absurd value
    // (huge allocations / output) or got quietly clamped (e.g. snippet-lines down to 20 with no
    // signal), hiding typos from users. Each cap below is the documented user-facing maximum.
    // 数値 CLI フラグごとの上限値。上限が無いと `--limit 2147483647` や
    // `--snippet-lines 999999` が黙って通り、巨大確保/出力をそのまま走らせるか silent に clamp
    // されてユーザーのタイポを隠していた。下の値は各フラグのドキュメント上の最大値。
    internal static readonly IReadOnlyDictionary<string, int> NumericFlagUpperBounds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["--limit"] = MaxQueryResultLimit,
            ["--max-results"] = MaxQueryResultLimit,
            ["--snippet-lines"] = SearchSnippetFormatter.MaxSnippetLines,
            ["--max-line-width"] = LineWidthFormatter.MaxAllowedLineWidth,
            ["--slow-query-ms"] = 3_600_000,
            ["--body-start"] = 10_000_000,
            ["--body-lines"] = DbReader.DefinitionBodyMaxRequestedLines,
            ["--body-line-count"] = DbReader.DefinitionBodyMaxRequestedLines,
            ["--max-hops"] = 64,
            ["--depth"] = 64,
            ["--before"] = 1_000,
            ["--after"] = 1_000,
            ["--start"] = 10_000_000,
            ["--end"] = 10_000_000,
            ["--focus-line"] = 10_000_000,
            ["--focus-column"] = 100_000,
            ["--focus-length"] = 100_000,
        };

    private static int ResolveDefaultPositiveInt(string environmentVariable, int fallback, string optionName, out string? error)
    {
        var raw = CdidxEnvironment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return fallback;
        }

        if (TryParsePositiveInt(raw, optionName, out var value, out var parseError, ConsoleUi.FormatBoundedValue(raw)))
        {
            error = null;
            return value;
        }

        error = parseError!.Replace(optionName, environmentVariable, StringComparison.Ordinal);
        return fallback;
    }

    private static int ResolveDefaultNonNegativeInt(string environmentVariable, int fallback, string optionName, out string? error)
    {
        var raw = CdidxEnvironment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = null;
            return fallback;
        }

        if (TryParseNonNegativeInt(raw, optionName, out var value, out var parseError, ConsoleUi.FormatBoundedValue(raw)))
        {
            error = null;
            return value;
        }

        error = parseError!.Replace(optionName, environmentVariable, StringComparison.Ordinal);
        return fallback;
    }

    private static bool TryParsePositiveInt(string rawValue, string optionName, out int value, out string? error, string? displayRawValue = null)
    {
        if (string.Equals(optionName, "--max-line-width", StringComparison.Ordinal))
            return TryParseNonNegativeInt(rawValue, optionName, out value, out error, displayRawValue);

        displayRawValue ??= ConsoleUi.FormatBoundedValue(rawValue);
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            value = 0;
            error = BuildPositiveIntegerError(optionName, displayRawValue);
            return false;
        }

        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && value > maxAllowed)
        {
            error = BuildPositiveIntegerUpperBoundError(optionName, displayRawValue, maxAllowed);
            value = 0;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseNonNegativeInt(string rawValue, string optionName, out int value, out string? error, string? displayRawValue = null)
    {
        displayRawValue ??= ConsoleUi.FormatBoundedValue(rawValue);
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            value = 0;
            error = BuildNonNegativeIntegerError(optionName, displayRawValue);
            return false;
        }

        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed) && value > maxAllowed)
        {
            error = BuildNonNegativeIntegerUpperBoundError(optionName, displayRawValue, maxAllowed);
            value = 0;
            return false;
        }

        error = null;
        return true;
    }

    private static string BuildPositiveIntegerError(string optionName, string rawValue, string? displayOptionName = null)
    {
        displayOptionName ??= optionName;
        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed))
            return $"Error: {displayOptionName} requires an integer between 1 and {maxAllowed}, got '{rawValue}'. Hint: retry with `{displayOptionName} 1` or another value up to {maxAllowed}.";
        return $"Error: {displayOptionName} requires a positive integer, got '{rawValue}'. Hint: retry with `{displayOptionName} 1` or another positive integer.";
    }

    private static string BuildPositiveIntegerUpperBoundError(string optionName, string rawValue, int maxAllowed)
    {
        return $"Error: {optionName} must be less than or equal to {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} {maxAllowed}` or a smaller positive integer.";
    }

    private static string BuildNonNegativeIntegerError(string optionName, string rawValue)
    {
        if (NumericFlagUpperBounds.TryGetValue(optionName, out var maxAllowed))
            return $"Error: {optionName} requires an integer between 0 and {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} 0` or another value up to {maxAllowed}.";
        return $"Error: {optionName} requires a non-negative integer, got '{rawValue}'. Hint: retry with `{optionName} 0` or another non-negative integer.";
    }

    private static string BuildNonNegativeIntegerUpperBoundError(string optionName, string rawValue, int maxAllowed)
    {
        return $"Error: {optionName} must be less than or equal to {maxAllowed}, got '{rawValue}'. Hint: retry with `{optionName} {maxAllowed}` or a smaller non-negative integer.";
    }
}
