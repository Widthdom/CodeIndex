namespace CodeIndex.Database;

/// <summary>Opt-in indexed regex windows / 明示指定の索引正規表現ウィンドウ。</summary>
public sealed record FindWindowOptions(int Lines = 8, int Bytes = 65_536)
{
    internal FindWindowBudget? Budget { get; init; }
    public const int MaxLines = 64;
    public const int MaxBytes = 262_144;
    internal const int ChunkBytes = 1_048_576;
    internal const int QueryBytes = 32 * 1_048_576;
    internal const int QueryLines = 250_000;
    internal const int QueryMilliseconds = 5_000;
    internal const int MaxResults = 200;

    internal string? Validate(bool regex, FindSemanticFilters? filters, int? focusLine, int? focusColumn)
        => !regex ? "Multiline find requires --regex (MCP: regex=true)."
        : Lines is < 1 or > MaxLines ? $"window-lines/windowLines must be between 1 and {MaxLines}."
        : Bytes is < 1 or > MaxBytes ? $"window-bytes/windowBytes must be between 1 and {MaxBytes}."
        : filters is not null ? "Multiline find does not support semantic origin/result-kind filters or origin-passes; remove them to search complete textual spans."
        : focusLine.HasValue || focusColumn.HasValue ? "Multiline find does not support focus-line/focus-column; narrow the path or pattern instead."
        : null;
}

internal sealed class FindWindowBudget
{
    internal long BytesRead { get; set; }
    internal long BytesMatched { get; set; }
    internal int Lines { get; set; }
    internal System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
}
