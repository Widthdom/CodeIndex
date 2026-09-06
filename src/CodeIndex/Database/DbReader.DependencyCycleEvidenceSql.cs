namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class DependencyCycleEvidenceSqlBuilder
    {
        private readonly DbReader _reader;
        private readonly DependencyQueryRequest _request;
        private readonly DependencyCycleQueryExpressions _expressions;
        private readonly DependencySqlFragmentBuilder _sql = new();

        internal DependencyCycleEvidenceSqlBuilder(
            DbReader reader,
            DependencyQueryRequest request,
            DependencyCycleQueryExpressions expressions)
        {
            _reader = reader;
            _request = request;
            _expressions = expressions;
        }

        internal DependencySqlFragment Build()
        {
            AppendCandidateSymbols();
            AppendEdgeAggregates();
            AppendEvidencePayloads();
            AppendFinalProjection();
            return _sql.Build();
        }

        private void AppendCandidateSymbols()
        {
            _sql.Append(@"
            candidate_symbols AS (
                SELECT candidate_edges.source_path,
                       candidate_edges.target_path,
                       r.id AS reference_id,
                       r.symbol_name,
                       src.lang AS source_lang,
                       CASE
                           WHEN " + _expressions.MarkdownExplicitLink + @"
                               THEN 'markdown_explicit_link'
                           WHEN src.lang = 'markdown' AND s.kind = 'heading'
                               THEN 'markdown_heading_name_match'
                           ELSE 'symbol_name_match'
                       END AS origin,
                       " + _expressions.ResolutionState + @" AS resolution_state,
                       r.reference_kind AS raw_reference_kind,
                       CASE WHEN s.kind = 'heading' THEN 'heading' ELSE 'symbol' END AS target_kind,
                       CASE
                           WHEN src.lang = 'markdown' AND s.kind = 'heading' AND NOT " + _expressions.MarkdownExplicitLink + @"
                               THEN 'markdown_heading_name_match'
                           WHEN " + _expressions.CSharpNonAuthoritativeQualifiedCall + @"
                               THEN 'csharp_non_authoritative_qualified_call'
                           ELSE NULL
                       END AS suppression_reason
                FROM candidate_edges
                JOIN files src ON src.path = candidate_edges.source_path
                JOIN symbol_references r ON r.file_id = src.id
                JOIN symbols s ON s.name = r.symbol_name
                JOIN files dst ON s.file_id = dst.id
                 AND dst.path = candidate_edges.target_path
                WHERE src.path != dst.path
                  AND src.lang = dst.lang");
            _sql.Append(_reader.BuildDependencyEvidenceFilter(_request.EvidenceFilter, "cycleAggregateEvidence"));
            _sql.Append(BuildDependencySymbolFilter(
                "r.symbol_name",
                _request.DependencySymbols,
                _request.DependencySymbolFamilies,
                suppressDependencyNoise: false,
                parameterPrefix: "cycleAggregateNames"));
            _sql.Append(BuildDependencySymbolFilter(
                "r.symbol_name",
                dependencySymbols: null,
                dependencySymbolFamilies: null,
                suppressDependencyNoise: _request.SuppressDependencyNoise,
                parameterPrefix: "cycleAggregateNoise",
                filterScopeSql: "NOT " + _expressions.NoiseEvidenceScope));
        }

        private void AppendEdgeAggregates()
        {
            _sql.Append(@"
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
                       resolution_state,
                       raw_reference_kind,
                       target_kind,
                       suppression_reason,
                       COUNT(DISTINCT reference_id) AS evidence_reference_count
                FROM candidate_symbols
                GROUP BY source_path,
                         target_path,
                         source_lang,
                         origin,
                         resolution_state,
                         raw_reference_kind,
                         target_kind,
                         suppression_reason
            ),");
        }

        private void AppendEvidencePayloads()
        {
            _sql.Append(@"
            ordered_edge_evidence AS (
                SELECT source_path,
                       target_path,
                       source_lang || char(31) ||
                       origin || char(31) ||
                       resolution_state || char(31) ||
                       raw_reference_kind || char(31) ||
                       target_kind || char(31) ||
                       COALESCE(suppression_reason, '') || char(31) ||
                       evidence_reference_count AS evidence_item
                FROM edge_evidence_rows
                 ORDER BY source_path, target_path, source_lang, origin, resolution_state, raw_reference_kind, target_kind, suppression_reason
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
                FROM candidate_symbols" + _expressions.RetainedSymbolFilter + @"
            ),
            ranked_edge_symbols AS (
                SELECT source_path,
                       target_path,
                       symbol_name,
                       ROW_NUMBER() OVER (PARTITION BY source_path, target_path ORDER BY symbol_name) AS symbol_rank
                FROM distinct_edge_symbols
            )");
        }

        private void AppendFinalProjection()
        {
            _sql.Append(@"
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
            ORDER BY edge_reference_totals.source_path, edge_reference_totals.target_path");
        }
    }
}
