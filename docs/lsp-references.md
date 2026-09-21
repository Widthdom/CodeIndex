# LSP reference delivery (#5392)

## English

`textDocument/references` returns all selected indexed locations within the safety
limits below. It preserves the position-resolved definition, overload and lexical
scope, exact indexed file identity, URI escaping and UTF-16 ranges. Existing
unresolved/name-fallback behavior remains; successful delivery does not imply
compiler-complete indexing. Document highlights retain their separate 50-reference
bound.

- Without `partialResultToken`, success returns one complete `Location[]`, including
  `[]` for no matches. The entire JSON-RPC response body is limited to 512 KiB.
- With a bounded string or integer `partialResultToken`, locations arrive in
  deterministic `$/progress` arrays, each at most 100 locations and 64 KiB of JSON
  notification body. Success ends with `result: null`; no locations are repeated in
  the final response. Empty results send no location chunks.
- `includeDeclaration: true` sends declarations first. Reference sources retain
  their resolved target order and each source retains database query order across
  pages. Identical URI/range locations are deduplicated across the entire request.
- The provider advertises work-done support. A supplied `workDoneToken` receives
  `begin`, optional count reports, and exactly one `end` on success, cancellation
  or failure while the transport remains writable.

Each request reads at most 100 reference rows per database page, examines at most
10,000 selected reference rows (plus one overflow probe), retains at most 10,000
unique locations, and permits at most 8 MiB summed serialized Location bodies.
Declarations count against the location/byte limits. The no-token response is
materialized only within its 512 KiB budget; the token path retains only one page,
one output chunk and the bounded deduplication set. Filtered fallback queries also
bound each raw materialization to the requested page size. A cancellable five-second
query deadline bounds database work; transport backpressure remains governed by the
existing bounded notification queue and disconnect cancellation.

Incomplete delivery never returns a successful truncated array. Resource/deadline
exhaustion returns LSP `RequestFailed` (`-32803`) with `error.data.reason`,
`deliveredLocationCount` and `recovery`. Reasons are `reference_row_limit`,
`reference_delivery_limit`, `response_byte_limit`, `progress_byte_limit` and
`query_deadline_exceeded`. A detected index-generation change returns
`ContentModified` (`-32801`, `index_generation_changed`). `$/cancelRequest` uses the
original typed request ID and returns `RequestCancelled` (`-32800`). Discard all
partial locations on any error; they are not a complete reference set.

For `response_byte_limit`, retry the same request with `partialResultToken`.
For generation changes, wait for indexing to finish and restart. For other limits,
use CLI pagination against the same database:

1. Run `cdidx inspect <symbol> --exact-name --json --limit 50 --db <db>` and identify
   the matching path, declaration line and signature in `candidate_bundles`.
   If needed, use `cdidx inspect --path <file> --line <declaration-line> --json --db <db>`
   to select the source location (CLI line numbers are one-based).
2. Copy that candidate's `selector.selector` into
   `cdidx inspect --selector <selector> --json --limit 50 --db <db>`.
3. Repeat that exact selector command with
   `--cursor <graph_sections.references.next_cursor>` from its preceding response
   until the references section has no continuation. Keep the database, selector
   and options unchanged; restart after an index-generation change. For a lexical
   overload family, repeat for each selected definition and deduplicate URI/ranges.

## 日本語

`textDocument/references` は、以下の安全上限内で選択対象のインデックス済み参照を
全件返します。位置から解決した定義、オーバーロード、字句スコープ、インデックス内の
完全一致ファイル識別、URI のエスケープ、UTF-16 範囲を維持します。未解決時や名前検索への
フォールバックは従来どおりで、配送成功がコンパイラー相当のインデックス完全性を意味する
わけではありません。ドキュメントハイライトの参照上限は別途 50 件のままです。

- `partialResultToken` がなければ、成功時に完全な `Location[]` を一度だけ返します。
  一致がなければ `[]` です。JSON-RPC 応答本文全体の上限は 512 KiB です。
- 上限付き文字列または整数の `partialResultToken` があれば、決定的な順序で
  `$/progress` の配列を送ります。各通知は最大 100 位置、JSON 本文 64 KiB です。
  成功時の最終応答は `result: null` で、位置を再送しません。空結果では位置通知を送りません。
- `includeDeclaration: true` では宣言を先に送ります。参照元の順序は解決済み対象順、
  各対象内はページを通じて DB クエリ順です。同じ URI・範囲は要求全体で重複除去します。
- プロバイダーは作業進捗への対応を通知します。`workDoneToken` があれば、通信経路が
  書込み可能な限り、成功・取消・失敗のいずれでも `begin`、必要に応じた件数報告、
  ちょうど一度の `end` を送ります。

要求ごとの DB ページは最大 100 参照行、調査対象は最大 10,000 参照行と超過確認の 1 行、
一意な位置は最大 10,000 件、シリアライズした Location 本文の合計は最大 8 MiB です。
宣言も位置件数・バイト上限に含めます。トークンなしでは 512 KiB の予算内だけを保持し、
トークンありでは 1 ページ、1 出力チャンク、上限付き重複除去集合を保持します。
フィルター付きフォールバックでも生データの取得単位は要求ページサイズ以内です。
取消可能な 5 秒の期限で DB 処理を制限します。通信の待機は既存の上限付き通知キューと
切断時の取消に従います。

不完全な配送を切り詰め済み成功配列として返すことはありません。リソース・期限の上限に
達すると LSP `RequestFailed` (`-32803`) と `error.data.reason`、
`deliveredLocationCount`、`recovery` を返します。理由は `reference_row_limit`、
`reference_delivery_limit`、`response_byte_limit`、`progress_byte_limit`、
`query_deadline_exceeded` です。インデックス世代の変更を検出すると
`ContentModified` (`-32801`、`index_generation_changed`) を返します。
`$/cancelRequest` は元の要求 ID を型も含めて照合し、`RequestCancelled` (`-32800`) を
返します。どのエラーでも、それまでの部分結果は完全な参照集合ではないため破棄してください。

`response_byte_limit` では同じ要求に `partialResultToken` を付けて再試行します。
世代変更時はインデックス作成の終了を待ち、要求をやり直します。その他の上限では、
同じ DB に対する CLI のページ取得を使います。

1. `cdidx inspect <symbol> --exact-name --json --limit 50 --db <db>` を実行し、
   `candidate_bundles` のパス・宣言行・シグネチャで対象を確認します。必要なら
   `cdidx inspect --path <file> --line <declaration-line> --json --db <db>` で
   ソース位置を選びます。CLI の行番号は 1 始まりです。
2. 対象候補の `selector.selector` をコピーして
   `cdidx inspect --selector <selector> --json --limit 50 --db <db>` を実行します。
3. 直前の応答の `graph_sections.references.next_cursor` を
   `--cursor <cursor>` として同じ selector コマンドに加え、継続がなくなるまで繰り返します。
   DB・selector・オプションは変えず、世代変更時は最初から取得します。字句スコープ内の
   オーバーロード群では各選択定義について取得し、URI・範囲を重複除去します。
