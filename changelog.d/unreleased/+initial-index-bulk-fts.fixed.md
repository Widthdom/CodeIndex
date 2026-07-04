---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Initial indexing bulk-loads the text search index** — fresh and rebuild indexing now suspend per-row FTS trigger maintenance, rebuild the FTS table once from `chunks`, and then optimize it, reducing first-index write amplification on large repositories.
- **MCP indexing defers mutual-recursion finalization** — MCP `index` now inserts per-file references without recalculating the whole mutual-recursion graph each time, then refreshes the graph once after all changed files are committed.

## 日本語

- **初回インデックス作成で全文検索インデックスを一括投入するようになりました** — fresh / rebuild のインデックス作成では行ごとの FTS トリガー更新を一時停止し、`chunks` から FTS テーブルを一度だけ再構築してから最適化するため、大規模リポジトリ初回投入時の書き込み増幅を抑えます。
- **MCP index の相互再帰最終化を後段へ延期しました** — MCP `index` はファイルごとの参照挿入時に相互再帰グラフ全体を毎回再計算せず、変更ファイルのコミット後に一度だけ更新するようになりました。
