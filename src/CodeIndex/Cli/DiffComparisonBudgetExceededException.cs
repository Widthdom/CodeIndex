using System.Globalization;
using System.Text.Json.Serialization;

namespace CodeIndex.Cli;

// Only fixed comparison identities and numeric counters cross this diagnostic boundary.
internal sealed class DiffComparisonBudgetExceededException : InvalidOperationException
{
    internal DiffComparisonBudgetExceededException(string side, string table, string kind, long limit, long observed)
        : this(new DiffComparisonBudgetResult(
            side is "left" or "right" ? side : "unknown",
            side is "left" or "right" ? side : "unknown",
            table is "files" or "symbols" or "symbol_references" or "chunks" or "reference_lines" or "file_issues" or "codeindex_meta" ? table : "unknown",
            kind is "rows_per_table_per_side" or "row_bytes" ? kind : "unknown",
            limit, observed))
    {
    }

    private DiffComparisonBudgetExceededException(DiffComparisonBudgetResult budget)
        : base(FormattableString.Invariant($"diff {budget.Side} row comparison exceeded the safety budget of {budget.Limit} {(budget.Kind == "row_bytes" ? "bytes per row" : "rows")} (table={budget.Table}, kind={budget.Kind}, observed_at_least={budget.Observed})."))
        => Budget = budget;

    private DiffComparisonBudgetResult Budget { get; }

    internal DiffComparisonBudgetResult ToResult(bool import)
        => Budget with { Role = import ? Budget.Side switch { "left" => "destination", "right" => "archive", _ => "unknown" } : Budget.Side };

    internal string GetRecoveryHint(bool import)
    {
        var role = ToResult(import).Role;
        var prefix = import ? "the destination was left unchanged; " : "both databases were left unchanged; ";
        var constraint = $"{role} table {Budget.Table} exceeds {Budget.Kind}={Budget.Limit.ToString(CultureInfo.InvariantCulture)}. ";
        var fixedBudget = "Changing --limit, --offset, or --max-json-bytes does not change comparison safety budgets. ";
        if (import && Budget.Side == "left")
            return prefix + constraint + "Comparison against the current destination cannot complete under the existing budget. Shrinking the incoming archive cannot reduce this destination constraint. "
                + fixedBudget + "If a reduced scope is acceptable, compare a separately prepared smaller destination snapshot; that does not validate replacement of the current destination.";
        if (Budget.Table is "codeindex_meta" or "unknown")
            return prefix + constraint + "File filters cannot reduce this metadata constraint. Comparison of this input cannot complete under the existing budget; a separately prepared input with smaller metadata is required. " + fixedBudget;
        var reduction = Budget.Kind == "row_bytes"
            ? "excludes the file contributing the oversized row"
            : "retains fewer rows in the reported table";
        return prefix + constraint + (import
            ? $"Re-export from the archive source with a file scope that {reduction}, then retry the import check against the same destination. "
            : $"Compare a separately prepared snapshot of the {role} input with a file scope that {reduction}, if reduced scope is acceptable. ")
            + "Other comparison constraints may still prevent completion. " + fixedBudget;
    }
}

internal sealed record DiffComparisonBudgetResult(
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("limit")] long Limit,
    [property: JsonPropertyName("observed")] long Observed,
    [property: JsonPropertyName("observed_is_lower_bound")] bool ObservedIsLowerBound = true);
