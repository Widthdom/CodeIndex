# Find Scan Controls

## English

Literal and line-regex `find` read indexed chunks in line/chunk order without
sorting source content. Current indexes use the ordered partial chunk index;
older indexes page at most 64 scalar chunk records through a metadata-only sort,
then read each selected chunk's content separately. Pages use the last ordering
key rather than an increasing offset. Retained sort data is bounded, but legacy
pages may rescan file metadata and one chunk's content is still read at a time.
A bounded-memory NULL-content check preserves the existing missing-content error
by selecting the all-row fallback for such files; that check may scan the file's
chunk entries. Missing chunks never trigger live-file reads. Overlap ownership,
counts, line caps and continuation positions are unchanged; no reindex is required.

### CLI cursor validation errors (#5412)

Bounded `find` validation honors machine output selected by `--json`,
`--json=ndjson`, `--format json`, `--json-envelope`, `--fields`, compact output,
or `--max-json-bytes`. Malformed, query-mismatched and stale cursors return exit
`1` with a single error object on stdout and no human diagnostic on stderr.
Before query execution, this is a top-level error object even for envelope or
field projection requests: `api_version:"1"`, `status:"error"`, `command:"find"`,
`exit_code:1`, `error_code:"E010_USAGE_ERROR"`, `message`, `hint`, and `category`
(`cursor_malformed`, `cursor_mismatch`, or `cursor_stale`). Errors are not projected.
Other shared bounded-control validation failures use `category:"usage"`.
The shared bounded-query wrapper uses the same error contract for other commands
when machine output is selected, with their own `command` identity.

Use an opaque `next_cursor` from the same query and filters. After indexing,
restart without `--cursor`. This applies to literal, line-regex and multiline
find; ordinary human diagnostics and standalone count continuation retain their
existing formats.

The byte cap includes the serialized error's final platform newline. If the
complete error cannot fit, stdout instead contains `E028_RESPONSE_BUDGET_TOO_SMALL`
with the measured `minimum_required_bytes`, retry budget and original
`validation_error` object. This budget diagnostic may exceed the requested cap,
as with other response-budget errors; it never reports an empty success or drops
the cursor reason. Retry at the recommended size, then follow the validation hint.

