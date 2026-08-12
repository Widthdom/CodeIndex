namespace CodeIndex.Database;

public partial class DbReader
{
    private DependencySqlFragment BuildResolvedDependencyIdentitySql(DependencyQueryRequest request)
    {
        if (!_referenceIdentityContractCurrent)
            return DependencySqlFragment.Empty;

        var builder = new DependencySqlFragmentBuilder();
        builder.Append(@"
                SELECT resolved.source_path,
                       resolved.target_path,
                       resolved.symbol_name,
                       COUNT(*) AS ref_count,
                       resolved.source_lang,
                       'resolved_identity' AS origin,
                       resolved.raw_reference_kind,
                       resolved.target_kind
                FROM (
                    SELECT DISTINCT lrp.source_path,
                           target_file.path AS target_path,
                           lrp.symbol_name,
                           lrp.reference_id,
                           lrp.source_lang,
                           lrp.raw_reference_kind,
                           target.kind AS target_kind
                    FROM logical_references_primary lrp
                    JOIN symbol_reference_candidates candidate
                      ON candidate.reference_id = lrp.reference_id
                    JOIN symbols target ON target.id = candidate.symbol_id
                    JOIN files target_file ON target_file.id = target.file_id
                    JOIN target_files scoped_target
                      ON scoped_target.target_path = target_file.path
                     AND scoped_target.target_lang = target_file.lang
                    WHERE lrp.identity_scoped = 1
                      AND lrp.resolution_state IN ('resolved', 'resolved_group')
                      AND lrp.source_path != target_file.path");
        builder.Append(BuildDependencySymbolFilter(
            "lrp.symbol_name",
            request.DependencySymbols,
            request.DependencySymbolFamilies,
            request.SuppressDependencyNoise,
            "resolvedDependency"));
        var limitSql = request.Lang == "csharp" ? " LIMIT @sourceCandidateLimit" : string.Empty;
        builder.Append(@"
                     ORDER BY lrp.source_path, lrp.symbol_name, lrp.reference_id" + limitSql + @"
                ) resolved
                GROUP BY resolved.source_path, resolved.target_path, resolved.symbol_name,
                         resolved.source_lang, resolved.raw_reference_kind, resolved.target_kind
                UNION ALL
                ");
        return builder.Build();
    }
}
