using CodeIndex.Indexer;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record ImpactDefinitionRequest(
        string ResolvedName,
        string NormalizedName,
        string LeafName,
        int SegmentCount,
        bool AllowLeafFallback,
        int RepresentativeLimit,
        int RepresentativeOffset,
        string? Lang,
        IReadOnlyList<string>? PathPatterns,
        IReadOnlyList<string>? ExcludePathPatterns,
        bool ExcludeTests)
    {
        public static ImpactDefinitionRequest Create(
            string resolvedName,
            int representativeLimit,
            int representativeOffset,
            string? lang,
            IReadOnlyList<string>? pathPatterns,
            IReadOnlyList<string>? excludePathPatterns,
            bool excludeTests)
        {
            return new ImpactDefinitionRequest(
                resolvedName,
                SqlNameResolver.NormalizeQualifiedName(resolvedName),
                SqlNameResolver.GetLeafName(resolvedName),
                SqlNameResolver.GetSegmentCount(resolvedName),
                !SqlNameResolver.HasQualifier(resolvedName),
                Math.Max(1, representativeLimit),
                Math.Max(0, representativeOffset),
                lang,
                pathPatterns,
                excludePathPatterns,
                excludeTests);
        }
    }

    private sealed record ImpactDefinitionResolution(
        List<SymbolResult> Definitions,
        int PhysicalCount,
        int PhysicalFileCount,
        int LogicalCount,
        int PreciseDefinitionCount,
        int PreciseLogicalDefinitionCount,
        int PreciseDefinitionFileCount,
        int NonCallableDefinitionCount,
        SymbolResult? SinglePreciseDefinition,
        HashSet<long> PhysicalSymbolIds,
        HashSet<string> PhysicalDefinitionPaths,
        bool PhysicalSymbolIdsTruncated);

    private ImpactDefinitionResolution ResolveImpactDefinitions(
        string resolvedName,
        int representativeLimit,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        int representativeOffset = 0)
    {
        var request = ImpactDefinitionRequest.Create(
            resolvedName,
            representativeLimit,
            representativeOffset,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests);
        EnsureCSharpCallableTypeKinds(request.Lang, [request.LeafName], exact: true);

        ImpactDefinitionProjection projection;
        ImpactDefinitionQueryPlan plan;
        using (var cmd = _conn.CreateCommand())
        {
            plan = ImpactDefinitionQueryBuilder.Build(this, cmd, request);
            cmd.CommandText = plan.Sql;
            ImpactDefinitionQueryBuilder.Bind(this, cmd, plan);
            projection = ImpactDefinitionRowProjector.Read(cmd);
        }

        var families = ImpactDefinitionFamilyResolver.Resolve(
            this,
            plan,
            projection.Definitions);
        var stats = projection.Stats;
        return new ImpactDefinitionResolution(
            projection.Definitions,
            stats.PhysicalCount,
            stats.PhysicalFileCount,
            stats.LogicalCount,
            stats.PreciseDefinitionCount,
            stats.PreciseLogicalDefinitionCount,
            stats.PreciseDefinitionFileCount,
            stats.NonCallableDefinitionCount,
            stats.PreciseLogicalDefinitionCount == 1 ? projection.SinglePreciseDefinition : null,
            families.PhysicalSymbolIds,
            families.PhysicalDefinitionPaths,
            families.Truncated);
    }

    private static ImpactDefinitionResolution ResolveSelectedImpactDefinition(
        DefinitionResult definition)
    {
        var precise = IsPreciseImpactFallbackKind(definition.Kind);
        var nonCallable = definition.Kind is "namespace" or "import";
        var symbolIds = definition.SymbolId is long symbolId
            ? new HashSet<long> { symbolId }
            : [];
        return new ImpactDefinitionResolution(
            [definition],
            PhysicalCount: 1,
            PhysicalFileCount: 1,
            LogicalCount: 1,
            PreciseDefinitionCount: precise ? 1 : 0,
            PreciseLogicalDefinitionCount: precise ? 1 : 0,
            PreciseDefinitionFileCount: precise ? 1 : 0,
            NonCallableDefinitionCount: nonCallable ? 1 : 0,
            SinglePreciseDefinition: precise ? definition : null,
            PhysicalSymbolIds: symbolIds,
            PhysicalDefinitionPaths: new HashSet<string>(StringComparer.Ordinal) { definition.Path },
            PhysicalSymbolIdsTruncated: false);
    }

    private static ImpactDefinitionResolution EmptyImpactDefinitionResolution()
        => new(
            [],
            PhysicalCount: 0,
            PhysicalFileCount: 0,
            LogicalCount: 0,
            PreciseDefinitionCount: 0,
            PreciseLogicalDefinitionCount: 0,
            PreciseDefinitionFileCount: 0,
            NonCallableDefinitionCount: 0,
            SinglePreciseDefinition: null,
            PhysicalSymbolIds: [],
            PhysicalDefinitionPaths: [],
            PhysicalSymbolIdsTruncated: false);

    private bool SelectedDefinitionMatchesImpactFilters(
        DefinitionResult definition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (lang != null && !string.Equals(definition.Lang, lang, StringComparison.Ordinal))
            return false;

        using var command = _conn.CreateCommand();
        var sql = "SELECT 1 FROM files f WHERE f.path = @selectedDefinitionPath";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";
        command.CommandText = sql;
        SqliteCommandPolicy.Add(command, "@selectedDefinitionPath", definition.Path);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        return command.ExecuteScalar() != null;
    }
}
