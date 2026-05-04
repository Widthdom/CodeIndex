---
category: added
affected:
  - src/CodeIndex/Cli/IndexFreshnessChecker.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - docs/USER_GUIDE.md
  - AGENT_GUIDE.md
---

## English

- **Added `status --check` for exact DB/workspace freshness checks** — `cdidx status --check --json` now compares indexed file paths and raw-byte checksums against the current indexable workspace, returns `index_matches_workspace`, and exits `5` when the DB is stale so agents can skip unnecessary reindexing when the check passes.

## 日本語

- **DB と workspace の完全一致を確認する `status --check` を追加** — `cdidx status --check --json` は indexed file の path と raw-byte checksum を現在の index 対象 workspace と比較し、`index_matches_workspace` を返します。不一致なら終了コード `5` になるため、agent は一致時に不要な再インデックスを避けられます。
