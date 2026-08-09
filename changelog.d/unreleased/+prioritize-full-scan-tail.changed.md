---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ExtractionPipeline.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ExtractionWorkers.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
---

## English

- **Parallel full scans start large tail files earlier** — extraction now checks only a fixed-size suffix of at most 64 work items and starts known in-limit files largest-first, reducing the final worker-wave tail without adding a repository-wide metadata pass.

## 日本語

- **parallel full scanが末尾の大きなfileを早く開始します** — extractionは最大64件の固定長suffixだけを確認し、size取得済みで上限内のfileを大きい順に開始するため、repository全体のmetadata passを増やさず最終worker waveの長い尾を短縮します。
