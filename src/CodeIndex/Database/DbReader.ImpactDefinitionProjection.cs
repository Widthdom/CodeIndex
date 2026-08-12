using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private readonly record struct ImpactDefinitionStats(
        int PhysicalCount,
        int PhysicalFileCount,
        int LogicalCount,
        int PreciseDefinitionCount,
        int PreciseLogicalDefinitionCount,
        int PreciseDefinitionFileCount,
        int NonCallableDefinitionCount);

    private sealed record ImpactDefinitionProjection(
        List<SymbolResult> Definitions,
        ImpactDefinitionStats Stats,
        SymbolResult? SinglePreciseDefinition);

    private static class ImpactDefinitionRowProjector
    {
        private static class Column
        {
            public const int Path = 0;
            public const int Lang = 1;
            public const int Kind = 2;
            public const int Name = 3;
            public const int Line = 4;
            public const int StartLine = 5;
            public const int StartColumn = 6;
            public const int EndLine = 7;
            public const int BodyStartLine = 8;
            public const int BodyEndLine = 9;
            public const int Signature = 10;
            public const int ContainerKind = 11;
            public const int ContainerName = 12;
            public const int Visibility = 13;
            public const int ReturnType = 14;
            public const int ContainerQualifiedName = 15;
            public const int LogicalPartialKey = 16;
            public const int SymbolId = 17;
            public const int DefinitionSites = 18;
            public const int RequestedRow = 19;
            public const int PhysicalCount = 20;
            public const int PhysicalFileCount = 21;
            public const int LogicalCount = 22;
            public const int PreciseCount = 23;
            public const int PreciseFileCount = 24;
            public const int NonCallableCount = 25;
            public const int RepresentativeReason = 26;
            public const int FamilyMembersJson = 27;
            public const int FamilyMembersTruncated = 28;
            public const int IdentifierStartColumn = 29;
            public const int PreciseLogicalCount = 30;
        }

        public static ImpactDefinitionProjection Read(SqliteCommand cmd)
        {
            var definitions = new List<SymbolResult>();
            var stats = EmptyStats;
            SymbolResult? preciseDefinition = null;
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var row = ReadRow(reader);
                stats = ReadStats(reader);
                if (IsPreciseImpactFallbackKind(row.Result.Kind))
                    preciseDefinition ??= row.Result;
                if (row.Requested)
                    definitions.Add(row.Result);
            }

            return new ImpactDefinitionProjection(
                definitions,
                stats,
                preciseDefinition);
        }

        private static readonly ImpactDefinitionStats EmptyStats =
            new(0, 0, 0, 0, 0, 0, 0);

        private static (SymbolResult Result, bool Requested) ReadRow(
            SqliteDataReader reader)
        {
            var definitionSites = reader.GetInt32(Column.DefinitionSites);
            var result = new SymbolResult
            {
                Path = reader.GetString(Column.Path),
                Lang = reader.GetString(Column.Lang),
                Kind = reader.GetString(Column.Kind),
                Name = reader.GetString(Column.Name),
                Line = reader.GetInt32(Column.Line),
                StartLine = ReadStartLine(reader),
                StartColumn = ReadStartColumn(reader),
                EndLine = ReadEndLine(reader),
                BodyStartLine = GetNullableInt32(reader, Column.BodyStartLine),
                BodyEndLine = GetNullableInt32(reader, Column.BodyEndLine),
                Signature = GetNullableString(reader, Column.Signature),
                ContainerKind = GetNullableString(reader, Column.ContainerKind),
                ContainerName = GetNullableString(reader, Column.ContainerName),
                ContainerQualifiedName = GetNullableString(
                    reader,
                    Column.ContainerQualifiedName),
                LogicalPartialKey = GetNullableString(reader, Column.LogicalPartialKey),
                Visibility = GetNullableString(reader, Column.Visibility),
                ReturnType = GetNullableString(reader, Column.ReturnType),
                SymbolId = reader.GetInt64(Column.SymbolId),
                DefinitionSites = definitionSites > 1 ? definitionSites : null,
            };
            AddFamily(reader, definitionSites, result);
            return (result, reader.GetInt32(Column.RequestedRow) == 1);
        }

        private static int ReadStartLine(SqliteDataReader reader)
            => GetInt32OrFallback(reader, Column.StartLine, Column.Line);

        private static int ReadEndLine(SqliteDataReader reader)
            => GetInt32OrFallback(reader, Column.EndLine, Column.Line);

        private static int? ReadStartColumn(SqliteDataReader reader)
        {
            return GetNullableInt32(reader, Column.IdentifierStartColumn)
                ?? ResolveSymbolIdentifierStartColumn(
                    GetNullableInt32(reader, Column.StartColumn),
                    GetNullableString(reader, Column.Signature),
                    reader.GetString(Column.Name),
                    reader.GetString(Column.Kind));
        }

        private static void AddFamily(
            SqliteDataReader reader,
            int definitionSites,
            SymbolResult result)
        {
            if (definitionSites <= 1)
                return;

            result.PartialFamilyId =
                LogicalPartialSymbolGrouper.BuildPartialFamilyId(result.LogicalPartialKey!);
            result.RepresentativeReason = reader.GetString(Column.RepresentativeReason);
            result.FamilyMembers = ReadPartialFamilyMembers(
                reader.GetString(Column.FamilyMembersJson),
                result);
            result.FamilyMembersTruncated =
                reader.GetInt64(Column.FamilyMembersTruncated) != 0;
        }

        private static ImpactDefinitionStats ReadStats(SqliteDataReader reader)
        {
            return new ImpactDefinitionStats(
                reader.GetInt32(Column.PhysicalCount),
                reader.GetInt32(Column.PhysicalFileCount),
                reader.GetInt32(Column.LogicalCount),
                reader.GetInt32(Column.PreciseCount),
                reader.GetInt32(Column.PreciseLogicalCount),
                reader.GetInt32(Column.PreciseFileCount),
                reader.GetInt32(Column.NonCallableCount));
        }
    }
}
