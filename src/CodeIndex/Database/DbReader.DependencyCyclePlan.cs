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
        string ReferenceLineJoin,
        string ResolutionState,
        string TargetMatch,
        string SymbolName);

    private DependencyCycleQueryPlan BuildDependencyCycleQueryPlan(DependencyQueryRequest request)
    {
        var expressions = BuildDependencyCycleQueryExpressions(request);
        var candidates = new DependencyCycleCandidateSqlBuilder(this, request, expressions).Build();
        var evidence = new DependencyCycleEvidenceSqlBuilder(this, request, expressions).Build();
        var builder = new DependencySqlFragmentBuilder();
        builder.Append("WITH ");
        builder.Append(new DependencyTargetSqlBuilder(this, request, BuildDependencyQueryExpressions()).BuildCycleTargets());
        builder.Append(BuildDependencyCycleSqlMatches());
        // Both cycle stages revisit references for each candidate edge. Reconstruct
        // Python source positions once, before the per-import/target matching loops.
        builder.Append($@"
            cycle_python_contexts AS MATERIALIZED (
                SELECT r.id AS reference_id, {DependencyReferenceContextSql("r", "src")} AS context
                FROM symbol_references r
                JOIN files src ON src.id = r.file_id
                {ReferenceLineJoinSql("r")}
                WHERE src.lang = 'python'{BuildDependencyCyclePythonContextScope(request)}
            ),");
        builder.Append(candidates.Sql);
        builder.Append(evidence.Sql);
        builder.AddParameters(candidates.Parameters);
        builder.AddParameters(evidence.Parameters);
        AppendDependencyCycleTerminalParameters(builder, request);
        var query = builder.Build();
        return new DependencyCycleQueryPlan(query.Sql, query.Parameters);
    }

    private string BuildDependencyCyclePythonContextScope(DependencyQueryRequest request)
    {
        var scope = BuildDependencyGeneratedFilter("src");
        if (request.Lang != null)
            scope += " AND src.lang = @lang";
        // Reverse path filters constrain target files, so every source can contribute.
        if (!request.Reverse)
        {
            if (request.PathPatterns is { Count: > 0 })
                scope += " AND (" + string.Join(" OR ", request.PathPatterns.Select((path, i) =>
                    BuildPathFilterPredicate("src", "pathPattern", i, path))) + ")";
            if (request.ExcludePathPatterns is { Count: > 0 })
                scope += string.Concat(request.ExcludePathPatterns.Select((path, i) =>
                    $" AND NOT {BuildPathFilterPredicate("src", "excludePath", i, path)}"));
        }
        if (request.ExcludeTests)
            scope += $" AND NOT {DependencyTestPathCondition("src.path")}";
        return scope;
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
            ReferenceLineJoinSql("r"),
            BuildDependencyCycleResolutionState(hasCurrentReferenceIdentityContract),
            BuildDependencyCycleTargetMatch(hasCurrentReferenceIdentityContract),
            "CASE WHEN src.lang = 'sql' THEN sql_normalize_name(s.name) WHEN src.lang = 'python' THEN s.name ELSE r.symbol_name END");
    }

    private string BuildDependencyCycleTargetMatch(bool identityCurrent)
    {
        // Keep each branch as an indexed ID/name lookup. Both candidate selection and
        // evidence must use the same reference/definition pairs before graph budgets.
        // A current resolved reference cannot lend its state to a same-name decoy;
        // grouped/ambiguous references admit only their persisted candidate identities.
        var identityTargets = identityCurrent ? @"
                SELECT r.target_symbol_id
                WHERE src.lang != 'sql' AND r.resolution_state = 'resolved'
                UNION ALL
                SELECT candidate.symbol_id FROM symbol_reference_candidates candidate
                WHERE src.lang != 'sql' AND candidate.reference_id = r.id
                  AND r.resolution_state IN ('resolved_group', 'ambiguous')
                UNION ALL" : string.Empty;
        var nameFallback = identityCurrent
            ? " AND COALESCE(r.resolution_state, '') NOT IN ('resolved', 'resolved_group', 'ambiguous')"
            : string.Empty;
        var importIdentityScope = identityCurrent ? @"
                  AND (COALESCE(r.resolution_state, '') NOT IN ('resolved', 'resolved_group', 'ambiguous')
                       OR (r.resolution_state = 'resolved' AND r.target_symbol_id = py_import.id)
                       OR (r.resolution_state IN ('resolved_group', 'ambiguous') AND EXISTS (
                           SELECT 1 FROM symbol_reference_candidates candidate
                           WHERE candidate.reference_id = r.id AND candidate.symbol_id = py_import.id)))" : string.Empty;
        var importSignature = GetSymbolColumnSql("signature", "NULL", "py_import");
        const string context = "(SELECT context FROM cycle_python_contexts WHERE reference_id = r.id)";
        // Python's persisted identity can be the source-local import binding. Follow
        // only that binding using ordinary deps' module/alias matcher, never all names.
        var pythonImports = @"
                UNION ALL
                SELECT imported.id FROM symbols py_import
                CROSS JOIN symbols imported ON imported.name = python_import_target_name(src.path, r.symbol_name, " + context + @", r.column_number, " + importSignature + @")
                CROSS JOIN files imported_file ON imported_file.id = imported.file_id
                WHERE src.lang = 'python' AND imported_file.lang = 'python'
                  AND py_import.file_id = src.id AND py_import.kind = 'import'" + importIdentityScope + @"
                  AND python_import_resolves(src.path, imported_file.path, r.symbol_name, r.reference_kind, " + context + @", r.column_number, " + importSignature + @")";
        return @"s.id IN (" + identityTargets + @"
                SELECT named.id FROM symbols named
                WHERE src.lang NOT IN ('sql', 'python') AND named.name = r.symbol_name" + nameFallback + pythonImports + @"
                UNION ALL
                SELECT sql_match.symbol_id WHERE src.lang = 'sql')";
    }

    private string BuildDependencyCycleResolutionState(bool identityCurrent)
    {
        var state = DependencyResolutionStateSql();
        if (!identityCurrent)
            return state;
        // An import-binding identity proves the binding, not the imported definition.
        // Keep semantic fallback visible without lending it a confirmed target label.
        return @"CASE WHEN src.lang = 'python' AND (
                (r.resolution_state = 'resolved' AND r.target_symbol_id != s.id)
                OR (r.resolution_state = 'resolved_group' AND NOT EXISTS (
                    SELECT 1 FROM symbol_reference_candidates candidate
                    WHERE candidate.reference_id = r.id AND candidate.symbol_id = s.id)))
            THEN 'unavailable' ELSE " + state + " END";
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
