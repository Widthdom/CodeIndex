using CodeIndex.Indexer;
using CodeIndex.Models;
using System.Text;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record DependencyQueryRequest(
        int Limit,
        string? Lang,
        IReadOnlyList<string>? PathPatterns,
        IReadOnlyList<string>? ExcludePathPatterns,
        bool ExcludeTests,
        bool Reverse,
        IReadOnlyList<string>? DependencySymbols,
        IReadOnlyList<string>? DependencySymbolFamilies,
        bool SuppressDependencyNoise);

    private sealed record DependencyQueryPlan(
        DependencyQueryRequest Request,
        string Sql,
        IReadOnlyList<DependencyQueryParameter> Parameters);

    private sealed record DependencySqlFragment(
        string Sql,
        IReadOnlyList<DependencyQueryParameter> Parameters)
    {
        internal static readonly DependencySqlFragment Empty = new(string.Empty, Array.Empty<DependencyQueryParameter>());
    }

    private enum DependencyQueryParameterKind
    {
        Text,
        Int32,
    }

    private readonly record struct DependencyQueryParameter(
        string Name,
        DependencyQueryParameterKind Kind,
        string? TextValue,
        int Int32Value)
    {
        internal static DependencyQueryParameter Text(string name, string value)
            => new(name, DependencyQueryParameterKind.Text, value, default);

        internal static DependencyQueryParameter Int32(string name, int value)
            => new(name, DependencyQueryParameterKind.Int32, null, value);
    }

    private sealed class DependencySqlFragmentBuilder
    {
        private readonly StringBuilder _sql = new();
        private readonly List<DependencyQueryParameter> _parameters = [];

        internal void Append(string sql) => _sql.Append(sql);

        internal void Append(DependencySqlFragment fragment)
        {
            _sql.Append(fragment.Sql);
            AddParameters(fragment.Parameters);
        }

        internal void AddParameters(IReadOnlyList<DependencyQueryParameter> parameters)
        {
            for (var i = 0; i < parameters.Count; i++)
                _parameters.Add(parameters[i]);
        }

        internal void AddText(string name, string value)
            => _parameters.Add(DependencyQueryParameter.Text(name, value));

        internal void AddInt32(string name, int value)
            => _parameters.Add(DependencyQueryParameter.Int32(name, value));

        internal DependencySqlFragment Build()
            => new(_sql.ToString(), _parameters.ToArray());
    }

    private sealed record DependencyQueryExpressions(
        string ReferenceLineJoin,
        string ContextSql,
        string ReferenceIdSql,
        string ScopedResolutionStateSql,
        string IdentityScopedSql,
        string IdentityNameEdgePredicate,
        string TargetLogicalSymbolName,
        string TargetLogicalSymbolSegmentCount,
        string PythonImportMatchSignature,
        string PythonImportSignature,
        string SqlDependencyTargetMatch);

    private DependencyQueryExpressions BuildDependencyQueryExpressions()
    {
        var identityScopeCondition = """
            (
                (src.lang = 'csharp' AND r.reference_kind NOT IN ('attribute', 'annotation'))
                OR (src.lang = 'dependency_lock' AND r.reference_kind = 'dependency')
            )
            """;
        var resolutionState = _referenceIdentityContractCurrent ? "r.resolution_state" : "NULL";
        var pythonImportMatchSignature = GetSymbolColumnSql("signature", "NULL", "py_import_match");
        var sqlDependencyTargetMatch = @"(
                    (tf.target_lang != 'sql'
                     AND NOT (snc.source_lang IN ('msbuild', 'solution') AND snc.logical_reference_kind IN ('import', 'project_reference'))
                     AND NOT (snc.source_lang = 'markdown' AND snc.logical_reference_kind = 'import')
                     AND NOT (snc.source_lang = 'markdown' AND snc.logical_reference_kind = 'reference')
                     AND tf.symbol_name = snc.symbol_name)
                 OR (snc.source_lang = 'python'
                     AND tf.target_lang = 'python'
                     AND EXISTS (
                         SELECT 1 FROM symbols py_import_match
                         WHERE py_import_match.file_id = snc.source_file_id
                           AND py_import_match.kind = 'import'
                           AND python_import_target_name(snc.source_path, snc.symbol_name, snc.context, snc.column_number, " + pythonImportMatchSignature + @") = tf.symbol_name
                     ))
                 OR (tf.target_lang = 'sql' AND (
                        (tf.symbol_segment_count = snc.symbol_segment_count AND tf.symbol_name = snc.symbol_name COLLATE NOCASE)
                     OR (sql_segment_count(snc.raw_symbol_name) = 1
                         AND snc.allow_leaf_fallback = 1
                         AND tf.symbol_segment_count > 1
                         AND sql_leaf_name(tf.symbol_name) = snc.raw_symbol_name COLLATE NOCASE
                         AND NOT EXISTS (
                                SELECT 1
                                FROM target_files tf_exact
                                WHERE tf_exact.target_lang = tf.target_lang
                                  AND tf_exact.symbol_segment_count = 1
                                  AND tf_exact.symbol_name = snc.symbol_name COLLATE NOCASE
                            )
                         AND NOT EXISTS (
                                SELECT 1
                                FROM target_files tf_resolved
                                WHERE tf_resolved.target_lang = tf.target_lang
                                  AND tf_resolved.symbol_segment_count = snc.symbol_segment_count
                                  AND tf_resolved.symbol_name = snc.symbol_name COLLATE NOCASE
                            ))
                 ))
                )";

        return new DependencyQueryExpressions(
            ReferenceLineJoinSql("r"),
            ReferenceContextSql("r"),
            $"CASE WHEN {identityScopeCondition} THEN r.id ELSE 0 END",
            $"CASE WHEN {identityScopeCondition} THEN {resolutionState} ELSE NULL END",
            $"CASE WHEN {identityScopeCondition} THEN 1 ELSE 0 END",
            _referenceIdentityContractCurrent ? "snc.identity_scoped = 0" : "snc.source_lang <> 'dependency_lock'",
            BuildLogicalDependencySymbolNameExpr("dst", "s.name"),
            BuildLogicalDependencySymbolSegmentCountExpr("dst", "s.name"),
            pythonImportMatchSignature,
            GetSymbolColumnSql("signature", "NULL", "py_import"),
            sqlDependencyTargetMatch);
    }

    private DependencyQueryPlan BuildDependencyQueryPlan(DependencyQueryRequest request)
    {
        var expressions = BuildDependencyQueryExpressions();
        var resolvedIdentity = BuildResolvedDependencyIdentitySql(request);
        var source = new DependencySourceSqlBuilder(this, request, expressions).Build();
        var sourceCandidate = new DependencySourceCandidateSqlBuilder(this, request, expressions).Build();
        var target = new DependencyTargetSqlBuilder(this, request, expressions).Build();
        var edge = new DependencyEdgeSqlBuilder(request, expressions, resolvedIdentity).Build();
        var specialEdges = new DependencySpecialEdgeSqlBuilder(request).Build();
        var final = BuildDependencyFinalSql(request);

        var builder = new DependencySqlFragmentBuilder();
        builder.Append(source.Sql);
        builder.Append(sourceCandidate.Sql);
        builder.Append(target.Sql);
        builder.Append(edge.Sql);
        builder.Append(specialEdges.Sql);
        builder.Append(final.Sql);

        // Preserve the historical binding order even though the resolved-identity
        // fragment appears after the source/target CTEs in the final SQL text.
        builder.AddParameters(resolvedIdentity.Parameters);
        builder.AddParameters(source.Parameters);
        builder.AddParameters(sourceCandidate.Parameters);
        builder.AddParameters(target.Parameters);
        builder.AddParameters(edge.Parameters);
        builder.AddParameters(specialEdges.Parameters);
        AppendDependencyTerminalParameters(builder, request);
        var fragment = builder.Build();
        return new DependencyQueryPlan(request, fragment.Sql, fragment.Parameters);
    }

    private static void AppendDependencyTerminalParameters(
        DependencySqlFragmentBuilder builder,
        DependencyQueryRequest request)
    {
        if (request.Lang != null)
            builder.AddText("@lang", request.Lang);
        AppendDependencyPathParameters(builder, "pathPattern", request.PathPatterns);
        AppendDependencyPathParameters(builder, "excludePath", request.ExcludePathPatterns);
        builder.AddInt32("@limit", DependencyNoiseProfile.GetRankingCandidateLimit(request.Limit));
        if (request.Lang == "csharp")
            builder.AddInt32("@sourceCandidateLimit", DependencyNoiseProfile.GetRankingCandidateLimit(request.Limit));
        builder.AddInt32("@symbolSampleLimit", DependencySymbolSampleLimit);
    }

    private static void AppendDependencyPathParameters(
        DependencySqlFragmentBuilder builder,
        string parameterPrefix,
        IReadOnlyList<string>? pathPatterns)
    {
        if (pathPatterns == null)
            return;

        for (var i = 0; i < pathPatterns.Count; i++)
        {
            builder.AddText(
                SqliteDynamicSql.BuildParameterName(parameterPrefix, i),
                BuildPathLikePattern(pathPatterns[i]));
        }
    }
}
