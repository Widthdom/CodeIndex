# Find Scan Controls

## English

### Regex origin filters (#5324)

Use `cdidx find 'XmlReader\.Create' --regex --path src/ --origin code --json`.
`--origin` (alias `--match-origin`), `--exclude-origin`, `--result-kind`,
`--exclude-comments`, `--exclude-strings`, and `--exclude-fixtures` require
`--regex`. Inclusive origin/kind lists use OR within a list and AND between
filters; exclusions win. Repeated and comma-separated values are accepted.
Filtering happens per exact regex occurrence before counts, offsets and limits.
All semantic options bind continuation cursors.

This v1 reuses search's bounded C# indexed-prefix classifier (4,096 lines,
8 Mi characters, 128 chunks) and line-local shell classifier. Other languages,
missing/capped C# context, and zero-width end-of-line positions remain `unknown`.
Rows expose `match_facets` with original UTF-16 line/column/length, including
zero length. `result_kinds` supports origin names and search's `identifier`
projection for code; `declaration` and `call_site` are not supported here.
Fixture classification uses recognized test-file paths and string-like origins;
`test_symbol` is not inferred from regex text. There is no new MCP find surface.

Filtered row output supports text and JSON/NDJSON, including bounded `--fields`,
`--cursor` and `--max-json-bytes`; formats without terminal authority metadata
are rejected. Count mode remains available. The terminal/count object reports
`origin_classification_complete` and `unknown_origin_matches` for the scanned
segment, including unknown occurrences rejected by filters. Unknowns make
absence/count authority false and return exit `11` (`--allow-partial` accepts
`0`). Scan completion is separate from classification completeness; inspect
unknown matches without exclusions instead of treating filtered absence as
proof. Regex timeouts, cancellation, scan caps and literal/indexed fast paths
retain their existing behavior. No reindex is needed.

`cdidx find --all` scans indexed files across the repository with bounded safety
caps. The default indexed-line cap is 250,000 lines. Use
`--line-scan-limit <n>` with `--all` to lower or raise that cap, up to
10,000,000 lines.

Default JSON rows end with one `terminal_record` containing `scan_complete`,
`authoritative_rows`, `candidate_files`, `files_scanned`, `lines_scanned`, the
effective file/line caps, truncation reason, continuation action, and recovery
guidance. Count JSON carries the same terminal scan state in its single object
and uses `authoritative_count` for count authority.

With `--all`, row formats that cannot carry this terminal state are rejected:
JSON array, compact, CSV/TSV, LSP, quickfix, and SARIF. Use default text,
streaming NDJSON, or count output instead.

Candidate-file or line-scan truncation returns partial-result exit code `11`.
Pass `--allow-partial` only when an incomplete scan may return exit code `0`.
An ordinary result-limit early stop remains successful, but the row terminal
sets `scan_complete=false`, `result_limit_reached=true`, and returns a
`next_cursor`. Pass it back with `--cursor`; `--limit` may be changed for the
next page. Human output writes the cursor and scan summary to stderr and uses
the same partial exit semantics.

Find continuation cursors are opaque and resume at the next match record,
including when multiple matches share a line, the line contains Unicode, or
the line is very large. The cursor binds the query, literal/regex mode and
other result-affecting options, candidate-file ordinal and path, line and match
ordinal, UTF-8 byte position, source identity, and index generation. Reusing a
cursor with different options returns `cursor_mismatch`; using it after the
indexed source changes returns `cursor_stale`; malformed or invalid positions
return `cursor_malformed`. The final page sets `has_more=false` and
`next_cursor=null`. A cancelled or regex-timeout request does not advance or
issue a continuation cursor; retry the last cursor from a successfully
completed page.

When count mode reaches a scan cap, its `count` is the count for that partial
scan page and `next_cursor` resumes at the next line boundary. Continue until
`has_more=false`; summing the page counts yields the complete count for an
unchanged indexed source. Because a resumed page contains only its segment of
the total, every resumed count page keeps `authoritative_count=false`, including
the final page.

For scoped `--path` searches, `--format compact` returns locations only. Use
text or JSON output when context from `--before`, `--after`, or
`--snippet-lines` is needed.

## 日本語

### 正規表現の origin フィルター (#5324)

