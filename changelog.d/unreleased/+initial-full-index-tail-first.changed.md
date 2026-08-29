---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ExtractionPipeline.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ExtractionWorkers.cs
---

## English

- **Cold full scans start scheduled tail work in the first worker wave** — The bounded cross-language tail schedule is now consumed before the untouched prefix, so large eligible files near the scan tail begin promptly while every work item still runs exactly once.

## 日本語

- **初回full scanの最初のworker waveでtail scheduleを開始** — 全言語共通のbounded tail scheduleを未変更のprefixより先に消費し、scan末尾付近の大きな対象fileを早く開始しながら、全work itemを引き続き厳密に1回ずつ処理します。
