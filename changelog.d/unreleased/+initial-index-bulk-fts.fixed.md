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
- **Fresh indexing skips empty issue cleanup writes** — CLI fresh indexes and MCP rebuild/empty indexes now avoid per-file `file_issues` cleanup DELETEs for newly-created file rows that cannot have existing validation issues.
- **MCP empty indexes skip stale file-data cleanup probes** — MCP `index` now matches the CLI fresh-index path by avoiding per-file stale chunk/symbol cleanup lookups when the database started empty or was just rebuilt.
- **MCP empty indexes skip stale purge queries** — MCP `index` now skips stale-file purge, unsupported-reference purge, and pre-purge C# contract reads when the database started empty or was just rebuilt.
- **CLI fresh indexes skip stale purge queries** — CLI full scans that start from an empty database now skip retained-set construction, stale-file purge, and unsupported-reference purge while still preserving scan checkpoint cleanup semantics.
- **Fresh non-TypeScript indexes skip augmentation rebuilds** — CLI and MCP fresh indexes now stamp the current TypeScript augmentation contract without running the augmentation DELETE/SELECT rebuild when the scan found no TypeScript files.
- **Fresh full scans avoid duplicate fold-readiness verification** — CLI and MCP fresh/rebuild-style full scans now rely on the guarded `MarkFoldReady` re-verification instead of running an identical folded-column table scan immediately before it.

## 日本語

- **初回インデックス作成で全文検索インデックスを一括投入するようになりました** — fresh / rebuild のインデックス作成では行ごとの FTS トリガー更新を一時停止し、`chunks` から FTS テーブルを一度だけ再構築してから最適化するため、大規模リポジトリ初回投入時の書き込み増幅を抑えます。
- **MCP index の相互再帰最終化を後段へ延期しました** — MCP `index` はファイルごとの参照挿入時に相互再帰グラフ全体を毎回再計算せず、変更ファイルのコミット後に一度だけ更新するようになりました。
- **fresh index の空 issue cleanup 書き込みを省きます** — CLI の fresh index と MCP の rebuild / 空DB index では、既存の検証 issue が存在し得ない新規ファイル行に対するファイル単位の `file_issues` DELETE を避けるようになりました。
- **MCP の空DB index で古いファイルデータ cleanup probe を省きます** — MCP `index` は、DB が空の状態または rebuild 直後に始まった場合、CLI fresh-index 経路と同じくファイル単位の古い chunk / symbol cleanup lookup を避けるようになりました。
- **MCP の空DB index で stale purge query を省きます** — MCP `index` は、DB が空の状態または rebuild 直後に始まった場合、stale file purge、unsupported reference purge、purge 前の C# contract 読み出しをスキップするようになりました。
- **CLI fresh index で stale purge query を省きます** — 空DBから始まる CLI full scan は、scan checkpoint の保存/削除 semantics を保ったまま、retained set 構築、stale file purge、unsupported reference purge をスキップするようになりました。
- **TypeScript を含まない fresh index で augmentation rebuild を省きます** — CLI と MCP の fresh index は、scan で TypeScript ファイルが見つからなかった場合、augmentation の DELETE/SELECT rebuild を実行せず current contract だけを stamp するようになりました。
- **fresh full scan の fold-readiness 重複検証を省きます** — CLI と MCP の fresh / rebuild 相当の full scan は、直後に guarded な `MarkFoldReady` 再検証を行うため、その前の同種の folded-column 全表 scan を省くようになりました。
