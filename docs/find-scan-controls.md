# Find Scan Controls

## English

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
