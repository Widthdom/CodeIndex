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
            var currentPayload = fields.Length == 7;
            var referenceCountField = currentPayload ? 6 : 4;
            if (fields.Length is not (5 or 7) || !int.TryParse(fields[referenceCountField], out var referenceCount))
                continue;

            evidence.Add(new FileDependencyEvidence
            {
                SourceLanguage = fields[0],
                Origin = fields[1],
                ResolutionState = currentPayload ? fields[2] : "unavailable",
                ReferenceKind = fields[currentPayload ? 3 : 2],
                TargetKind = fields[currentPayload ? 4 : 3],
                ReferenceCount = referenceCount,
                SuppressionReason = currentPayload && fields[5].Length > 0 ? fields[5] : null,
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
