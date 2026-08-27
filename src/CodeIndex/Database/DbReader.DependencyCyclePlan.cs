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
        string ResolutionState);

    private DependencyCycleQueryPlan BuildDependencyCycleQueryPlan(DependencyQueryRequest request)
    {
        var expressions = BuildDependencyCycleQueryExpressions(request);
        var candidates = new DependencyCycleCandidateSqlBuilder(this, request, expressions).Build();
        var evidence = new DependencyCycleEvidenceSqlBuilder(request, expressions).Build();
        var builder = new DependencySqlFragmentBuilder();
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
        var markdownExplicitLink = _referenceColumns.Contains("target_qualifier")
            ? "(src.lang = 'markdown' AND r.reference_kind = 'reference' AND r.target_qualifier IS NOT NULL AND dst.path = markdown_resolve_path(src.path, r.target_qualifier))"
            : "0 = 1";
        var resolvedCSharpNonTarget = _referenceColumns.Contains("target_symbol_id")
            ? "(r.resolution_state = 'resolved' AND r.target_symbol_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM symbols confirmed_target WHERE confirmed_target.id = r.target_symbol_id AND confirmed_target.file_id = dst.id))"
            : "0 = 1";
        var csharpNonAuthoritativeQualifiedCall = _referenceColumns.Contains("target_qualifier")
                                                   && _referenceColumns.Contains("resolution_state")
            ? "(src.lang = 'csharp' AND r.reference_kind = 'call' AND r.target_qualifier IS NOT NULL AND (COALESCE(r.resolution_state, 'unresolved') NOT IN ('resolved', 'resolved_group') OR "
              + resolvedCSharpNonTarget
              + "))"
            : "0 = 1";
        var suppressedEvidenceScope = "((src.lang = 'markdown' AND s.kind = 'heading' AND NOT "
                                      + markdownExplicitLink
                                      + ") OR "
                                      + csharpNonAuthoritativeQualifiedCall
                                      + ")";
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
            _referenceColumns.Contains("resolution_state")
                ? "COALESCE(r.resolution_state, 'unavailable')"
                : "'unavailable'");
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