`cdidx find 'XmlReader\.Create' --regex --path src/ --origin code --json` を使います。
`--origin`（別名 `--match-origin`）、`--exclude-origin`、`--result-kind`、
`--exclude-comments`、`--exclude-strings`、`--exclude-fixtures` は `--regex` が必要です。
指定値のリスト内は OR、フィルター間は AND で、除外指定を優先します。繰り返し指定と
カンマ区切りに対応します。正規表現の各一致を件数・offset・limit の適用前に分類し、
すべての意味フィルターを継続カーソルへ紐づけます。

v1 は search と共通の C# 索引済みプレフィックス分類器（4,096 行、8 Mi 文字、128 チャンク）と
行単位の shell 分類器を使います。それ以外の言語、C# 文脈の欠落・上限超過、行末のゼロ幅位置は
`unknown` のままです。行の `match_facets` は元の UTF-16 行・列・長さを保持し、長さ 0 にも対応します。
`result_kinds` は origin 名と、code に対する search と同じ `identifier` 投影に対応します。
`declaration` と `call_site` には対応しません。fixture は認識済みテストファイルのパスと文字列系 origin から
判定し、正規表現の文字列から `test_symbol` を推測しません。MCP の find 機能は追加しません。

フィルター付きの行出力は text と JSON/NDJSON に対応し、`--fields`、`--cursor`、
`--max-json-bytes` も使用できます。終端の確定性情報を保持できない形式は拒否します。件数出力も利用できます。
終端・件数オブジェクトには走査区間の `origin_classification_complete` と `unknown_origin_matches` を出力し、
フィルターで除外した unknown も計上します。unknown があれば不在・件数の確定性を false にして終了コード `11` を
返します（`--allow-partial` で `0` を許容）。走査完了と分類完了は別です。フィルター後の不在を証明とせず、
除外指定なしで unknown の一致を確認してください。タイムアウト、キャンセル、走査上限、従来のリテラル検索と
索引候補の高速経路は従来どおりです。再索引は不要です。

`cdidx find --all` は repository 全体の index 済みファイルを safety cap 付きで
走査します。既定の indexed-line cap は 250,000 行です。`--all` と一緒に
`--line-scan-limit <n>` を指定すると、この cap を最大 10,000,000 行まで上げ下げできます。

既定 JSON row は、`scan_complete`、`authoritative_rows`、
`candidate_files`、`files_scanned`、`lines_scanned`、有効な file / line cap、
切り詰め理由、continuation action、復旧案内を含む 1 件の `terminal_record` で
終了します。count JSON は単一 object に同じ終端 scan 状態を持ち、count の
authority には `authoritative_count` を使います。

`--all` では、この終端状態を表現できない JSON array、compact、CSV/TSV、
LSP、quickfix、SARIF の row 形式を拒否します。代わりに既定 text、streaming
NDJSON、count 出力を使ってください。

candidate-file または line-scan による切り詰めは partial-result 終了コード `11` を
返します。不完全な scan でも終了コード `0` を許容する場合だけ `--allow-partial` を
指定してください。通常の result limit による早期停止は成功のままですが、row 終端は
`scan_complete=false`、`result_limit_reached=true` と `next_cursor` を返します。
この値を `--cursor` へ渡して続行してください。次の page では `--limit` を変更できます。
human output は cursor と scan summary を stderr に出し、同じ partial exit semantics を
使います。

find continuation cursor は opaque で、同じ行に複数の match がある場合、Unicode を
含む行、非常に長い行でも、次の match record から再開します。cursor は query、
literal / regex mode、結果に影響するその他の option、candidate-file ordinal と path、
line と match ordinal、UTF-8 byte position、source identity、index generation に
紐づきます。異なる option での再利用は `cursor_mismatch`、indexed source の変更後の
再利用は `cursor_stale`、不正な形式または位置は `cursor_malformed` を返します。
最終 page は `has_more=false`、`next_cursor=null` になります。cancel または regex
timeout になった request は continuation cursor を進めたり新しく発行したりしません。
正常に完了した直前の page が返した cursor を再試行してください。

count mode が scan cap に達した場合、`count` はその部分 scan page の件数となり、
`next_cursor` は次の line boundary から再開します。`has_more=false` になるまで続けると、
変更されていない indexed source では各 page の count の合計が完全な件数になります。
再開後の page は全体の一部分だけを含むため、最終 page を含むすべての再開 count page で
`authoritative_count=false` のままになります。

`--path` で scope を限定した検索では、`--format compact` は location のみを返します。
`--before`、`--after`、`--snippet-lines` の context が必要な場合は text または
JSON output を使ってください。
