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
- **Fresh full scans reuse scan language metadata** — CLI and MCP successful fresh indexes now use the scan target language set for C#/SQL finalizer readiness instead of probing the newly-written `files` table.
- **MCP fresh indexes skip impossible file-reuse lookups** — MCP fresh/rebuild indexes now match the CLI path by disabling existing-symbol/stat/content reuse probes when the database started empty.
- **Fresh index summaries avoid table-wide count probes** — CLI and MCP successful fresh indexes now derive summary totals from committed insert counts, including TypeScript augmentation references, instead of running four final `COUNT(*)` scans.
- **Successful full indexes reuse captured Git branch metadata** — CLI and MCP successful full-index finalization now reuse one captured HEAD branch value across both index-head stamps instead of invoking git twice.
- **Fresh-index empty checks use a single existence probe** — CLI and MCP now decide whether a database started empty with `SELECT 1 FROM files LIMIT 1` instead of calling the four-table summary count helper.
- **Index-start metadata cleanup batches related keys** — CLI and MCP index startup now clear failed-run and hotspot-family metadata with one multi-key upsert per group instead of issuing one metadata write per key.
- **Fresh fold readiness stamps reuse indexed symbol languages** — CLI and MCP fresh/rebuild full scans now stamp symbol-extractor contract versions from the languages committed during the run instead of querying distinct indexed languages from the database.
- **Successful index metadata stamps batch related writes** — CLI and MCP successful index finalization now persist unknown-extension and last-index-run metadata with grouped upserts instead of one metadata statement per field.
- **Successful index HEAD stamps batch related writes** — CLI and MCP now persist indexed HEAD commit/branch and HEAD freshness metadata with grouped upserts during successful finalization.
- **Fold readiness metadata stamps batch version writes** — successful fold readiness finalization now persists fold-key and symbol-extractor contract metadata with grouped upserts instead of per-key metadata writes.
- **Index diagnostic metadata batches related writes** — successful diagnostic cleanup and failed/partial run metadata persistence now use grouped upserts instead of writing each diagnostic field separately.
- **FTS bulk-load finalization avoids duplicate counter resets** — normal bulk-load completion now lets FTS optimize reset incremental-write metadata once, while recovery and abandon rebuilds still reset it when no optimize follows.
- **FTS bulk-load trigger changes execute as grouped SQL** — suspending and restoring FTS sync triggers now issue one grouped trigger statement set instead of three separate database commands.
- **Hotspot-family readiness stamps batch related metadata** — successful hotspot-family finalization now writes per-language version, marker fingerprint, and superseded global-key clears with one grouped upsert.
- **Writer-version and symbol-filter stamps share one write** — CLI full scans, CLI updates, and MCP indexes now persist the writer version and symbol-kind filter signature with one grouped upsert when the writer version is available.
- **Reader contract readiness stamps batch related metadata** — CLI full scans and MCP indexes now persist C# symbol-name, SQL graph, and symbols-only graph contract metadata with grouped upserts during successful finalization.
- **Index startup batches fixed metadata reads** — CLI index startup now reads fixed prior-run metadata keys with one multi-key query instead of issuing one metadata lookup per field.

## 日本語

