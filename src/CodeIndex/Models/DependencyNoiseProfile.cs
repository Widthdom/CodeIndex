namespace CodeIndex.Models;

internal static class DependencyNoiseProfile
{
    internal const double SymbolRankPenalty = 0.1;
    private const int RankingCandidateMinimum = 200;
    private const int RankingCandidateMultiplier = 50;
    private const int RankingCandidateMaximum = 5000;

    internal static readonly string[] SymbolNames =
    [
        "Array",
        "BoundedRegex",
        "CancellationToken",
        "Code",
        "Console",
        "Count",
        "DateTime",
        "Dictionary",
        "Directory",
        "Enumerable",
        "Escape",
        "File",
        "IEnumerable",
        "IsMatch",
        "Kind",
        "Length",
        "List",
        "Match",
        "Matches",
        "Math",
        "Name",
        "None",
        "Option",
        "Options",
        "Path",
        "Read",
        "ReadLine",
        "Record",
        "Regex",
        "Replace",
        "Result",
        "Results",
        "String",
        "StringBuilder",
        "Task",
        "Token",
        "Unescape",
        "Value",
        "Values",
        "Write",
        "WriteLine",
    ];

    internal static readonly HashSet<string> Symbols = new(SymbolNames, StringComparer.OrdinalIgnoreCase);

    internal static bool IsNoiseSymbol(string symbol) => Symbols.Contains(symbol);

    internal static int GetRankingCandidateLimit(int limit)
    {
        if (limit <= 0)
            return limit;

        var scaled = Math.Max(RankingCandidateMinimum, (long)limit * RankingCandidateMultiplier);
        var bounded = (int)Math.Min(RankingCandidateMaximum, scaled);
        return Math.Max(limit, bounded);
    }

    internal static double ComputeRankingScore(int referenceCount, string symbols)
    {
        var symbolNames = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (symbolNames.Length == 0)
            return referenceCount;

        var penaltySum = 0.0;
        foreach (var symbolName in symbolNames)
            penaltySum += IsNoiseSymbol(symbolName) ? SymbolRankPenalty : 1.0;

        return referenceCount * penaltySum / symbolNames.Length;
    }
}
