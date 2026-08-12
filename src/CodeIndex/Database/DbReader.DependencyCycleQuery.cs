using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    public List<FileDependencyResult> GetFileDependencyCycleCandidates(
        int limit,
        out int candidateRowCount,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool reverse = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? dependencySymbols = null,
        IReadOnlyList<string>? dependencySymbolFamilies = null,
        bool suppressDependencyNoise = false)
    {
        candidateRowCount = 0;
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable || limit <= 0)
            return [];

        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = _conn.CreateCommand();
        var constrainedAlias = reverse ? "dst" : "src";
        var cycleMarkdownExplicitLinkSql = _referenceColumns.Contains("target_qualifier")
            ? "(src.lang = 'markdown' AND r.reference_kind = 'reference' AND r.target_qualifier IS NOT NULL AND dst.path = markdown_resolve_path(src.path, r.target_qualifier))"
            : "0 = 1";
        var cycleNoiseEvidenceScopeSql =
            "(" + cycleMarkdownExplicitLinkSql + " OR (src.lang = 'markdown' AND s.kind = 'heading'))";
        var cycleCandidateOrderSql = suppressDependencyNoise
            ? "retained_evidence DESC, source_path, target_path"
            : "source_path, target_path";
        var retainedCycleSymbolFilterSql = suppressDependencyNoise
            ? " WHERE origin <> 'markdown_heading_name_match'"
            : string.Empty;
        var sql = @"
            WITH candidate_edges AS (
                SELECT src.path AS source_path,
                       dst.path AS target_path,
                       MAX(CASE
                               WHEN src.lang = 'markdown'
                                AND s.kind = 'heading'
                                AND NOT " + cycleMarkdownExplicitLinkSql + @" THEN 0
                               ELSE 1
                           END) AS retained_evidence
            FROM symbol_references r
            JOIN files src ON r.file_id = src.id
            JOIN symbols s ON s.name = r.symbol_name
            JOIN files dst ON s.file_id = dst.id
            WHERE src.path != dst.path
              AND src.lang = dst.lang";
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "src", "depsCycleSourceLang")}";
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "dst", "depsCycleTargetLang")}";
        AppendDependencyGeneratedFilter(ref sql, "src");
        AppendDependencyGeneratedFilter(ref sql, "dst");
        if (lang != null)
            sql += " AND src.lang = @lang AND dst.lang = @lang";
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add(BuildPathFilterPredicate(constrainedAlias, "pathPattern", i, pathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND NOT {BuildPathFilterPredicate(constrainedAlias, "excludePath", i, excludePathPatterns[i])}";
        }
        if (excludeTests)
        {
            sql += $" AND NOT {DependencyTestPathCondition("src.path")}";
            sql += $" AND NOT {DependencyTestPathCondition("dst.path")}";
        }
        AppendDependencySymbolFilter(
            cmd,
            ref sql,
            "r.symbol_name",
            dependencySymbols,
            dependencySymbolFamilies,
            suppressDependencyNoise: false,
            parameterPrefix: "cycleDependencyNames");
        AppendDependencySymbolFilter(
            cmd,
            ref sql,
            "r.symbol_name",
            dependencySymbols: null,
            dependencySymbolFamilies: null,
            suppressDependencyNoise: suppressDependencyNoise,
            parameterPrefix: "cycleDependencyNoise",
            filterScopeSql: "NOT " + cycleNoiseEvidenceScopeSql);
        sql += @"
                GROUP BY src.path, dst.path
                ORDER BY " + cycleCandidateOrderSql + @"
                LIMIT @limit
            ),
            candidate_symbols AS (
                SELECT candidate_edges.source_path,
                       candidate_edges.target_path,
                       r.id AS reference_id,
                       r.symbol_name,
                       src.lang AS source_lang,
                       CASE
                           WHEN " + cycleMarkdownExplicitLinkSql + @"
                               THEN 'markdown_explicit_link'
                           WHEN src.lang = 'markdown' AND s.kind = 'heading'
                               THEN 'markdown_heading_name_match'
                           ELSE 'symbol_name_match'
                       END AS origin,
                       r.reference_kind AS raw_reference_kind,
                       CASE WHEN s.kind = 'heading' THEN 'heading' ELSE 'symbol' END AS target_kind
                FROM candidate_edges
                JOIN files src ON src.path = candidate_edges.source_path
                JOIN symbol_references r ON r.file_id = src.id
                JOIN symbols s ON s.name = r.symbol_name
                JOIN files dst ON s.file_id = dst.id
                 AND dst.path = candidate_edges.target_path
                WHERE src.path != dst.path
                  AND src.lang = dst.lang";
        AppendDependencySymbolFilter(
            cmd,
            ref sql,
            "r.symbol_name",
            dependencySymbols,
            dependencySymbolFamilies,
            suppressDependencyNoise: false,
            parameterPrefix: "cycleAggregateNames");
        AppendDependencySymbolFilter(
            cmd,
            ref sql,
            "r.symbol_name",
            dependencySymbols: null,
            dependencySymbolFamilies: null,
            suppressDependencyNoise: suppressDependencyNoise,
            parameterPrefix: "cycleAggregateNoise",
            filterScopeSql: "NOT " + cycleNoiseEvidenceScopeSql);
        sql += @"
            ),
            edge_reference_totals AS (
                SELECT source_path,
                       target_path,
                       COUNT(DISTINCT reference_id) AS reference_count
                FROM candidate_symbols
                GROUP BY source_path, target_path
            ),
            edge_evidence_rows AS (
                SELECT source_path,
                       target_path,
                       source_lang,
                       origin,
                       raw_reference_kind,
                       target_kind,
                       COUNT(DISTINCT reference_id) AS evidence_reference_count
                FROM candidate_symbols
                GROUP BY source_path,
                         target_path,
                         source_lang,
                         origin,
                         raw_reference_kind,
                         target_kind
            ),
            ordered_edge_evidence AS (
                SELECT source_path,
                       target_path,
                       source_lang || char(31) ||
                       origin || char(31) ||
                       raw_reference_kind || char(31) ||
                       target_kind || char(31) ||
                       evidence_reference_count AS evidence_item
                FROM edge_evidence_rows
                ORDER BY source_path, target_path, source_lang, origin, raw_reference_kind, target_kind
            ),
            edge_evidence_payloads AS (
                SELECT source_path,
                       target_path,
                       GROUP_CONCAT(evidence_item, char(30)) AS evidence_payload
                FROM ordered_edge_evidence
                GROUP BY source_path, target_path
            ),
            distinct_edge_symbols AS (
                SELECT DISTINCT source_path,
                                target_path,
                                symbol_name
                FROM candidate_symbols" + retainedCycleSymbolFilterSql + @"
            ),
            ranked_edge_symbols AS (
                SELECT source_path,
                       target_path,
                       symbol_name,
                       ROW_NUMBER() OVER (PARTITION BY source_path, target_path ORDER BY symbol_name) AS symbol_rank
                FROM distinct_edge_symbols
            )
            SELECT edge_reference_totals.source_path,
                   edge_reference_totals.target_path,
                   edge_reference_totals.reference_count,
                   COALESCE(GROUP_CONCAT(CASE WHEN symbol_rank <= @symbolSampleLimit THEN symbol_name END, char(31)), '') AS symbols,
                   COALESCE(edge_evidence_payloads.evidence_payload, '') AS evidence_payload
            FROM edge_reference_totals
            LEFT JOIN ranked_edge_symbols
              ON ranked_edge_symbols.source_path = edge_reference_totals.source_path
             AND ranked_edge_symbols.target_path = edge_reference_totals.target_path
            LEFT JOIN edge_evidence_payloads
              ON edge_evidence_payloads.source_path = edge_reference_totals.source_path
             AND edge_evidence_payloads.target_path = edge_reference_totals.target_path
            GROUP BY edge_reference_totals.source_path,
                     edge_reference_totals.target_path,
                     edge_reference_totals.reference_count,
                     edge_evidence_payloads.evidence_payload
            ORDER BY edge_reference_totals.source_path, edge_reference_totals.target_path";

        cmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        if (pathPatterns is { Count: > 0 })
            AddPathFilterParameterSet(cmd, "pathPattern", pathPatterns);
        if (excludePathPatterns is { Count: > 0 })
            AddPathFilterParameterSet(cmd, "excludePath", excludePathPatterns);
        SqliteCommandPolicy.Add(cmd, "@limit", limit);
        SqliteCommandPolicy.Add(cmd, "@symbolSampleLimit", DependencySymbolSampleLimit);

        var results = new List<FileDependencyResult>();
        using var cancellationRegistration = cancellationToken.Register(static state => ((SqliteCommand)state!).Cancel(), cmd);
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidateRowCount++;
                var symbolSamples = ParseDependencySymbols(reader.GetString(3));
                results.Add(new FileDependencyResult
                {
                    SourcePath = reader.GetString(0),
                    TargetPath = reader.GetString(1),
                    ReferenceCount = reader.GetInt32(2),
                    RankingScore = reader.GetInt32(2),
                    SymbolSamples = symbolSamples,
                    Symbols = string.Join(",", symbolSamples),
                    Evidence = ParseDependencyEvidence(reader.GetString(4)),
                });
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return results;
    }
}
