namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record DependencyCycleQueryPlan(
        string Sql,
        IReadOnlyList<DependencyQueryParameter> Parameters);

    private sealed record DependencyCycleQueryExpressions(
        string MarkdownExplicitLink,
        string CSharpNonAuthoritativeQualifiedCall,
        string SuppressedEvidenceScope,
        string NoiseEvidenceScope,
        string CandidateOrder,
        string RetainedSymbolFilter,
        string ConstrainedAlias,
        string ResolutionState,
        string ReferenceLineJoin,
        string SymbolNameMatch,
        string ReferenceName);

    private DependencyCycleQueryPlan BuildDependencyCycleQueryPlan(DependencyQueryRequest request)
    {
        var expressions = BuildDependencyCycleQueryExpressions(request);
        var candidates = new DependencyCycleCandidateSqlBuilder(this, request, expressions).Build();
        var evidence = new DependencyCycleEvidenceSqlBuilder(this, request, expressions).Build();
        var builder = new DependencySqlFragmentBuilder();
        builder.Append("WITH ");
        builder.Append(new DependencyTargetSqlBuilder(this, request, BuildDependencyQueryExpressions()).BuildCycleTargets());
        builder.Append(candidates.Sql);
        builder.Append(evidence.Sql);
        builder.AddParameters(candidates.Parameters);
        builder.AddParameters(evidence.Parameters);
        AppendDependencyCycleTerminalParameters(builder, request);
        var query = builder.Build();
        return new DependencyCycleQueryPlan(query.Sql, query.Parameters);
    }

    private DependencyCycleQueryExpressions BuildDependencyCycleQueryExpressions(DependencyQueryRequest request)
    {
        var hasCurrentReferenceIdentityContract = HasCurrentReferenceIdentityContractForRead();
        var markdownExplicitLink = _referenceColumns.Contains("target_qualifier")
            ? "(src.lang = 'markdown' AND r.reference_kind = 'reference' AND r.target_qualifier IS NOT NULL AND dst.path = markdown_resolve_path(src.path, r.target_qualifier))"
            : "0 = 1";
        var resolvedCSharpNonTarget = hasCurrentReferenceIdentityContract
                                      && _referenceColumns.Contains("target_symbol_id")
            ? "(r.resolution_state = 'resolved' AND r.target_symbol_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM symbols confirmed_target WHERE confirmed_target.id = r.target_symbol_id AND confirmed_target.file_id = dst.id))"
            : "0 = 1";
        var resolvedGroupCSharpNonCandidate = hasCurrentReferenceIdentityContract
            ? "(r.resolution_state = 'resolved_group' AND NOT EXISTS (SELECT 1 FROM symbol_reference_candidates confirmed_candidate JOIN symbols confirmed_target ON confirmed_target.id = confirmed_candidate.symbol_id WHERE confirmed_candidate.reference_id = r.id AND confirmed_target.file_id = dst.id))"
            : "0 = 1";
        var csharpNonAuthoritativeQualifiedCall = hasCurrentReferenceIdentityContract
                                                   && _referenceColumns.Contains("target_qualifier")
                                                   && _referenceColumns.Contains("resolution_state")
                                                   && _referenceColumns.Contains("target_symbol_id")
            ? "(src.lang = 'csharp' AND r.reference_kind = 'call' AND r.target_qualifier IS NOT NULL AND (COALESCE(r.resolution_state, 'unresolved') NOT IN ('resolved', 'resolved_group') OR "
              + resolvedCSharpNonTarget
              + " OR "
              + resolvedGroupCSharpNonCandidate
              + "))"
            : "0 = 1";
        var suppressedEvidenceScope = "((src.lang = 'markdown' AND s.kind = 'heading' AND NOT "
                                      + markdownExplicitLink
                                      + ") OR "
                                      + csharpNonAuthoritativeQualifiedCall
                                      + ")";
        var context = ReferenceContextSql("r");
        var referenceName = BuildLogicalReferenceNameExpr("src.lang", "r.symbol_name", context, "r.container_name", "r.column_number");
        var sqlNameMatch = BuildSqlDependencyNameMatch(
            "sql_normalize_name(sql_target.name)", "sql_segment_count(sql_target.name)", referenceName,
            BuildLogicalReferenceSegmentCountExpr("src.lang", "r.symbol_name", context, "r.container_name", "r.column_number"),
            "r.symbol_name",
            BuildLogicalReferenceLeafFallbackAllowedExpr("src.lang", "r.symbol_name", context, "r.container_name", "r.column_number"));
        // Separate the SQL branch so other languages retain an indexed name lookup,
        // rather than scanning all symbols through an OR with SQL normalization.
        return new DependencyCycleQueryExpressions(
            markdownExplicitLink,
            csharpNonAuthoritativeQualifiedCall,
            suppressedEvidenceScope,
            "(" + markdownExplicitLink + " OR (src.lang = 'markdown' AND s.kind = 'heading') OR " + csharpNonAuthoritativeQualifiedCall + ")",
            request.SuppressDependencyNoise
                ? "retained_evidence DESC, source_path, target_path"
                : "source_path, target_path",
            request.SuppressDependencyNoise
                ? " WHERE suppression_reason IS NULL"
                : string.Empty,
            request.Reverse ? "dst" : "src",
            DependencyResolutionStateSql(),
            ReferenceLineJoinSql("r"),
            $@"s.id IN (
                SELECT named.id FROM symbols named
                WHERE src.lang != 'sql' AND named.name = r.symbol_name
                UNION ALL
                SELECT sql_target.id FROM symbols sql_target
                JOIN files sql_target_file ON sql_target_file.id = sql_target.file_id
                WHERE src.lang = 'sql' AND sql_target_file.lang = 'sql' AND {sqlNameMatch})",
            "CASE WHEN src.lang = 'sql' THEN " + referenceName + " ELSE r.symbol_name END");
    }

    private static void AppendDependencyCycleTerminalParameters(
        DependencySqlFragmentBuilder builder,
        DependencyQueryRequest request)
    {
        if (request.Lang != null)
            builder.AddText("@lang", request.Lang);
        AppendDependencyPathParameters(builder, "pathPattern", request.PathPatterns);
        AppendDependencyPathParameters(builder, "excludePath", request.ExcludePathPatterns);
        builder.AddInt32("@limit", request.Limit);
        builder.AddInt32("@symbolSampleLimit", DependencySymbolSampleLimit);
    }
}
