namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record DependencyCycleQueryPlan(
        string Sql,
        IReadOnlyList<DependencyQueryParameter> Parameters);

    private sealed record DependencyCycleQueryExpressions(
        string MarkdownExplicitLink,
        string NoiseEvidenceScope,
        string CandidateOrder,
        string RetainedSymbolFilter,
        string ConstrainedAlias);

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
        return new DependencyCycleQueryExpressions(
            markdownExplicitLink,
            "(" + markdownExplicitLink + " OR (src.lang = 'markdown' AND s.kind = 'heading'))",
            request.SuppressDependencyNoise
                ? "retained_evidence DESC, source_path, target_path"
                : "source_path, target_path",
            request.SuppressDependencyNoise
                ? " WHERE origin <> 'markdown_heading_name_match'"
                : string.Empty,
            request.Reverse ? "dst" : "src");
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