Regex is line-local by default. To match adjacent lines such as `A\nB`, use
`--regex --multiline` (MCP `regex:true, multiline:true`); see
[bounded multiline windows](find-multiline.md#english). Window mode rejects semantic
filters rather than silently dropping cross-origin evidence.

Python code/comment/string origins are available in search and filtered regex find,
including MCP. CLI Python context also supports `--origin-passes`; see the
[syntax envelope, budgets and recovery contract](python-origin-classification.md#english).

### Ad hoc search count authority (#5357)

CLI `search --count` / `--format count`, `--group-by ... --count`, `--count-by`,
`--unique`, grouped results, and named-query count summaries observe origin
coverage before origin/result-kind exclusions and output selection. With semantic
filters (or grouping by origin), unknown candidates set
`origin_classification_complete:false`, `degraded:true`, `authoritative_count:false`,
and `partial_result:true`, even when filtering leaves zero results. Named count
summaries carry this evidence per query and for the combined result. Counts still
describe accepted indexed search result rows, not regex-find occurrences.

Incomplete classification returns exit `11`; `--allow-partial` permits `0` while
preserving the incomplete metadata. Human counts warn on stderr. JSON includes
`origin_passes`, bounded `classification_incomplete_reasons`, and
`classification_recovery_guidance`. When `retry_origin_passes` is present, rerun
with that `--origin-passes` value for another bounded pass. Inspect unknown matches
without semantic exclusions and manually review missing/malformed context or
regions beyond the maximum budget. Increasing output limits cannot repair lexical
coverage. Complete zero counts remain authoritative; intentional group/row output
omissions remain separate from classification completeness. Existing index and
query degradation checks still apply: complete classification cannot restore
count authority when `wal_stale_snapshot_risk:true`. This query fix requires no reindex.

### MCP search and continuation (#5349)

MCP `search` (including recipes), `find`, and `find_in_file` accept `origin`,
`excludeOrigin`, and `resultKind` as comma-separated strings or string arrays,
plus `excludeComments`, `excludeStrings`, and `excludeFixtures` booleans.
They share CLI validation and classification, applying filters before counts
and pagination. Find semantic filters require `regex:true` and support origin
names and `identifier` result kinds. Search also supports its declaration and
call-site classifications. Classifier coverage is unchanged.

```json
{"name":"search","arguments":{"query":"File.Delete","origin":"code"}}
{"name":"find","arguments":{"query":"TODO|FIXME|HACK","regex":true,"all":true,"limit":30,"lineScanLimit":20000,"maxBytes":65536,"excludeTests":true}}
```

`find` requires either `all:true` or `path`; `find_in_file` continues to require
`path`. Both accept the existing context, language and exclusion options, plus
`cursor`, `countOnly`, and `maxBytes`. Only `find` with `all:true` accepts
`lineScanLimit`: default 250,000, maximum 10,000,000 lines per page, with a
4,096-file cap. Both tools retain the MCP 200-result maximum. Their default
`maxBytes` is 65,536 UTF-8 bytes in `structuredContent`; the enclosing response
must also fit the server budget. Byte fitting keeps whole rows and returns a
cursor before any omitted matches. An unfit minimum page returns
`E028_RESPONSE_BUDGET_TOO_SMALL` without consuming the input cursor.

Pass `next_cursor` back as `cursor` until `has_more:false`. Keep the query,
scope, classification filters and `countOnly` mode unchanged; `limit`,
`maxBytes`, and `lineScanLimit` may change. Cursors bind indexed source identity
and generation, including raw same-line and zero-width match positions. Discard
them after indexing. Malformed, mismatched and stale cursors return structured
errors. Timeout or cancellation does not issue a new cursor.

MCP find results preserve scan budgets, `scan_complete`, `partial_result`,
`authoritative_rows`/`authoritative_count`, `origin_classification_complete`,
`unknown_origin_matches`, and recovery guidance. Unknowns rejected by filters
still degrade authority. Resumed pages describe only their segment and remain
non-authoritative; sum unchanged-source count pages to obtain the full count.
Semantic search also exposes `candidate_scan_complete` and classification
completeness; bounded or unknown coverage cannot prove absence. Its cursor
binds filters and generation and supports changing the row limit. STDIO, HTTP,
and `batch_query` share these handlers and structured results.

Guarded semantic `countOnly` requests use the CLI's indexed result units; with
`tokenBoundary:true` they retain its row-count convention. Query-limit and
unavailable same-symbol-scope errors preserve the existing argument-error
recovery. Recipe children and their parent expose `partial_result`, `degraded`,
and recovery guidance when classification or candidate coverage is incomplete.
An empty page inside a capped ranking window does not establish complete absence.
Page fullness alone does not degrade an otherwise completed scan.

### Bounded C# lexical continuation (#5348)

For `search`, `audit`, and `find --regex`, explicitly request `--origin-passes <n>`
(1–16, default 1) to continue C# lexical state across additional indexed windows.
For example, rerun `cdidx find return --regex --path large.cs --origin code
--origin-passes 2 --count --json`. Each pass classifies at most 4,096 new lines
and reads at most 8 Mi UTF-16 characters (including chunk overlap) and 128 chunks
per file. Previously retained overlap is compared without replaying lexical state.
Up to 16 passes retain at most 65,536 lines / 128 Mi source characters per file;
result limits and page offsets do not change this budget. Smaller character/chunk
windows can advance too, provided at least one complete new line is available.

Lexical state is resumed within that invocation's indexed snapshot; no checkpoint
survives the query and no schema migration or reindex is required. Every new query
replays its requested passes from the beginning. The pass setting binds cursors and
recipe replay: restart **without `--cursor`** when changing it. Comments, string
delimiters, interpolation frames and schema context carry across windows; results
are classified only after the requested bounded work finishes. Indexed generation
changes discard the provisional context. Conflicting overlap also discards the
entire context, because earlier interpolation decisions may depend on its closing
text. Live source edits require normal indexing.

Unknown facets expose `origin_unavailable.reason` and, when another pass may help,
`retry_origin_passes` plus `recovery_guidance`. Find terminal/count output also
exposes `origin_passes`, `classification_incomplete_reasons`, and optional
`retry_origin_passes`. A retry may make further bounded progress without completing
the file. Missing/conflicting chunks, malformed constructs, an individual line or
chunk prefix that cannot fit, and the 16-pass ceiling can still prevent completion;
inspect those regions manually. Find retains unknowns and partial exit `11`,
including explicit `--origin unknown` and unknown matches rejected by filters.
MCP search retains its existing one-pass behavior and shared classifier.

### Regex origin filters (#5324)

Use `cdidx find 'XmlReader\.Create' --regex --path src/ --origin code --json`.
`--origin` (alias `--match-origin`), `--exclude-origin`, `--result-kind`,
`--exclude-comments`, `--exclude-strings`, and `--exclude-fixtures` require
`--regex`. Inclusive origin/kind lists use OR within a list and AND between
filters; exclusions win. Repeated and comma-separated values are accepted.
Filtering happens per exact regex occurrence before counts, offsets and limits.
All semantic options bind continuation cursors. A row page resumed from a native find scan position reports only
its scan segment; its bounded `total_count_authoritative` remains false.

Find reuses search's bounded C#/Python indexed-prefix classifiers (4,096 lines,
8 Mi characters, 128 chunks per pass) and line-local shell classifier. Other
languages and missing/capped context remain `unknown`. Python additionally
classifies zero-width end-of-line positions; C#/shell keep their existing unknowns.
Rows expose `match_facets` with original UTF-16 line/column/length, including
zero length. `result_kinds` supports origin names and search's `identifier`
projection for code; `declaration` and `call_site` are not supported here.
Fixture classification uses recognized test-file paths and string-like origins;
`test_symbol` is not inferred from regex text.

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

リテラル検索と行単位の正規表現 `find` は、本文をソートせず行・チャンク順に索引を読みます。
現在の索引では順序付き部分インデックスを使い、旧索引では最大64件のチャンクメタデータだけを
ソートしてから、選択した各チャンクの本文を個別に取得します。ページは増大する offset ではなく
直前の順序キーから再開します。ソートの保持量には上限がありますが、旧索引ではページごとに
ファイルのメタデータを再走査する場合があり、本文もチャンク1個単位では読み取ります。
NULL 本文の確認は保持メモリを制限して行い、該当するファイルでは全行を対象とする代替経路により
従来の本文欠落エラーを維持します。この確認もファイルのチャンク行を走査する場合があります。
チャンクが欠けても実ファイルへ読み取りを切り替えません。重複行の優先順、件数、行上限、
継続位置は従来どおりで、再索引は不要です。

### CLI カーソルの検証エラー (#5412)

上限付き `find` の検証は、`--json`、`--json=ndjson`、`--format json`、
`--json-envelope`、`--fields`、compact 出力、`--max-json-bytes` による機械向け出力の
指定を尊重します。不正・クエリ不一致・索引の世代変更済みカーソルでは終了コード `1` と
単一のエラーオブジェクトを stdout に返し、人向け診断を stderr に出しません。
クエリ実行前は envelope・フィールド投影の指定時もトップレベルのエラーです。
`api_version:"1"`、`status:"error"`、`command:"find"`、`exit_code:1`、
`error_code:"E010_USAGE_ERROR"`、`message`、`hint`、`category` を含みます。
`category` は `cursor_malformed`、`cursor_mismatch`、`cursor_stale` のいずれかで、
その他の共有出力制御の検証失敗は `usage` です。エラーにはフィールド投影を適用しません。
共有ラッパーを使う他のコマンドでも、機械向け出力の指定時は同じエラー形式を使い、
`command` にそのコマンド名を保持します。

同じクエリ・フィルターから返された不透明な `next_cursor` を使ってください。
索引更新後は `--cursor` を外して再開します。リテラル・行単位正規表現・複数行 find に
適用され、人向け診断と独立した件数取得の継続処理は既存の形式を維持します。

バイト上限にはエラー末尾のプラットフォーム固有の改行も含めます。完全なエラーが収まらない
場合は `E028_RESPONSE_BUDGET_TOO_SMALL` を stdout に返し、実測した
`minimum_required_bytes`、再試行用の上限値、元の `validation_error` を保持します。
他の応答サイズエラーと同様、この診断自体は指定上限を超える場合があります。
空の成功応答にしたり、カーソルの理由を省略したりはしません。推奨サイズで再試行してから、
検証エラーの復旧案内に従ってください。

通常の正規表現は行単位です。`A\nB` のような隣接行には `--regex --multiline`
（MCP は `regex:true, multiline:true`）を使います。[上限付きの複数行窓](find-multiline.md#日本語)を
参照してください。窓モードは意味フィルターを明示的に拒否し、origin をまたぐ証拠を黙って省略しません。

search と意味フィルター付き正規表現 find は、MCP を含め Python のコード・コメント・文字列の
origin に対応します。CLI の Python 文脈も `--origin-passes` を使えます。
[構文の対応範囲・予算・復旧契約](python-origin-classification.md#日本語)を参照してください。

### 通常検索の件数の確定性 (#5357)

CLI の `search --count` / `--format count`、`--group-by ... --count`、`--count-by`、
`--unique`、グループ化した結果、名前付きクエリの件数要約は、origin・結果種別の除外や
出力選択より前に分類の網羅性を確認します。意味フィルター付き、または origin ごとの集計で
unknown の候補があれば、除外後がゼロ件でも `origin_classification_complete:false`、
`degraded:true`、`authoritative_count:false`、`partial_result:true` を返します。
名前付きクエリの件数要約は各クエリと全体の両方にこの情報を保持します。件数の単位は
引き続き採用された索引検索の結果行であり、正規表現 find の一致箇所数とは異なります。

分類が不完全な場合の終了コードは `11` です。`--allow-partial` は不完全さの情報を保ったまま
`0` を許容し、人向けの件数出力は標準エラーへ警告します。JSON は `origin_passes`、上限付きの
`classification_incomplete_reasons`、`classification_recovery_guidance` を返します。
`retry_origin_passes` があれば、その値を `--origin-passes` に指定して追加の上限付き分類を
実行できます。意味フィルターによる除外を外して unknown を確認し、欠落・不正な文脈や最大予算を
越える領域は手動で調べてください。出力上限を増やしても字句分類の網羅性は回復しません。
完全に評価したゼロ件は確定性を保ち、意図的なグループ・行の出力省略と分類完了状態は区別します。
索引やクエリに関する従来の確定性チェックも適用されます。`wal_stale_snapshot_risk:true` の
場合は、分類が完了しても件数を確定扱いにはしません。この修正に再索引は不要です。

### MCP の検索と継続取得 (#5349)

MCP の `search`（recipe を含む）、`find`、`find_in_file` は、カンマ区切り文字列または
文字列配列の `origin`、`excludeOrigin`、`resultKind` と、真偽値の `excludeComments`、
`excludeStrings`、`excludeFixtures` に対応します。CLI と同じ検証・分類処理を使い、
件数とページ分割の前にフィルターを適用します。find の意味フィルターには `regex:true` が
必要で、結果種別は origin 名と `identifier` に対応します。search は宣言・呼び出し位置の
分類にも対応します。分類器の対応範囲は従来どおりです。

```json
{"name":"search","arguments":{"query":"File.Delete","origin":"code"}}
{"name":"find","arguments":{"query":"TODO|FIXME|HACK","regex":true,"all":true,"limit":30,"lineScanLimit":20000,"maxBytes":65536,"excludeTests":true}}
```

`find` は `all:true` または `path` の一方が必要です。`find_in_file` は引き続き `path` を
必須とします。両方で従来の文脈・言語・除外指定に加え、`cursor`、`countOnly`、`maxBytes` を
使用できます。`lineScanLimit` は `find` の `all:true` 時のみ対応し、ページあたり既定
250,000 行、最大 10,000,000 行、ファイル数上限は 4,096 件です。MCP の結果上限は両方とも
200 件を維持します。`maxBytes` の既定値は `structuredContent` の UTF-8 サイズで
65,536 バイトです。外側の応答もサーバーの上限内に収めます。サイズ調整では行を分断せず、
省略した一致の直前を指すカーソルを返します。最小ページも入らない場合は入力カーソルを
進めず `E028_RESPONSE_BUDGET_TOO_SMALL` を返します。

`has_more:false` になるまで `next_cursor` を `cursor` として渡します。検索語・範囲・
分類フィルター・`countOnly` は同じ値を維持し、`limit`、`maxBytes`、`lineScanLimit` は
変更できます。カーソルは索引の識別情報と世代、同じ行やゼロ幅の一致位置も保持します。
再索引後は破棄してください。形式不正・条件不一致・古い世代は構造化エラーになり、
タイムアウトやキャンセルでは新しいカーソルを発行しません。

MCP の find は走査上限、`scan_complete`、`partial_result`、`authoritative_rows` /
`authoritative_count`、`origin_classification_complete`、`unknown_origin_matches` と
復旧案内を保持します。フィルターで除外した unknown も確定性を低下させます。再開ページは
その区間だけを表すため、最終ページでも確定扱いにはしません。同じ索引の件数ページを
合算すると全件数が得られます。意味フィルター付き search も `candidate_scan_complete` と
分類完了状態を示し、上限到達や unknown が残る場合に不在を証明しません。カーソルは条件と
世代に紐づき、行数上限は変更できます。STDIO・HTTP・`batch_query` は共通ハンドラーと
構造化結果を使います。

guard 付きの意味フィルター検索で `countOnly` を指定すると、CLI と同じ索引結果単位で
数えます。`tokenBoundary:true` 時は CLI の行数カウントを維持します。検索上限や
same-symbol 範囲を利用できない場合のエラーは、従来の引数エラーと復旧案内を保ちます。
分類や候補の走査が不完全な recipe は、子結果と全体の両方に `partial_result`、
`degraded` と復旧案内を含めます。順位付けの候補上限内で空ページになっても、完全な不在を
示すものではありません。
一方、ページが満杯になっただけで、完了済みの走査を不完全扱いにはしません。

### 上限付き C# 字句分類の継続 (#5348)

`search`、`audit`、`find --regex` では `--origin-passes <n>`（1〜16、既定 1）を
明示すると、索引済みの次の窓へ C# の字句状態を引き継げます。例えば
`cdidx find return --regex --path large.cs --origin code --origin-passes 2 --count --json`
で再実行します。各パスで新しく分類する行はファイルごとに最大 4,096 行、読み取る量は
チャンクの重複分を含む 8 Mi UTF-16 文字、128 チャンクです。保持済みの重複部分は
字句状態を再実行せず照合します。最大 16 パスで保持するソースはファイルごとに
65,536 行／128 Mi 文字以内で、結果件数やページ位置で上限は変わりません。文字数・チャンク数で
窓が小さくなっても、完全な新しい行を 1 行以上取得できれば前進できます。

状態の継続は同じ呼び出しの索引スナップショット内に限定し、クエリを越えてチェックポイントを
保持しません。スキーマ移行や再索引は不要で、新しいクエリは指定パス数を先頭から再実行します。
パス数はカーソルと recipe 再実行の条件に含まれるため、変更するときは **`--cursor` を外して**
再開始してください。コメント、文字列の区切り、補間フレーム、schema の文脈を窓の間で引き継ぎ、
指定した上限付き処理が終了してから結果を分類します。索引世代が変われば暫定文脈を破棄します。
重複行が不一致の場合も、先行する補間の判定がその閉じ区切りに依存し得るため、文脈全体を破棄します。
実ソースの編集を反映するには通常の索引更新が必要です。

unknown の facet は `origin_unavailable.reason` を返し、追加パスが役立つ場合は
`retry_origin_passes` と `recovery_guidance` も返します。find の終端・件数出力には
`origin_passes`、`classification_incomplete_reasons`、任意の `retry_origin_passes` が加わります。
再試行は前進してもファイル全体を完了できるとは限りません。チャンクの欠落・不整合、不正な構文、
窓に収まらない単独行やチャンク先頭、16 パスの上限で完了できない部分は手動で確認してください。
`--origin unknown` の明示指定やフィルターで除外した unknown を含め、unknown と partial 終了コード
`11` は find で維持します。MCP search は共通分類器を使い、従来の 1 パス動作を維持します。

### 正規表現の origin フィルター (#5324)

`cdidx find 'XmlReader\.Create' --regex --path src/ --origin code --json` を使います。
`--origin`（別名 `--match-origin`）、`--exclude-origin`、`--result-kind`、
`--exclude-comments`、`--exclude-strings`、`--exclude-fixtures` は `--regex` が必要です。
指定値のリスト内は OR、フィルター間は AND で、除外指定を優先します。繰り返し指定と
カンマ区切りに対応します。正規表現の各一致を件数・offset・limit の適用前に分類し、
すべての意味フィルターを継続カーソルへ紐づけます。find の走査位置から再開した行ページはその走査区間だけを
報告するため、上限付き出力の `total_count_authoritative` は false を維持します。

find は search と共通の C#／Python 索引済みプレフィックス分類器（各パス 4,096 行、8 Mi 文字、
128 チャンク）と行単位の shell 分類器を使います。それ以外の言語や文脈の欠落・上限超過は
`unknown` のままです。Python は行末のゼロ幅位置も分類し、C#／shell は従来の unknown を維持します。
行の `match_facets` は元の UTF-16 行・列・長さを保持し、長さ 0 にも対応します。
`result_kinds` は origin 名と、code に対する search と同じ `identifier` 投影に対応します。
`declaration` と `call_site` には対応しません。fixture は認識済みテストファイルのパスと文字列系 origin から
判定し、正規表現の文字列から `test_symbol` を推測しません。

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
