using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static class SymbolSearchRowProjector
    {
        private const int DefinitionSitesIndex = 22;
        private const int UngroupedIdentifierStartColumnIndex = 36;
        private const int GroupedIdentifierStartColumnIndex = 31;

        public static List<SymbolResult> ReadAll(
            SqliteDataReader reader,
            SymbolSearchQueryPlan plan)
        {
            var results = new List<SymbolResult>();
            var includeRankingMetadata = plan.SortMode != SymbolSortMode.Name;
            var sortModeName = includeRankingMetadata
                ? plan.SortMode.ToString().ToLowerInvariant()
                : null;
            while (reader.TrackedRead())
                results.Add(Read(reader, plan, includeRankingMetadata, sortModeName));
            return results;
        }

        private static SymbolResult Read(
            SqliteDataReader reader,
            SymbolSearchQueryPlan plan,
            bool includeRankingMetadata,
            string? sortModeName)
        {
            var definitionSites = Convert.ToInt32(reader.GetInt64(DefinitionSitesIndex));
            var identifierColumn = plan.GroupPartials
                ? GroupedIdentifierStartColumnIndex
                : UngroupedIdentifierStartColumnIndex;
            var result = new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                Kind = reader.GetString(2),
                SubKind = GetNullableString(reader, 3),
                Name = reader.GetString(4),
                Line = reader.GetInt32(5),
                StartLine = GetInt32OrFallback(reader, 6, 5),
                StartColumn = GetNullableInt32(reader, identifierColumn)
                    ?? ResolveSymbolIdentifierStartColumn(
                        GetNullableInt32(reader, 7),
                        GetNullableString(reader, 11),
                        reader.GetString(4),
                        reader.GetString(2)),
                EndLine = GetInt32OrFallback(reader, 8, 5),
                BodyStartLine = GetNullableInt32(reader, 9),
                BodyEndLine = GetNullableInt32(reader, 10),
                Signature = GetNullableString(reader, 11),
                ContainerKind = GetNullableString(reader, 12),
                ContainerName = GetNullableString(reader, 13),
                ContainerQualifiedName = GetNullableString(reader, 25),
                LogicalPartialKey = GetNullableString(reader, 26),
                Visibility = GetNullableString(reader, 14),
                ReturnType = GetNullableString(reader, 15),
                SortMode = sortModeName,
                ReferenceCount = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(16)) : null,
                HotspotScore = includeRankingMetadata ? Math.Round(reader.GetDouble(17), 3) : null,
                RankingReferenceScore = includeRankingMetadata ? Math.Round(reader.GetDouble(18), 3) : null,
                RankingHotspotScore = includeRankingMetadata ? Math.Round(reader.GetDouble(19), 3) : null,
                GenericNamePenalty = includeRankingMetadata ? Math.Round(reader.GetDouble(20), 3) : null,
                StructuralRankPenalty = includeRankingMetadata ? Math.Round(reader.GetDouble(21), 3) : null,
                DefinitionSites = includeRankingMetadata || (plan.GroupPartials && definitionSites > 1) ? definitionSites : null,
                SizeLines = includeRankingMetadata ? Convert.ToInt32(reader.GetInt64(23)) : null,
                ComplexityScore = includeRankingMetadata ? Math.Round(reader.GetDouble(24), 3) : null,
                SymbolId = reader.GetInt64(27),
            };
            AddPartialFamily(reader, plan, definitionSites, result);
            return result;
        }

        private static void AddPartialFamily(
            SqliteDataReader reader,
            SymbolSearchQueryPlan plan,
            int definitionSites,
            SymbolResult result)
        {
            if (!plan.GroupPartials || definitionSites <= 1)
                return;

            result.PartialFamilyId =
                LogicalPartialSymbolGrouper.BuildPartialFamilyId(result.LogicalPartialKey!);
            result.RepresentativeReason = reader.GetString(28);
            result.FamilyMembers = ReadPartialFamilyMembers(reader.GetString(29), result);
            result.FamilyMembersTruncated = reader.GetInt64(30) != 0;
        }
    }

    private static List<PartialFamilyMember> ReadPartialFamilyMembers(string json, SymbolResult representative)
    {
        using var document = JsonDocument.Parse(json);
        var members = new List<PartialFamilyMember>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var symbolId = element.GetProperty("symbol_id").GetInt64();
            var path = element.GetProperty("path").GetString() ?? string.Empty;
            var startLine = element.GetProperty("start_line").GetInt32();
            var rawStartColumn = element.GetProperty("start_column").ValueKind == JsonValueKind.Null
                ? (int?)null
                : element.GetProperty("start_column").GetInt32();
            var memberName = element.GetProperty("name").GetString() ?? representative.Name;
            var memberSignature = element.GetProperty("signature").ValueKind == JsonValueKind.Null
                ? null
                : element.GetProperty("signature").GetString();
            var identifierStartColumn = element.TryGetProperty("identifier_start_column", out var identifierColumnElement)
                && identifierColumnElement.ValueKind != JsonValueKind.Null
                    ? identifierColumnElement.GetInt32()
                    : (int?)null;
            members.Add(new PartialFamilyMember
            {
                SymbolId = symbolId,
                Path = path,
                Line = element.GetProperty("line").GetInt32(),
                StartLine = startLine,
                StartColumn = identifierStartColumn
                    ?? ResolveSymbolIdentifierStartColumn(
                        rawStartColumn,
                        memberSignature,
                        memberName,
                        representative.Kind),
                EndLine = element.GetProperty("end_line").GetInt32(),
                Generated = element.GetProperty("generated").GetInt32() != 0,
                Representative = representative.SymbolId == symbolId
                    || (representative.SymbolId == null
                        && string.Equals(representative.Path, path, StringComparison.Ordinal)
                        && representative.StartLine == startLine),
            });
        }
        return members;
    }

    private static int? ResolveSymbolIdentifierStartColumn(
        int? declarationStartColumn,
        string? signature,
        string name,
        string kind)
    {
        if (!declarationStartColumn.HasValue || string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(name))
            return declarationStartColumn;

        var firstLineEnd = signature.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd >= 0 ? signature[..firstLineEnd] : signature;
        var callable = kind is "function" or "test.method";
        var relativeColumn = callable
            ? LogicalPartialSymbolGrouper.FindCallableNameOffset(firstLine, name)
            : firstLine.IndexOf(name, StringComparison.Ordinal);
        if (relativeColumn < 0 && callable)
            relativeColumn = firstLine.IndexOf(name, StringComparison.Ordinal);
        return relativeColumn >= 0 ? declarationStartColumn.Value + relativeColumn : declarationStartColumn;
    }
}
