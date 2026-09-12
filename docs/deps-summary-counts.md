# Dependency summary counts

## English

`cdidx deps --summary-only --json --limit 2 --exclude-tests` reports the
same **returned/page count** as the corresponding dependency query. Omitting
the edge array does not turn `count` into a whole-graph total. This also applies
to `--cycles --summary-only`: its unit is a dependency cycle (SCC).

| Field | Meaning |
| --- | --- |
| `count`, `returned_count` | Dependency rows selected for this page after filters; summary mode omits the edge array. |
| `count_kind`, `count_unit` | `returned`, with unit `dependency_edges` or `dependency_cycles`. |
| `page_limit` | Requested display limit; distinct from candidate and graph work budgets. |
| `has_more` | `true` proves an additional matching row was observed. `false` means the available query result was exhausted; `null` means exhaustion could not be established within the candidate window. For cycles it retains the existing meaning: more SCCs in the analyzed candidate graph. |
| `candidate_scan_complete` | No candidate safety boundary was reached. For cycles this matches `analysis_complete`; it does not establish extraction completeness. |
| `query_exhausted` | Candidate coverage is complete and there is no later page. This proves exhaustion of the indexed query, independently of extraction coverage. |
| `total_count`, `total_count_available` | Exact indexed-query total when already known, otherwise `null` / `false`. Ordinary summaries expose it only on exhaustion. Cycles can reuse the total from complete SCC analysis even on a partial page. |
| `total_count_authoritative` | The total is available, the reference graph is complete, its graph contract is not degraded, and workspace graph coverage is verified. It is not a claim of compiler-complete dependency analysis. |
| `total_count_unavailable_reason` | `page_limit`, `candidate_scan_incomplete`, `workspace_candidate_scan_unverified`, or `graph_edge_budget`; `null` when available. |
| `total_count_non_authoritative_reason` | When a numeric total is available but not authoritative: `reference_graph_incomplete`, `graph_contract_degraded`, or `workspace_graph_coverage_unverified`. |
| `truncated`, `truncated_reason` | Ordinary summaries identify a partial page or unverified candidate coverage. Cycles retain their existing page/graph-budget truncation contract. |

On three matching edges, limits 1 and 2 return their respective counts with
`has_more=true`, `query_exhausted=false`, and no total. Limits 3 and above return
3 with `has_more=false` and `query_exhausted=true`, unless a candidate boundary
prevents that proof. An exhausted zero-row query can report a total of zero;
an incomplete or missing graph still prevents authoritative absence claims.

Ordinary summaries look ahead by at most one **ranked result** inside the
existing SQL ranking window; they do not increase that window or either C#
source-candidate budget. C# name candidates and resolved-identity reference
candidates are checked separately before relying on the edge count. Reaching
a candidate boundary, even exactly, is conservatively incomplete. Filters can
remove the lookahead row, so an unexamined remainder remains unknown.
Workspace fan-out currently lacks combined candidate-exhaustion evidence:
its ordinary summary preserves the existing query budget and reports unknown
`has_more` and an unavailable total. Narrow to one database for this proof.

The broad-summary guard (250 candidate files without a narrowing filter),
generated/test exclusions, noise/evidence filters, and incomplete-graph warnings
remain in force. `--summary-only --format json-graph` remains unsupported.
`reference_graph_complete=true` alone never proves query exhaustion.
Cycle node sampling (`display_truncated`) is separate from SCC pagination.

`--max-json-bytes` is a response budget, not a query-work budget. A summary that
cannot fit returns `E028_RESPONSE_BUDGET_TOO_SMALL`; it does not silently discard
the count/coverage metadata or return an empty success. Successful batch child
summary payloads carry the same fields. These additions describe CLI summary
output; detailed output and MCP retain their existing contracts.

## 日本語

`cdidx deps --summary-only --json --limit 2 --exclude-tests` の `count` は、
対応する依存関係クエリと同じ **返却ページの件数** です。edge 配列を省略しても、
グラフ全体の総件数にはなりません。`--cycles --summary-only` でも同様で、
件数の単位は依存循環（SCC）です。

