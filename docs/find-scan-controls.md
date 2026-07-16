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

Candidate-file or line-scan truncation returns partial-result exit code `11`.
Pass `--allow-partial` only when an incomplete scan may return exit code `0`.
An ordinary result-limit early stop remains successful, but the row terminal
sets `scan_complete=false`, `result_limit_reached=true`, and explains how to
increase `--limit` or narrow the query. Human output writes its scan summary to
stderr and uses the same partial exit semantics.

`--format compact` returns locations only. Use text or JSON output when context
from `--before`, `--after`, or `--snippet-lines` is needed.

## 日本語

`cdidx find --all` は repository 全体の index 済みファイルを safety cap 付きで
走査します。既定の indexed-line cap は 250,000 行です。`--all` と一緒に
`--line-scan-limit <n>` を指定すると、この cap を最大 10,000,000 行まで上げ下げできます。

既定 JSON row は、`scan_complete`、`authoritative_rows`、
`candidate_files`、`files_scanned`、`lines_scanned`、有効な file / line cap、
切り詰め理由、continuation action、復旧案内を含む 1 件の `terminal_record` で
終了します。count JSON は単一 object に同じ終端 scan 状態を持ち、count の
authority には `authoritative_count` を使います。

candidate-file または line-scan による切り詰めは partial-result 終了コード `11` を
返します。不完全な scan でも終了コード `0` を許容する場合だけ `--allow-partial` を
指定してください。通常の result limit による早期停止は成功のままですが、row 終端は
`scan_complete=false`、`result_limit_reached=true` を設定し、`--limit` を増やすか
query を絞る方法を示します。human output は scan summary を stderr に出し、同じ
partial exit semantics を使います。

`--format compact` は location のみを返します。`--before`、`--after`、
`--snippet-lines` の context が必要な場合は text または JSON output を使ってください。
