using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static FileDependencyResult ProjectDependencyRow(SqliteDataReader reader)
    {
        var symbolSamples = ParseDependencySymbols(reader.GetString(3));
        return new FileDependencyResult
        {
            SourcePath = reader.GetString(0),
            TargetPath = reader.GetString(1),
            ReferenceCount = reader.GetInt32(2),
            SymbolSamples = symbolSamples,
            Symbols = string.Join(",", symbolSamples),
            Evidence = ParseDependencyEvidence(reader.GetString(4)),
        };
    }

    internal static List<FileDependencyEvidence> ParseDependencyEvidence(string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return [];

        var evidence = new List<FileDependencyEvidence>();
        foreach (var item in payload.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = item.Split('\u001f');
            if (fields.Length != 5 || !int.TryParse(fields[4], out var referenceCount))
                continue;

            evidence.Add(new FileDependencyEvidence
            {
                SourceLanguage = fields[0],
                Origin = fields[1],
                ReferenceKind = fields[2],
                TargetKind = fields[3],
                ReferenceCount = referenceCount,
            });
        }

        return evidence;
    }

    internal static List<string> ParseDependencySymbols(string payload)
        => string.IsNullOrEmpty(payload)
            ? []
            : payload.Split('\u001f', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<FileDependencyResult> RankDependencyResults(
        List<FileDependencyResult> results,
        int limit,
        bool suppressDependencyNoise)
    {
        foreach (var result in results)
        {
            var rankingReferenceCount = suppressDependencyNoise && result.Evidence is { Count: > 0 }
                ? result.Evidence
                    .Where(static evidence => evidence.Origin != "markdown_heading_name_match")
                    .Sum(static evidence => evidence.ReferenceCount)
                : result.ReferenceCount;
            result.RankingScore = result.SymbolSamples is { } symbolSamples
                ? DependencyNoiseProfile.ComputeRankingScore(rankingReferenceCount, symbolSamples)
                : DependencyNoiseProfile.ComputeRankingScore(rankingReferenceCount, result.Symbols);
        }

        return results
            .OrderByDescending(result => result.RankingScore)
            .ThenByDescending(result => result.ReferenceCount)
            .ThenBy(result => result.SourcePath, StringComparer.Ordinal)
            .ThenBy(result => result.TargetPath, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}
