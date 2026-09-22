namespace CodeIndex.Database;

public partial class DbReader
{
    private static DependencySqlFragment BuildResolvedDependencyIdentitySql(DependencyQueryRequest request, bool identityCurrent)
    {
        if (!identityCurrent)
            return DependencySqlFragment.Empty;

        var builder = new DependencySqlFragmentBuilder();
        builder.Append(@"
                SELECT resolved.source_path,
                       resolved.target_path,
                       resolved.symbol_name,
                       COUNT(*) AS ref_count,
                       resolved.source_lang,
                       'resolved_identity' AS origin,
                       resolved.evidence_resolution_state,
                       resolved.raw_reference_kind,
                       resolved.target_kind
                FROM (
                    SELECT lrp.source_path,
                           target_file.path AS target_path,
                           lrp.symbol_name,
                           lrp.reference_id,
                           lrp.source_lang,
                           lrp.evidence_resolution_state,
                           lrp.raw_reference_kind,
                           CASE WHEN MIN(target.kind) = MAX(target.kind) THEN MIN(target.kind)
                                ELSE 'symbol' END AS target_kind
                    FROM logical_references_primary lrp
                    JOIN symbols target ON target.id IN (
                        SELECT lrp.target_symbol_id WHERE lrp.resolution_state = 'resolved'
                        UNION ALL
                        SELECT candidate.symbol_id FROM symbol_reference_candidates candidate
                        WHERE candidate.reference_id = lrp.reference_id
                          AND lrp.resolution_state IN ('resolved_group', 'ambiguous'))
                    JOIN files target_file ON target_file.id = target.file_id
                    JOIN target_files scoped_target
                      ON scoped_target.target_path = target_file.path
                     AND scoped_target.target_lang = target_file.lang
                    WHERE lrp.identity_scoped = 1
                      AND (lrp.resolution_state IN ('resolved', 'resolved_group')
                           OR (lrp.resolution_state = 'ambiguous' AND lrp.source_lang NOT IN ('csharp', 'dependency_lock')))
                      AND target_file.lang = lrp.source_lang
                      AND lrp.source_path != target_file.path");
        builder.Append(BuildDependencySymbolFilter(
            "lrp.symbol_name",
            request.DependencySymbols,
            request.DependencySymbolFamilies,
            request.SuppressDependencyNoise,
            "resolvedDependency"));
        var limitSql = request.Lang == "csharp" ? " LIMIT @sourceCandidateLimit" : string.Empty;
        // One observation contributes once per destination, including a group
        // containing different kinds (for example C++ struct/function stat).
        builder.Append(@"
                     GROUP BY lrp.source_path, target_file.path, lrp.symbol_name, lrp.reference_id,
                              lrp.source_lang, lrp.evidence_resolution_state, lrp.raw_reference_kind
                     ORDER BY lrp.source_path, lrp.symbol_name, lrp.reference_id" + limitSql + @"
                ) resolved
                GROUP BY resolved.source_path, resolved.target_path, resolved.symbol_name,
                         resolved.source_lang, resolved.evidence_resolution_state, resolved.raw_reference_kind, resolved.target_kind
                UNION ALL
                ");
        return builder.Build();
    }
}