| フィールド | 意味 |
| --- | --- |
| `count`、`returned_count` | フィルター後にこのページへ選択された依存関係の件数。summary では edge 配列を省略します。 |
| `count_kind`、`count_unit` | `returned` と、単位 `dependency_edges` または `dependency_cycles`。 |
| `page_limit` | 要求された表示上限。候補走査やグラフ解析の処理量上限とは別です。 |
| `has_more` | `true` は追加の一致を確認済み、`false` は利用できるクエリ結果を走査済み、`null` は候補範囲内では完了を証明できなかったことを示します。cycles では従来どおり、解析対象の候補グラフに後続の SCC があるかを示します。 |
| `candidate_scan_complete` | 候補の安全上限に達していないこと。cycles では `analysis_complete` と一致し、抽出の完全性は保証しません。 |
| `query_exhausted` | 候補範囲の確認が完了し、後続ページがないこと。索引に対するクエリの完了を示し、抽出範囲とは独立です。 |
| `total_count`、`total_count_available` | 既知の場合は索引に対するクエリの正確な総件数、それ以外は `null` / `false`。通常の summary は完了時のみ返します。cycles は完全な SCC 解析の総件数を部分ページでも再利用できます。 |
| `total_count_authoritative` | 総件数が既知で、参照グラフが完全かつ契約が縮退しておらず、workspace のグラフ範囲も確認済みであること。コンパイラーと同等の依存解析を保証するものではありません。 |
| `total_count_unavailable_reason` | `page_limit`、`candidate_scan_incomplete`、`workspace_candidate_scan_unverified`、`graph_edge_budget`。総件数が既知なら `null`。 |
| `total_count_non_authoritative_reason` | 数値の総件数が既知でも authoritative ではない理由。`reference_graph_incomplete`、`graph_contract_degraded`、`workspace_graph_coverage_unverified`。 |
| `truncated`、`truncated_reason` | 通常の summary では部分ページまたは未確認の候補範囲を示します。cycles は従来のページ／グラフ上限の契約を維持します。 |

一致する edge が3件なら、limit 1／2 はそれぞれの返却件数とともに
`has_more=true`、`query_exhausted=false`、総件数未確定を返します。
limit 3以上は、候補上限によって証明が妨げられない限り、3件と
`has_more=false`、`query_exhausted=true` を返します。0件の完了クエリも
総件数0を返せますが、抽出が不完全、またはグラフがない場合は
authoritative な不在の証拠にはなりません。

通常の summary は既存の SQL ranking window 内で、順位付きの結果を最大1件だけ
先読みします。この範囲や C# の候補上限は増やしません。C# の名前候補と
解決済み参照の候補を別々に確認し、edge 件数だけから完了を推測しません。
候補上限ちょうどの場合も安全側に倒して未完了とします。フィルターが先読み結果を
除去した場合、未確認の残りがあれば追加結果の有無は不明です。
複数 DB の workspace 集計には候補範囲を統合した完了証拠がないため、既存の
処理量上限を維持して `has_more` を不明、総件数を未確定とします。
完了を確認する場合は単一 DB に絞ってください。

絞り込みがない場合の250候補ファイルの guard、生成コード／テスト除外、
noise／参照証拠のフィルター、不完全なグラフの警告は維持します。
`--summary-only --format json-graph` は引き続き非対応です。
`reference_graph_complete=true` だけでは検索完了を証明できません。
循環内の node の表示省略（`display_truncated`）も SCC のページングとは別です。

`--max-json-bytes` は応答サイズの上限であり、クエリの処理量上限ではありません。
summary が収まらなければ `E028_RESPONSE_BUDGET_TOO_SMALL` を返し、件数や範囲の
メタデータを黙って捨てたり、空の成功応答を返したりしません。成功した batch の
子 summary も同じフィールドを持ちます。この追加は CLI の summary 出力が対象で、
詳細出力と MCP は既存の契約を維持します。
