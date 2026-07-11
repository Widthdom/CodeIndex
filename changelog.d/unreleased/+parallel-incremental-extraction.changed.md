---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
---

## English

- **Incremental full scans extract changed files in parallel** - full-workspace incremental indexing now sends changed files through the same bounded parallel extraction pipeline as fresh and rebuilt indexes. Worker counts are capped to the actual changed-file workload, the result queue is limited to two completed payloads per worker, fixed worker slots replace a contended dictionary for phase tracking, and isolated symbol-worker clients are created only on extraction paths that use them. This bounds source, chunk, symbol, and reference memory and reduces synchronization and setup overhead. The existing pre-check still avoids loading unchanged files, while post-extraction hooks or symbol-kind filters retain their required sequential path across every indexed language.

## 日本語

- **incremental full scan で変更ファイルを並列抽出するようにしました** - ワークスペース全体の incremental indexing は、変更ファイルを fresh / rebuild index と同じ bounded parallel extraction pipeline に送るようになりました。worker 数は実際の変更ファイル数を上限とし、result queue は worker ごとに完了 payload 2 件までに制限し、phase 追跡の競合 dictionary を固定 worker slot に置き換え、isolated symbol-worker client は実際に使う抽出経路でのみ生成します。これにより source / chunk / symbol / reference のメモリと同期・setup overhead を抑えます。既存の事前判定は引き続き未変更ファイルの読み込みを避け、post-extraction hook と symbol-kind filter は全 indexed language で必要な直列経路を維持します。
