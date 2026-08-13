namespace CodeIndex.Database;

public partial class DbReader
{
    private readonly record struct ImpactPhysicalFamilyMember(long SymbolId, string Path);

    private sealed record ImpactDefinitionFamilyResolution(
        HashSet<long> PhysicalSymbolIds,
        HashSet<string> PhysicalDefinitionPaths,
        bool Truncated);

    private static class ImpactDefinitionFamilyResolver
    {
        private sealed class Accumulator(
            int budget,
            IEqualityComparer<string> pathComparer)
        {
            public HashSet<long> SymbolIds { get; } = [];
            public HashSet<string> DefinitionPaths { get; } = new(pathComparer);
            public bool Truncated { get; private set; }

            public void Add(long symbolId, string path)
            {
                if (SymbolIds.Contains(symbolId))
                    return;
                if (SymbolIds.Count >= budget)
                {
                    Truncated = true;
                    return;
                }

                SymbolIds.Add(symbolId);
                DefinitionPaths.Add(path);
            }

            public void MarkTruncated() => Truncated = true;
        }

        public static ImpactDefinitionFamilyResolution Resolve(
            DbReader reader,
            ImpactDefinitionQueryPlan plan,
            IReadOnlyList<SymbolResult> definitions)
        {
            var accumulator = new Accumulator(
                Math.Max(1, reader.ImpactPartialFamilyMemberBudget),
                reader.GetIndexedPathComparer());
            foreach (var definition in definitions)
            {
                accumulator.Add(definition.SymbolId!.Value, definition.Path);
                if (accumulator.Truncated || definition.DefinitionSites is not > 1)
                    continue;

                if (!definition.FamilyMembersTruncated)
                {
                    AddProjectedMembers(accumulator, definition);
                    continue;
                }

                var (members, familyTruncated) = ResolvePhysicalMembers(
                    reader,
                    definition,
                    plan);
                foreach (var member in members)
                {
                    accumulator.Add(member.SymbolId, member.Path);
                    if (accumulator.Truncated)
                        break;
                }
                if (familyTruncated)
                    accumulator.MarkTruncated();
            }

            return new ImpactDefinitionFamilyResolution(
                accumulator.SymbolIds,
                accumulator.DefinitionPaths,
                accumulator.Truncated);
        }

        private static void AddProjectedMembers(
            Accumulator accumulator,
            SymbolResult definition)
        {
            foreach (var member in definition.FamilyMembers ?? [])
            {
                if (member.SymbolId is long memberSymbolId)
                    accumulator.Add(memberSymbolId, member.Path);
                if (accumulator.Truncated)
                    break;
            }
        }

        private static (List<ImpactPhysicalFamilyMember> Members, bool Truncated)
        ResolvePhysicalMembers(
            DbReader dbReader,
            SymbolResult definition,
            ImpactDefinitionQueryPlan plan)
        {
            using var cmd = dbReader._conn.CreateCommand();
            var supportedLangFilter = dbReader.BuildGraphSupportedLanguagePredicate(
                cmd,
                "f",
                "impactFamilyLang");
            var familyKindPredicate = definition.Kind is "function" or "test.method"
                ? "s.kind IN ('function', 'test.method')"
                : "s.kind = @familyKind";
            var sql = BuildSql(
                dbReader,
                plan.LogicalPartialKeySql,
                familyKindPredicate,
                supportedLangFilter,
                plan.Request);
            cmd.CommandText = sql;
            Bind(dbReader, cmd, definition, plan.Request);

            var familyMemberBudget = Math.Max(1, dbReader.ImpactPartialFamilyMemberBudget);
            var members = new List<ImpactPhysicalFamilyMember>();
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (members.Count >= familyMemberBudget)
                    return (members, true);
                members.Add(new ImpactPhysicalFamilyMember(
                    reader.GetInt64(0),
                    reader.GetString(1)));
            }
            return (members, false);
        }

        private static void Bind(
            DbReader reader,
            Microsoft.Data.Sqlite.SqliteCommand cmd,
            SymbolResult definition,
            ImpactDefinitionRequest request)
        {
            SqliteCommandPolicy.Add(cmd, "@familyLang", definition.Lang!);
            if (definition.Kind is not ("function" or "test.method"))
                SqliteCommandPolicy.Add(cmd, "@familyKind", definition.Kind);
            SqliteCommandPolicy.Add(cmd, "@familyName", definition.Name);
            SqliteCommandPolicy.Add(cmd, "@logicalPartialKey", definition.LogicalPartialKey!);
            if (request.Lang != null)
                SqliteCommandPolicy.Add(cmd, "@lang", request.Lang);
            SqliteCommandPolicy.Add(
                cmd,
                "@familyMemberLimit",
                Math.Max(1, reader.ImpactPartialFamilyMemberBudget) + 1);
            DbReader.AddPathFilterParameters(
                cmd,
                request.PathPatterns,
                request.ExcludePathPatterns);
        }

        private static string BuildSql(
            DbReader reader,
            string logicalPartialKeySql,
            string familyKindPredicate,
            string supportedLangFilter,
            ImpactDefinitionRequest request)
        {
            var sql = $@"
            SELECT s.id, f.path
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = @familyLang
              AND {familyKindPredicate}
              AND s.name = @familyName COLLATE BINARY
              AND ({logicalPartialKeySql}) = @logicalPartialKey
              AND {supportedLangFilter}";
            if (request.Lang != null)
                sql += " AND f.lang = @lang";
            DbReader.AppendPathFilters(
                ref sql,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests);
            return sql + " ORDER BY s.id LIMIT @familyMemberLimit";
        }
    }
}
