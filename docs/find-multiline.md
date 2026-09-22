# Bounded multiline find

## English

Ordinary `find --regex` passes one indexed line at a time to the regex engine.
`(?s)` alone cannot match across lines. Opt in to source windows explicitly:

```sh
cdidx find 'using System\.Globalization;\nusing System\.Text\.Json;' \
  --regex --multiline --window-lines 8 --path src/CodeIndex/Cli/QueryCommandRunner.Find.cs --json
```

MCP `find` and `find_in_file` use `regex:true`, `multiline:true`, `windowLines`
and `windowBytes`. For example:

```json
{"name":"find","arguments":{"query":"A\\nB","path":"src/","regex":true,"multiline":true,"windowLines":8}}
```

The source is the indexed snapshot, never live files. Existing indexes need no
migration. Literal and ordinary line regex behavior are unchanged.

### Matching and coordinates

Each eligible physical source line owns a forward window of up to `windowLines`
complete lines, ending earlier only at EOF. Adjacent windows overlap; stored
chunk overlap is deduplicated in the existing start-line/chunk-index order.
Windows never cross files or missing source lines. CRLF is normalized to LF.
The byte limit counts the normalized UTF-8 window, including LF separators.
LF is inserted between window lines, with no extra separator after the final line;
a stored empty final line still contributes its preceding separator.

Only matches starting on the owning line are emitted. A consumed match suppresses
starts inside its span, even on later lines. Matches are non-overlapping and ordered
by the existing file order, then original start line and column. Zero-width matches
advance one UTF-16 code unit; they are emitted once, including end-of-line positions.
The engine retains its case-insensitive default; `--exact` makes matching case-sensitive.

