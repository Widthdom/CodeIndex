# Find Scan Controls

## English

`cdidx find --all` scans indexed files across the repository with bounded safety
caps. The default indexed-line cap is 250,000 lines. Use
`--line-scan-limit <n>` with `--all` to lower or raise that cap, up to
10,000,000 lines.

Count JSON reports the effective cap and degradation state through
`line_scan_limit`, `lines_scanned`, `scan_truncated`, `scan_cap_reached`,
`scan_truncation_reason`, `degraded`, and `authoritative_count`.

`--format compact` returns locations only. Use text or JSON output when context
from `--before`, `--after`, or `--snippet-lines` is needed.

## 日本語

`cdidx find --all` は repository 全体の index 済みファイルを safety cap 付きで
走査します。既定の indexed-line cap は 250,000 行です。`--all` と一緒に
`--line-scan-limit <n>` を指定すると、この cap を最大 10,000,000 行まで上げ下げできます。

count JSON では、有効な cap と degradation 状態を `line_scan_limit`、
`lines_scanned`、`scan_truncated`、`scan_cap_reached`、
`scan_truncation_reason`、`degraded`、`authoritative_count` で返します。

`--format compact` は location のみを返します。`--before`、`--after`、
`--snippet-lines` の context が必要な場合は text または JSON output を使ってください。