- **初回インデックス作成で全文検索インデックスを一括投入するようになりました** — fresh / rebuild のインデックス作成では行ごとの FTS トリガー更新を一時停止し、`chunks` から FTS テーブルを一度だけ再構築してから最適化するため、大規模リポジトリ初回投入時の書き込み増幅を抑えます。
- **MCP index の相互再帰最終化を後段へ延期しました** — MCP `index` はファイルごとの参照挿入時に相互再帰グラフ全体を毎回再計算せず、変更ファイルのコミット後に一度だけ更新するようになりました。
- **fresh index の空 issue cleanup 書き込みを省きます** — CLI の fresh index と MCP の rebuild / 空DB index では、既存の検証 issue が存在し得ない新規ファイル行に対するファイル単位の `file_issues` DELETE を避けるようになりました。
- **MCP の空DB index で古いファイルデータ cleanup probe を省きます** — MCP `index` は、DB が空の状態または rebuild 直後に始まった場合、CLI fresh-index 経路と同じくファイル単位の古い chunk / symbol cleanup lookup を避けるようになりました。
- **MCP の空DB index で stale purge query を省きます** — MCP `index` は、DB が空の状態または rebuild 直後に始まった場合、stale file purge、unsupported reference purge、purge 前の C# contract 読み出しをスキップするようになりました。
- **CLI fresh index で stale purge query を省きます** — 空DBから始まる CLI full scan は、scan checkpoint の保存/削除 semantics を保ったまま、retained set 構築、stale file purge、unsupported reference purge をスキップするようになりました。
- **TypeScript を含まない fresh index で augmentation rebuild を省きます** — CLI と MCP の fresh index は、scan で TypeScript ファイルが見つからなかった場合、augmentation の DELETE/SELECT rebuild を実行せず current contract だけを stamp するようになりました。
- **fresh full scan の fold-readiness 重複検証を省きます** — CLI と MCP の fresh / rebuild 相当の full scan は、直後に guarded な `MarkFoldReady` 再検証を行うため、その前の同種の folded-column 全表 scan を省くようになりました。
- **fresh full scan で scan 済みの言語 metadata を再利用します** — CLI と MCP の成功した fresh index は、C# / SQL finalizer readiness の判定に新しく書いた `files` table への probe ではなく scan target の言語集合を使うようになりました。
- **MCP fresh index で成立しない file reuse lookup を省きます** — MCP の fresh / rebuild index は、DB が空で始まった場合に既存 symbol / stat / content reuse probe を無効化し、CLI 経路と同じ挙動になりました。
- **fresh index summary でテーブル全体の count probe を避けます** — CLI と MCP の成功した fresh index は、TypeScript augmentation references を含む commit 済み挿入件数から summary totals を作り、最後の4本の `COUNT(*)` scan を省くようになりました。
- **成功した full index で取得済み Git branch metadata を再利用します** — CLI と MCP の成功時 finalization は、2種類の index-head stamp で同じ HEAD branch 値を再利用し、git 呼び出しを重複させないようになりました。
- **fresh index の空DB判定を単一の存在確認にします** — CLI と MCP は DB が空で始まったかを4テーブル summary count helper ではなく `SELECT 1 FROM files LIMIT 1` で判定するようになりました。
- **index start の metadata cleanup で関連キーをまとめます** — CLI と MCP の index startup は、failed-run metadata と hotspot-family metadata のクリアをキーごとの個別 metadata write ではなく、グループごとに1回の multi-key upsert で行うようになりました。
- **fresh fold readiness stamp で index 済み symbol 言語を再利用します** — CLI と MCP の fresh / rebuild full scan は、symbol-extractor contract version を DB から distinct indexed language として読み直さず、その run で commit した言語集合から stamp するようになりました。
- **成功時 index metadata stamp の関連 write をまとめます** — CLI と MCP の成功時 finalization は、unknown-extension metadata と last-index-run metadata をフィールドごとの個別 metadata statement ではなく grouped upsert で保存するようになりました。
- **成功時 index HEAD stamp の関連 write をまとめます** — CLI と MCP は成功時 finalization で indexed HEAD commit / branch と HEAD freshness metadata を grouped upsert で保存するようになりました。
- **fold readiness metadata stamp の version write をまとめます** — 成功時の fold readiness finalization は、fold-key と symbol-extractor contract metadata をキーごとの個別 metadata write ではなく grouped upsert で保存するようになりました。
- **index diagnostic metadata の関連 write をまとめます** — 成功時の diagnostic cleanup と失敗 / partial run metadata 保存は、diagnostic field ごとの個別 write ではなく grouped upsert を使うようになりました。
- **FTS bulk-load finalization の counter reset 重複を省きます** — 通常の bulk-load 完了では FTS optimize が incremental-write metadata を一度だけ reset し、recovery / abandon rebuild では optimize が続かない場合も従来通り reset するようになりました。
- **FTS bulk-load trigger 変更を grouped SQL で実行します** — FTS sync trigger の一時停止と復元は、3回の個別 database command ではなく1つの grouped trigger statement set で実行するようになりました。
- **hotspot-family readiness stamp の関連 metadata をまとめます** — 成功時 hotspot-family finalization は、言語別 version、marker fingerprint、廃止済み global key clear を1回の grouped upsert で保存するようになりました。
- **writer-version と symbol-filter stamp を1回の write にします** — CLI full scan、CLI update、MCP index は writer version がある場合、writer version と symbol-kind filter signature を1回の grouped upsert で保存するようになりました。
- **reader contract readiness stamp の関連 metadata をまとめます** — CLI full scan と MCP index は成功時 finalization で C# symbol-name、SQL graph、symbols-only graph contract metadata を grouped upsert で保存するようになりました。
- **index startup の固定 metadata read をまとめます** — CLI index startup は prior-run metadata の固定キーをフィールドごとの個別 lookup ではなく、1回の multi-key query で読み出すようになりました。