This is a **window regex contract**, not whole-file regex semantics. The maximum
supported span is the selected number of source lines, provided the entire window
fits its byte limit. Longer matches and lookaround outside that window are outside
the contract. Greedy quantifiers see the window end. Without `(?m)`, `^`/`$` refer
to window boundaries (and .NET's final-newline rule); `\A`/`\z` also refer to that
window. `(?m)` changes anchors, while `(?s)` lets dot consume LF. Neither flag
enables source windows by itself. For example, `A\nB` spans two lines, while
`(?s)A.*B` also crosses LF; `(?m)^A$` anchors to physical lines inside the window.

Rows retain `line`/`column` as the one-based original start. Additive
`match_end_line`/`match_end_column` give the one-based **exclusive** end.
Columns count UTF-16 code units, including astral characters as two units.
`length` counts the LF-normalized UTF-16 match, including intervening LF.
`start_line`/`end_line` still describe the snippet, not the match span.
Snippets show only the starting line, at most 512 columns; use `excerpt` to inspect
the returned span. A long match never forces a correspondingly large snippet.

### Limits and completeness

| Resource | Default | Hard maximum |
|---|---:|---:|
| `--window-lines` / `windowLines` | 8 | 64 |
| `--window-bytes` / `windowBytes` | 65,536 | 262,144 |
| Materialized indexed chunk | fixed | 1 MiB UTF-8 |
| Source bytes read per query, including chunk overlap | fixed | 32 MiB |
| Regex input bytes per query, including window overlap | fixed | 32 MiB |
| Evaluated owner lines per query | fixed | 250,000 |
| Regex invocation timeout | fixed | 100 ms |
| Cooperative query deadline | fixed | 5 seconds |
| Returned rows | 20 CLI default | 200 |

The deadline is checked between source/matcher steps; an in-flight regex may run
until its 100 ms timeout, and database operations retain their existing cancellation
policy. Source retention is bounded independently of file size: one chunk of at
most 1 MiB, at most 64 window lines totaling the window byte limit, one incoming
line bounded by that limit, and one joined window. Each UTF-8 byte can require up
to two managed UTF-16 bytes. Completed rows retain at most 200 bounded snippets;
no full-file buffer or query-wide source cache is retained. Regex allocations are
also limited by the existing query-length cap and the bounded input and timeout.

`--all` retains its 4,096-file cap and configurable `--line-scan-limit`
(MCP `lineScanLimit`); this counts evaluated owner lines in multiline mode.
Consumed lines inside a preceding match need no additional regex evaluation.
Lookahead is separately charged to the byte limits. Window limits also apply to
scoped `--path` queries, and cannot be disabled with `--allow-partial`.

Terminal/count metadata includes `multiline`, `window_contract_version:1`,
`window_lines`, `window_bytes`, `count_scope:bounded_multiline_nonoverlapping_matches`
and `unbounded_absence_authoritative:false`. A completed count is authoritative
only for this bounded contract and selected indexed scope. Zero matches never
prove absence of arbitrary-length matches. Count means accepted occurrences,
not matching source lines or files.

Execution omissions set `scan_truncated`, `partial_result`, and false authority,
with exit `11` (or `0` with explicit `--allow-partial`). Stable reasons are
`multiline_window_bytes`, `multiline_chunk_bytes`, `multiline_source_gap`,
`multiline_query_bytes`, `multiline_query_lines`, and `multiline_query_time`.
These failures have no advancing cursor. Follow `recovery_guidance`: restart with
a reviewed window size or narrower file scope, refresh missing indexed source,
or manually inspect/split oversized source. Increasing output limits cannot repair
coverage. Invalid regex, regex timeout (`E014_REGEX_MATCH_TIMEOUT`) and cancellation
retain their established errors; timeouts/cancellation issue no new cursor.

### Pagination and option combinations

Use `next_cursor` with an unchanged query, regex/window settings, filters and index
generation. The cursor binds window contract version 1 and the existing file order,
and retains the next start position (including same-line matches). Resume reloads
bounded forward context, so crossing matches are recovered once. Count scan-cap
cursors additionally preserve the first unconsumed column when a previous match
ended mid-line. `limit`, response byte limits and `lineScanLimit` may change;
window, scope or generation changes require a fresh query. Incompatible/stale or
invalid positions fail explicitly. Resumed pages describe only their segment and
remain non-authoritative; sum count pages from an unchanged index for a full count.

Supported: text, JSON/NDJSON, JSON envelopes and their compact/field projections,
count, `--all` or path selection, language and file exclusions. Required span fields
survive projections automatically, and terminal authority survives byte fitting.
Whole rows are omitted with replayable continuation; an unfit minimum response
returns `E028_RESPONSE_BUDGET_TOO_SMALL`. Existing `--all` and count format
restrictions still apply.

Semantic origin/result-kind filters and `origin-passes`, focus options, surrounding
context, and row-only array/CSV/TSV/LSP/quickfix/SARIF formats are rejected with
guidance. Cross-origin spans are treated as text and have no semantic classification;
no origin filter is silently discarded. Use at most 200 rows and a positive
`maxLineWidth`/`--max-line-width` of at most 512. The CLI and both MCP tools use the
same matching and counting pipeline.

## 日本語

通常の `find --regex` は索引済みのソースを1行ずつ正規表現へ渡します。
`(?s)` だけでは行をまたげません。複数行のソースを渡すには明示指定します。

```sh
cdidx find 'using System\.Globalization;\nusing System\.Text\.Json;' \
  --regex --multiline --window-lines 8 --path src/CodeIndex/Cli/QueryCommandRunner.Find.cs --json
```

MCP の `find` と `find_in_file` は `regex:true`、`multiline:true`、`windowLines`、
`windowBytes` を使います。

```json
{"name":"find","arguments":{"query":"A\\nB","path":"src/","regex":true,"multiline":true,"windowLines":8}}
```

対象は実ファイルではなく索引のスナップショットです。索引移行は不要で、従来のリテラル検索と
行単位の正規表現の動作は変わりません。

### 一致と座標

各物理行から最大 `windowLines` 行の完全な行を前方へ読み、ファイル末尾だけで短くします。
隣接する窓は重なり、保存チャンクの重複は既存の開始行・チャンク番号の順序で除去します。
ファイル間や欠落行を連結しません。CRLF は LF に正規化し、バイト上限には区切りの LF を
含む正規化後の UTF-8 サイズを使います。
窓内の行間にだけ LF を入れ、最後の行の後には追加しません。保存された末尾の空行がある場合は、
その直前の区切りも含みます。

その窓の先頭行に開始位置がある一致だけを返します。一致が消費した範囲内の開始位置は、
後続行であっても除外するため一致は重複しません。既存のファイル順、開始行、開始列で並べます。
ゼロ幅一致は UTF-16 の1コード単位だけ進め、行末を含め同じ位置を一度だけ返します。
既定では大小文字を区別せず、`--exact` で区別します。

これは**窓内の正規表現の契約**です。ファイル全体への正規表現とは異なり、窓全体がバイト上限に
収まる場合に、指定行数以内の一致を扱います。それより長い一致や窓外の先読み・後読みは対象外です。
貪欲量指定子は窓末尾を見ます。`(?m)` がなければ `^`/`$` は窓の境界を指し、.NET の末尾改行規則も
適用されます。`\A`/`\z` も窓の境界です。`(?m)` はアンカー、`(?s)` は dot による LF の消費を
変える指定で、どちらも単独では複数行ソースを有効にしません。`A\nB` は2行にまたがり、
`(?s)A.*B` も LF をまたげます。`(?m)^A$` は窓内の物理行にアンカーを合わせます。

`line`/`column` は1始まりの元の開始位置で、追加の `match_end_line`/`match_end_column` は
1始まりの**終端の次の位置**です。列は UTF-16 のコード単位で数え、補助平面の文字は2単位です。
`length` は途中の LF を含む正規化後の UTF-16 長です。既存の `start_line`/`end_line` は引き続き
スニペットの範囲です。スニペットは開始行のみ、最大512列で、一致が長くても拡大しません。
範囲全体の確認には `excerpt` を使ってください。

### 上限と完全性

| 対象 | 既定値 | 最大値 |
|---|---:|---:|
| `--window-lines` / `windowLines` | 8 | 64 |
| `--window-bytes` / `windowBytes` | 65,536 | 262,144 |
| 実体化する索引チャンク | 固定 | UTF-8 で1 MiB |
| クエリのソース読取量（チャンク重複を含む） | 固定 | 32 MiB |
| クエリの正規表現入力（窓の重複を含む） | 固定 | 32 MiB |
| クエリで評価する開始行数 | 固定 | 250,000 |
| 正規表現1回のタイムアウト | 固定 | 100 ms |
| 協調的に確認するクエリの期限 | 固定 | 5秒 |
| 返す一致の件数 | CLI は20 | 200 |

期限はソース読取・照合の間で確認します。実行中の正規表現は100 msまで継続し得ます。
DB操作は既存のキャンセル規則を維持します。保持するソースは、最大1 MiBのチャンク、
窓のバイト上限内の最大64行、同じ上限内の追加読取行、連結した窓1個です。
UTF-8 の1バイトにつき managed UTF-16 で最大2バイトを要します。完了した結果は最大200件の
上限付きスニペットだけを保持し、ファイル全体やクエリ全体のソースキャッシュは作りません。
正規表現の割り当ても既存のクエリ長と入力サイズ、タイムアウトの上限で制限されます。

`--all` は既存の4,096ファイル上限と `--line-scan-limit`（MCP は `lineScanLimit`）を維持します。
複数行モードでは評価した開始行を数え、先行一致が消費した行は追加評価しません。
先読みは別途バイト予算に計上します。`--path` で限定しても窓の上限は有効で、
`--allow-partial` で無効にはできません。

終端・件数には `multiline`、`window_contract_version:1`、`window_lines`、`window_bytes`、
`count_scope:bounded_multiline_nonoverlapping_matches`、`unbounded_absence_authoritative:false` を
出します。完了した件数の確定性は、この窓の契約と選択した索引範囲だけに限定されます。
ゼロ件でも任意長の一致がない証明にはなりません。件数は一致箇所数であり、行数やファイル数ではありません。

実行上限で網羅できない場合は `scan_truncated` と `partial_result` を有効にし、確定性を false、
終了コードを `11` にします（明示的な `--allow-partial` で `0`）。理由は
`multiline_window_bytes`、`multiline_chunk_bytes`、`multiline_source_gap`、
`multiline_query_bytes`、`multiline_query_lines`、`multiline_query_time` です。
これらでは前進するカーソルを出しません。`recovery_guidance` に従って窓の大きさを見直すか
ファイル範囲を狭めて再開始し、欠落した索引を更新するか巨大なソースを分割・手動確認してください。
出力件数を増やしても網羅性は回復しません。不正な正規表現、正規表現タイムアウト
（`E014_REGEX_MATCH_TIMEOUT`）、キャンセルは既存のエラーを維持し、タイムアウト・キャンセルで
新しいカーソルは発行しません。

### ページ再開と併用指定

`next_cursor` は検索語、正規表現・窓の設定、フィルター、索引世代を変えずに再利用します。
カーソルは窓契約のバージョン1、既存のファイル順、同じ行を含む次の一致開始位置に紐づきます。
上限内の前方文脈を読み直すため、境界をまたぐ一致も一度だけ回復します。件数の走査上限では、
先行一致が行の途中で終わった場合の未消費列も保存します。件数上限、応答サイズ、`lineScanLimit` は
変更できますが、窓・範囲・世代の変更時は再開始します。不一致・古い世代・不正位置は明示的なエラーです。
再開ページはその区間だけを表し、最終ページでも確定扱いにはしません。同じ索引の件数ページを合算できます。

text、JSON/NDJSON、JSON envelope とその compact・フィールド投影、件数、`--all` または path、
言語・ファイル除外に対応します。必須の一致座標は投影でも自動保持し、サイズ調整でも終端の確定性を保ちます。
行単位で省略して再開位置を返し、最小応答も収まらなければ `E028_RESPONSE_BUDGET_TOO_SMALL` にします。
既存の `--all` と件数出力の形式制限は引き続き適用します。

意味分類フィルター、`origin-passes`、focus、前後の文脈、行だけの array/CSV/TSV/LSP/quickfix/SARIF は
案内付きで拒否します。origin をまたぐ一致もテキストとして扱い、意味分類は付けず、フィルターを黙って
無視しません。最大200件、`maxLineWidth` / `--max-line-width` は1〜512で指定してください。
CLI と2つの MCP ツールは同じ照合・件数処理を使います。
