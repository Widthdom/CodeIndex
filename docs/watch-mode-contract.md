# Watch Mode Contract

## English

`cdidx index --watch --json` emits a `watch_contract` object on the `watching`
lifecycle event. The object captures the effective watch-mode contracts that
clients can depend on:

- `debounce: "quiet_window"` means changed paths drain only after the configured
  debounce window has been quiet.
- `change_coalescing: "distinct_paths_refresh_debounce"` means duplicate path
  events refresh the same quiet window without duplicating the path batch.
- `path_comparison` is `ordinal_ignore_case` or `ordinal`, matching the
  repository filesystem comparison used by the batcher.
- `rename_events: "old_and_new_paths"` means rename notifications enqueue both
  the old and new path.
- `overflow_recovery` and `watcher_error_recovery` are
  `full_rescan_after_debounce`, so pending-path overflow and watcher errors
  recover by emitting an overflow event and running a full rescan after the
  quiet window.
- `cancellation: "emit_stopped_after_current_poll_or_sub_run"` means
  cancellation stops the watch loop and emits `stopped` after the current poll
  or sub-run completes.
- `sub_run_output: "json_quiet_sub_runs"` means watch sub-runs are invoked with
  `--json --quiet`; JSON watch mode forwards the sub-run JSON after a watch
  event header, while human mode summarizes the sub-run.
- `mcp_watch_mode: "unsupported"` records the current CLI/MCP parity boundary:
  MCP accepts compatibility-shaped watch/debounce inputs only as unsupported
  diagnostics, and long-running watch mode remains a CLI-only behavior.

The legacy top-level `debounce_ms` and `watch_pending_path_limit` fields remain
on the `watching` event for compatibility. The contract object repeats the
effective values and adds `max_debounce_ms`, `poll_interval_ms`, and the path
comparison contract.

## 日本語

`cdidx index --watch --json` は `watching` lifecycle event に
`watch_contract` オブジェクトを出力します。このオブジェクトは、クライアントが
依存できる watch mode の有効な契約を示します。

- `debounce: "quiet_window"` は、設定された debounce window が静穏になって
  から changed path を drain することを示します。
- `change_coalescing: "distinct_paths_refresh_debounce"` は、同じ path の重複
  event が path batch を重複させず、同じ静穏 window を更新することを示します。
- `path_comparison` は `ordinal_ignore_case` または `ordinal` で、batcher が使う
  repository filesystem の比較方式に対応します。
- `rename_events: "old_and_new_paths"` は、rename 通知で old path と new path の
  両方を enqueue することを示します。
- `overflow_recovery` と `watcher_error_recovery` は
  `full_rescan_after_debounce` で、pending-path overflow と watcher error が静穏
  window 後の overflow event と full rescan で復旧することを示します。
- `cancellation: "emit_stopped_after_current_poll_or_sub_run"` は、キャンセル時に
  現在の poll または sub-run が終わってから watch loop を停止し、`stopped` を
  出力することを示します。
- `sub_run_output: "json_quiet_sub_runs"` は、watch sub-run を `--json --quiet` で
  実行することを示します。JSON watch mode は watch event header の後に sub-run
  JSON を転送し、human mode は sub-run を要約します。
- `mcp_watch_mode: "unsupported"` は、現在の CLI/MCP の境界を示します。MCP は
  watch/debounce 形式の入力を unsupported diagnostic として受け付けますが、
  長時間動作する watch mode は CLI 専用の動作です。

互換性のため、従来の top-level `debounce_ms` と `watch_pending_path_limit` は
`watching` event に残ります。contract object は有効値を繰り返し、さらに
`max_debounce_ms`、`poll_interval_ms`、path comparison contract を追加します。
